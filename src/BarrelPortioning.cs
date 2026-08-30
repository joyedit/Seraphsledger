using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Barrel ingredient portioning: only let a barrel's input slot take as many
    // items as its current liquid can actually turn into something, and leave the
    // rest in the player's inventory.
    //
    // The annoyance this fixes: barrel recipes are exact ratios, not "up to".
    // A full 50L barrel of water wants exactly 50 lime for limewater (1L : 1) and
    // exactly 5 logs for weak tannin (10L : 1); 50L of tannin wants at most 25
    // small hides (2L : 1). Drop a whole stack of 64 lime in and vanilla happily
    // swallows all 64 - BarrelRecipe.GetOutputSize then finds the ratio off and
    // matches nothing at all, so the barrel just sits there holding your lime
    // until you work out how much to pull back out.
    //
    // How it works: every path that moves items into a slot (left-click, shift-
    // click, wheel, hopper push) sizes the transfer with
    // ItemSlot.GetRemainingSlotSpace, and CanTakeFrom refuses the slot outright
    // when that returns 0. A postfix on it clamps the answer for
    // ItemSlotBarrelInput to what the recipes can consume, which is enough to make
    // every one of those paths stop at the right number and keep the remainder
    // where it was. It must run on both sides: inventory moves are executed
    // client-side for responsiveness and re-executed server-side from the packet,
    // and a one-sided clamp would desync the two.
    //
    // The clamp only applies when the barrel already holds a liquid - that liquid
    // is what fixes the ratio. An empty barrel is left alone so the old habit of
    // loading the solids first still works.
    public static class BarrelPortionPatches
    {
        // No cap: either this slot isn't a recipe-bounded barrel input, or the
        // item is one vanilla lets you overstock anyway.
        private const int Unbounded = -1;

        public static void GetRemainingSlotSpacePostfix(ItemSlot __instance, ItemStack forItemstack, ref int __result)
        {
            if (__result <= 0 || forItemstack?.Collectible == null) return;
            if (!(__instance is ItemSlotBarrelInput)) return;

            // A slot holding something else can't merge in the first place; the
            // stack count in it says nothing about how much of forItemstack fits.
            if (__instance.Itemstack != null &&
                __instance.Itemstack.Collectible != forItemstack.Collectible) return;

            int cap = MaxUsefulQuantity(__instance, forItemstack);
            if (cap == Unbounded) return;

            int space = Math.Max(0, cap - __instance.StackSize);
            if (space < __result) __result = space;
        }

        // The largest amount of forItemstack any barrel recipe could consume
        // against the liquid currently in the barrel, or Unbounded when nothing
        // pins it down.
        private static int MaxUsefulQuantity(ItemSlot inputSlot, ItemStack forItemstack)
        {
            InventoryBase inv = inputSlot.Inventory;
            if (inv == null || inv.Count < 2) return Unbounded;

            // Barrel inventories are always [0] = input, [1] = liquid.
            ItemSlot liquidSlot = inv[1];
            if (!(liquidSlot is ItemSlotLiquidOnly) || liquidSlot.Empty) return Unbounded;

            ICoreAPI api = inv.Api;
            if (api == null) return Unbounded;

            ItemStack liquidStack = liquidSlot.Itemstack;
            int liquidItems = liquidSlot.StackSize;
            if (liquidItems <= 0) return Unbounded;

            List<BarrelRecipe> recipes;
            try
            {
                recipes = api.GetBarrelRecipes();
            }
            catch (Exception)
            {
                return Unbounded;
            }
            if (recipes == null) return Unbounded;

            int best = Unbounded;
            foreach (BarrelRecipe recipe in recipes)
            {
                // A barrel has exactly two slots, so a recipe with any other
                // number of ingredients can never match one (BarrelRecipe.PairInput
                // requires one filled slot per ingredient) and has no say here.
                if (recipe.Ingredients == null || recipe.Ingredients.Length != 2) continue;

                BarrelRecipeIngredient liquidIng = null;
                BarrelRecipeIngredient inputIng = null;
                foreach (BarrelRecipeIngredient ing in recipe.Ingredients)
                {
                    // Litres is left at -1 for anything without liquid props, so
                    // it doubles as "this is the ingredient in the liquid slot".
                    if (liquidIng == null && ing.Litres >= 0 &&
                        ing.SatisfiesAsIngredient(liquidStack, false))
                    {
                        liquidIng = ing;
                        continue;
                    }
                    if (inputIng == null && ing.SatisfiesAsIngredient(forItemstack, false))
                    {
                        inputIng = ing;
                    }
                }
                if (liquidIng == null || inputIng == null || inputIng == liquidIng) continue;
                if (liquidIng.Quantity <= 0 || inputIng.Quantity <= 0) continue;

                // consumeQuantity marks an ingredient that is only partly used up
                // (a hide soaking in limewater takes 2L and leaves the rest). For
                // those vanilla accepts a surplus without breaking the match, so
                // there's nothing to clamp and we bow out entirely rather than let
                // some other recipe's ratio cut the stack short.
                if (inputIng.ConsumeQuantity.HasValue) return Unbounded;

                // How many times over the recipe can run on the liquid at hand.
                // This holds whichever side carries consumeQuantity: the batch
                // count is set by the ingredients without it, and the ones with it
                // only have to cover Quantity * batches.
                //
                // Too little liquid for even one batch still allows a single
                // recipe unit through - a slot that silently refuses every item
                // reads as a broken barrel, and topping the water up afterwards is
                // a perfectly normal way to work.
                int batches = Math.Max(1, liquidItems / liquidIng.Quantity);
                int useful = inputIng.Quantity * batches;
                if (useful > best) best = useful;
            }

            return best;
        }
    }
}
