using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace SeraphsLedger
{
    // Two bulk transfers out of an open inventory grid:
    //
    //   - move-all      (Ctrl + left-click, fixed) dumps every non-empty slot of
    //                   the grid's inventory into the open container(s).
    //   - move-matching (Shift + middle mouse by default, rebindable) moves only
    //                   the stacks of the same kind as the slot you pressed on.
    //
    // Both ride the engine's own shift-click pipeline (ActivateSlot with
    // EnumModifierKey.SHIFT), so a stack only ever lands where a shift-click
    // would have put it - into the container from the player, back into the
    // player from the container, and between hotbar and backpack when nothing
    // else is open.
    //
    // They share a single Harmony prefix on purpose. Move-matching is
    // player-bindable, so it can be pointed at Ctrl + left-click; with two
    // prefixes on one method, whichever Harmony happened to order first would
    // silently win. One prefix decides between them in a defined order instead.
    public static class MoveAll
    {
        // Registered as a real hotkey so it shows up under Settings > Controls >
        // Inventory hotkeys and the player can rebind it to any key or mouse
        // button combination, with the game persisting that choice for us.
        public const string MatchingHotkeyCode = "seraphsledgermovematching";

        public static bool MoveAllEnabled;
        public static bool MoveMatchingEnabled;

        private static ICoreClientAPI capi;

        private static readonly FieldInfo InventoryField =
            AccessTools.Field(typeof(GuiElementItemSlotGridBase), "inventory");
        private static readonly FieldInfo SendPacketHandlerField =
            AccessTools.Field(typeof(GuiElementItemSlotGridBase), "SendPacketHandler");
        private static readonly FieldInfo ComposerElementsField =
            AccessTools.Field(typeof(GuiComposer), "interactiveElements");

        public static void Init(ICoreClientAPI api)
        {
            capi = api;
        }

        public static void Shutdown()
        {
            capi = null;
        }

        // IInputAPI can only register a keyboard default, so register with a
        // placeholder key and then rewrite the mapping to the middle mouse
        // button. Since 1.19.4 a mouse button is just a keycode of
        // KeyCombination.MouseStart + the button index, and the vanilla Controls
        // dialog captures those exactly like keys - so the binding stays fully
        // rebindable, to Alt + right-click or to a plain letter key alike.
        public static void RegisterHotkey(ICoreClientAPI api)
        {
            api.Input.RegisterHotKey(MatchingHotkeyCode, "Move all of one item", GlKeys.Unknown,
                HotkeyType.InventoryHotkeys, altPressed: false, ctrlPressed: false, shiftPressed: true);

            HotKey hk = api.Input.GetHotKeyByCode(MatchingHotkeyCode);
            if (hk == null) return;

            int middleMouse = KeyCombination.MouseStart + (int)EnumMouseButton.Middle;

            // RegisterHotKey copies what we passed into DefaultMapping, then
            // replaces CurrentMapping with the player's saved binding if they
            // have one. So always fix up the default (that's what the "set
            // default" button in Controls restores), but only fix up the current
            // mapping while it is still the placeholder we passed in - anything
            // else is the player's own choice and must not be overwritten.
            if (hk.DefaultMapping != null) hk.DefaultMapping.KeyCode = middleMouse;
            if (hk.CurrentMapping != null && hk.CurrentMapping.KeyCode == (int)GlKeys.Unknown)
            {
                hk.CurrentMapping.KeyCode = middleMouse;
            }

            api.Input.SetHotKeyHandler(MatchingHotkeyCode, OnMatchingHotkey);
        }

        // The hotkey half of move-matching: it acts on whatever slot the mouse is
        // hovering rather than on a click.
        //
        // This is reached for a key binding, and for the mouse buttons the game
        // does not route into the GUI at all (anything past left/right/middle,
        // which have no vanilla hotkey to forward them). For the three it does
        // route, the slot-grid prefix below has already run - nested inside
        // vanilla's own mouse hotkey handler - and its args.Handled bubbles back
        // up as a true from that handler, which ends the hotkey dispatch before
        // it ever reaches us. So a press can never be served twice.
        private static bool OnMatchingHotkey(KeyCombination comb)
        {
            if (capi == null) return false;

            // The same binding, aimed at a crate out in the world instead of at a
            // slot in a dialog: every stack of what the crate holds goes into it.
            // Asked first because out in the world there is no hovered slot to
            // find, so the lookup below would only bail.
            //
            // This serves a keyboard binding. A mouse binding never reaches any
            // mod's hotkey out in the world - vanilla's own "pickblock" consumes
            // the press first - so that case is served from Event.MouseDown
            // instead. See CrateSweep.InitClient.
            if (CrateSweep.TryRequestSweep()) return true;

            if (!MoveMatchingEnabled) return false;

            ItemSlot hovered = capi.World?.Player?.InventoryManager?.CurrentHoveredSlot;
            if (hovered == null || hovered.Empty) return false;

            GuiElementItemSlotGridBase grid = FindHoveredGrid(hovered);
            if (grid == null) return false;

            IInventory inv = InventoryField?.GetValue(grid) as IInventory;
            if (inv == null) return false;

            ItemSlot slot = inv[grid.hoverSlotId];
            if (slot == null || slot.Empty) return false;

            var send = SendPacketHandlerField?.GetValue(grid) as Action<object>;
            return MoveMatching(inv, slot.Itemstack, send) > 0;
        }

        // Which slot grid is the mouse over? Each grid tracks that itself in the
        // public hoverSlotId, so the answer is just a matter of finding the grids,
        // which means walking the open dialogs' composers. Only ever runs on a key
        // press, so the reflection and the walk cost nothing worth saving.
        //
        // A grid's hoverSlotId can go stale - a dialog that handles a mouse move
        // ends the dispatch, so grids behind it never hear that the mouse left -
        // hence the cross-check against the slot the engine itself last reported
        // as hovered.
        private static GuiElementItemSlotGridBase FindHoveredGrid(ItemSlot hovered)
        {
            foreach (GuiDialog dlg in capi.Gui.OpenedGuis)
            {
                if (dlg?.Composers == null) continue;

                foreach (GuiComposer composer in dlg.Composers.ToArray())
                {
                    if (composer == null) continue;
                    var elems = ComposerElementsField?.GetValue(composer) as Dictionary<string, GuiElement>;
                    if (elems == null) continue;

                    foreach (GuiElement e in elems.Values)
                    {
                        if (!(e is GuiElementItemSlotGridBase grid) || grid.hoverSlotId < 0) continue;
                        if ((InventoryField?.GetValue(grid) as IInventory)?[grid.hoverSlotId] == hovered) return grid;
                    }
                }
            }
            return null;
        }

        // Does this click match the move-matching binding? Modifiers are compared
        // exactly, except when the binding asks for none at all - then they are
        // ignored, the same concession HotKey.DidPress makes for mouse buttons, so
        // a plain middle-click binding still fires with Shift held.
        internal static bool MatchesBinding(ICoreClientAPI api, EnumMouseButton button, bool ctrl, bool shift, bool alt)
        {
            KeyCombination comb = api.Input.GetHotKeyByCode(MatchingHotkeyCode)?.CurrentMapping;
            if (comb == null) return false;
            if (comb.KeyCode != KeyCombination.MouseStart + (int)button) return false;
            if (!comb.Ctrl && !comb.Shift && !comb.Alt) return true;
            return comb.Ctrl == ctrl && comb.Shift == shift && comb.Alt == alt;
        }

        // Moves every stack of the same kind as `kind` out of the inventory.
        //
        // "Same kind" means the same item or block - what you'd see one icon for.
        // Durability, freshness and the rest of a stack's attributes are ignored
        // on purpose: a chest of copper axes at assorted wear is still a chest of
        // copper axes, and that is what you meant to move.
        internal static int MoveMatching(IInventory inv, ItemStack kind, Action<object> sendPacket)
        {
            CollectibleObject wanted = kind?.Collectible;
            if (wanted == null) return 0;
            return MoveOut(inv, sendPacket, s => s.Itemstack?.Collectible == wanted);
        }

        internal static int MoveEverything(IInventory inv, Action<object> sendPacket)
        {
            return MoveOut(inv, sendPacket, s => true);
        }

        private static int MoveOut(IInventory inv, Action<object> sendPacket, System.Func<ItemSlot, bool> accept)
        {
            // Snapshot slot references up-front; slot IDs may shift if a worn bag
            // is removed mid-iteration, but the slot objects themselves remain
            // valid for emptiness checks.
            List<ItemSlot> targets = new List<ItemSlot>();
            foreach (ItemSlot s in inv)
            {
                if (s == null || s.Empty) continue;
                if (s is ItemSlotBackpack) continue; // don't pull bags off the player
                if (!accept(s)) continue;
                targets.Add(s);
            }

            int moved = 0;
            foreach (ItemSlot sslot in targets)
            {
                if (sslot.Empty) continue;
                int sid = inv.GetSlotId(sslot);
                if (sid < 0) continue;

                int before = sslot.StackSize;

                var op = new ItemStackMoveOperation(
                    capi.World, EnumMouseButton.Left,
                    EnumModifierKey.SHIFT, EnumMergePriority.AutoMerge);
                op.ActingPlayer = capi.World.Player;
                op.RequestedQuantity = before;

                object packets = inv.ActivateSlot(sid, sslot, ref op);
                if (sslot.Empty || sslot.StackSize != before) moved++;

                if (packets == null || sendPacket == null) continue;

                if (packets is object[] arr)
                {
                    foreach (var p in arr) sendPacket(p);
                }
                else
                {
                    sendPacket(packets);
                }
            }

            return moved;
        }
    }

    // Click handler for both bulk moves.
    //
    // Patched at OnMouseDownOnElement rather than SlotClick because the outer
    // method reads inventory[keyAtIndex] after the click; player backpack
    // inventories (InventoryPlayerBackpacks) shrink when a worn bag is removed,
    // and its indexer returns null past Count -> NPE on cleanup. By patching the
    // outer method and short-circuiting, we skip that cleanup entirely.
    //
    // Patched manually (not via a [HarmonyPatch] attribute + PatchAll) so it can be
    // gated by the config switches independently of the other patches.
    public static class OnMouseDownMoveAllPatch
    {
        private static readonly FieldInfo InventoryField =
            AccessTools.Field(typeof(GuiElementItemSlotGridBase), "inventory");
        private static readonly FieldInfo SendPacketHandlerField =
            AccessTools.Field(typeof(GuiElementItemSlotGridBase), "SendPacketHandler");

        public static bool Prefix(
            GuiElementItemSlotGridBase __instance,
            ICoreClientAPI api,
            MouseEvent args)
        {
            bool shift = api.Input.KeyboardKeyState[(int)GlKeys.LShift] || api.Input.KeyboardKeyState[(int)GlKeys.RShift];
            bool ctrl = api.Input.KeyboardKeyState[(int)GlKeys.LControl] || api.Input.KeyboardKeyState[(int)GlKeys.RControl];
            bool alt = api.Input.KeyboardKeyState[(int)GlKeys.LAlt] || api.Input.KeyboardKeyState[(int)GlKeys.RAlt];

            // Move-matching is asked first because it's the bindable one: if the
            // player points it at Ctrl + left-click, that's what they get.
            bool matching = MoveAll.MoveMatchingEnabled
                && MoveAll.MatchesBinding(api, args.Button, ctrl, shift, alt);
            bool everything = !matching
                && MoveAll.MoveAllEnabled
                && args.Button == EnumMouseButton.Left && ctrl && !shift && !alt;

            if (!matching && !everything) return true;

            if (!__instance.Bounds.ParentBounds.PointInside(args.X, args.Y)) return true;

            // Make sure the click actually landed on a rendered slot.
            int slotId = -1;
            var bounds = __instance.SlotBounds;
            var rendered = __instance.renderedSlots;
            int n = Math.Min(bounds?.Length ?? 0, rendered?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                if (bounds[i].PointInside(args.X, args.Y)) { slotId = rendered.GetKeyAtIndex(i); break; }
            }
            if (slotId < 0) return true;

            IInventory inv = InventoryField?.GetValue(__instance) as IInventory;
            if (inv == null) return true;

            var sendHandler = SendPacketHandlerField?.GetValue(__instance) as Action<object>;

            if (matching)
            {
                // A click on an empty slot names nothing to match, so leave it to
                // the engine - that's still how you put the held stack down.
                ItemSlot clicked = inv[slotId];
                if (clicked == null || clicked.Empty) return true;

                MoveAll.MoveMatching(inv, clicked.Itemstack, sendHandler);
                args.Handled = true;
                return false;
            }

            // Move-all only acts when at least one non-player inventory
            // (chest/trunk/etc.) is open, so it is a no-op in the bare E-key
            // player inventory - where it would otherwise shovel the whole
            // backpack into the hotbar. Move-matching is targeted enough to be
            // useful there, so it carries no such guard.
            bool hasContainer = false;
            foreach (var oi in api.World.Player.InventoryManager.OpenedInventories)
            {
                if (oi != null && !(oi is InventoryBasePlayer))
                {
                    hasContainer = true;
                    break;
                }
            }
            if (!hasContainer) return true;

            MoveAll.MoveEverything(inv, sendHandler);
            args.Handled = true;
            return false;
        }
    }
}
