using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SeraphsLedger
{
    // Distance-budget "challenge run" mode.
    //
    // Each player has a remaining-travel budget (in blocks/metres) that drains as
    // they walk or run on the ground. When it hits 0 they can no longer walk; add
    // more to the budget and they move again. Built for singleplayer but tracked
    // per player, so it behaves on a small server too.
    //
    // Server-authoritative by design: the budget lives in the player entity's
    // WatchedAttributes, which both persist across relogs and auto-sync to the
    // client (so the HUD just reads them - no custom packets). Immobilization is
    // done through the engine's "walkspeed" entity stat under our own key, which
    // GetWalkSpeedMultiplier clamps to [0, 999]. Setting it hugely negative pins
    // the total to 0 no matter what movement buffs other mods (e.g. XSkills) have
    // stacked on, and the clamp means the player is never flung backwards.
    public class StepCountdown
    {
        // WatchedAttribute key holding the player's remaining distance (double).
        public const string AttrKey = "seraphsledger:distanceRemaining";

        // Our private key inside the "walkspeed" stat category.
        private const string WalkStatCategory = "walkspeed";
        private const string WalkStatKey = "seraphsledger:rooted";

        // Position deltas larger than this in a single sample are treated as a
        // teleport / respawn / waypoint jump and don't drain the budget.
        private const double TeleportThreshold = 8.0;

        private const int SampleIntervalMs = 100;

        private readonly ICoreServerAPI sapi;
        private readonly double startMeters;

        // Last sampled horizontal position per player UID.
        private readonly Dictionary<string, Vec3d> lastPos = new Dictionary<string, Vec3d>();
        // Players we've currently rooted, so we only touch the stat on transitions.
        private readonly HashSet<string> rooted = new HashSet<string>();

        private long tickListenerId;

        public StepCountdown(ICoreServerAPI sapi, double startMeters)
        {
            this.sapi = sapi;
            this.startMeters = startMeters;

            tickListenerId = sapi.Event.RegisterGameTickListener(OnTick, SampleIntervalMs);
            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            RegisterCommands();
        }

        public void Dispose()
        {
            sapi.Event.UnregisterGameTickListener(tickListenerId);
            sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        }

        // Seed a starting budget the first time we ever see a player, so a fresh
        // challenge doesn't begin with the player instantly frozen.
        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            Entity e = player?.Entity;
            if (e == null) return;
            if (!e.WatchedAttributes.HasAttribute(AttrKey))
            {
                SetRemaining(player, startMeters);
            }
            lastPos[player.PlayerUID] = e.Pos.XYZ;
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            if (player == null) return;
            lastPos.Remove(player.PlayerUID);
            rooted.Remove(player.PlayerUID);
        }

        private void OnTick(float dt)
        {
            foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
            {
                EntityPlayer ep = player?.Entity;
                if (ep == null || !ep.Alive) continue;

                string uid = player.PlayerUID;
                Vec3d now = ep.Pos.XYZ;

                if (lastPos.TryGetValue(uid, out Vec3d prev))
                {
                    // Walking/running only: must be on the ground and not swimming.
                    // Jumping (airborne), climbing, swimming and falling don't count.
                    if (ep.OnGround && !ep.Swimming)
                    {
                        double dx = now.X - prev.X;
                        double dz = now.Z - prev.Z;
                        double moved = System.Math.Sqrt(dx * dx + dz * dz);
                        if (moved > 0.0001 && moved < TeleportThreshold)
                        {
                            double remaining = GetRemaining(ep);
                            if (remaining > 0)
                            {
                                SetRemaining(player, remaining - moved);
                            }
                        }
                    }
                }
                lastPos[uid] = now;

                UpdateRooted(player);
            }
        }

        // Applies or clears the movement lock based on the player's budget, only
        // touching the stat when the rooted state actually changes.
        private void UpdateRooted(IServerPlayer player)
        {
            EntityPlayer ep = player.Entity;
            if (ep == null) return;

            bool shouldRoot = GetRemaining(ep) <= 0;
            bool isRooted = rooted.Contains(player.PlayerUID);

            if (shouldRoot && !isRooted)
            {
                ep.Stats.Set(WalkStatCategory, WalkStatKey, -999f, false);
                rooted.Add(player.PlayerUID);
            }
            else if (!shouldRoot && isRooted)
            {
                ep.Stats.Remove(WalkStatCategory, WalkStatKey);
                rooted.Remove(player.PlayerUID);
            }
        }

        private static double GetRemaining(Entity e)
        {
            return e.WatchedAttributes.GetDouble(AttrKey, 0);
        }

        private void SetRemaining(IServerPlayer player, double value)
        {
            if (value < 0) value = 0;
            Entity e = player.Entity;
            e.WatchedAttributes.SetDouble(AttrKey, value);
            e.WatchedAttributes.MarkPathDirty(AttrKey);
        }

        private void RegisterCommands()
        {
            var parsers = sapi.ChatCommands.Parsers;

            sapi.ChatCommands.Create("steps")
                .WithDescription("Distance-challenge travel budget: show, add to, or set your remaining blocks.")
                .RequiresPrivilege(Privilege.controlserver)
                .RequiresPlayer()
                .HandleWith(OnShow)
                .BeginSubCommand("add")
                    .WithDescription("Add blocks to your remaining budget (use a negative number to subtract).")
                    .WithArgs(parsers.Double("blocks"))
                    .HandleWith(OnAdd)
                .EndSubCommand()
                .BeginSubCommand("set")
                    .WithDescription("Set your remaining budget to an exact number of blocks.")
                    .WithArgs(parsers.Double("blocks"))
                    .HandleWith(OnSet)
                .EndSubCommand();
        }

        private TextCommandResult OnShow(TextCommandCallingArgs args)
        {
            double remaining = GetRemaining(((IServerPlayer)args.Caller.Player).Entity);
            return TextCommandResult.Success($"Blocks remaining: {(int)remaining}");
        }

        private TextCommandResult OnAdd(TextCommandCallingArgs args)
        {
            var player = (IServerPlayer)args.Caller.Player;
            double delta = (double)args[0];
            double now = GetRemaining(player.Entity) + delta;
            SetRemaining(player, now);
            UpdateRooted(player);
            return TextCommandResult.Success($"Added {delta:0.#} blocks. Remaining: {(int)GetRemaining(player.Entity)}");
        }

        private TextCommandResult OnSet(TextCommandCallingArgs args)
        {
            var player = (IServerPlayer)args.Caller.Player;
            SetRemaining(player, (double)args[0]);
            UpdateRooted(player);
            return TextCommandResult.Success($"Blocks remaining set to {(int)GetRemaining(player.Entity)}");
        }
    }

    // Stops a player whose budget is exhausted from jumping. The walkspeed stat
    // only zeroes ground walking; a jump would lob them into the air where the
    // physics no longer scales movement by walkspeed, letting them hop-travel. We
    // prefix the ground physics module - the one that reads controls.Jump to fire
    // the jump impulse - and clear the flag right before it's checked, so the jump
    // never starts and the player can't leave the ground at all.
    //
    // Runs every physics tick for every entity, so it bails out as cheaply as
    // possible: only players, only when actually trying to jump, and only then
    // reads the (both-sides-synced) remaining-distance attribute.
    public static class StepCountdownJumpBlockPatch
    {
        public static void Prefix(Entity entity, EntityControls controls)
        {
            if (controls == null || !controls.Jump) return;
            if (!(entity is EntityPlayer)) return;
            if (entity.WatchedAttributes.GetDouble(StepCountdown.AttrKey, 0) <= 0)
            {
                controls.Jump = false;
            }
        }
    }
}
