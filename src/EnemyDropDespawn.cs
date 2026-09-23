using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Auto-loot on death, in two flavours.
    //
    // Hostiles (EnemyFastDespawn): a creature whose spawn group is "hostile" -
    // drifters, locusts, bells, shivers, bowtorn, wolves, bears, hyenas, plus
    // any modded mob using the same group - has its body removed 2 seconds
    // after death. Their bodies are litter, so nothing is left behind.
    //
    // Everything else (AutoLootAllCreatures): deer, sheep, pigs, hares and the
    // rest are looted after a longer, configurable delay and the body is then
    // handed to the game's own EntityBehaviorDeadDecay, which puts down the
    // carcass block the animal decays into. You get the loot without the
    // butchering, and the carcass still appears where the animal fell. Clear
    // AutoLootLeaveCarcass and they are removed like hostiles instead, which is
    // what you want before culling a pen - see the config for why.
    //
    // The catch both paths share: vanilla mobs carry their loot in an
    // EntityBehaviorHarvestable, not in their top-level `drops` (which is
    // empty). That loot is normally only obtained by knife-harvesting the
    // corpse, so disposing of the body would silently destroy it. We generate
    // the harvest drops and spit them onto the ground first, mirroring what a
    // full harvest would have given.
    public class EnemyDropDespawn
    {
        private const int HostileDespawnDelayMs = 2000;
        private const int MinDelayMs = 1000;
        private const int MaxDelayMs = 300000;

        private readonly ICoreServerAPI sapi;
        private readonly bool fastDespawnHostiles;
        private readonly bool autoLootEverythingElse;
        private readonly bool leaveCarcass;
        private readonly int autoLootDelayMs;
        private HashSet<string> hostileFamilies;

        public EnemyDropDespawn(ICoreServerAPI sapi, SeraphsLedgerConfig cfg)
        {
            this.sapi = sapi;
            fastDespawnHostiles = cfg.EnemyFastDespawn;
            autoLootEverythingElse = cfg.AutoLootAllCreatures;
            leaveCarcass = cfg.AutoLootLeaveCarcass;
            autoLootDelayMs = GameMath.Clamp(cfg.AutoLootDelaySeconds * 1000, MinDelayMs, MaxDelayMs);

            sapi.Event.OnEntityDeath += OnEntityDeath;
        }

        // Cubs and pups are spawned by the multiply behavior, never by world or
        // runtime spawning, so their entity types carry no spawnConditions at
        // all and the group test below finds nothing to read. Collect the
        // hostile creature groups once - "wolf", "bear", "hyena", "drifter" -
        // so a wolf pup is recognised by its family rather than by a spawn rule
        // it was never given.
        private HashSet<string> HostileFamilies()
        {
            var families = new HashSet<string>(StringComparer.Ordinal);
            foreach (EntityProperties type in sapi.World.EntityTypes)
            {
                var sc = type?.Server?.SpawnConditions;
                if (sc == null) continue;
                if (sc.Runtime?.Group == "hostile" || sc.Worldgen?.Group == "hostile")
                {
                    families.Add(type.Code.Domain + ":" + type.Code.FirstCodePart());
                }
            }
            return families;
        }

        public void Dispose()
        {
            sapi.Event.OnEntityDeath -= OnEntityDeath;
        }

        private void OnEntityDeath(Entity entity, DamageSource damageSource)
        {
            if (entity == null || entity is EntityPlayer) return;

            // A hostile handled by the fast-despawn path keeps the old behavior;
            // anything else is only touched when auto-loot-everything is on.
            bool hostile = fastDespawnHostiles && IsHostile(entity);
            if (!hostile && !autoLootEverythingElse) return;

            int delayMs = hostile ? HostileDespawnDelayMs : autoLootDelayMs;

            // A hostile's body is always litter. An animal's is only removed
            // when the player has asked for no carcasses.
            bool removeBody = hostile || !leaveCarcass;

            // GenerateDrops dereferences byPlayer.Entity for non-mechanical mobs
            // (to scale loot by the looter's "animalLootDropRate" stat), so a null
            // player would NPE there. Capture whoever landed the kill; null is only
            // safe to pass for mechanical mobs (drifters/locusts/bells).
            IPlayer byPlayer = (damageSource?.GetCauseEntity() as EntityPlayer)?.Player;

            long entityId = entity.EntityId;
            sapi.Event.RegisterCallback(dt =>
            {
                // The entity may already be gone (player harvested it and it
                // decayed, chunk unloaded, etc.) - look it up fresh rather than
                // capturing the reference.
                Entity e = sapi.World.GetEntityById(entityId);
                if (e == null) return;

                // Someone has the carcass window open: they are butchering it by
                // hand right now. Emptying the inventory out from under an open
                // GUI is how you get items that appear to come back, so leave
                // this one alone entirely and let vanilla finish the job.
                if (IsBeingLootedByHand(e)) return;

                // Only dispose of the body once its loot is safely out. If we
                // could not roll or hand over the drops, the corpse stays and
                // can be knife-harvested the ordinary way - deleting it would
                // destroy the loot outright.
                if (!DropLoot(e, byPlayer)) return;

                if (removeBody)
                {
                    sapi.World.DespawnEntity(e, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    return;
                }

                LeaveCarcass(e);
            }, delayMs);
        }

        // Turns the emptied body into the carcass block it would have decayed
        // into, the same way vanilla does when you close an empty carcass
        // window. DecayNow also flags the entity for despawn, so the body goes
        // with it.
        private void LeaveCarcass(Entity entity)
        {
            var decay = entity.GetBehavior<EntityBehaviorDeadDecay>();
            if (decay != null)
            {
                decay.DecayNow();
                return;
            }

            // Nothing to decay into (mechanical mobs and some modded creatures
            // have no deaddecay behavior). Its loot is already on the ground, so
            // an empty body left standing is just litter.
            sapi.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
        }

        private bool IsBeingLootedByHand(Entity entity)
        {
            var hb = entity.GetBehavior<EntityBehaviorHarvestable>();
            var inv = hb?.Inventory;
            if (inv == null) return false;

            foreach (IPlayer plr in sapi.World.AllOnlinePlayers)
            {
                if (plr != null && inv.HasOpened(plr)) return true;
            }
            return false;
        }

        // Spits the corpse's harvest loot onto the ground. Returns true when the
        // body is safe to dispose of: either everything it carried is now lying
        // on the ground, or it never had a harvest inventory to begin with. A
        // false means the loot is still inside the corpse and the corpse must
        // survive.
        private bool DropLoot(Entity entity, IPlayer byPlayer)
        {
            var hb = entity.GetBehavior<EntityBehaviorHarvestable>();
            // Nothing is stashed in a harvest inventory, so nothing is lost by
            // removing the body; its top-level `drops` were already handled on death.
            if (hb == null) return true;

            var attr = entity.Properties?.Attributes;
            bool mechanical = attr != null && attr["isMechanical"].AsBool(false);

            // SetHarvested -> GenerateDrops fills the behavior's inventory, guarded
            // on the "harvested"/"dropsgenerated" flags. Once the drops exist there
            // is nothing left to roll, and whatever the player already took by knife
            // is simply gone from the inventory - both fine to hand over below.
            if (!hb.DropsGenerated)
            {
                // GenerateDrops dereferences byPlayer.Entity for non-mechanical mobs,
                // so with no killer to scale the loot by we cannot generate it at all.
                // Leave the corpse standing to be harvested by hand instead - this is
                // the common case for an animal that fell, drowned, starved or lost a
                // fight with a predator.
                if (byPlayer == null && !mechanical) return false;

                try
                {
                    hb.SetHarvested(byPlayer);
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("[seraphsledger] harvest-drop failed for {0}: {1}", entity.Code, ex);
                    return false;
                }

                // A silent failure leaves an empty inventory that looks just like a
                // fully looted one; don't dispose of the body on the strength of it.
                if (!hb.DropsGenerated) return false;
            }

            var inv = hb.Inventory;
            if (inv == null) return false;

            Vec3d pos = entity.Pos.XYZ.Add(0.0, 0.25, 0.0);
            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                if (slot == null || slot.Empty) continue;
                sapi.World.SpawnItemEntity(slot.TakeOutWhole(), pos);
                inv.MarkSlotDirty(i);
            }
            return true;
        }

        // "Enemy" == any creature whose spawn group is "hostile". This is how the
        // vanilla survival entities flag drifters/locusts/bells/shivers/bowtorn and
        // the hostile animals (wolf/bear/hyena), and modded mobs follow the same
        // convention.
        private bool IsHostile(Entity entity)
        {
            var sc = entity.Properties?.Server?.SpawnConditions;
            if (sc != null && (sc.Runtime?.Group == "hostile" || sc.Worldgen?.Group == "hostile"))
            {
                return true;
            }

            // No spawn rule of its own: fall back to the family. Built lazily
            // because entity types are not all registered when this class is
            // constructed.
            hostileFamilies ??= HostileFamilies();
            AssetLocation code = entity.Code;
            return code != null
                   && hostileFamilies.Contains(code.Domain + ":" + code.FirstCodePart());
        }
    }
}
