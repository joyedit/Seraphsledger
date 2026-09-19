using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Sent client -> server when the player clicks "Sort" in a large storage
    // vessel's dialog. Carries the vessel's position; the server re-checks that
    // the block really is a large vessel and that this player has it open.
    [ProtoContract]
    public class SortVesselPacket
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
    }

    // Server-authoritative sort of a large storage vessel, laid out as a food
    // store: crocks first so the ones to eat next sit at the top of the grid.
    //
    //   1. unsealed crocks with food in them - these spoil at the normal rate,
    //      so they come first
    //   2. sealed crocks with food in them
    //   3. empty crocks
    //   4. everything else, by item code
    //
    // Within 1 and 2, the crock that goes off soonest comes first - ordered by
    // the same "fresh for ..." figure its tooltip shows, so the grid reads in
    // the order the player would eat it. (An earlier version ordered by when the
    // food was made; that disagreed with the tooltip, since foods keep for
    // different lengths and a meal's seal slows it more than raw food's.)
    public static class VesselSorter
    {
        public static readonly AssetLocation VesselCode = new AssetLocation("seraphsledger", "largestoragevessel");

        public static void Sort(InventoryBase inv, IWorldAccessor world)
        {
            // Every slot in the vessel has the same storage type, so any stack
            // already in it is accepted by every other slot - reordering can't
            // strand anything.
            var stacks = new List<ItemStack>();
            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                if (slot == null || slot.Empty) continue;
                stacks.Add(slot.Itemstack);
                slot.Itemstack = null;
            }
            if (stacks.Count == 0) return;

            // Consolidate partial stacks. Going through TryPutInto rather than
            // adding StackSize directly lets the collectible merge them its own
            // way - perishables average their freshness, and stacks that differ
            // (two crocks of different meals) simply refuse.
            var merged = new List<ItemStack>();
            foreach (ItemStack stack in stacks)
            {
                var source = new DummySlot(stack);
                foreach (ItemStack target in merged)
                {
                    if (source.Empty) break;
                    source.TryPutInto(world, new DummySlot(target), source.StackSize);
                }
                if (!source.Empty) merged.Add(source.Itemstack);
            }

            var keyed = new List<(ItemStack stack, int rank, double fresh, string code)>(merged.Count);
            foreach (ItemStack stack in merged)
            {
                int rank = Rank(stack, inv, world, out double fresh);
                keyed.Add((stack, rank, fresh, stack.Collectible?.Code?.ToString() ?? ""));
            }

            keyed.Sort((a, b) =>
            {
                if (a.rank != b.rank) return a.rank - b.rank;
                int byFresh = a.fresh.CompareTo(b.fresh);
                if (byFresh != 0) return byFresh;
                int byCode = string.CompareOrdinal(a.code, b.code);
                return byCode != 0 ? byCode : b.stack.StackSize - a.stack.StackSize;
            });

            for (int i = 0; i < inv.Count; i++)
            {
                inv[i].Itemstack = i < keyed.Count ? keyed[i].stack : null;
                inv.MarkSlotDirty(i);
            }
        }

        // 0 unsealed crock with food, 1 sealed crock with food, 2 empty crock,
        // 3 anything else. freshness is only meaningful for 0 and 1: the hours
        // the contents stay fresh at this vessel's rate, i.e. the number the
        // crock's tooltip shows as "fresh for ...". It is MaxValue (sorts last
        // in its group) for contents that don't perish.
        private static int Rank(ItemStack stack, InventoryBase vessel, IWorldAccessor world, out double freshness)
        {
            freshness = double.MaxValue;
            if (!(stack.Collectible is BlockCrock crock)) return 3;

            ItemStack[] contents = crock.GetNonEmptyContents(world, stack);
            if (contents == null || contents.Length == 0) return 2;

            freshness = FreshHoursShown(crock, stack, contents, vessel, world);
            return stack.Attributes.GetBool("sealed", false) ? 1 : 0;
        }

        // Mirrors BlockCrock.GetHeldItemInfo: the first perishable content stack
        // goes into a dummy slot whose inventory perishes at the vessel's rate
        // times the crock's own modifier (the seal: x0.1 for a meal, x0.25 for
        // anything else), and the fresh hours left are divided by that rate.
        // GetNonEmptyContents deserializes fresh copies, so updating them here
        // leaves the crock itself untouched.
        private static double FreshHoursShown(BlockCrock crock, ItemStack crockStack, ItemStack[] contents,
            InventoryBase vessel, IWorldAccessor world)
        {
            var crockSlot = new DummySlot(crockStack, vessel);
            var dummyInv = new DummyInventory(world.Api, 1);
            dummyInv.OnAcquireTransitionSpeed += (transType, _, _) =>
                transType != EnumTransitionType.Perish ? 0f
                    : vessel.GetTransitionSpeedMul(transType, crockStack)
                      * crock.GetContainingTransitionModifierContained(world, crockSlot, transType);

            ItemSlot slot = BlockCrock.GetDummySlotForFirstPerishableStack(world, contents, null, dummyInv);
            ItemStack food = slot?.Itemstack;
            if (food == null) return double.MaxValue;

            TransitionState[] states = food.Collectible.UpdateAndGetTransitionStates(world, slot);
            if (states == null) return double.MaxValue;
            foreach (TransitionState state in states)
            {
                if (state.Props.Type != EnumTransitionType.Perish) continue;
                float rate = food.Collectible.GetTransitionRateMul(world, slot, EnumTransitionType.Perish);
                return rate > 0 ? state.FreshHoursLeft / rate : double.MaxValue;
            }
            return double.MaxValue;
        }

        public static void OnSortPacket(IServerPlayer player, SortVesselPacket packet)
        {
            IWorldAccessor world = player?.Entity?.World;
            if (world == null) return;

            var pos = new BlockPos(packet.X, packet.Y, packet.Z);
            if (!(world.BlockAccessor.GetBlockEntity(pos) is BlockEntityGenericTypedContainer be)) return;
            if (be.Block?.Code == null || !be.Block.Code.Equals(VesselCode)) return;

            InventoryBase inv = be.Inventory;
            if (inv == null || !inv.HasOpened(player)) return;

            Sort(inv, world);
            be.MarkDirty();
        }
    }

    // Adds a "Sort" button under the slot grid of the large storage vessel's
    // dialog.
    //
    // Container dialogs are GuiDialogBlockEntityInventory, built and composed in
    // the constructor, so this is a constructor postfix. The dialog's outer
    // bounds are a fixed-size fork around the slot grid, so we grow them by one
    // button row, add the button in that strip, and recompose - the same
    // Composed = false / add / Compose dance as the player-inventory buttons.
    public static class VesselSortButtonPatch
    {
        private const string ButtonKey = "seraphsledgerVesselSortBtn";
        private const double ButtonW = 160;
        private const string TooltipText =
            "Unsealed crocks first, then sealed crocks, each soonest to spoil first. "
            + "Then empty crocks, then everything else by item.";

        // Set from the config when the patch is applied.
        internal static bool ShowTooltip = true;
        private const double ButtonH = 20;
        private const double RowH = ButtonH + 8;

        public static void Postfix(GuiDialogBlockEntityInventory __instance, ICoreClientAPI capi)
        {
            BlockPos pos = __instance.BlockEntityPosition;
            GuiComposer composer = __instance.SingleComposer;
            if (pos == null || composer == null || composer.GetButton(ButtonKey) != null) return;

            Block block = capi.World.BlockAccessor.GetBlock(pos);
            if (block?.Code == null || !block.Code.Equals(VesselSorter.VesselCode)) return;

            ElementBounds dialog = composer.Bounds;
            double pad = GuiStyle.ElementToDialogPadding;

            // The grid's inset ends `pad` above the dialog's bottom edge; the new
            // row goes directly below it.
            double y = dialog.fixedHeight - pad + 6;
            dialog.fixedHeight += RowH;

            ElementBounds buttonBounds = ElementBounds.Fixed(pad, y, ButtonW, ButtonH);

            composer.Composed = false;
            composer.AddSmallButton("Sort by freshness", () => OnSortClicked(pos), buttonBounds,
                EnumButtonStyle.Normal, ButtonKey);
            // Hover text ignores mouse clicks, so it can sit on top of the button.
            if (ShowTooltip)
            {
                composer.AddHoverText(TooltipText, CairoFont.WhiteSmallText(), 260, buttonBounds.FlatCopy());
            }
            composer.Compose(focusFirstElement: false);
            composer.UnfocusOwnElements();
        }

        private static bool OnSortClicked(BlockPos pos)
        {
            SeraphsLedgerModSystem.ClientChannel?.SendPacket(new SortVesselPacket { X = pos.X, Y = pos.Y, Z = pos.Z });
            return true;
        }
    }
}
