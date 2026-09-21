using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Fast-despawn + auto-loot for hostile creatures.
    //
    // When a creature whose spawn group is "hostile" dies (drifters, locusts,
    // bells, shivers, bowtorn, wolves, bears, hyenas - plus any modded mob using
    // the same spawn group), its body is removed 2 seconds later.
    //
    // The catch: vanilla hostile mobs carry their loot in an
    // EntityBehaviorHarvestable, not in their top-level `drops` (which is empty).
    // That loot is normally only obtained by knife-harvesting the corpse, so
    // despawning the body would silently destroy it. To make the kill self-
    // looting, we generate the harvest drops and spit them onto the ground right
    // before the body disappears - mirroring what a full harvest would have given.
    public class EnemyDropDespawn
    {
        private const int DespawnDelayMs = 2000;
        private readonly ICoreServerAPI sapi;

        public EnemyDropDespawn(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            sapi.Event.OnEntityDeath += OnEntityDeath;
        }

        public void Dispose()
        {
            sapi.Event.OnEntityDeath -= OnEntityDeath;
        }

        private void OnEntityDeath(Entity entity, DamageSource damageSource)
        {
            if (!IsHostile(entity)) return;

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

                // Only remove the body once its loot is safely out. If we could
                // not roll or hand over the drops, the corpse stays and can be
                // knife-harvested the ordinary way - deleting it would destroy
                // the loot outright.
                if (!DropLoot(e, byPlayer)) return;

                sapi.World.DespawnEntity(e, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }, DespawnDelayMs);
        }

        // Spits the corpse's harvest loot onto the ground. Returns true when the
        // body is safe to remove: either everything it carried is now lying on
        // the ground, or it never had a harvest inventory to begin with. A false
        // means the loot is still inside the corpse and the corpse must survive.
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
                // the common case for a wolf that fell, drowned or lost a fight.
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
                // fully looted one; don't delete the body on the strength of it.
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
        // convention. Players are never hostile-group, but guard anyway.
        private static bool IsHostile(Entity entity)
        {
            if (entity == null || entity is EntityPlayer) return false;
            var sc = entity.Properties?.Server?.SpawnConditions;
            if (sc == null) return false;
            return sc.Runtime?.Group == "hostile" || sc.Worldgen?.Group == "hostile";
        }
    }
}
