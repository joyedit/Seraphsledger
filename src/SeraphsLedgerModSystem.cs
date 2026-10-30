using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace SeraphsLedger
{
    // Sent client -> server when the player clicks the "Sort" button in their
    // backpack inventory. Empty messages still need a ProtoContract so
    // protobuf-net can (de)serialize them across the network channel.
    [ProtoContract]
    public class SortBackpackPacket { }

    // Sent client -> server when the player clicks "Trash" to destroy whatever
    // stack is currently held on the mouse cursor.
    [ProtoContract]
    public class TrashCursorPacket { }

    // Sent client -> server when the player presses the show-region hotkey while
    // looking at a tally board. Carries the board so the server doesn't have to
    // guess what the player was aiming at.
    [ProtoContract]
    public class ShowTallyRegionPacket
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
    }

    public class SeraphsLedgerModSystem : ModSystem
    {
        public const string NetworkChannelName = "seraphsledger";

        // The client GUI patch lives in a static Harmony method and needs a way
        // to fire packets; stash the channel here so it can reach it.
        internal static IClientNetworkChannel ClientChannel;

        private const string HarmonyId = "seraphsledger.ctrlmoveall";
        private const string IceCellarHarmonyId = "seraphsledger.icecellar";
        private const string BarrelHarmonyId = "seraphsledger.barrelportions";
        private const string CrateHarmonyId = "seraphsledger.cratesweep";
        private Harmony harmony;
        private Harmony iceCellarHarmony;
        private Harmony barrelHarmony;
        private Harmony crateHarmony;
        private ICoreServerAPI sapi;
        private ICoreClientAPI capi;
        private ContainerLedger containerLedger;
        private EnemyDropDespawn enemyDropDespawn;
        private StepCountdown stepCountdown;
        private StepCountdownHud stepCountdownHud;
        private GuiDialogFeatureConfig configDialog;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            // Registered identically on both sides so the message type ids match.
            api.Network.RegisterChannel(NetworkChannelName)
                .RegisterMessageType<SortBackpackPacket>()
                .RegisterMessageType<TrashCursorPacket>()
                .RegisterMessageType<SortVesselPacket>()
                .RegisterMessageType<ShowTallyRegionPacket>()
                .RegisterMessageType<SweepCratePacket>();

            // Ice-cooled cellars run on both sides: the server applies the
            // reduced perish rate to stored food, the client shows it in the
            // container tooltip. Start() runs for each side (same process in
            // singleplayer), hence the HasAnyPatches guard.
            //
            // The same feature also ships as the standalone Ice Cellar mod. It
            // patches the same vanilla methods, and two stacked postfixes would
            // chill a cellar twice, so whenever it's installed we let it own the
            // feature regardless of our own toggle - a player who installed it
            // asked for it explicitly, and its config governs from there.
            var cfg = SeraphsLedgerConfig.Load(api);

            // The tally board's block entity is registered whatever the toggle
            // says, so boards already placed in a save still load; the toggle
            // only stops them scanning (and the recipe, in AssetsFinalize).
            api.RegisterBlockClass("SeraphsLedgerTallyBoard", typeof(BlockTallyBoard));
            api.RegisterBlockEntityClass("SeraphsLedgerTallyBoard", typeof(BlockEntityTallyBoard));
            BlockEntityTallyBoard.ScanEnabled = cfg.AnimalTally;

            if (cfg.IceCooledCellars && api.ModLoader.IsModEnabled("icecooledcellars"))
            {
                api.Logger.Notification(
                    "[seraphsledger] standalone Ice Cellar mod detected, leaving ice-cooled cellars to it");
            }
            else if (cfg.IceCooledCellars && !Harmony.HasAnyPatches(IceCellarHarmonyId))
            {
                IceCellarPatches.Configure(cfg);
                iceCellarHarmony = new Harmony(IceCellarHarmonyId);

                // The one method every food container's perish speed (and its
                // tooltip number) funnels through.
                iceCellarHarmony.Patch(
                    AccessTools.Method(typeof(Vintagestory.GameContent.InWorldContainer), "GetPerishRate"),
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(IceCellarPatches), nameof(IceCellarPatches.GetPerishRatePostfix))));

                // Make Ice count as a cooling cellar-wall material (vanilla
                // counts it as non-cooling, so placed ice would otherwise
                // degrade the room's cellar rating).
                iceCellarHarmony.Patch(
                    AccessTools.Method(typeof(Block), nameof(Block.GetRetention),
                        new[] { typeof(BlockPos), typeof(BlockFacing), typeof(EnumRetentionType) }),
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(IceCellarPatches), nameof(IceCellarPatches.GetRetentionPostfix))));

                // Cellar-aware ice melting: slower melt driven by the chilled
                // room temperature, and no water-source flooding indoors.
                iceCellarHarmony.Patch(
                    AccessTools.Method(typeof(Vintagestory.GameContent.BlockLakeIce), "OnServerGameTick"),
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(IceCellarPatches), nameof(IceCellarPatches.IceMeltPrefix))));

                // Ice-count line in container tooltips, for discoverability.
                iceCellarHarmony.Patch(
                    AccessTools.Method(typeof(Vintagestory.GameContent.BlockEntityContainer),
                        nameof(Vintagestory.GameContent.BlockEntityContainer.GetBlockInfo)),
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(IceCellarPatches), nameof(IceCellarPatches.GetBlockInfoPostfix))));
            }

            // Barrel ingredient portioning. Also both sides: an inventory move is
            // run on the client for responsiveness and re-run on the server from
            // the packet, so a clamp applied to only one of them would desync.
            // GetRemainingSlotSpace is the one method every put-into-slot path
            // sizes itself with; the postfix ignores everything that isn't a
            // barrel input slot.
            if (cfg.BarrelIngredientPortions && !Harmony.HasAnyPatches(BarrelHarmonyId))
            {
                barrelHarmony = new Harmony(BarrelHarmonyId);
                barrelHarmony.Patch(
                    AccessTools.Method(typeof(ItemSlot), nameof(ItemSlot.GetRemainingSlotSpace)),
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(BarrelPortionPatches), nameof(BarrelPortionPatches.GetRemainingSlotSpacePostfix))));
            }

            // Crate sweeps. Both sides, for the same reason the barrel clamp is:
            // a crate interaction is run on the client for responsiveness and
            // re-run on the server from the interact packet, so a sweep applied
            // to only one of them would desync. The flags are read on both sides
            // too - the hotkey route is asked for by the client and carried out
            // by the server.
            CrateSweep.EmptyHandEnabled = cfg.CrateSweepEmptyHand;
            CrateSweep.HeldStackEnabled = cfg.CrateSweepHeldStack;
            CrateSweep.HotkeyEnabled = cfg.CrateSweepHotkey;

            if ((cfg.CrateSweepEmptyHand || cfg.CrateSweepHeldStack) && !Harmony.HasAnyPatches(CrateHarmonyId))
            {
                crateHarmony = new Harmony(CrateHarmonyId);

                var crateInteract = AccessTools.Method(
                    typeof(Vintagestory.GameContent.BlockEntityCrate),
                    nameof(Vintagestory.GameContent.BlockEntityCrate.OnBlockInteractStart));
                if (crateInteract != null)
                {
                    crateHarmony.Patch(crateInteract,
                        prefix: new HarmonyMethod(AccessTools.Method(
                            typeof(CrateInteractSweepPatch), nameof(CrateInteractSweepPatch.Prefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(CrateInteractSweepPatch), nameof(CrateInteractSweepPatch.Postfix))));
                }

                // The extra line in the crate's interaction help is client-only
                // text, but the method is the block's, so it is patched here with
                // the rest rather than split across two Harmony instances.
                if (cfg.CrateSweepEmptyHand)
                {
                    var crateHelp = AccessTools.Method(
                        typeof(Vintagestory.GameContent.BlockCrate),
                        nameof(Vintagestory.GameContent.BlockCrate.GetPlacedBlockInteractionHelp));
                    if (crateHelp != null)
                    {
                        crateHarmony.Patch(crateHelp, postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(CrateInteractionHelpPatch), nameof(CrateInteractionHelpPatch.Postfix))));
                    }
                }
            }
        }

        // The large-container recipes are plain grid-recipe JSON, auto-loaded by the
        // survival mod's RecipeLoader. Rather than race that loader to strip the
        // assets (mod load order isn't guaranteed), wait until every mod's recipes
        // are registered and disable ours. AssetsFinalize runs after all
        // AssetsLoaded passes, so the recipes are guaranteed present here.
        //
        // We set Enabled = false rather than removing them from World.GridRecipes:
        // the crafting matcher (InventoryCraftingGrid.FindMatchingRecipe) searches a
        // precomputed ingredient index, not the GridRecipes list, so list removal
        // wouldn't take effect - but that index holds the same recipe objects and
        // the matcher live-checks `Enabled`, which is exactly the engine's own
        // disable flag (what `enabled: false` in recipe JSON sets). The blocks
        // themselves stay registered, so already-placed large containers in a save
        // keep working - they just can't be crafted while this is off.
        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            var cfg = SeraphsLedgerConfig.Load(api);
            if (cfg.LargeContainers && cfg.AnimalTally) return;

            var recipes = api.World?.GridRecipes;
            if (recipes == null) return;

            foreach (var r in recipes)
            {
                AssetLocation code = r?.Output?.Code;
                if (code == null || code.Domain != "seraphsledger") continue;
                if ((!cfg.LargeContainers && code.Path.StartsWith("large", StringComparison.Ordinal))
                    || (!cfg.AnimalTally && code.Path.StartsWith("tallyboard", StringComparison.Ordinal)))
                {
                    r.Enabled = false;
                }
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            capi = api;
            ClientChannel = api.Network.GetChannel(NetworkChannelName);

            // The settings dialog is always reachable, regardless of which features
            // are on, so the player can turn things back on.
            api.Input.RegisterHotKey("seraphsledgerconfig", "Seraph's Ledger settings", GlKeys.L,
                HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("seraphsledgerconfig", _ => ToggleConfigGui());

            // Show a tally board's marked region without having to click it.
            // Rebindable like any other hotkey, under Controls.
            api.Input.RegisterHotKey("seraphsledgertallyregion", "Show tally board region", GlKeys.X,
                HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("seraphsledgertallyregion", _ => ShowTallyRegion());
            api.ChatCommands.Create("slconfig")
                .WithDescription("Open The Seraph's Ledger settings (toggle features on/off)")
                .HandleWith(_ => { ToggleConfigGui(); return TextCommandResult.Success(); });

            var cfg = SeraphsLedgerConfig.Load(api);

            // Move-all / move-matching. The move-matching binding is a real
            // hotkey so the player can rebind it under Controls > Inventory
            // hotkeys; the click itself is served by a Harmony prefix below,
            // because a click on a slot is consumed by the dialog before the
            // hotkey system ever sees which slot it landed on.
            MoveAll.Init(api);
            MoveAll.MoveAllEnabled = cfg.CtrlClickMoveAll;
            MoveAll.MoveMatchingEnabled = cfg.MoveMatchingItems;
            // Registered when either feature wants it: the crate sweep reuses this
            // one binding, so it has to exist even with move-matching itself off.
            if (cfg.MoveMatchingItems || cfg.CrateSweepHotkey) MoveAll.RegisterHotkey(api);

            // The crate sweep's own client hook. It has to watch Event.MouseDown
            // as well as the hotkey, because out in the world a mouse binding is
            // swallowed by vanilla's "pickblock" before any mod hotkey is
            // reached - see CrateSweep.InitClient.
            CrateSweep.InitClient(api);

            // Each feature is a load-time Harmony patch, so apply only the enabled
            // ones; toggling one off in the settings dialog takes effect on restart.
            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                harmony = new Harmony(HarmonyId);

                // The two bulk moves out of an inventory grid share one prefix
                // (see MoveAll), patched manually rather than via PatchAll so it
                // can be gated independently of the other patches.
                if (cfg.CtrlClickMoveAll || cfg.MoveMatchingItems)
                {
                    var onMouseDown = AccessTools.Method(typeof(GuiElementItemSlotGridBase), "OnMouseDownOnElement");
                    if (onMouseDown != null)
                    {
                        harmony.Patch(onMouseDown, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(OnMouseDownMoveAllPatch), nameof(OnMouseDownMoveAllPatch.Prefix))));
                    }
                }

                // GuiDialogInventory lives in an internal-ish namespace inside
                // VintagestoryLib; resolve it by name so we don't depend on it
                // at compile time.
                var invType = AccessTools.TypeByName("GuiDialogInventory");

                if (cfg.SortTrashButtons)
                {
                    var compose = AccessTools.Method(invType, "ComposeSurvivalInvDialog");
                    if (compose != null)
                    {
                        harmony.Patch(compose, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(SurvivalInvButtonsPatch), nameof(SurvivalInvButtonsPatch.Postfix))));
                    }
                }

                // Sort button in the large storage vessel's dialog.
                if (cfg.LargeVesselSort)
                {
                    VesselSortButtonPatch.ShowTooltip = cfg.LargeVesselSortTooltip;
                    var ctor = AccessTools.Constructor(typeof(GuiDialogBlockEntityInventory), new[]
                    {
                        typeof(string), typeof(InventoryBase), typeof(BlockPos),
                        typeof(int), typeof(int), typeof(ICoreClientAPI)
                    });
                    if (ctor != null)
                    {
                        harmony.Patch(ctor, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(VesselSortButtonPatch), nameof(VesselSortButtonPatch.Postfix))));
                    }
                }

                // Auto-focus the creative search box when the dialog opens so you
                // can type a search term immediately without clicking it first.
                if (cfg.CreativeSearchAutofocus)
                {
                    var onOpened = AccessTools.Method(invType, "OnGuiOpened");
                    if (onOpened != null)
                    {
                        harmony.Patch(onOpened, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(CreativeSearchAutofocusPatch), nameof(CreativeSearchAutofocusPatch.Postfix))));
                    }
                }

                // Silence the chattering voice sounds of the player and traders by
                // skipping EntityTalkUtil's sound emitter. Patched here (rather than
                // overriding the voice oggs with silent assets) so it can be toggled.
                // The 5-arg PlaySound overload is the single point every talk sound
                // funnels through; the 3-arg overload just forwards into it.
                if (cfg.SilencedVoices)
                {
                    var playSound = AccessTools.Method(typeof(EntityTalkUtil), "PlaySound",
                        new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) });
                    if (playSound != null)
                    {
                        harmony.Patch(playSound, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(VoiceSilencePatch), nameof(VoiceSilencePatch.Prefix))));
                    }
                }

                // Distance-challenge: block jumping while the budget is exhausted so
                // the player can't hop out of the walkspeed lock. Patched here (in
                // the singleplayer client process, which also hosts the integrated
                // server physics) so both prediction and authority honour it.
                if (cfg.StepCountdown)
                {
                    var doApply = AccessTools.Method(
                        typeof(Vintagestory.API.Common.Entities.PModuleOnGround), "DoApply");
                    if (doApply != null)
                    {
                        harmony.Patch(doApply, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(StepCountdownJumpBlockPatch), nameof(StepCountdownJumpBlockPatch.Prefix))));
                    }
                }
            }

            // Distance-challenge HUD. The budget itself is tracked server-side; this
            // just renders the value the server syncs onto the player entity.
            if (cfg.StepCountdown)
            {
                stepCountdownHud = new StepCountdownHud(api);
                stepCountdownHud.TryOpen();
            }

        }

        // Asks the server to light up the board the player is looking at. The
        // highlight itself is server-issued, same as right-clicking the board.
        private bool ShowTallyRegion()
        {
            BlockSelection sel = capi?.World?.Player?.CurrentBlockSelection;
            if (sel?.Position == null) return false;

            ClientChannel?.SendPacket(new ShowTallyRegionPacket
            {
                X = sel.Position.X,
                Y = sel.Position.Y,
                Z = sel.Position.Z
            });
            return true;
        }

        private bool ToggleConfigGui()
        {
            if (configDialog == null) configDialog = new GuiDialogFeatureConfig(capi);
            if (configDialog.IsOpened()) configDialog.TryClose();
            else configDialog.TryOpen();
            return true;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            sapi = api;
            // Handlers stay registered regardless of the Sort/Trash toggle (they're
            // dormant without the client-side buttons), keeping the network channel
            // symmetric across both sides.
            api.Network.GetChannel(NetworkChannelName)
                .SetMessageHandler<SortBackpackPacket>(OnSortBackpack)
                .SetMessageHandler<TrashCursorPacket>(OnTrashCursor)
                .SetMessageHandler<SortVesselPacket>(VesselSorter.OnSortPacket)
                .SetMessageHandler<ShowTallyRegionPacket>(OnShowTallyRegion)
                .SetMessageHandler<SweepCratePacket>(CrateSweep.OnSweepPacket);

            var cfg = SeraphsLedgerConfig.Load(api);

            // Corner marking for the tally board's optional counting region.
            if (cfg.AnimalTally)
            {
                api.ChatCommands.Create("tally")
                    .WithDescription("Mark a pen for an Animal Tally Board to count")
                    .RequiresPrivilege(Privilege.chat)
                    .BeginSubCommand("mark")
                        .WithDescription("Mark a corner of the pen (aim at a block). Two opposite corners define it")
                        .HandleWith(OnTallyMark)
                    .EndSubCommand()
                    .BeginSubCommand("clear")
                        .WithDescription("Forget the corners you marked")
                        .HandleWith(OnTallyClear)
                    .EndSubCommand();
            }

            // The large containers' safety net: a copy of their contents in the
            // savegame's mod data, and /sl rescue to put back whatever a
            // mod-less load trimmed off. See ContainerLedger for why it is
            // needed and why nothing is restored automatically.
            if (cfg.ContainerRescue)
            {
                containerLedger = new ContainerLedger(api);
            }

            // Hostiles vanish 2s after death dropping their harvest loot, and/or
            // every other animal is looted after a longer delay and left as a
            // carcass. One system serves both; it does nothing unless at least
            // one of the two is on.
            if (cfg.EnemyFastDespawn || cfg.AutoLootAllCreatures)
            {
                enemyDropDespawn = new EnemyDropDespawn(api, cfg);
            }

            // Distance-challenge: track per-player travel budget and lock movement
            // at 0. Registers the /steps command.
            if (cfg.StepCountdown)
            {
                stepCountdown = new StepCountdown(api, cfg.StepCountdownStartMeters);
            }
        }

        private void OnShowTallyRegion(IServerPlayer player, ShowTallyRegionPacket packet)
        {
            var pos = new BlockPos(packet.X, packet.Y, packet.Z);

            // Don't let a crafted packet light up arbitrary blocks from across
            // the map: it has to be a board, and one the player could reach.
            if (player?.Entity == null) return;
            if (player.Entity.Pos.AsBlockPos.DistanceTo(pos) > 16) return;
            if (sapi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityTallyBoard board)
            {
                board.ShowPen(player);
            }
        }

        private TextCommandResult OnTallyMark(TextCommandCallingArgs args)
        {
            if (!(args.Caller.Player is IServerPlayer player))
            {
                return TextCommandResult.Error("Only a player can mark a pen.");
            }

            // Aiming at nothing marks the block you are standing on, so you can
            // walk the corners of a pen rather than having to find a block to
            // look at.
            BlockPos pos = player.CurrentBlockSelection?.Position
                           ?? player.Entity.Pos.AsBlockPos.DownCopy();

            int corner = TallyMarks.Mark(player.PlayerUID, pos);
            TallyMarks.UpdateHighlight(sapi, player);
            if (corner == 1)
            {
                return TextCommandResult.Success(
                    $"Corner 1 marked at {pos.X}, {pos.Y}, {pos.Z}. Aim at the opposite corner and /tally mark again.");
            }

            if (!TallyMarks.TryGetBox(args.Caller.Entity.World, player.PlayerUID,
                                      out BlockPos min, out BlockPos max, out string error))
            {
                return TextCommandResult.Error(error);
            }

            string size = $"{max.X - min.X + 1} x {max.Z - min.Z + 1}, Y {min.Y}-{max.Y}";
            return TextCommandResult.Success(
                $"Corner 2 marked. Region {size}. Now sneak + right-click an Animal Tally Board to bind it.");
        }

        private TextCommandResult OnTallyClear(TextCommandCallingArgs args)
        {
            if (!(args.Caller.Player is IServerPlayer player))
            {
                return TextCommandResult.Error("Only a player can mark a pen.");
            }
            TallyMarks.Clear(player.PlayerUID);
            TallyMarks.UpdateHighlight(sapi, player);
            return TextCommandResult.Success("Corners forgotten. Boards already bound keep their region.");
        }

        private void OnSortBackpack(IServerPlayer player, SortBackpackPacket packet)
        {
            var inv = player?.InventoryManager?.GetOwnInventory(GlobalConstants.backpackInvClassName) as InventoryBase;
            if (inv == null) return;
            BackpackSorter.Sort(inv, player.Entity.World);
        }

        private void OnTrashCursor(IServerPlayer player, TrashCursorPacket packet)
        {
            var mouseInv = player?.InventoryManager?.GetOwnInventory(GlobalConstants.mousecursorInvClassName) as InventoryBase;
            if (mouseInv == null) return;

            for (int i = 0; i < mouseInv.Count; i++)
            {
                ItemSlot slot = mouseInv[i];
                if (slot == null || slot.Empty) continue;
                slot.Itemstack = null;
                mouseInv.MarkSlotDirty(i);
            }
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            iceCellarHarmony?.UnpatchAll(IceCellarHarmonyId);
            iceCellarHarmony = null;
            barrelHarmony?.UnpatchAll(BarrelHarmonyId);
            barrelHarmony = null;
            crateHarmony?.UnpatchAll(CrateHarmonyId);
            crateHarmony = null;
            ClientChannel = null;
            MoveAll.Shutdown();
            CrateSweep.Shutdown();
            configDialog = null;
            containerLedger?.Dispose();
            containerLedger = null;
            enemyDropDespawn?.Dispose();
            enemyDropDespawn = null;
            stepCountdown?.Dispose();
            stepCountdown = null;
            stepCountdownHud?.TryClose();
            stepCountdownHud?.Dispose();
            stepCountdownHud = null;
            capi = null;
            sapi = null;
        }
    }

    // Server-authoritative sort of the player's backpack (bag) contents.
    //
    // The first 4 slots of the backpack inventory are the worn-bag slots; only
    // those are sent over the network (CountForNetworkPacket == 4). The actual
    // storage slots (ItemSlotBagContent, slot id >= 4) are virtual views whose
    // contents live inside the bag itemstacks' attributes. So we rearrange the
    // content slots, persist each back into its bag via OnItemSlotModified, then
    // mark the 4 bag slots dirty so the updated bag stacks are re-sent and the
    // client rebuilds its content slots from them.
    public static class BackpackSorter
    {
        public static void Sort(InventoryBase inv, IWorldAccessor world)
        {
            // Collect the content slots, grouped by their accepted storage type.
            // Sorting within a group is always valid because every item already
            // sitting in those slots is, by definition, accepted there - so no
            // item can ever get stranded or lost.
            var groups = new Dictionary<int, List<ItemSlot>>();
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i] is ItemSlotBagContent content)
                {
                    int key = (int)content.StorageType;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<ItemSlot>();
                        groups[key] = list;
                    }
                    list.Add(content);
                }
            }
            if (groups.Count == 0) return;

            foreach (var group in groups.Values)
            {
                SortGroup(group, world);
            }

            // Persist every content slot back into its bag itemstack.
            for (int i = 4; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                if (slot != null) inv.OnItemSlotModified(slot);
            }

            // Re-send the worn-bag stacks (now carrying sorted contents) to the client.
            int bagSlots = Math.Min(4, inv.Count);
            for (int i = 0; i < bagSlots; i++) inv.MarkSlotDirty(i);
        }

        private static void SortGroup(List<ItemSlot> slots, IWorldAccessor world)
        {
            // Pull out and clear all stacks in this group.
            var stacks = new List<ItemStack>();
            foreach (ItemSlot slot in slots)
            {
                if (!slot.Empty) stacks.Add(slot.Itemstack);
                slot.Itemstack = null;
            }
            if (stacks.Count == 0) return;

            // Consolidate partial stacks of identical items.
            var merged = new List<ItemStack>();
            foreach (ItemStack stack in stacks)
            {
                ItemStack remaining = stack;
                foreach (ItemStack target in merged)
                {
                    if (remaining.StackSize <= 0) break;
                    if (target.Collectible == null) continue;
                    int max = target.Collectible.MaxStackSize;
                    if (target.StackSize >= max) continue;
                    if (!target.Equals(world, remaining)) continue;

                    int moved = Math.Min(max - target.StackSize, remaining.StackSize);
                    target.StackSize += moved;
                    remaining.StackSize -= moved;
                }
                if (remaining.StackSize > 0) merged.Add(remaining);
            }

            // Sort by item code, then by larger stacks first.
            merged.Sort((a, b) =>
            {
                int byCode = string.CompareOrdinal(
                    a.Collectible?.Code?.ToString() ?? "",
                    b.Collectible?.Code?.ToString() ?? "");
                return byCode != 0 ? byCode : b.StackSize - a.StackSize;
            });

            // Drop them back into the group's slots in order.
            for (int i = 0; i < merged.Count && i < slots.Count; i++)
            {
                slots[i].Itemstack = merged[i];
            }
        }
    }

    // Adds "Sort" and "Trash" buttons to the survival player-inventory dialog.
    //
    // The buttons are appended after the engine has already composed the dialog.
    // AddSmallButton is a no-op once a composer is Composed, so we flip the public
    // Composed flag off, add our buttons, and recompose. The dialog's outer bounds
    // are fixed (forked from the 7-row backpack grid) and don't grow from the
    // extra children, so the buttons sit in the empty space under the crafting
    // output without resizing or shifting anything.
    public static class SurvivalInvButtonsPatch
    {
        public static void Postfix(object __instance)
        {
            var field = AccessTools.Field(__instance.GetType(), "survivalInvDialog");
            var composer = field?.GetValue(__instance) as GuiComposer;
            if (composer == null) return;

            // Already added to this composer (defensive against repeat calls).
            if (composer.GetButton("seraphsledgerSortBtn") != null) return;

            ElementBounds craft = composer.GetSlotGrid("craftinggrid")?.Bounds;
            ElementBounds output = composer.GetSlotGrid("outputslot")?.Bounds;
            if (craft == null || output == null) return;

            // Make sure our trash-can icon is registered (lazily, the first time
            // the inventory is composed - assets are loaded long before then).
            var icons = composer.Api.Gui.Icons;
            if (!icons.CustomIcons.ContainsKey(TrashIcon))
            {
                icons.CustomIcons[TrashIcon] =
                    icons.SvgIconSource(new AssetLocation("seraphsledger:textures/icons/trash.svg"));
            }

            double x = craft.fixedX;
            double width = craft.fixedWidth;
            double y = output.fixedY + output.fixedHeight + 14.0;

            const double sortHeight = 26.0;
            const double trashSize = 38.0;
            const double gap = 6.0;

            ElementBounds sortBounds = ElementBounds.Fixed(x, y, width, sortHeight);
            ElementBounds trashBounds = ElementBounds.Fixed(
                x + (width - trashSize) / 2.0, y + sortHeight + gap, trashSize, trashSize);

            composer.Composed = false;
            composer
                .AddSmallButton("Sort", OnSortClicked, sortBounds, EnumButtonStyle.Normal, "seraphsledgerSortBtn")
                .AddIconButton(TrashIcon, OnTrashClicked, trashBounds, "seraphsledgerTrashBtn")
                .Compose(focusFirstElement: false);
        }

        private const string TrashIcon = "seraphsledgertrash";

        private static bool OnSortClicked()
        {
            SeraphsLedgerModSystem.ClientChannel?.SendPacket(new SortBackpackPacket());
            return true;
        }

        private static void OnTrashClicked(bool on)
        {
            SeraphsLedgerModSystem.ClientChannel?.SendPacket(new TrashCursorPacket());
        }
    }

    // Focuses the creative inventory's search box the moment the dialog opens, so
    // typing goes straight into the filter instead of requiring a click first.
    //
    // This mirrors what the built-in Ctrl+F "creativesearch" hotkey does
    // (FocusElement on the searchbox's TabIndex). OnGuiOpened recomposes the
    // dialog before this postfix runs, so the searchbox element already exists.
    // Side effect (inherent to a focused text field): the E key now types into
    // the search instead of closing the dialog - use Escape or the X to close.
    public static class CreativeSearchAutofocusPatch
    {
        public static void Postfix(object __instance)
        {
            var type = __instance.GetType();

            var capi = AccessTools.Field(type, "capi")?.GetValue(__instance) as ICoreClientAPI;
            if (capi?.World?.Player?.WorldData?.CurrentGameMode != EnumGameMode.Creative) return;

            var composerField = AccessTools.Field(type, "creativeInvDialog");

            // Defer focus to the next frame. Focusing during OnGuiOpened doesn't
            // stick: TryOpen calls TriggerDialogOpened right after this, which
            // resets element focus. The built-in Ctrl+F handler works precisely
            // because it focuses *after* TryOpen has fully returned - so we match
            // that timing with a one-shot 0ms callback.
            capi.Event.RegisterCallback(dt =>
            {
                if (capi.World?.Player?.WorldData?.CurrentGameMode != EnumGameMode.Creative) return;
                var composer = composerField?.GetValue(__instance) as GuiComposer;
                var searchbox = composer?.GetTextInput("searchbox");
                if (searchbox == null) return;
                composer.FocusElement(searchbox.TabIndex);
            }, 0);
        }
    }

    // Silences entity voice chatter by short-circuiting EntityTalkUtil's sound
    // emitter. Returning false skips the original method entirely, so no voice
    // sound is ever loaded or started; talk scheduling (letters/chord delays) is
    // unaffected, it just plays into silence.
    public static class VoiceSilencePatch
    {
        public static bool Prefix() => false;
    }
}
