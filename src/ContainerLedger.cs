using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // A copy of what is in every large container, kept where it outlives the mod
    // itself, and a /sl rescue command to put any of it back.
    //
    // Why this exists
    // ---------------
    // A large container holds more slots than the vanilla block entity defaults
    // to - a large trunk 100 against 16. That count lives in the *block*, and a
    // saved container names its block by code, so with this mod absent:
    //
    //   BlockEntityGenericTypedContainer.FromTreeAttributes
    //     -> InitInventory(world.GetBlock("seraphsledger:largetrunk-east"))  // null
    //     -> quantitySlots stays at the class default of 16
    //   InventoryGeneric.FromTreeAttributes
    //     -> SlotsFromTreeAttributes(tree, slots)   // loops slots.Length == 16
    //
    // The saved qslots count is only honoured when the slot array is null, so
    // slots 16 and up are never read - and the next chunk save writes the
    // container back without them. There is no error and no log line; the items
    // are simply gone. Loading a save once without this mod is enough, and every
    // chunk that loads in that session is trimmed.
    //
    // Nothing the engine does can be prevented from inside a mod that isn't
    // loaded. What can be done is to keep the contents somewhere the engine
    // leaves alone: the savegame's mod data is a plain dictionary of byte
    // arrays, loaded and written back untouched for mods that aren't present. So
    // the ledger records every large container there, spots the gap when the
    // chunk comes back, and holds what is missing until it is asked for it.
    //
    // It never writes to a container on its own. Detection only records; /sl
    // rescue is what restores, one container at a time, into empty slots only.
    public class ContainerLedger
    {
        private const string SnapshotKey = "seraphsledger:containersnapshot";
        private const string LossKey = "seraphsledger:containerlosses";
        private const string Domain = "seraphsledger";

        private const int SweepIntervalMs = 15000;

        // What BlockEntityGenericTypedContainer falls back to when it cannot
        // read the block's own slot count - the edge every trimmed container is
        // cut at.
        private const int VanillaFallbackSlots = 16;

        private readonly ICoreServerAPI sapi;
        private long sweepListener = -1;
        private int lastRecorded = -1;

        // Positions already set against the record in this run of the server.
        //
        // A container must be *compared before it is recorded*, or the record is
        // overwritten with the very contents the comparison was meant to catch.
        // The chunk-load event is the natural moment for that, but a block
        // entity is not always in the chunk by the time it fires - so the sweep
        // compares anything the event missed, and this is what keeps either one
        // from recording over a container that has not been looked at yet.
        private readonly HashSet<string> compared = new HashSet<string>();

        // position key -> { blockCode, inv }, the last known good contents.
        private TreeAttribute snapshots = new TreeAttribute();
        // position key -> { blockCode, detected, stacks }, what came back short.
        private TreeAttribute losses = new TreeAttribute();

        public ContainerLedger(ICoreServerAPI api)
        {
            sapi = api;

            Load();

            sapi.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
            sapi.Event.GameWorldSave += OnGameWorldSave;

            // The world save is the natural moment to record - it is the pass
            // that writes the chunks - but it is not the only one. A timer as
            // well means the record is kept whether or not that event reaches
            // us, and costs a walk of the loaded chunks once a minute.
            sweepListener = sapi.Event.RegisterGameTickListener(_ => Sweep("timer"), SweepIntervalMs);

            RegisterCommand();

            sapi.Logger.Notification(
                "[seraphsledger] container rescue armed: {0} container(s) on record, {1} awaiting rescue",
                snapshots.Count, losses.Count);
        }

        public void Dispose()
        {
            if (sapi == null) return;
            sapi.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
            sapi.Event.GameWorldSave -= OnGameWorldSave;
            if (sweepListener >= 0) sapi.Event.UnregisterGameTickListener(sweepListener);
            sweepListener = -1;
        }

        // --- the record ------------------------------------------------------

        private void Load()
        {
            snapshots = ReadTree(SnapshotKey);
            losses = ReadTree(LossKey);
        }

        private TreeAttribute ReadTree(string key)
        {
            try
            {
                byte[] data = sapi.WorldManager.SaveGame.GetData(key);
                if (data == null || data.Length == 0) return new TreeAttribute();
                return TreeAttribute.CreateFromBytes(data) as TreeAttribute ?? new TreeAttribute();
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[seraphsledger] could not read {0}, starting a fresh one: {1}", key, e);
                return new TreeAttribute();
            }
        }

        private void Save()
        {
            sapi.WorldManager.SaveGame.StoreData(SnapshotKey, snapshots.ToBytes());
            sapi.WorldManager.SaveGame.StoreData(LossKey, losses.ToBytes());
        }

        // Refresh the record of every large container currently in memory. Runs
        // on each world save, which is also the last thing the server does on the
        // way out, so the record matches what lands on disk.
        //
        // Detection has already run for these chunks (it runs the moment a chunk
        // arrives from disk), so overwriting freely here is safe: a container
        // that came back short is in `losses` before it is ever snapshotted
        // short.
        private void OnGameWorldSave()
        {
            Sweep("world save");
        }

        // Walk every large container in memory and write the record. `why` only
        // names the thing that asked, for the log.
        private int Sweep(string why)
        {
            int seen = 0;
            var tally = new Tally();
            try
            {
                foreach (IServerChunk chunk in sapi.WorldManager.AllLoadedChunks.Values.ToArray())
                {
                    if (chunk?.BlockEntities == null) continue;
                    foreach (BlockEntity be in chunk.BlockEntities.Values.ToArray())
                    {
                        InventoryBase inv = OursWithInventory(be);
                        if (inv == null) continue;

                        Examine(be, inv, tally);
                        Record(be, inv);
                        seen++;
                    }
                }
                Save();
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[seraphsledger] container ledger could not be written ({0}): {1}", why, e);
                return seen;
            }

            Announce(tally);

            // Only when the number moves, so a minute timer doesn't fill the log.
            if (seen != lastRecorded)
            {
                lastRecorded = seen;
                sapi.Logger.Notification(
                    "[seraphsledger] container rescue: {0} large container(s) in loaded chunks recorded ({1}), {2} on record in all",
                    seen, why, snapshots.Count);
            }
            return seen;
        }

        private void Record(BlockEntity be, InventoryBase inv)
        {
            string key = PosKey(be.Pos);

            // An empty container is worth nothing to a rescue, and the record is
            // carried in the savegame - so drop it rather than keep a page of
            // nothing.
            if (inv.Empty)
            {
                snapshots.RemoveAttribute(key);
                return;
            }

            var entry = new TreeAttribute();
            entry.SetString("blockCode", be.Block?.Code?.ToShortString() ?? "");

            var invTree = new TreeAttribute();
            inv.ToTreeAttributes(invTree);
            entry["inv"] = invTree;

            snapshots[key] = entry;
        }

        // --- spotting the gap ------------------------------------------------

        // Runs as a chunk arrives from disk: the one moment when a difference
        // from the record cannot be the player's own doing, since nobody has
        // been able to reach into the container since it was last written.
        private void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
        {
            if (chunks == null) return;

            var tally = new Tally();
            try
            {
                foreach (IWorldChunk chunk in chunks)
                {
                    if (chunk?.BlockEntities == null) continue;
                    foreach (BlockEntity be in chunk.BlockEntities.Values.ToArray())
                    {
                        InventoryBase inv = OursWithInventory(be);
                        if (inv != null) Examine(be, inv, tally);
                    }
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[seraphsledger] container ledger could not check a chunk: {0}", e);
                return;
            }

            if (tally.Nothing) return;

            Save();
            Announce(tally);
        }

        // What one pass found, for the one line it says about it afterwards.
        private class Tally
        {
            public int Containers;      // containers put back in full
            public int Stacks;          // stacks put back
            public int Pending;         // containers left for /sl rescue

            public bool Nothing => Containers == 0 && Stacks == 0 && Pending == 0;
        }

        private void Announce(Tally tally)
        {
            if (tally.Nothing) return;

            if (tally.Stacks > 0)
            {
                string msg = $"[Seraph's Ledger] Put {tally.Stacks} stack(s) back into "
                    + $"{tally.Containers} large container(s) that a load without this mod had trimmed.";
                sapi.Logger.Notification(msg);
                sapi.BroadcastMessageToAllGroups(msg, EnumChatType.Notification);
            }

            if (tally.Pending > 0)
            {
                string msg = $"[Seraph's Ledger] {tally.Pending} large container(s) are missing items that "
                    + "could not be put back on their own. /sl rescue";
                sapi.Logger.Notification(msg);
                sapi.BroadcastMessageToAllGroups(msg, EnumChatType.Notification);
            }
        }

        // Set a container against the record, once per position per run of the
        // server.
        private void Examine(BlockEntity be, InventoryBase inv, Tally tally)
        {
            if (!compared.Add(PosKey(be.Pos))) return;
            Detect(be, inv, tally);
        }

        // Set this container against the record and act on the difference: put
        // the items straight back when the shortfall is the engine's own doing,
        // and otherwise hold them for /sl rescue to decide.
        private void Detect(BlockEntity be, InventoryBase inv, Tally tally)
        {
            string key = PosKey(be.Pos);

            // Already caught, and not yet rescued. The record behind it is the
            // older and therefore fuller one, so leave it alone.
            if (losses.HasAttribute(key)) return;

            var entry = snapshots.GetTreeAttribute(key) as TreeAttribute;
            ITreeAttribute invTree = entry?.GetTreeAttribute("inv");
            if (invTree == null) return;

            List<ItemStack> recorded = StacksOf(invTree);
            List<ItemStack> missing = Missing(recorded, inv);
            bool trimmed = Trimmed(invTree, inv);

            sapi.Logger.VerboseDebug(
                "[seraphsledger] container {0} ({1}): {2} stack(s) on record, {3} in the container, {4} missing, trimmed={5}",
                key, be.Block?.Code, recorded.Count, inv.Count(s => s != null && !s.Empty), missing.Count, trimmed);

            if (missing.Count == 0) return;

            List<ItemStack> left = missing;
            if (trimmed)
            {
                int put = PutBack(inv, missing, out left);
                if (put > 0)
                {
                    be.MarkDirty(true);
                    tally.Stacks += put;
                    if (left.Count == 0) tally.Containers++;
                }
            }
            if (left.Count == 0) return;

            var loss = new TreeAttribute();
            loss.SetString("blockCode", be.Block?.Code?.ToShortString() ?? entry.GetString("blockCode"));
            loss.SetDouble("detectedTotalHours", sapi.World.Calendar.TotalHours);
            loss["stacks"] = StackTree(left);
            losses[key] = loss;
            tally.Pending++;
        }

        // Is this the engine's fingerprint rather than the player's hand?
        //
        // When a container is loaded without this mod the block entity cannot
        // read its slot count and falls back to the vanilla 16, so it reads the
        // first 16 slots and drops the rest. The mark that leaves is exact:
        // nothing survives at or past slot 16, while the record had something
        // there. A player emptying a container by hand leaves no such edge, so
        // anything else is held back for /sl rescue to decide rather than put
        // back on its own.
        private static bool Trimmed(ITreeAttribute invTree, InventoryBase inv)
        {
            if (inv.Count <= VanillaFallbackSlots) return false;

            ITreeAttribute slots = invTree.GetTreeAttribute("slots");
            if (slots == null) return false;

            bool recordedPastEdge = false;
            foreach (var kv in slots)
            {
                if (!(kv.Value is ItemstackAttribute attr) || attr.value == null) continue;
                if (int.TryParse(kv.Key, out int i) && i >= VanillaFallbackSlots)
                {
                    recordedPastEdge = true;
                    break;
                }
            }
            if (!recordedPastEdge) return false;

            for (int i = VanillaFallbackSlots; i < inv.Count; i++)
            {
                if (inv[i] != null && !inv[i].Empty) return false;
            }
            return true;
        }

        // Fills empty slots only, so a restore can add to a container but never
        // overwrite what is in it now. Returns how many stacks went in; whatever
        // did not fit comes back in `left`.
        private static int PutBack(InventoryBase inv, List<ItemStack> stacks, out List<ItemStack> left)
        {
            left = new List<ItemStack>();
            int put = 0;

            foreach (ItemStack stack in stacks)
            {
                ItemSlot free = inv.FirstOrDefault(s => s != null && s.Empty);
                if (free == null)
                {
                    left.Add(stack);
                    continue;
                }
                // A copy: the stack is still held in the record until the entry
                // is rewritten, and the two must not be the same object.
                free.Itemstack = stack.Clone();
                free.MarkDirty();
                put++;
            }
            return put;
        }

        // What the record holds that the container no longer does.
        //
        // Matched by contents rather than by slot: sorting a container (this mod
        // adds the buttons that do it) moves every stack somewhere else without
        // losing a thing, and a slot-by-slot comparison would call all of that a
        // loss. Each recorded stack is set against what is still in the
        // container of that same item, and only the shortfall is reported.
        private List<ItemStack> Missing(List<ItemStack> recorded, InventoryBase inv)
        {
            var live = new Dictionary<string, int>();
            foreach (ItemSlot slot in inv)
            {
                ItemStack stack = slot?.Itemstack;
                if (stack?.Collectible?.Code == null) continue;
                string code = stack.Collectible.Code.ToString();
                live.TryGetValue(code, out int had);
                live[code] = had + stack.StackSize;
            }

            var missing = new List<ItemStack>();
            foreach (ItemStack stack in recorded)
            {
                if (stack?.Collectible?.Code == null) continue;

                string code = stack.Collectible.Code.ToString();
                live.TryGetValue(code, out int have);

                if (have >= stack.StackSize)
                {
                    live[code] = have - stack.StackSize;
                    continue;
                }

                ItemStack gone = stack.Clone();
                gone.StackSize = stack.StackSize - have;
                live[code] = 0;
                missing.Add(gone);
            }
            return missing;
        }

        // --- putting it back -------------------------------------------------

        private void RegisterCommand()
        {
            sapi.ChatCommands.Create("sl")
                .WithDescription("The Seraph's Ledger")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("rescue")
                    .WithDescription("List large containers that came back missing items, and put the items back")
                    .WithArgs(sapi.ChatCommands.Parsers.OptionalWord("number"))
                    .HandleWith(OnRescue)
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("What the container rescue has on record")
                    .HandleWith(OnStatus)
                .EndSubCommand()
                .BeginSubCommand("forget")
                    .WithDescription("Drop a rescue entry without restoring it")
                    .WithArgs(sapi.ChatCommands.Parsers.Word("number"))
                    .HandleWith(OnForget)
                .EndSubCommand();
        }

        // Entries in a fixed order, so the numbers the player reads stay put
        // between the listing and the restore.
        private List<string> Entries()
        {
            return losses.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        private TextCommandResult OnRescue(TextCommandCallingArgs args)
        {
            List<string> keys = Entries();
            if (keys.Count == 0) return TextCommandResult.Success("No container is missing anything on record.");

            string arg = args[0] as string;
            if (string.IsNullOrEmpty(arg)) return TextCommandResult.Success(List(keys));

            if (!int.TryParse(arg, out int n) || n < 1 || n > keys.Count)
            {
                return TextCommandResult.Error($"Pick a number from 1 to {keys.Count}, or run /sl rescue on its own to see them.");
            }
            return Restore(keys[n - 1]);
        }

        private TextCommandResult OnStatus(TextCommandCallingArgs args)
        {
            int seen = Sweep("status");
            int stacks = snapshots
                .Select(kv => StacksOf((kv.Value as TreeAttribute)?.GetTreeAttribute("inv")).Count)
                .Sum();

            return TextCommandResult.Success(
                $"Container rescue: {snapshots.Count} container(s) on record holding {stacks} stack(s); "
                + $"{seen} large container(s) in the chunks loaded right now; {losses.Count} awaiting rescue.");
        }

        private TextCommandResult OnForget(TextCommandCallingArgs args)
        {
            List<string> keys = Entries();
            if (!int.TryParse(args[0] as string, out int n) || n < 1 || n > keys.Count)
            {
                return TextCommandResult.Error($"Pick a number from 1 to {keys.Count}.");
            }

            string key = keys[n - 1];
            losses.RemoveAttribute(key);
            Save();
            return TextCommandResult.Success($"Forgot the rescue for {key}. Its items are no longer recoverable.");
        }

        private string List(List<string> keys)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{keys.Count} container(s) came back missing items:");

            for (int i = 0; i < keys.Count; i++)
            {
                ITreeAttribute loss = losses.GetTreeAttribute(keys[i]);
                List<ItemStack> stacks = StacksOf(loss?.GetTreeAttribute("stacks"));
                int items = stacks.Sum(s => s.StackSize);
                string name = ShortName(loss?.GetString("blockCode"));

                sb.AppendLine($"  {i + 1}. {name} at {keys[i]} - {stacks.Count} stack(s), {items:N0} item(s)");
                foreach (var g in stacks.GroupBy(s => s.GetName()).OrderByDescending(g => g.Sum(s => s.StackSize)).Take(6))
                {
                    sb.AppendLine($"       {g.Key} x{g.Sum(s => s.StackSize):N0}");
                }
            }
            sb.Append("Stand near one and run /sl rescue <number> to put its items back, or /sl forget <number> to drop it.");
            return sb.ToString();
        }

        // Fills empty slots only. A stack that is in the container now is never
        // touched, so a rescue can add to a container but never overwrite it;
        // whatever doesn't fit stays on the books for a second run after the
        // container has been cleared out.
        private TextCommandResult Restore(string key)
        {
            if (!TryParsePos(key, out BlockPos pos)) return TextCommandResult.Error("That entry has an unreadable position.");

            if (!(sapi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntity be) || OursWithInventory(be) == null)
            {
                return TextCommandResult.Error(
                    $"No large container at {key} right now - go and stand near it so its chunk is loaded, then try again.");
            }

            InventoryBase inv = OursWithInventory(be);
            ITreeAttribute loss = losses.GetTreeAttribute(key);
            List<ItemStack> stacks = StacksOf(loss?.GetTreeAttribute("stacks"));
            if (stacks.Count == 0)
            {
                losses.RemoveAttribute(key);
                Save();
                return TextCommandResult.Success("That entry held nothing. Dropped it.");
            }

            int restored = PutBack(inv, stacks, out List<ItemStack> left);
            be.MarkDirty(true);

            if (left.Count == 0)
            {
                losses.RemoveAttribute(key);
            }
            else
            {
                var remainder = (TreeAttribute)loss;
                remainder["stacks"] = StackTree(left);
                losses[key] = remainder;
            }
            Save();

            string msg = $"Put {restored} stack(s) back into the {ShortName(loss?.GetString("blockCode"))} at {key}.";
            if (left.Count > 0) msg += $" {left.Count} stack(s) did not fit - clear some space and run it again.";
            return TextCommandResult.Success(msg);
        }

        // --- odds and ends ---------------------------------------------------

        // The inventory of a container of ours, or null for anything else. Our
        // large containers all run on vanilla block entity classes, so the block
        // code is what identifies them.
        private static InventoryBase OursWithInventory(BlockEntity be)
        {
            if (!(be is BlockEntityContainer container)) return null;
            if (be.Block?.Code?.Domain != Domain) return null;
            return container.Inventory;
        }

        private List<ItemStack> StacksOf(ITreeAttribute tree)
        {
            var stacks = new List<ItemStack>();
            ITreeAttribute slots = tree?.GetTreeAttribute("slots");
            if (slots == null) return stacks;

            foreach (var kv in slots)
            {
                if (!(kv.Value is ItemstackAttribute attr) || attr.value == null) continue;
                ItemStack stack = attr.value;
                stack.ResolveBlockOrItem(sapi.World);
                if (stack.Collectible == null) continue;   // an item from a mod that has since gone
                stacks.Add(stack);
            }
            return stacks;
        }

        private static TreeAttribute StackTree(List<ItemStack> stacks)
        {
            var slots = new TreeAttribute();
            for (int i = 0; i < stacks.Count; i++) slots.SetItemstack(i.ToString(), stacks[i]);

            var tree = new TreeAttribute();
            tree["slots"] = slots;
            return tree;
        }

        private static string PosKey(BlockPos pos)
        {
            return pos.X + "," + pos.Y + "," + pos.Z;
        }

        private static bool TryParsePos(string key, out BlockPos pos)
        {
            pos = null;
            string[] parts = key.Split(',');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)
                || !int.TryParse(parts[2], out int z)) return false;
            pos = new BlockPos(x, y, z);
            return true;
        }

        private static string ShortName(string blockCode)
        {
            if (string.IsNullOrEmpty(blockCode)) return "container";
            string path = new AssetLocation(blockCode).Path;
            int dash = path.IndexOf('-');
            return dash > 0 ? path.Substring(0, dash) : path;
        }
    }
}
