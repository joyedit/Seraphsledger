using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphsLedger
{
    // Bigger fuel stacks, so a firepit can be loaded for longer.
    //
    // A firepit holds no fixed amount of fuel: its fuel slot is a plain
    // ItemSlot with the default MaxSlotStackSize of 999999, and it burns one
    // item out of that slot at a time (BlockEntityFirepit sets fuelBurnTime
    // from the item's combustibleProps.BurnDuration). What caps a load is the
    // fuel itself - every merge into a slot is clamped to the collectible's
    // MaxStackSize by CollectibleObject.GetMergableQuantity, and no slot can
    // hold more than that. So "more fuel per firepit" is really "a bigger fuel
    // stack", and it is set on the item.
    //
    // Vanilla is inconsistent about it: charcoal, coal and coke stack to 64
    // while firewood, aged firewood and peat bricks stop at 32, so the
    // wood-and-peat firepit needs refilling twice as often as the charcoal
    // one. Levelling them at 64 doubles a wood or peat load.
    //
    // Which codes count as fuel is config (FuelStackCodes), matched on the
    // first part of the code so every variant and every domain follows - a
    // modded firewood is raised with the vanilla one. Deliberately a list
    // rather than "everything with a burn temperature": 197 vanilla assets
    // have one, and they include beds, barrels, baskets, armour stands,
    // candles, mannequins, waterwheels, windmill rotors and every wooden gear.
    // Those stack to 1, 4 or 16 by design, and quietly turning them into
    // 64-stacks would be a far bigger change to the game than fuel.
    //
    // Blocks and items both: peat bricks are a block (vanilla's own comment
    // says there is no good reason for that), charcoal is an item, so this
    // walks World.Collectibles rather than either list alone.
    //
    // Nothing is lowered. A fuel already at or above the configured size is
    // left exactly as it is, so the default of 64 moves firewood, aged
    // firewood and peat bricks and leaves charcoal, coal and coke at vanilla.
    //
    // Applied in AssetsFinalize, after every mod's definitions are registered,
    // and on both sides. In multiplayer the server's value is the one that
    // counts whatever a client's own config says: the server sends its block
    // and item definitions on connect (BlockTypeNet/ItemTypeNet carry
    // MaxStackSize, and VintagestoryLib assigns it straight onto the client's
    // block/item), which lands after the client's own AssetsFinalize has run.
    public static class FuelStacks
    {
        public const int MinSize = 32;
        public const int MaxSize = 999;

        public static void Apply(ICoreAPI api, SeraphsLedgerConfig cfg)
        {
            if (!cfg.BiggerFuelStacks) return;

            string[] configured = cfg.FuelStackCodes ?? Array.Empty<string>();
            if (configured.Length == 0) return;

            int size = GameMath.Clamp(cfg.FuelStackSize, MinSize, MaxSize);
            var codes = new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);
            var raised = new List<string>();
            int variants = 0;

            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                AssetLocation code = obj?.Code;
                if (code == null) continue;
                if (!codes.Contains(code.FirstCodePart())) continue;

                // A fuel that cannot burn is a name collision with some other
                // mod's item, not a fuel - leave it alone.
                if ((obj.CombustibleProps?.BurnTemperature ?? 0) <= 0) continue;
                if (obj.MaxStackSize >= size) continue;

                if (variants++ < 12) raised.Add(code.ToShortString() + " " + obj.MaxStackSize);
                obj.MaxStackSize = size;
            }

            if (variants == 0) return;
            api.Logger.Notification("[seraphsledger] fuel stacks raised to {0} ({1} collectible{2}): {3}{4}",
                size, variants, variants == 1 ? "" : "s", string.Join(", ", raised),
                variants > raised.Count ? ", ..." : "");
        }
    }
}
