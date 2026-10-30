using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // Sent client -> server when the move-matching hotkey is pressed while
    // looking at a crate. Carries the crate, because the server has no idea what
    // the player was aiming at.
    [ProtoContract]
    public class SweepCratePacket
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
    }

    // Bulk deposit into a crate: every stack of what the crate holds, out of the
    // hotbar and the worn bags, in one gesture.
    //
    // A crate is the one container where this cannot make a mess, because a crate
    // holds exactly one kind of thing - the crate itself names the kind and
    // anything else is simply not swept. Three ways to ask for it:
    //
    //   - Ctrl + Shift + right-click with an empty hand. Vanilla does nothing at
    //     all for that gesture (its put path is guarded by a non-empty hand, and
    //     holding Shift suppresses the take), so this adds a behaviour rather
    //     than replacing one.
    //   - Ctrl + Shift + right-click holding the item. Vanilla puts the held
    //     stack; the sweep follows it in and empties every other matching stack
    //     after it.
    //   - The move-matching hotkey (Shift + middle mouse by default) aimed at a
    //     crate - the same binding that moves all of one item inside an
    //     inventory grid. See MoveAll.
    //
    // The two right-click routes ride vanilla's own interaction, which the engine
    // runs on the client for prediction and again on the server from the interact
    // packet, so a postfix on it lands on both sides exactly as the vanilla put
    // does. The hotkey has no such pipeline behind it, so it goes over the mod's
    // network channel and is swept server-side only; the crate's own block entity
    // sync brings the result back to every client.
    public static class CrateSweep
    {
        public static bool EmptyHandEnabled;
        public static bool HeldStackEnabled;
        public static bool HotkeyEnabled;

        private static ICoreClientAPI capi;

        // How far a sweep packet may reach, matching the tally board's own bound:
        // comfortably past the player's interaction range, short of letting a
        // crafted packet rummage through a crate across the map.
        private const int MaxReach = 16;

        // Moves every stack of the crate's own kind out of the player's inventory
        // and into the crate. Returns the number of items moved, and the kind
        // moved, for the sound and the audit line.
        //
        // The kind is whatever the crate already holds. Crate slots do not
        // enforce that themselves - an empty slot in a crate will take anything -
        // so vanilla's interact code gates every put on this exact comparison,
        // and so must a sweep, or it would quietly drop a second item into the
        // spare slots and leave a crate no interaction can empty cleanly.
        //
        // An empty crate is therefore left alone: it has no kind yet, and the
        // first stack into one also has to redraw the label, which is vanilla's
        // job on the ordinary put path. Ctrl + Shift + right-click still fills an
        // empty crate - vanilla puts the held stack, and the sweep follows it.
        internal static int Sweep(BlockEntityCrate crate, IPlayer byPlayer, out ItemStack kind)
        {
            kind = null;

            InventoryBase inv = crate?.Inventory;
            if (inv == null || byPlayer?.Entity == null) return 0;

            // Retrieve-only crates (the ones a ruin spawns pre-filled) carry
            // PutLocked. GetBestSuitedSlot honours it anyway; this just saves
            // walking the player's inventory to find that out.
            if (inv.PutLocked) return 0;

            ItemSlot filled = inv.FirstNonEmptySlot;
            if (filled == null) return 0;
            kind = filled.Itemstack;

            IWorldAccessor world = byPlayer.Entity.World;
            int moved = 0;

            foreach (ItemSlot source in SourceSlots(byPlayer))
            {
                if (source.Empty) continue;
                if (!kind.Equals(world, source.Itemstack, GlobalConstants.IgnoredStackAttributes)) continue;

                int before = source.StackSize;

                // The same shape as vanilla's own put loop: ask the crate for its
                // best slot, fill it, then take that slot out of the running, so
                // the loop is bounded by the crate's slot count however the
                // transfers turn out.
                List<ItemSlot> tried = new List<ItemSlot>();
                while (source.StackSize > 0 && tried.Count < inv.Count)
                {
                    WeightedSlot best = inv.GetBestSuitedSlot(source, null, tried);
                    if (best.slot == null) break;

                    source.TryPutInto(world, best.slot, source.StackSize);
                    tried.Add(best.slot);
                }

                if (source.StackSize == before) continue;
                moved += before - source.StackSize;
                source.MarkDirty();
            }

            return moved;
        }

        // Where a sweep pulls from: the bags first, the hotbar last. When the
        // crate fills up partway through, the leftovers are then sitting in the
        // hotbar in plain sight rather than buried in a bag.
        //
        // Worn bags themselves (ItemSlotBackpack) are skipped - the same rule
        // MoveAll keeps - so a sweep empties the bags without pulling them off
        // the player. Snapshotted up front because the transfers below modify the
        // inventories being walked.
        private static List<ItemSlot> SourceSlots(IPlayer byPlayer)
        {
            List<ItemSlot> slots = new List<ItemSlot>();
            IPlayerInventoryManager im = byPlayer.InventoryManager;
            if (im == null) return slots;

            foreach (string invClass in new[] { GlobalConstants.backpackInvClassName, GlobalConstants.hotBarInvClassName })
            {
                IInventory inv = im.GetOwnInventory(invClass);
                if (inv == null) continue;

                foreach (ItemSlot slot in inv)
                {
                    if (slot == null || slot.Empty) continue;
                    if (slot is ItemSlotBackpack) continue;
                    slots.Add(slot);
                }
            }
            return slots;
        }

        // Visual and audible parity with vanilla's own put. The block entity sync
        // is what rebuilds the crate's content mesh on every client - its
        // FromTreeAttributes drops the cached mesh - and the stack's place sound
        // is what vanilla plays for each slot it fills.
        //
        // actorPlaysLocally says whether this sweep also ran on the acting
        // player's own client. On the right-click routes it did, so the sound is
        // marked as a dual call and the server leaves it to that client. The
        // hotkey route is server-only, so the server has to send the sound to
        // them like it would to anybody else.
        internal static void FinishSweep(BlockEntityCrate crate, IPlayer byPlayer, ItemStack kind, bool actorPlaysLocally)
        {
            crate.MarkDirty(false);

            SoundAttributes sound = kind?.Block?.Sounds?.Place ?? GlobalConstants.DefaultBuildSound;
            byPlayer.Entity.World.PlaySoundAt(sound, byPlayer.Entity, actorPlaysLocally ? byPlayer : null);
        }

        // One audit line per sweep, in the same shape as the per-slot lines
        // vanilla writes for an ordinary put. Server-side only: on the client the
        // audit log is just noise in the player's own log file.
        internal static void Audit(BlockEntityCrate crate, IPlayer byPlayer, int quantity, ItemStack kind)
        {
            if (crate.Api == null || crate.Api.Side != EnumAppSide.Server) return;

            crate.Api.Logger.Audit("{0} Swept {1}x{2} into Crate at {3}.",
                byPlayer.PlayerName, quantity, kind?.Collectible?.Code, crate.Pos);
        }

        // A mouse binding has to be served from the mouse-down event rather than
        // from the hotkey handler, because out in the world the hotkey never
        // reaches us: "pickblock" sits on the bare middle mouse button, its
        // DidPress ignores modifiers (that concession is made for every
        // unmodified mouse binding), and its handler returns true whatever
        // happens - so it ends the hotkey dispatch before any mod's hotkey is
        // looked at. In a dialog the press never gets that far, which is why the
        // slot grids have worked all along.
        //
        // What does still get a look in is this event. "middlemouse" is
        // registered ahead of "pickblock" and its handler goes through
        // ClientMain.UpdateMouseButtonState, which fires Event.MouseDown first
        // and reports the press as consumed if the event comes back handled -
        // which ends the dispatch, so marking it handled is also what stops the
        // click from picking the block.
        //
        // Keyboard bindings are fine through the hotkey handler, so both routes
        // stay: see MoveAll.OnMatchingHotkey.
        internal static void InitClient(ICoreClientAPI api)
        {
            capi = api;
            if (HotkeyEnabled) api.Event.MouseDown += OnMouseDown;
        }

        // Unsubscribing a handler that was never added is a no-op, so this needs
        // no record of whether InitClient actually subscribed - which matters in
        // singleplayer, where the client and server mod systems share these
        // statics and either one may be disposed first.
        internal static void Shutdown()
        {
            if (capi != null) capi.Event.MouseDown -= OnMouseDown;
            capi = null;
        }

        private static void OnMouseDown(MouseEvent args)
        {
            if (args.Handled || capi == null) return;

            // The event carries no modifiers of its own on this path - the
            // platform builds it with a modifier mask of 0 - so read the keyboard
            // the way the slot-grid prefix does.
            bool shift = capi.Input.KeyboardKeyState[(int)GlKeys.LShift] || capi.Input.KeyboardKeyState[(int)GlKeys.RShift];
            bool ctrl = capi.Input.KeyboardKeyState[(int)GlKeys.LControl] || capi.Input.KeyboardKeyState[(int)GlKeys.RControl];
            bool alt = capi.Input.KeyboardKeyState[(int)GlKeys.LAlt] || capi.Input.KeyboardKeyState[(int)GlKeys.RAlt];

            if (!MoveAll.MatchesBinding(capi, args.Button, ctrl, shift, alt)) return;
            if (TryRequestSweep()) args.Handled = true;
        }

        // Client side of the hotkey route: ask the server to sweep the crate the
        // player is aiming at. Returns false when they aren't aiming at one, so
        // the press falls through to whatever else wants it - pick block, in the
        // case of the default middle-mouse binding.
        internal static bool TryRequestSweep()
        {
            if (!HotkeyEnabled || capi == null) return false;

            // World interaction only. While a dialog holds the mouse, a press is
            // about the slot under the cursor, not about whatever block happens to
            // be behind the dialog.
            if (!capi.Input.MouseGrabbed) return false;

            BlockPos pos = capi.World?.Player?.CurrentBlockSelection?.Position;
            if (pos == null) return false;
            if (!(capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityCrate crate)) return false;

            // An empty crate names no kind, so there is nothing to sweep and the
            // press is better left to anything else that wants it.
            if (crate.Inventory?.FirstNonEmptySlot == null) return false;

            SeraphsLedgerModSystem.ClientChannel?.SendPacket(new SweepCratePacket
            {
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z
            });
            return true;
        }

        // Server side of the hotkey route.
        internal static void OnSweepPacket(IServerPlayer player, SweepCratePacket packet)
        {
            if (!HotkeyEnabled || player?.Entity == null) return;

            BlockPos pos = new BlockPos(packet.X, packet.Y, packet.Z);
            if (player.Entity.Pos.AsBlockPos.DistanceTo(pos) > MaxReach) return;
            if (!(player.Entity.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityCrate crate)) return;

            int moved = Sweep(crate, player, out ItemStack kind);
            if (moved <= 0) return;

            FinishSweep(crate, player, kind, actorPlaysLocally: false);
            Audit(crate, player, moved, kind);
        }
    }

    // The two right-click routes, as a prefix/postfix pair on
    // BlockEntityCrate.OnBlockInteractStart.
    //
    // A postfix rather than a prefix on purpose. Vanilla's own put already
    // handles the awkward parts of a first stack into an empty crate - it
    // redraws the label from the new contents and writes the audit line - so the
    // sweep runs after it and only has to deal with what is left behind. Nothing
    // vanilla does is replaced, and the empty-hand gesture it does nothing for
    // needs no prefix at all.
    //
    // The prefix is there only to read the context the postfix can no longer see:
    // whether the player's hand was full before vanilla drained it, and whether
    // what they held was the kind this crate takes.
    public static class CrateInteractSweepPatch
    {
        // Carried from prefix to postfix as Harmony's __state.
        public class Context
        {
            public bool HeldSomething;
            public bool HeldMatched;
            public bool LabelGesture;
        }

        public static void Prefix(BlockEntityCrate __instance, IPlayer byPlayer, BlockSelection blockSel,
                                  out Context __state)
        {
            // A null state tells the postfix this press was none of its business.
            __state = null;

            if (!CrateSweep.EmptyHandEnabled && !CrateSweep.HeldStackEnabled) return;
            if (__instance == null || byPlayer?.Entity == null) return;

            // Only vanilla's "put the whole stack" gesture. Every other crate
            // interaction - take one, take a stack, put one - is left alone.
            EntityControls controls = byPlayer.Entity.Controls;
            if (controls == null || !controls.ShiftKey || !controls.CtrlKey) return;

            ItemSlot active = byPlayer.InventoryManager?.ActiveHotbarSlot;
            bool held = active != null && !active.Empty;
            if (held ? !CrateSweep.HeldStackEnabled : !CrateSweep.EmptyHandEnabled) return;

            InventoryBase inv = __instance.Inventory;
            if (inv == null) return;

            Context ctx = new Context { HeldSomething = held };

            if (held)
            {
                // Ctrl + a pigment on the label box is vanilla's "draw the label"
                // gesture, and that one ignores Shift - so this press is not a put
                // at all. Leave it be rather than also emptying the player's
                // pigment into the crate.
                ctx.LabelGesture = __instance.Labelled
                    && blockSel != null && blockSel.SelectionBoxIndex == 1
                    && IsPigment(active.Itemstack);

                // An empty crate takes its kind from whatever goes in first, so
                // anything in hand matches it.
                ItemSlot filled = inv.FirstNonEmptySlot;
                ctx.HeldMatched = filled == null
                    || filled.Itemstack.Equals(byPlayer.Entity.World, active.Itemstack,
                                               GlobalConstants.IgnoredStackAttributes);
            }

            __state = ctx;
        }

        public static void Postfix(BlockEntityCrate __instance, IPlayer byPlayer, Context __state)
        {
            if (__state == null || __state.LabelGesture) return;

            // A press whose own put went nowhere - holding something this crate
            // does not take - is not an instruction to sweep the crate's kind out
            // of the player's bags instead.
            if (__state.HeldSomething && !__state.HeldMatched) return;

            int moved = CrateSweep.Sweep(__instance, byPlayer, out ItemStack kind);
            if (moved <= 0) return;

            CrateSweep.FinishSweep(__instance, byPlayer, kind, actorPlaysLocally: true);
            CrateSweep.Audit(__instance, byPlayer, moved, kind);
        }

        private static bool IsPigment(ItemStack stack)
        {
            JsonObject attributes = stack?.ItemAttributes;
            if (attributes == null) return false;

            // JsonObject's indexer never returns null for a missing key, so this
            // walk is safe once the attributes themselves are known to exist.
            return attributes["pigment"]["color"].Exists;
        }
    }

    // One extra line in the crate's interaction help, for the gesture a player
    // would otherwise have no way of guessing at.
    //
    // Only the empty-handed sweep needs one: the held-stack route extends
    // vanilla's Ctrl + Shift put, which already has its own "Add all items" line,
    // and that still describes what happens. RequireFreeHand keeps the two from
    // ever being shown side by side for the same key combination.
    public static class CrateInteractionHelpPatch
    {
        public static void Postfix(ref WorldInteraction[] __result)
        {
            if (!CrateSweep.EmptyHandEnabled || __result == null) return;

            __result = __result.Append(new WorldInteraction
            {
                ActionLangCode = "seraphsledger:blockhelp-crate-sweep",
                MouseButton = EnumMouseButton.Right,
                HotKeyCodes = new[] { "shift", "ctrl" },
                RequireFreeHand = true
            });
        }
    }
}
