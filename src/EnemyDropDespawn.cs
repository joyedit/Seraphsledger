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

                DropLoot(e, byPlayer);
                sapi.World.DespawnEntity(e, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }, DespawnDelayMs);
        }

        private void DropLoot(Entity entity, IPlayer byPlayer)
        {
            var hb = entity.GetBehavior<EntityBehaviorHarvestable>();
            if (hb == null) return;

            var attr = entity.Properties?.Attributes;
            bool mechanical = attr != null && attr["isMechanical"].AsBool(false);

            // SetHarvested -> GenerateDrops fills the behavior's inventory. It is
            // idempotent (guards on the "harvested"/"dropsgenerated" flags), so if
            // the player already knife-harvested the corpse this is a no-op and the
            // inventory is already empty. Skip non-mechanical mobs with no killer
            // player, since GenerateDrops would NPE on the missing looter stats.
            if (byPlayer != null || mechanical)
            {
                try
                {
                    hb.SetHarvested(byPlayer);
                }
                catch (Exception ex)
                {
                    sapi.Logger.Warning("[seraphsledger] harvest-drop failed for {0}: {1}", entity.Code, ex);
                    return;
                }
            }

            var inv = hb.Inventory;
            if (inv == null) return;

            Vec3d pos = entity.Pos.XYZ.Add(0.0, 0.25, 0.0);
            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                if (slot == null || slot.Empty) continue;
                sapi.World.SpawnItemEntity(slot.TakeOutWhole(), pos);
                inv.MarkSlotDirty(i);
            }
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
