using System;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Ice-cooled cellars: stacking blocks of ice (glacier ice, packed glacier
    // ice, lake ice - anything with the Ice block material) inside a proper
    // cellar chills it below the vanilla 5°C cellar floor, down to a configured
    // target (default -3°C at 10+ blocks), which roughly doubles food shelf
    // life. Vanilla has no freezing mechanic for stored food; sub-zero
    // temperatures just slow perishing further, and we stay inside the engine's
    // own 0.1x rate floor.
    //
    // How vanilla computes it (InWorldContainer.GetPerishRate): a "cellarness"
    // score in 0..1 - the room must be small (<=7x7x7, or one axis 9 if <=150
    // blocks total, zero exits), penalized by skylight ratio and by the ratio
    // of non-cooling to cooling wall faces - lerps an effective temperature
    // from ambient toward a hardcoded 5°C, then maps temperature to a perish
    // rate via rate = 3^(T/19 - 1.2) - 0.1, clamped to [0.1, 2.4].
    //
    // We postfix GetPerishRate: invert the returned rate back to the effective
    // temperature (the mapping is monotonic between the clamps), pull it toward
    // the ice target proportionally to ice count and cellarness, and re-apply
    // the same mapping. Because vanilla already takes min(effective, ambient),
    // winter cold passes through untouched and there is no double-dipping.
    //
    // Companion fix: vanilla's Block.GetRetention only counts Ore, Stone, Soil
    // and Ceramic as cooling wall materials - Ice counts as a NON-cooling wall
    // (+1), so without help every placed ice block would drag the cellarness
    // score down. A postfix flips solid Ice faces to cooling (-1).
    public static class IceCellarPatches
    {
        // Tunables, seeded from SeraphsLedgerConfig at patch time.
        private static int minBlocks = 4;
        private static int fullBlocks = 10;
        private static float targetTemp = -3f;
        private static float minMeltChance = 0.01f;

        private static readonly AccessTools.FieldRef<InWorldContainer, ICoreAPI> ApiField =
            AccessTools.FieldRefAccess<InWorldContainer, ICoreAPI>("Api");
        private static readonly AccessTools.FieldRef<InWorldContainer, Vintagestory.GameContent.PositionProviderDelegate> PositionProviderField =
            AccessTools.FieldRefAccess<InWorldContainer, Vintagestory.GameContent.PositionProviderDelegate>("positionProvider");
        private static readonly AccessTools.FieldRef<BlockEntityContainer, InWorldContainer> ContainerField =
            AccessTools.FieldRefAccess<BlockEntityContainer, InWorldContainer>("container");

        // Ice count per room, refreshed at most every 10s. GetPerishRate runs
        // per container tick (and per tooltip refresh), so don't rescan the
        // room volume every call. Keyed weakly: rooms are rebuilt by the
        // RoomRegistry on block changes, and stale Room objects just fall out.
        private class IceCount
        {
            public int Count;
            public long ValidUntilMs;
        }

        private static readonly ConditionalWeakTable<Room, IceCount> iceCounts = new();

        public static void Configure(SeraphsLedgerConfig cfg)
        {
            minBlocks = Math.Max(1, cfg.IceCellarMinBlocks);
            fullBlocks = Math.Max(minBlocks, cfg.IceCellarFullBlocks);
            // Below -5°C the vanilla mapping is floored at 0.1x anyway.
            targetTemp = GameMath.Clamp(cfg.IceCellarTemp, -5f, 5f);
            minMeltChance = GameMath.Clamp(cfg.IceCellarMinMeltChance, 0f, 1f);
        }

        public static void GetPerishRatePostfix(InWorldContainer __instance, ref float __result)
        {
            // Outside the invertible band there is nothing to gain: at the
            // floor the cellar is already as cold as the engine allows
            // (winter), and near the ceiling this isn't a functioning cellar.
            if (__result <= 0.102f || __result >= 2.35f) return;

            Room room = __instance.Room;
            if (room == null || !room.IsSmallRoom) return;

            ICoreAPI api = ApiField(__instance);
            if (api == null) return;

            int ice = CountIceBlocks(api, room, PositionProviderField(__instance)?.Invoke());
            if (ice < minBlocks) return;

            // Same cellarness formula GetPerishRate itself uses, so the chill
            // scales with how good the cellar already is.
            float cellarness = Cellarness(room);
            if (cellarness <= 0f) return;

            // rate = 3^(T/19 - 1.2) - 0.1  =>  T = 19 * (log3(rate + 0.1) + 1.2)
            float effTemp = 19f * ((float)(Math.Log(__result + 0.1f) / Math.Log(3.0)) + 1.2f);
            if (effTemp <= targetTemp) return;

            float chilled = GameMath.Lerp(effTemp, targetTemp, IceFactor(ice) * cellarness);
            __result = Math.Max(0.1f, Math.Min(2.4f, (float)Math.Pow(3.0, chilled / 19f - 1.2) - 0.1f));
        }

        // Vanilla only treats Ore/Stone/Soil/Ceramic as cooling cellar walls;
        // flip solid Ice faces from non-cooling (+1) to cooling (-1) so ice
        // blocks help rather than hurt the room's wall rating.
        public static void GetRetentionPostfix(Block __instance, EnumRetentionType type, ref int __result)
        {
            if (type == EnumRetentionType.Heat && __result == 1
                && __instance.BlockMaterial == EnumBlockMaterial.Ice)
            {
                __result = -1;
            }
        }

        // Container tooltip: surface the ice so the mechanic is discoverable.
        // The perish-speed number above it already reflects the chill, since
        // vanilla computes it through the patched GetPerishRate.
        public static void GetBlockInfoPostfix(BlockEntityContainer __instance, StringBuilder dsc)
        {
            InWorldContainer container = ContainerField(__instance);
            Room room = container?.Room;
            if (room == null || !room.IsSmallRoom || __instance.Api == null) return;

            int ice = CountIceBlocks(__instance.Api, room, __instance.Pos);
            if (ice <= 0) return;

            if (ice >= minBlocks)
            {
                dsc.AppendLine(Lang.Get("seraphsledger:icecellar-chilled", ice,
                    Math.Round(IceFactor(ice) * 100f)));
            }
            else
            {
                dsc.AppendLine(Lang.Get("seraphsledger:icecellar-needsmore", ice, minBlocks));
            }
        }

        // Cellar-aware ice melting. Vanilla BlockLakeIce melts by random server
        // tick with chance (outdoorClimateTemp - 2) / 20 - it never looks at
        // rooms, so cellar ice melts as fast as ice in the summer sun (the
        // candidate roll happens off-thread where room lookups aren't safe),
        // and OnServerGameTick replaces the melted ice with a FULL water
        // source, slowly flooding the room.
        //
        // OnServerGameTick runs on the main server thread, so we fix both
        // there: re-roll the vanilla melt decision against the ice-chilled
        // effective room temperature (more ice -> colder -> slower melt; a
        // small floor chance means ice always wastes away eventually), and in
        // any fully enclosed room melt to air instead of a water source.
        // Random ticks only fire at all when the outdoor temp is above 2°C, so
        // winter still preserves cellar ice completely, for free.
        public static bool IceMeltPrefix(Block __instance, IWorldAccessor world, BlockPos pos)
        {
            RoomRegistry roomReg = world.Api.ModLoader.GetModSystem<RoomRegistry>();
            if (roomReg == null) return true;

            // Ice is a solid block, so the cell it occupies is never part of a
            // room's air space - asking the registry about the ice's own
            // position yields a degenerate one-cell "room", not the cellar.
            // Resolve the room it actually serves through its open (non-wall)
            // neighbor cells instead; that covers both ice standing inside a
            // cellar and ice lining its walls, floor or ceiling.
            Room cellar = null;
            bool enclosed = false;
            bool anyOpenNeighbor = false;
            IBlockAccessor acc = world.BlockAccessor;
            BlockPos npos = pos.Copy();
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                npos.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);
                // Same wall test the room flood-fill uses: non-zero heat
                // retention means that cell is a wall, not room space.
                if (acc.GetBlock(npos).GetRetention(npos, face.Opposite, EnumRetentionType.Heat) != 0) continue;

                anyOpenNeighbor = true;
                Room r = roomReg.GetRoomForPosition(npos);
                if (r == null) continue;
                if (r.IsSmallRoom) { cellar = r; break; }
                if (r.ExitCount == 0) enclosed = true;
            }

            // Fully buried ice is insulated - it neither melts nor floods.
            // Ice open to a non-room / exited space melts like vanilla.
            if (cellar == null && !enclosed) return anyOpenNeighbor;

            if (cellar != null)
            {
                // The chance that got this tick scheduled, same formula as
                // vanilla's ShouldReceiveServerGameTicks.
                float outTemp = world.BlockAccessor.GetClimateAt(pos,
                    EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, world.Calendar.TotalDays).Temperature;
                float outChance = GameMath.Clamp((outTemp - 2f) / 20f, 0f, 1f);
                if (outChance <= 0f) return false; // stale roll, it's cold now

                // Melt chance at the ice-adjusted room temperature instead.
                // Below minBlocks IceFactor is 0 and this degrades to the plain
                // 5°C cellar. Thinning the vanilla roll by inChance/outChance
                // yields a net per-tick melt probability of exactly inChance.
                int ice = CountIceBlocks(world.Api, cellar, pos);
                float target = GameMath.Lerp(5f, targetTemp, IceFactor(ice));
                float effTemp = Math.Min(GameMath.Lerp(outTemp, target, Cellarness(cellar)), outTemp);
                float inChance = Math.Max(minMeltChance, GameMath.Clamp((effTemp - 2f) / 20f, 0f, 1f));

                if (world.Rand.NextDouble() * outChance > inChance) return false; // survived
            }

            // Melting inside an enclosed room: vanish instead of flooding.
            world.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
            return false;
        }

        private static float Cellarness(Room room)
        {
            float skyRatio = (float)room.SkylightCount
                / Math.Max(1, room.SkylightCount + room.NonSkylightCount);
            return 1f - 0.4f * skyRatio - 0.5f * GameMath.Clamp(
                (float)room.NonCoolingWallCount / Math.Max(1, room.CoolingWallCount), 0f, 1f);
        }

        // Ramps from the first counted block at minBlocks up to full effect.
        private static float IceFactor(int ice)
        {
            return GameMath.Clamp(
                (ice - minBlocks + 1) / (float)(fullBlocks - minBlocks + 1), 0f, 1f);
        }

        // Counts Ice-material blocks inside the room's bounding box plus a
        // one-block shell around it, so ice lining the walls, floor or ceiling
        // counts the same as ice standing in the room. Small rooms are at most
        // 9x7x7 (11x9x9 with the shell), so a full scan is cheap; results are
        // cached per room for 10s. GetBlock's default layer access resolves
        // fluid-layer blocks that are solid - which is exactly what placed ice
        // is (BlockLakeIce lives in the fluid layer) - so one lookup per
        // position sees both placed ice and worldgen glacier ice.
        private static int CountIceBlocks(ICoreAPI api, Room room, BlockPos anchorPos)
        {
            long now = api.World.ElapsedMilliseconds;
            IceCount cached = iceCounts.GetOrCreateValue(room);
            if (cached.ValidUntilMs > now) return cached.Count;

            Cuboidi loc = room.Location.Clone();
            loc.GrowBy(1, 1, 1);
            // Copy the container's position so the scan pos carries the right
            // dimension; fall back to the default dimension without an anchor.
            BlockPos pos = anchorPos?.Copy() ?? new BlockPos(0);
            IBlockAccessor acc = api.World.BlockAccessor;

            int count = 0;
            for (int x = loc.X1; x <= loc.X2; x++)
            {
                for (int y = loc.Y1; y <= loc.Y2; y++)
                {
                    for (int z = loc.Z1; z <= loc.Z2; z++)
                    {
                        pos.Set(x, y, z);
                        if (acc.GetBlock(pos).BlockMaterial == EnumBlockMaterial.Ice) count++;
                    }
                }
            }

            cached.Count = count;
            cached.ValidUntilMs = now + 10000;
            return count;
        }
    }
}
