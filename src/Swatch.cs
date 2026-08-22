using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    /// <summary>Client asks for the current selection and the materials in it.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwInfoRequest { }

    /// <summary>
    /// Server's answer: what the selection covers and which material tokens in it
    /// have at least one valid replacement. Names/Counts are parallel lists.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwInfoResponse
    {
        public bool HasSelection;
        public string SelectionDesc;
        public List<string> Sources = new();
        public List<int> Counts = new();
    }

    /// <summary>Client picked a source material; asks what it can become.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwTargetsRequest
    {
        public string From;
    }

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwTargetsResponse
    {
        public string From;
        public List<string> Targets = new();
        public List<int> Counts = new();
    }

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwApplyRequest
    {
        public string From;
        public string To;
    }

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwUndoRequest { }

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwClearRequest { }

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SwStatus
    {
        public bool Success;
        public string Message;
    }

    /// <summary>
    /// Mark two corners, pick a material, swap it across the selection. Every
    /// swap is pushed onto a per-player undo stack so a look can be tried and
    /// dropped without leaving a mess behind.
    /// </summary>
    public class SwatchModSystem : ModSystem
    {
        ICoreServerAPI sapi;
        IServerNetworkChannel serverChannel;
        SwapEngine engine;

        ICoreClientAPI capi;
        IClientNetworkChannel clientChannel;
        GuiDialogSwatch dialog;

        class SwapRecord
        {
            public string From;
            public string To;
            // every block the swap replaced: where, what was there, what we put
            public List<(BlockPos pos, int prevId, int newId)> Blocks = new();
            // chiselled blocks keep their carving; only the material list changes
            public List<(BlockPos pos, int[] prevMaterials)> Micro = new();
        }

        readonly Dictionary<string, List<BlockPos>> marks = new();
        readonly Dictionary<string, Analysis> analyses = new();
        readonly Dictionary<string, List<SwapRecord>> undoStacks = new();

        // highlight slots are shared across mods for a given player - these sit
        // clear of Brickwright's 9451-9453
        const int HighlightSlot = 9471;
        const int MaxUndo = 25;
        const int MaxHeight = 256;
        const long MaxVolume = 512_000;
        // a swap over a big selection can record hundreds of thousands of
        // positions, so the history is bounded by what it holds, not just by
        // how many swaps are in it
        const long MaxUndoBlocks = 1_500_000;

        // Its own channel rather than the bundle's: the message ids stay
        // independent of what the main ModSystem registers.
        public const string ChannelName = "seraphsledgerswatch";

        // Settled at load time: the feature toggle, and the hand-off to the
        // standalone Swatch mod described in Start().
        bool enabled;

        public override void Start(ICoreAPI api)
        {
            // Registered on both sides whatever the toggle says, so the message
            // type ids line up even where a server and a client disagree about
            // the setting.
            api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<SwInfoRequest>()
                .RegisterMessageType<SwInfoResponse>()
                .RegisterMessageType<SwTargetsRequest>()
                .RegisterMessageType<SwTargetsResponse>()
                .RegisterMessageType<SwApplyRequest>()
                .RegisterMessageType<SwUndoRequest>()
                .RegisterMessageType<SwClearRequest>()
                .RegisterMessageType<SwStatus>();

            // The same tool also ships as the standalone Swatch mod. It claims
            // the same /sw command and hotkey, and registering either twice
            // fails, so whenever it's installed we let it own the feature
            // regardless of our own toggle - a player who installed it asked
            // for it explicitly. (Keyed off the modid, which is the check that
            // silently never fired for ice cellars in 1.15.1; "swatch" is this
            // author's own id and matches the standalone mod's modinfo.)
            var cfg = SeraphsLedgerConfig.Load(api);
            if (cfg.MaterialSwatch && api.ModLoader.IsModEnabled("swatch"))
            {
                api.Logger.Notification(
                    "[seraphsledger] standalone Swatch mod detected, leaving the material swatch tool to it");
                return;
            }
            enabled = cfg.MaterialSwatch;
        }

        // ------------------------------------------------------------------
        // Server
        // ------------------------------------------------------------------

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            engine = new SwapEngine(api);

            // Handlers go on whatever the toggle says - they're inert without a
            // client sending to them, and a client whose own copy of the setting
            // disagrees gets a spoken refusal rather than an unhandled packet.
            serverChannel = api.Network.GetChannel(ChannelName)
                .SetMessageHandler<SwInfoRequest>(OnInfoRequest)
                .SetMessageHandler<SwTargetsRequest>(OnTargetsRequest)
                .SetMessageHandler<SwApplyRequest>(OnApplyRequest)
                .SetMessageHandler<SwUndoRequest>(OnUndoRequest)
                .SetMessageHandler<SwClearRequest>(OnClearRequest);

            if (!enabled) return;

            var parsers = api.ChatCommands.Parsers;
            api.ChatCommands.Create("sw")
                .WithDescription(Lang.Get("seraphsledger:swatch-command-desc"))
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("mark")
                    .WithDescription("Mark a corner of the area to restyle (aim at a block). Two diagonally opposite corners define it")
                    .HandleWith(OnMarkCommand)
                .EndSubCommand()
                .BeginSubCommand("unmark")
                    .WithDescription("Clear the marked area")
                    .HandleWith(OnUnmarkCommand)
                .EndSubCommand()
                .BeginSubCommand("list")
                    .WithDescription("List the blocks inside the marked area")
                    .HandleWith(OnListCommand)
                .EndSubCommand()
                .BeginSubCommand("options")
                    .WithDescription("Show swappable materials in the area, or the replacements for one of them")
                    .WithArgs(parsers.OptionalWord("material"))
                    .HandleWith(OnOptionsCommand)
                .EndSubCommand()
                .BeginSubCommand("swap")
                    .WithDescription("Swap one material for another across the area, e.g. /sw swap andesite chalk")
                    .WithArgs(parsers.Word("from"), parsers.Word("to"))
                    .HandleWith(OnSwapCommand)
                .EndSubCommand()
                .BeginSubCommand("undo")
                    .WithDescription("Step back through your recent swaps")
                    .HandleWith(OnUndoCommand)
                .EndSubCommand();

            api.Logger.Notification("[seraphsledger] material swatch tool ready (/sw, Ctrl+Shift+M)");
        }

        // --- selection ---

        /// <summary>
        /// The box the two marks describe. Marks left at the same height read as
        /// a footprint - the top is taken from the rain map so a whole building
        /// is covered without flying up to its roof. Marks at different heights
        /// are taken literally.
        /// </summary>
        (BlockPos min, BlockPos max) SelectionBounds(List<BlockPos> mk)
        {
            var min = new BlockPos(
                GameMath.Min(mk[0].X, mk[1].X),
                GameMath.Min(mk[0].Y, mk[1].Y),
                GameMath.Min(mk[0].Z, mk[1].Z));
            var max = new BlockPos(
                GameMath.Max(mk[0].X, mk[1].X),
                GameMath.Max(mk[0].Y, mk[1].Y),
                GameMath.Max(mk[0].Z, mk[1].Z));

            if (mk[0].Y == mk[1].Y)
            {
                var ba = sapi.World.BlockAccessor;
                for (int x = min.X; x <= max.X; x++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        max.Y = GameMath.Max(max.Y, ba.GetRainMapHeightAt(x, z));
                    }
                }
                max.Y = GameMath.Min(max.Y, min.Y + MaxHeight - 1);
            }

            return (min, max);
        }

        static long Volume(BlockPos min, BlockPos max) =>
            (long)(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1);

        bool TryGetSelection(IServerPlayer player, out BlockPos min, out BlockPos max, out string error)
        {
            min = max = null;
            error = null;
            var mk = marks.GetValueOrDefault(player.PlayerUID);
            if (mk == null || mk.Count < 2)
            {
                error = "Mark two opposite corners first: aim at a block and use /sw mark, then aim at the far corner and /sw mark again.";
                return false;
            }
            (min, max) = SelectionBounds(mk);
            long volume = Volume(min, max);
            if (volume > MaxVolume)
            {
                error = $"That area holds {volume:N0} blocks, over the {MaxVolume:N0} limit. Mark something smaller.";
                return false;
            }
            return true;
        }

        /// <summary>Block id -> how many of them sit in the box.</summary>
        Dictionary<int, int> ScanSelection(BlockPos min, BlockPos max)
        {
            var counts = new Dictionary<int, int>();
            var ba = sapi.World.BlockAccessor;
            var pos = new BlockPos(min.X, min.Y, min.Z);

            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        pos.Set(x, y, z);
                        Block block = ba.GetBlock(pos);
                        if (block == null || block.Id == 0) continue;
                        counts[block.Id] = counts.GetValueOrDefault(block.Id) + 1;
                    }
                }
            }
            return counts;
        }

        /// <summary>Cached per player; thrown away whenever the world or the
        /// marks change under it.</summary>
        Analysis GetAnalysis(IServerPlayer player, BlockPos min, BlockPos max)
        {
            if (analyses.TryGetValue(player.PlayerUID, out var cached)) return cached;
            var analysis = engine.Analyze(ScanSelection(min, max));
            analyses[player.PlayerUID] = analysis;
            return analysis;
        }

        void InvalidateAnalysis(IServerPlayer player) => analyses.Remove(player.PlayerUID);

        // --- highlighting ---

        void SetHighlightCube(IServerPlayer player, int slot, BlockPos a, BlockPos b, int color)
        {
            var cube = a == null ? new List<BlockPos>() : new List<BlockPos> { a, b };
            sapi.World.HighlightBlocks(player, slot, cube, new List<int> { color },
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
        }

        /// <summary>Each corner shows as a glowing pillar; once both are set the
        /// whole box being restyled is outlined.</summary>
        void UpdateHighlight(IServerPlayer player)
        {
            var mk = marks.GetValueOrDefault(player.PlayerUID);
            int pillar1 = ColorUtil.ColorFromRgba(60, 230, 120, 160);
            int pillar2 = ColorUtil.ColorFromRgba(230, 180, 40, 160);
            int boxCol = ColorUtil.ColorFromRgba(200, 140, 240, 70);

            SetHighlightCube(player, HighlightSlot,
                mk?.Count >= 1 ? mk[0] : null,
                mk?.Count >= 1 ? mk[0].AddCopy(1, 5, 1) : null, pillar1);
            SetHighlightCube(player, HighlightSlot + 1,
                mk?.Count >= 2 ? mk[1] : null,
                mk?.Count >= 2 ? mk[1].AddCopy(1, 5, 1) : null, pillar2);

            BlockPos bmin = null, bmax = null;
            if (mk?.Count >= 2)
            {
                (bmin, bmax) = SelectionBounds(mk);
                bmax = bmax.AddCopy(1, 1, 1);
            }
            SetHighlightCube(player, HighlightSlot + 2, bmin, bmax, boxCol);
        }

        // --- the swap itself ---

        int Resolve(int blockId, string from, string to, Dictionary<int, int> cache, bool allowNatural = false)
        {
            if (cache.TryGetValue(blockId, out int known)) return known;
            int target = engine.ResolveTarget(blockId, from, to, allowNatural);
            cache[blockId] = target;
            return target;
        }

        (bool ok, string msg) ApplySwap(IServerPlayer player, string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return (false, "Pick a material and a replacement for it.");
            }
            if (from == to) return (false, "That is the same material.");
            if (!TryGetSelection(player, out var min, out var max, out string error))
            {
                return (false, error);
            }

            var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
            var worldBa = sapi.World.BlockAccessor;
            var record = new SwapRecord { From = from, To = to };
            var resolved = new Dictionary<int, int>();
            // chiselled materials answer to a looser rule than blocks in the
            // world do (raw stone counts once someone has carved it), so the two
            // cannot share a cache of block id -> what it becomes
            var resolvedMicro = new Dictionary<int, int>();
            int skipped = 0;

            var pos = new BlockPos(min.X, min.Y, min.Z);
            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        pos.Set(x, y, z);
                        Block block = ba.GetBlock(pos);
                        if (block == null || block.Id == 0) continue;

                        if (block.EntityClass != null)
                        {
                            var be = worldBa.GetBlockEntity(pos);
                            if (be is BlockEntityMicroBlock micro)
                            {
                                SwapMicroBlock(micro, from, to, resolvedMicro, record);
                                continue;
                            }
                            // a chest swapped this way would lose what is in it,
                            // and a door would lose which way it faces
                            if (be != null && Resolve(block.Id, from, to, resolved) != 0) skipped++;
                            continue;
                        }

                        int target = Resolve(block.Id, from, to, resolved);
                        if (target == 0) continue;

                        record.Blocks.Add((pos.Copy(), block.Id, target));
                        ba.SetBlock(target, pos);
                    }
                }
            }
            ba.Commit();

            if (record.Blocks.Count == 0 && record.Micro.Count == 0)
            {
                return (false, $"Nothing in the area turns from {from} into {to}.");
            }

            PushUndo(player, record);
            InvalidateAnalysis(player);

            var msg = new StringBuilder($"{from} -> {to}: {record.Blocks.Count:N0} block(s) changed");
            if (record.Micro.Count > 0) msg.Append($", {record.Micro.Count:N0} chiselled block(s) re-materialed");
            msg.Append('.');
            if (skipped > 0) msg.Append($" {skipped} left alone (they hold contents or state).");
            msg.Append(" Undo with /sw undo.");
            return (true, msg.ToString());
        }

        /// <summary>
        /// Chiselled blocks keep their shape in the block entity and their
        /// material as a list of block ids, so they are re-materialed rather
        /// than replaced - the same field the game's own material exchange
        /// rewrites.
        /// </summary>
        void SwapMicroBlock(BlockEntityMicroBlock micro, string from, string to,
            Dictionary<int, int> resolved, SwapRecord record)
        {
            int[] ids = micro.BlockIds;
            if (ids == null || ids.Length == 0) return;

            int[] updated = null;
            for (int i = 0; i < ids.Length; i++)
            {
                int target = Resolve(ids[i], from, to, resolved, allowNatural: true);
                if (target == 0) continue;
                updated ??= (int[])ids.Clone();
                updated[i] = target;
            }
            if (updated == null) return;

            record.Micro.Add((micro.Pos.Copy(), (int[])ids.Clone()));
            micro.BlockIds = updated;
            micro.MarkDirty(true);
        }

        void PushUndo(IServerPlayer player, SwapRecord record)
        {
            if (!undoStacks.TryGetValue(player.PlayerUID, out var stack))
            {
                undoStacks[player.PlayerUID] = stack = new List<SwapRecord>();
            }
            stack.Add(record);

            long held = stack.Sum(r => (long)r.Blocks.Count + r.Micro.Count);
            while (stack.Count > 1 && (stack.Count > MaxUndo || held > MaxUndoBlocks))
            {
                held -= stack[0].Blocks.Count + stack[0].Micro.Count;
                stack.RemoveAt(0);
            }
        }

        (bool ok, string msg) Undo(IServerPlayer player)
        {
            var stack = undoStacks.GetValueOrDefault(player.PlayerUID);
            if (stack == null || stack.Count == 0)
            {
                return (false, "Nothing to undo. Swaps are only remembered until the server restarts.");
            }

            var record = stack[^1];
            stack.RemoveAt(stack.Count - 1);

            // put back what was there, even where the area has been built on
            // since - getting the material back matters more than protecting
            // edits made inside the footprint afterwards
            var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
            foreach (var (pos, prevId, _) in record.Blocks)
            {
                ba.SetBlock(prevId, pos);
            }
            ba.Commit();

            foreach (var (pos, prevMaterials) in record.Micro)
            {
                if (sapi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityMicroBlock micro)
                {
                    micro.BlockIds = (int[])prevMaterials.Clone();
                    micro.MarkDirty(true);
                }
            }

            InvalidateAnalysis(player);
            return (true, $"Undid {record.From} -> {record.To} ({record.Blocks.Count:N0} block(s)). " +
                $"{stack.Count} swap(s) further back still undoable.");
        }

        // --- commands ---

        static bool AsPlayer(TextCommandCallingArgs args, out IServerPlayer player, out TextCommandResult error)
        {
            error = null;
            player = args.Caller.Player as IServerPlayer;
            if (player == null)
            {
                error = TextCommandResult.Error("Only an ingame player can do that.");
                return false;
            }
            return true;
        }

        TextCommandResult OnMarkCommand(TextCommandCallingArgs args)
        {
            if (!AsPlayer(args, out var player, out var error)) return error;

            BlockPos pos = player.CurrentBlockSelection?.Position
                ?? player.Entity.Pos.AsBlockPos.DownCopy();

            var mk = marks.GetValueOrDefault(player.PlayerUID);
            InvalidateAnalysis(player);

            if (mk == null || mk.Count >= 2)
            {
                marks[player.PlayerUID] = new List<BlockPos> { pos };
                UpdateHighlight(player);
                return TextCommandResult.Success(
                    $"Corner 1 marked at {pos} (highlighted). Now aim at the opposite corner and /sw mark again.");
            }

            mk.Add(pos);
            UpdateHighlight(player);
            var (min, max) = SelectionBounds(mk);

            return TextCommandResult.Success(
                $"Corner 2 marked. Area {max.X - min.X + 1}x{max.Y - min.Y + 1}x{max.Z - min.Z + 1}" +
                (mk[0].Y == mk[1].Y ? $" (top auto-detected at y={max.Y})" : "") +
                ". Look it over, then Ctrl+Shift+M to pick a material - or /sw options.");
        }

        TextCommandResult OnUnmarkCommand(TextCommandCallingArgs args)
        {
            if (!AsPlayer(args, out var player, out var error)) return error;
            marks.Remove(player.PlayerUID);
            InvalidateAnalysis(player);
            UpdateHighlight(player);
            return TextCommandResult.Success("Marked area cleared. Swaps already made are still undoable.");
        }

        TextCommandResult OnListCommand(TextCommandCallingArgs args)
        {
            if (!AsPlayer(args, out var player, out var error)) return error;
            if (!TryGetSelection(player, out var min, out var max, out string reason))
            {
                return TextCommandResult.Error(reason);
            }

            var counts = ScanSelection(min, max);
            if (counts.Count == 0) return TextCommandResult.Success("The marked area is empty.");

            var text = new StringBuilder($"{counts.Values.Sum():N0} block(s), {counts.Count} kind(s):\n");
            foreach (var (id, count) in counts.OrderByDescending(kv => kv.Value).Take(25))
            {
                text.AppendLine($"  {count,7:N0}  {sapi.World.GetBlock(id)?.Code}");
            }
            if (counts.Count > 25) text.AppendLine($"  ...and {counts.Count - 25} more");
            return TextCommandResult.Success(text.ToString().TrimEnd());
        }

        TextCommandResult OnOptionsCommand(TextCommandCallingArgs args)
        {
            if (!AsPlayer(args, out var player, out var error)) return error;
            if (!TryGetSelection(player, out var min, out var max, out string reason))
            {
                return TextCommandResult.Error(reason);
            }

            var analysis = GetAnalysis(player, min, max);
            string material = args[0] as string;

            if (string.IsNullOrEmpty(material))
            {
                var sources = analysis.SourcesRanked();
                if (sources.Count == 0)
                {
                    return TextCommandResult.Success("Nothing in the marked area has an alternative to swap to.");
                }
                var text = new StringBuilder("Materials in the area that can be swapped:\n");
                foreach (var (name, count) in sources.Take(30))
                {
                    text.AppendLine($"  {count,7:N0}  {name}");
                }
                text.Append("Replacements for one of them: /sw options <material>");
                return TextCommandResult.Success(text.ToString());
            }

            var targets = analysis.TargetsRanked(material);
            if (targets.Count == 0)
            {
                return TextCommandResult.Error($"'{material}' is not a swappable material in the marked area. See /sw options.");
            }

            var list = new StringBuilder($"'{material}' can become (with how many blocks would change):\n");
            foreach (var (name, count) in targets.Take(30))
            {
                list.AppendLine($"  {count,7:N0}  {name}");
            }
            list.Append($"Then: /sw swap {material} <replacement>");
            return TextCommandResult.Success(list.ToString());
        }

        TextCommandResult OnSwapCommand(TextCommandCallingArgs args)
        {
            if (!AsPlayer(args, out var player, out var error)) return error;
            var (ok, msg) = ApplySwap(player, (string)args[0], (string)args[1]);
            return ok ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
        }

        TextCommandResult OnUndoCommand(TextCommandCallingArgs args)
        {
            if (!AsPlayer(args, out var player, out var error)) return error;
            var (ok, msg) = Undo(player);
            return ok ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
        }

        // --- network handlers (dialog) ---

        bool Denied(IServerPlayer player)
        {
            // A client can have the feature switched on locally while this
            // server has it off; say so rather than going quiet.
            if (!enabled)
            {
                serverChannel.SendPacket(new SwStatus
                {
                    Success = false,
                    Message = "The material swatch tool is switched off on this server."
                }, player);
                return true;
            }
            if (player.HasPrivilege(Privilege.controlserver)) return false;
            serverChannel.SendPacket(new SwStatus
            {
                Success = false,
                Message = "You need the controlserver privilege to restyle blocks."
            }, player);
            return true;
        }

        void SendInfo(IServerPlayer player)
        {
            var response = new SwInfoResponse();
            if (!TryGetSelection(player, out var min, out var max, out string reason))
            {
                response.HasSelection = false;
                response.SelectionDesc = reason;
                serverChannel.SendPacket(response, player);
                return;
            }

            var analysis = GetAnalysis(player, min, max);
            response.HasSelection = true;
            response.SelectionDesc =
                $"Area {max.X - min.X + 1}x{max.Y - min.Y + 1}x{max.Z - min.Z + 1}";
            foreach (var (name, count) in analysis.SourcesRanked())
            {
                response.Sources.Add(name);
                response.Counts.Add(count);
            }
            serverChannel.SendPacket(response, player);
        }

        void OnInfoRequest(IServerPlayer player, SwInfoRequest packet)
        {
            if (Denied(player)) return;
            // opening or refreshing the dialog is the natural moment to look at
            // the area again, in case it has been built on since
            InvalidateAnalysis(player);
            SendInfo(player);
        }

        void OnTargetsRequest(IServerPlayer player, SwTargetsRequest packet)
        {
            if (Denied(player)) return;

            var response = new SwTargetsResponse { From = packet.From };
            if (TryGetSelection(player, out var min, out var max, out _))
            {
                foreach (var (name, count) in GetAnalysis(player, min, max).TargetsRanked(packet.From))
                {
                    response.Targets.Add(name);
                    response.Counts.Add(count);
                }
            }
            serverChannel.SendPacket(response, player);
        }

        void OnApplyRequest(IServerPlayer player, SwApplyRequest packet)
        {
            if (Denied(player)) return;
            var (ok, msg) = ApplySwap(player, packet.From, packet.To);
            serverChannel.SendPacket(new SwStatus { Success = ok, Message = msg }, player);
            if (ok) SendInfo(player);
        }

        void OnUndoRequest(IServerPlayer player, SwUndoRequest packet)
        {
            if (Denied(player)) return;
            var (ok, msg) = Undo(player);
            serverChannel.SendPacket(new SwStatus { Success = ok, Message = msg }, player);
            if (ok) SendInfo(player);
        }

        void OnClearRequest(IServerPlayer player, SwClearRequest packet)
        {
            if (Denied(player)) return;
            marks.Remove(player.PlayerUID);
            InvalidateAnalysis(player);
            UpdateHighlight(player);
            serverChannel.SendPacket(new SwStatus
            {
                Success = true,
                Message = "Marked area cleared. Mark two corners with /sw mark."
            }, player);
            SendInfo(player);
        }

        // ------------------------------------------------------------------
        // Client
        // ------------------------------------------------------------------

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (!enabled) return;

            capi = api;
            clientChannel = api.Network.GetChannel(ChannelName)
                .SetMessageHandler<SwInfoResponse>(OnInfoResponse)
                .SetMessageHandler<SwTargetsResponse>(OnTargetsResponse)
                .SetMessageHandler<SwStatus>(OnStatus);

            api.Input.RegisterHotKey("seraphsledgerswatch", Lang.Get("seraphsledger:swatch-hotkey-name"),
                GlKeys.M, HotkeyType.GUIOrOtherControls, altPressed: false,
                ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("seraphsledgerswatch", OnHotkey);

            dialog = new GuiDialogSwatch(api, this);
        }

        bool OnHotkey(KeyCombination comb)
        {
            if (dialog.IsOpened()) dialog.TryClose();
            else dialog.TryOpen();
            return true;
        }

        public void RequestInfo() => clientChannel.SendPacket(new SwInfoRequest());
        public void RequestTargets(string from) => clientChannel.SendPacket(new SwTargetsRequest { From = from });
        public void SendApply(string from, string to) => clientChannel.SendPacket(new SwApplyRequest { From = from, To = to });
        public void SendUndo() => clientChannel.SendPacket(new SwUndoRequest());
        public void SendClear() => clientChannel.SendPacket(new SwClearRequest());

        void OnInfoResponse(SwInfoResponse msg) => dialog.SetSelection(msg);
        void OnTargetsResponse(SwTargetsResponse msg) => dialog.SetTargets(msg);
        void OnStatus(SwStatus msg) => dialog.SetStatus(msg.Message);
    }
}
