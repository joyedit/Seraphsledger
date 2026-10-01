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
    // store: what has to be eaten soonest sits at the top of the grid.
    //
    //   1. unsealed crocks with food in them - these spoil at the normal rate,
    //      so they come first
    //   2. sealed crocks with food in them
    //   3. empty crocks
    //   4. everything else that spoils, in one queue - fruit, vegetables, eggs,
    //      grain, flour, bread, cheese, meat, a bowl of a meal
    //   5. food that keeps: what can be eaten with no spoil time on it, and what
    //      food is made with, by item code
    //   6. everything else, by item code
    //
    // Within 1, 2 and 4 the thing that goes off soonest comes first - ordered by
    // the same "fresh for ..." figure its tooltip shows, so the grid reads in the
    // order the player would eat it. (An earlier version ordered crocks by when
    // the food was made; that disagreed with the tooltip, since foods keep for
    // different lengths and a meal's seal slows it more than raw food's.)
    //
    // Nothing here writes to the food it reads: freshness is taken off copies,
    // because the figure comes from the engine's own transition update, which
    // would otherwise advance decay - and could turn a stack to rot - on a mere
    // press of the Sort button.
    public static class VesselSorter
    {
        public static readonly AssetLocation VesselCode = new AssetLocation("seraphsledger", "largestoragevessel");

        // The groups above, in grid order.
        private const int RankCrockOpen = 0;
        private const int RankCrockSealed = 1;
        private const int RankCrockEmpty = 2;
        private const int RankSpoils = 3;
        private const int RankKeeps = 4;
        private const int RankOther = 5;

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

            List<BarrelRecipeIngredient> foodInputs = FoodBarrelInputs(world);

            // The vessel's slots are empty from here on, so nothing that goes
            // wrong reading one stack may cost the player the rest: a stack that
            // can't be read falls in with "everything else" and still gets put
            // back.
            var keyed = new List<(ItemStack stack, int rank, double fresh, string code)>(merged.Count);
            foreach (ItemStack stack in merged)
            {
                int rank = RankOther;
                double fresh = double.MaxValue;
                try
                {
                    rank = Rank(stack, inv, world, foodInputs, out fresh);
                }
                catch (Exception e)
                {
                    world.Logger.Warning("[seraphsledger] could not read the freshness of {0}: {1}",
                        stack.Collectible?.Code, e);
                }
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

        // Which group this stack belongs in, and - for the three groups ordered
        // by it - the hours it stays fresh at this vessel's rate, i.e. the number
        // its tooltip shows as "fresh for ...". Freshness is MaxValue, which
        // sorts last inside a group and hands the ordering to the item code, for
        // anything with no figure to show: food that never spoils, and food whose
        // spoiling is halted, which a tooltip renders as a bare "Perishable".
        private static int Rank(ItemStack stack, InventoryBase vessel, IWorldAccessor world,
            List<BarrelRecipeIngredient> foodInputs, out double freshness)
        {
            freshness = double.MaxValue;

            if (stack.Collectible is BlockCrock crock)
            {
                ItemStack[] contents = crock.GetNonEmptyContents(world, stack);
                if (contents == null || contents.Length == 0) return RankCrockEmpty;

                ContainedFreshHours(crock, stack, contents, vessel, world, out freshness);
                return stack.Attributes.GetBool("sealed", false) ? RankCrockSealed : RankCrockOpen;
            }

            // Any other container of food - a bowl or a pot of a meal - reads its
            // contents the same way a crock does, then queues with the loose food
            // rather than with the crocks: a crock is a larder, a bowl is a
            // portion, and a portion is eaten before anything is opened.
            if (stack.Collectible is BlockContainer container)
            {
                ItemStack[] contents = container.GetNonEmptyContents(world, stack);
                if (contents != null && contents.Length > 0
                    && ContainedFreshHours(container, stack, contents, vessel, world, out freshness))
                {
                    return RankSpoils;
                }
            }

            // A loose stack, read on its own - and so is a container that holds
            // nothing that spoils, in case the container is the perishable thing.
            if (LooseFreshHours(stack, vessel, world, out freshness)) return RankSpoils;

            freshness = double.MaxValue;
            return IsFood(stack, world, foodInputs) ? RankKeeps : RankOther;
        }

        // A loose stack's fresh hours, read off a copy of it in a slot that
        // belongs to the vessel - so the reading picks up the vessel's own perish
        // rate, exactly as the tooltip does for the slot it is sitting in.
        private static bool LooseFreshHours(ItemStack stack, InventoryBase vessel, IWorldAccessor world,
            out double freshHours)
        {
            return PerishHours(Probe(stack.Clone(), vessel), world, out freshHours);
        }

        // A slot to read a stack's freshness through. It belongs to the vessel's
        // inventory so that the vessel's perish rate applies, but it is not one of
        // that inventory's own slots - and a transition update calls MarkDirty on
        // the slot it is given whenever something finishes transitioning, which
        // from a foreign slot throws ("Supplied slot is not part of this
        // inventory") and would also recurse into another update. Swallowing that
        // notification is what vanilla's own probe slots do.
        private static DummySlot Probe(ItemStack stack, InventoryBase inv)
        {
            var slot = new DummySlot(stack, inv);
            slot.MarkedDirty += () => true;
            return slot;
        }

        // Mirrors BlockCrock.GetHeldItemInfo: the first perishable content stack
        // goes into a dummy slot whose inventory perishes at the vessel's rate
        // times the container's own modifier (a crock's seal: x0.1 for a meal,
        // x0.25 for anything else), and the fresh hours left are divided by that
        // rate.
        private static bool ContainedFreshHours(BlockContainer container, ItemStack containerStack,
            ItemStack[] contents, InventoryBase vessel, IWorldAccessor world, out double freshHours)
        {
            DummySlot containerSlot = Probe(containerStack, vessel);
            var dummyInv = new DummyInventory(world.Api, 1);
            dummyInv.OnAcquireTransitionSpeed += (transType, _, _) =>
                transType != EnumTransitionType.Perish ? 0f
                    : vessel.GetTransitionSpeedMul(transType, containerStack)
                      * container.GetContainingTransitionModifierContained(world, containerSlot, transType);

            // GetNonEmptyContents hands out the very stacks the container holds in
            // its attributes, so hand the probe copies of them.
            var copies = new ItemStack[contents.Length];
            for (int i = 0; i < contents.Length; i++) copies[i] = contents[i]?.Clone();

            ItemSlot slot = BlockCrock.GetDummySlotForFirstPerishableStack(world, copies, null, dummyInv);
            return PerishHours(slot, world, out freshHours);
        }

        // The "fresh for ..." figure for whatever is in this slot, worked out the
        // way CollectibleObject.AppendPerishableInfoText does it: the fresh hours
        // left over the slot's perish rate.
        //
        // False means this doesn't spoil at all. True with freshHours left at
        // MaxValue means it spoils but no time can be put on it - a rate of zero
        // (food still hot from the pot), or a stack whose transitions are frozen.
        private static bool PerishHours(ItemSlot slot, IWorldAccessor world, out double freshHours)
        {
            freshHours = double.MaxValue;

            ItemStack stack = slot?.Itemstack;
            if (stack?.Collectible == null || !Spoils(stack, world)) return false;

            TransitionState[] states = stack.Collectible.UpdateAndGetTransitionStates(world, slot);
            if (states == null) return true;

            foreach (TransitionState state in states)
            {
                // A stack that transitions as it is read - food gone to rot -
                // leaves null entries behind.
                if (state?.Props == null || state.Props.Type != EnumTransitionType.Perish) continue;

                float rate = stack.Collectible.GetTransitionRateMul(world, slot, EnumTransitionType.Perish);
                if (rate > 0) freshHours = state.FreshHoursLeft / rate;
                return true;
            }
            return true;
        }

        // Does this have a spoil time at all? Taken from the transitionable
        // properties rather than from the figure, so that food whose spoiling is
        // halted is still filed with the food that spoils.
        private static bool Spoils(ItemStack stack, IWorldAccessor world)
        {
            TransitionableProperties[] props = stack.Collectible.GetTransitionableProperties(world, stack, null);
            if (props == null) return false;

            foreach (TransitionableProperties prop in props)
            {
                if (prop != null && prop.Type == EnumTransitionType.Perish) return true;
            }
            return false;
        }

        // Food with no spoil time on it: what can be eaten as it is (honeycomb),
        // and what food is made with (salt). Salt feeds nobody and keeps for
        // ever, but a food store is where it lives - it is what turns meat into
        // cured meat - so rather than name it, take the ingredients of the barrel
        // recipes that make food. That covers whatever a mod adds and leaves
        // quicklime and tannin bark out.
        private static bool IsFood(ItemStack stack, IWorldAccessor world,
            List<BarrelRecipeIngredient> foodInputs)
        {
            if (Edible(stack, world)) return true;

            foreach (BarrelRecipeIngredient ing in foodInputs)
            {
                if (ing.SatisfiesAsIngredient(stack)) return true;
            }
            return false;
        }

        private static bool Edible(ItemStack stack, IWorldAccessor world)
        {
            FoodNutritionProperties props = stack.Collectible?.GetNutritionProperties(world, stack, null);
            return props != null && props.FoodCategory != EnumFoodCategory.NoNutrition;
        }

        // The ingredients of every barrel recipe whose output is food. Walked
        // afresh on each sort rather than cached: a sort is a button press, and
        // the recipe list is a few dozen entries long.
        private static List<BarrelRecipeIngredient> FoodBarrelInputs(IWorldAccessor world)
        {
            var inputs = new List<BarrelRecipeIngredient>();

            List<BarrelRecipe> recipes;
            try
            {
                recipes = world.Api?.GetBarrelRecipes();
            }
            catch (Exception)
            {
                // No recipe registry to hand; nutrition alone decides what food is.
                return inputs;
            }
            if (recipes == null) return inputs;

            foreach (BarrelRecipe recipe in recipes)
            {
                if (recipe?.Ingredients == null) continue;

                ItemStack output = recipe.Output?.ResolvedItemstack;
                if (output?.Collectible == null) continue;
                if (!Spoils(output, world) && !Edible(output, world)) continue;

                foreach (BarrelRecipeIngredient ing in recipe.Ingredients)
                {
                    if (ing != null) inputs.Add(ing);
                }
            }
            return inputs;
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
            "Unsealed crocks first, then sealed crocks, then empty crocks. Then every other "
            + "food that spoils - fruit, eggs, bread, a bowl of a meal - in one queue. "
            + "Anything with a spoil time comes soonest-to-spoil first. Then food that keeps, "
            + "then everything else by item.";

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
