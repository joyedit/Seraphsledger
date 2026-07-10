using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace SeraphsLedger
{
    // Sent client -> server when the player clicks the "Sort" button in their
    // backpack inventory. Empty messages still need a ProtoContract so
    // protobuf-net can (de)serialize them across the network channel.
    [ProtoContract]
    public class SortBackpackPacket { }

    // Sent client -> server when the player clicks "Trash" to destroy whatever
    // stack is currently held on the mouse cursor.
    [ProtoContract]
    public class TrashCursorPacket { }

    public class SeraphsLedgerModSystem : ModSystem
    {
        public const string NetworkChannelName = "seraphsledger";

        // The client GUI patch lives in a static Harmony method and needs a way
        // to fire packets; stash the channel here so it can reach it.
        internal static IClientNetworkChannel ClientChannel;

        private const string HarmonyId = "seraphsledger.ctrlmoveall";
        private Harmony harmony;
        private ICoreServerAPI sapi;
        private ICoreClientAPI capi;
        private EnemyDropDespawn enemyDropDespawn;
        private StepCountdown stepCountdown;
        private StepCountdownHud stepCountdownHud;
        private GuiDialogFeatureConfig configDialog;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            // Registered identically on both sides so the message type ids match.
            api.Network.RegisterChannel(NetworkChannelName)
                .RegisterMessageType<SortBackpackPacket>()
                .RegisterMessageType<TrashCursorPacket>();
        }

        // The large-container recipes are plain grid-recipe JSON, auto-loaded by the
        // survival mod's RecipeLoader. Rather than race that loader to strip the
        // assets (mod load order isn't guaranteed), wait until every mod's recipes
        // are registered and disable ours. AssetsFinalize runs after all
        // AssetsLoaded passes, so the recipes are guaranteed present here.
        //
        // We set Enabled = false rather than removing them from World.GridRecipes:
        // the crafting matcher (InventoryCraftingGrid.FindMatchingRecipe) searches a
        // precomputed ingredient index, not the GridRecipes list, so list removal
        // wouldn't take effect - but that index holds the same recipe objects and
        // the matcher live-checks `Enabled`, which is exactly the engine's own
        // disable flag (what `enabled: false` in recipe JSON sets). The blocks
        // themselves stay registered, so already-placed large containers in a save
        // keep working - they just can't be crafted while this is off.
        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            var cfg = SeraphsLedgerConfig.Load(api);
            if (cfg.LargeContainers) return;

            var recipes = api.World?.GridRecipes;
            if (recipes == null) return;

            foreach (var r in recipes)
            {
                AssetLocation code = r?.Output?.Code;
                if (code != null
                    && code.Domain == "seraphsledger"
                    && code.Path.StartsWith("large", StringComparison.Ordinal))
                {
                    r.Enabled = false;
                }
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            capi = api;
            ClientChannel = api.Network.GetChannel(NetworkChannelName);

            // The settings dialog is always reachable, regardless of which features
            // are on, so the player can turn things back on.
            api.Input.RegisterHotKey("seraphsledgerconfig", "Seraph's Ledger settings", GlKeys.L,
                HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("seraphsledgerconfig", _ => ToggleConfigGui());
            api.ChatCommands.Create("slconfig")
                .WithDescription("Open The Seraph's Ledger settings (toggle features on/off)")
                .HandleWith(_ => { ToggleConfigGui(); return TextCommandResult.Success(); });

            var cfg = SeraphsLedgerConfig.Load(api);

            // Each feature is a load-time Harmony patch, so apply only the enabled
            // ones; toggling one off in the settings dialog takes effect on restart.
            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                harmony = new Harmony(HarmonyId);

                // Ctrl + left-click move-all (patched manually, not via PatchAll, so
                // it can be gated independently of the other patches).
                if (cfg.CtrlClickMoveAll)
                {
                    var onMouseDown = AccessTools.Method(typeof(GuiElementItemSlotGridBase), "OnMouseDownOnElement");
                    if (onMouseDown != null)
                    {
                        harmony.Patch(onMouseDown, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(OnMouseDownCtrlMoveAllPatch), nameof(OnMouseDownCtrlMoveAllPatch.Prefix))));
                    }
                }

                // GuiDialogInventory lives in an internal-ish namespace inside
                // VintagestoryLib; resolve it by name so we don't depend on it
                // at compile time.
                var invType = AccessTools.TypeByName("GuiDialogInventory");

                if (cfg.SortTrashButtons)
                {
                    var compose = AccessTools.Method(invType, "ComposeSurvivalInvDialog");
                    if (compose != null)
                    {
                        harmony.Patch(compose, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(SurvivalInvButtonsPatch), nameof(SurvivalInvButtonsPatch.Postfix))));
                    }
                }

                // Auto-focus the creative search box when the dialog opens so you
                // can type a search term immediately without clicking it first.
                if (cfg.CreativeSearchAutofocus)
                {
                    var onOpened = AccessTools.Method(invType, "OnGuiOpened");
                    if (onOpened != null)
                    {
                        harmony.Patch(onOpened, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(CreativeSearchAutofocusPatch), nameof(CreativeSearchAutofocusPatch.Postfix))));
                    }
                }

                // Silence the chattering voice sounds of the player and traders by
                // skipping EntityTalkUtil's sound emitter. Patched here (rather than
                // overriding the voice oggs with silent assets) so it can be toggled.
                // The 5-arg PlaySound overload is the single point every talk sound
                // funnels through; the 3-arg overload just forwards into it.
                if (cfg.SilencedVoices)
                {
                    var playSound = AccessTools.Method(typeof(EntityTalkUtil), "PlaySound",
                        new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) });
                    if (playSound != null)
                    {
                        harmony.Patch(playSound, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(VoiceSilencePatch), nameof(VoiceSilencePatch.Prefix))));
                    }
                }

                // Distance-challenge: block jumping while the budget is exhausted so
                // the player can't hop out of the walkspeed lock. Patched here (in
                // the singleplayer client process, which also hosts the integrated
                // server physics) so both prediction and authority honour it.
                if (cfg.StepCountdown)
                {
                    var doApply = AccessTools.Method(
                        typeof(Vintagestory.API.Common.Entities.PModuleOnGround), "DoApply");
                    if (doApply != null)
                    {
                        harmony.Patch(doApply, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(StepCountdownJumpBlockPatch), nameof(StepCountdownJumpBlockPatch.Prefix))));
                    }
                }
            }

            // Distance-challenge HUD. The budget itself is tracked server-side; this
            // just renders the value the server syncs onto the player entity.
            if (cfg.StepCountdown)
            {
                stepCountdownHud = new StepCountdownHud(api);
                stepCountdownHud.TryOpen();
            }

        }

        private bool ToggleConfigGui()
        {
            if (configDialog == null) configDialog = new GuiDialogFeatureConfig(capi);
            if (configDialog.IsOpened()) configDialog.TryClose();
            else configDialog.TryOpen();
            return true;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            sapi = api;
            // Handlers stay registered regardless of the Sort/Trash toggle (they're
            // dormant without the client-side buttons), keeping the network channel
            // symmetric across both sides.
            api.Network.GetChannel(NetworkChannelName)
                .SetMessageHandler<SortBackpackPacket>(OnSortBackpack)
                .SetMessageHandler<TrashCursorPacket>(OnTrashCursor);

            var cfg = SeraphsLedgerConfig.Load(api);

            // Hostile creatures vanish 2s after death, dropping their harvest loot.
            if (cfg.EnemyFastDespawn)
            {
                enemyDropDespawn = new EnemyDropDespawn(api);
            }

            // Distance-challenge: track per-player travel budget and lock movement
            // at 0. Registers the /steps command.
            if (cfg.StepCountdown)
            {
                stepCountdown = new StepCountdown(api, cfg.StepCountdownStartMeters);
            }
        }

        private void OnSortBackpack(IServerPlayer player, SortBackpackPacket packet)
        {
            var inv = player?.InventoryManager?.GetOwnInventory(GlobalConstants.backpackInvClassName) as InventoryBase;
            if (inv == null) return;
            BackpackSorter.Sort(inv, player.Entity.World);
        }

        private void OnTrashCursor(IServerPlayer player, TrashCursorPacket packet)
        {
            var mouseInv = player?.InventoryManager?.GetOwnInventory(GlobalConstants.mousecursorInvClassName) as InventoryBase;
            if (mouseInv == null) return;

            for (int i = 0; i < mouseInv.Count; i++)
            {
                ItemSlot slot = mouseInv[i];
                if (slot == null || slot.Empty) continue;
                slot.Itemstack = null;
                mouseInv.MarkSlotDirty(i);
            }
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            ClientChannel = null;
            configDialog = null;
            enemyDropDespawn?.Dispose();
            enemyDropDespawn = null;
            stepCountdown?.Dispose();
            stepCountdown = null;
            stepCountdownHud?.TryClose();
            stepCountdownHud?.Dispose();
            stepCountdownHud = null;
            capi = null;
            sapi = null;
        }
    }

    // Server-authoritative sort of the player's backpack (bag) contents.
    //
    // The first 4 slots of the backpack inventory are the worn-bag slots; only
    // those are sent over the network (CountForNetworkPacket == 4). The actual
    // storage slots (ItemSlotBagContent, slot id >= 4) are virtual views whose
    // contents live inside the bag itemstacks' attributes. So we rearrange the
    // content slots, persist each back into its bag via OnItemSlotModified, then
    // mark the 4 bag slots dirty so the updated bag stacks are re-sent and the
    // client rebuilds its content slots from them.
    public static class BackpackSorter
    {
        public static void Sort(InventoryBase inv, IWorldAccessor world)
        {
            // Collect the content slots, grouped by their accepted storage type.
            // Sorting within a group is always valid because every item already
            // sitting in those slots is, by definition, accepted there - so no
            // item can ever get stranded or lost.
            var groups = new Dictionary<int, List<ItemSlot>>();
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i] is ItemSlotBagContent content)
                {
                    int key = (int)content.StorageType;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<ItemSlot>();
                        groups[key] = list;
                    }
                    list.Add(content);
                }
            }
            if (groups.Count == 0) return;

            foreach (var group in groups.Values)
            {
                SortGroup(group, world);
            }

            // Persist every content slot back into its bag itemstack.
            for (int i = 4; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                if (slot != null) inv.OnItemSlotModified(slot);
            }

            // Re-send the worn-bag stacks (now carrying sorted contents) to the client.
            int bagSlots = Math.Min(4, inv.Count);
            for (int i = 0; i < bagSlots; i++) inv.MarkSlotDirty(i);
        }

        private static void SortGroup(List<ItemSlot> slots, IWorldAccessor world)
        {
            // Pull out and clear all stacks in this group.
            var stacks = new List<ItemStack>();
            foreach (ItemSlot slot in slots)
            {
                if (!slot.Empty) stacks.Add(slot.Itemstack);
                slot.Itemstack = null;
            }
            if (stacks.Count == 0) return;

            // Consolidate partial stacks of identical items.
            var merged = new List<ItemStack>();
            foreach (ItemStack stack in stacks)
            {
                ItemStack remaining = stack;
                foreach (ItemStack target in merged)
                {
                    if (remaining.StackSize <= 0) break;
                    if (target.Collectible == null) continue;
                    int max = target.Collectible.MaxStackSize;
                    if (target.StackSize >= max) continue;
                    if (!target.Equals(world, remaining)) continue;

                    int moved = Math.Min(max - target.StackSize, remaining.StackSize);
                    target.StackSize += moved;
                    remaining.StackSize -= moved;
                }
                if (remaining.StackSize > 0) merged.Add(remaining);
            }

            // Sort by item code, then by larger stacks first.
            merged.Sort((a, b) =>
            {
                int byCode = string.CompareOrdinal(
                    a.Collectible?.Code?.ToString() ?? "",
                    b.Collectible?.Code?.ToString() ?? "");
                return byCode != 0 ? byCode : b.StackSize - a.StackSize;
            });

            // Drop them back into the group's slots in order.
            for (int i = 0; i < merged.Count && i < slots.Count; i++)
            {
                slots[i].Itemstack = merged[i];
            }
        }
    }

    // Adds "Sort" and "Trash" buttons to the survival player-inventory dialog.
    //
    // The buttons are appended after the engine has already composed the dialog.
    // AddSmallButton is a no-op once a composer is Composed, so we flip the public
    // Composed flag off, add our buttons, and recompose. The dialog's outer bounds
    // are fixed (forked from the 7-row backpack grid) and don't grow from the
    // extra children, so the buttons sit in the empty space under the crafting
    // output without resizing or shifting anything.
    public static class SurvivalInvButtonsPatch
    {
        public static void Postfix(object __instance)
        {
            var field = AccessTools.Field(__instance.GetType(), "survivalInvDialog");
            var composer = field?.GetValue(__instance) as GuiComposer;
            if (composer == null) return;

            // Already added to this composer (defensive against repeat calls).
            if (composer.GetButton("seraphsledgerSortBtn") != null) return;

            ElementBounds craft = composer.GetSlotGrid("craftinggrid")?.Bounds;
            ElementBounds output = composer.GetSlotGrid("outputslot")?.Bounds;
            if (craft == null || output == null) return;

            // Make sure our trash-can icon is registered (lazily, the first time
            // the inventory is composed - assets are loaded long before then).
            var icons = composer.Api.Gui.Icons;
            if (!icons.CustomIcons.ContainsKey(TrashIcon))
            {
                icons.CustomIcons[TrashIcon] =
                    icons.SvgIconSource(new AssetLocation("seraphsledger:textures/icons/trash.svg"));
            }

            double x = craft.fixedX;
            double width = craft.fixedWidth;
            double y = output.fixedY + output.fixedHeight + 14.0;

            const double trashSize = 30.0;
            ElementBounds sortBounds = ElementBounds.Fixed(x, y, width, 20.0);
            ElementBounds trashBounds = ElementBounds.Fixed(
                x + (width - trashSize) / 2.0, y + 26.0, trashSize, trashSize);

            composer.Composed = false;
            composer
                .AddSmallButton("Sort", OnSortClicked, sortBounds, EnumButtonStyle.Normal, "seraphsledgerSortBtn")
                .AddIconButton(TrashIcon, OnTrashClicked, trashBounds, "seraphsledgerTrashBtn")
                .Compose(focusFirstElement: false);
        }

        private const string TrashIcon = "seraphsledgertrash";

        private static bool OnSortClicked()
        {
            SeraphsLedgerModSystem.ClientChannel?.SendPacket(new SortBackpackPacket());
            return true;
        }

        private static void OnTrashClicked(bool on)
        {
            SeraphsLedgerModSystem.ClientChannel?.SendPacket(new TrashCursorPacket());
        }
    }

    // Focuses the creative inventory's search box the moment the dialog opens, so
    // typing goes straight into the filter instead of requiring a click first.
    //
    // This mirrors what the built-in Ctrl+F "creativesearch" hotkey does
    // (FocusElement on the searchbox's TabIndex). OnGuiOpened recomposes the
    // dialog before this postfix runs, so the searchbox element already exists.
    // Side effect (inherent to a focused text field): the E key now types into
    // the search instead of closing the dialog - use Escape or the X to close.
    public static class CreativeSearchAutofocusPatch
    {
        public static void Postfix(object __instance)
        {
            var type = __instance.GetType();

            var capi = AccessTools.Field(type, "capi")?.GetValue(__instance) as ICoreClientAPI;
            if (capi?.World?.Player?.WorldData?.CurrentGameMode != EnumGameMode.Creative) return;

            var composerField = AccessTools.Field(type, "creativeInvDialog");

            // Defer focus to the next frame. Focusing during OnGuiOpened doesn't
            // stick: TryOpen calls TriggerDialogOpened right after this, which
            // resets element focus. The built-in Ctrl+F handler works precisely
            // because it focuses *after* TryOpen has fully returned - so we match
            // that timing with a one-shot 0ms callback.
            capi.Event.RegisterCallback(dt =>
            {
                if (capi.World?.Player?.WorldData?.CurrentGameMode != EnumGameMode.Creative) return;
                var composer = composerField?.GetValue(__instance) as GuiComposer;
                var searchbox = composer?.GetTextInput("searchbox");
                if (searchbox == null) return;
                composer.FocusElement(searchbox.TabIndex);
            }, 0);
        }
    }

    // Silences entity voice chatter by short-circuiting EntityTalkUtil's sound
    // emitter. Returning false skips the original method entirely, so no voice
    // sound is ever loaded or started; talk scheduling (letters/chord delays) is
    // unaffected, it just plays into silence.
    public static class VoiceSilencePatch
    {
        public static bool Prefix() => false;
    }

    // Ctrl + left-click on any slot in an inventory dialog dumps every non-empty
    // slot in that grid's inventory into the open container(s) using the engine's
    // own shift+click transfer pipeline (TryTransferAway via ActivateSlot).
    //
    // Patched at OnMouseDownOnElement rather than SlotClick because the outer
    // method reads inventory[keyAtIndex] after the click; player backpack
    // inventories (InventoryPlayerBackpacks) shrink when a worn bag is removed,
    // and its indexer returns null past Count -> NPE on cleanup. By patching the
    // outer method and short-circuiting, we skip that cleanup entirely.
    //
    // Patched manually (not via a [HarmonyPatch] attribute + PatchAll) so it can be
    // gated by the CtrlClickMoveAll config switch independently of the others.
    public static class OnMouseDownCtrlMoveAllPatch
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
            if (args.Button != EnumMouseButton.Left) return true;

            bool ctrl  = api.Input.KeyboardKeyState[3];
            bool shift = api.Input.KeyboardKeyState[1] || api.Input.KeyboardKeyState[2];
            bool alt   = api.Input.KeyboardKeyState[5];
            if (!ctrl || shift || alt) return true;

            if (!__instance.Bounds.ParentBounds.PointInside(args.X, args.Y)) return true;

            // Make sure the click actually landed on a rendered slot.
            bool hitSlot = false;
            var bounds = __instance.SlotBounds;
            var rendered = __instance.renderedSlots;
            int n = Math.Min(bounds?.Length ?? 0, rendered?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                if (bounds[i].PointInside(args.X, args.Y)) { hitSlot = true; break; }
            }
            if (!hitSlot) return true;

            IInventory inv = InventoryField?.GetValue(__instance) as IInventory;
            if (inv == null) return true;

            // Only act when at least one non-player inventory (chest/trunk/etc.)
            // is open, so this is a no-op in the bare E-key player inventory.
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

            var sendHandler = SendPacketHandlerField?.GetValue(__instance) as Action<object>;

            // Snapshot slot references up-front; slot IDs may shift if a worn bag
            // is removed mid-iteration, but the slot objects themselves remain
            // valid for emptiness checks.
            List<ItemSlot> targets = new List<ItemSlot>();
            foreach (ItemSlot s in inv)
            {
                if (s == null || s.Empty) continue;
                if (s is ItemSlotBackpack) continue; // don't pull bags off the player
                targets.Add(s);
            }

            foreach (var sslot in targets)
            {
                if (sslot.Empty) continue;
                int sid = inv.GetSlotId(sslot);
                if (sid < 0) continue;

                var op = new ItemStackMoveOperation(
                    api.World, EnumMouseButton.Left,
                    EnumModifierKey.SHIFT, EnumMergePriority.AutoMerge);
                op.ActingPlayer = api.World.Player;
                op.RequestedQuantity = sslot.StackSize;

                object packets = inv.ActivateSlot(sid, sslot, ref op);
                if (packets == null || sendHandler == null) continue;

                if (packets is object[] arr)
                {
                    foreach (var p in arr) sendHandler(p);
                }
                else
                {
                    sendHandler(packets);
                }
            }

            args.Handled = true;
            return false;
        }
    }
}
