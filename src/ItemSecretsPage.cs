using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphsLedger
{
    // The Page of Secrets is a key to a player's live lockbox registry, not a
    // snapshot: it binds to its crafter (owner) via item attributes, and whoever
    // HOLDS it sees the owner's hidden lockboxes - including ones placed after
    // the page was written. Steal someone's page and you can find their stashes;
    // that's the game. Pages crafted before this class existed have no seal and
    // bind lazily to the first player seen holding them (LockboxRegistry does
    // that server-side).
    public class ItemSecretsPage : Item
    {
        public const string AttrOwnerUid = "sealOwnerUid";
        public const string AttrOwnerName = "sealOwnerName";

        public override void OnCreatedByCrafting(ItemSlot[] allInputSlots, ItemSlot outputSlot, IRecipeBase byRecipe)
        {
            base.OnCreatedByCrafting(allInputSlots, outputSlot, byRecipe);
            IPlayer crafter = (outputSlot?.Inventory as InventoryBasePlayer)?.Player;
            Bind(outputSlot?.Itemstack, crafter);
        }

        public static void Bind(ItemStack stack, IPlayer owner)
        {
            if (stack == null || owner == null) return;
            stack.Attributes.SetString(AttrOwnerUid, owner.PlayerUID);
            stack.Attributes.SetString(AttrOwnerName, owner.PlayerName);
        }

        public static string OwnerUid(ItemStack stack)
        {
            return stack?.Attributes?.GetString(AttrOwnerUid);
        }

        public override string GetHeldItemName(ItemStack itemStack)
        {
            string owner = itemStack?.Attributes?.GetString(AttrOwnerName);
            if (owner == null) return base.GetHeldItemName(itemStack);
            return Lang.Get("seraphsledger:secretspage-owned", owner);
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            string owner = inSlot?.Itemstack?.Attributes?.GetString(AttrOwnerName);
            if (owner != null)
            {
                dsc.AppendLine(Lang.Get("seraphsledger:secretspage-sealedby", owner));
            }
        }
    }
}
