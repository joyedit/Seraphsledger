using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace SeraphsLedger
{
    // ---- Persisted data model -------------------------------------------------

    // One buy- or sell-offer a trader had on the last visit.
    public class TradeEntry
    {
        public string Code;   // collectible code, e.g. "game:ingot-iron"
        public string Name;   // readable display name
        public int Price;     // in gears
        public int Stock;     // remaining stock (meaningful for selling offers)
    }

    // Everything we know about one trader, keyed by their (stable) EntityId.
    public class TraderRecord
    {
        public long EntityId;
        public string Name;        // generated trader name, e.g. "Gerda"
        public string Wares;       // category parsed from the entity code, e.g. "luxuries"
        public string EntityCode;  // full resolved code, e.g. "trader-male-luxuries-temperate"
        public int X, Y, Z;        // absolute world position
        public string LastVisitedReal;   // real-world timestamp of the last visit
        public double LastVisitedDays;   // in-game total days at last visit
        public List<TradeEntry> Selling = new List<TradeEntry>(); // trader -> player
        public List<TradeEntry> Buying = new List<TradeEntry>();  // player -> trader
        public bool Waypointed;    // a map waypoint has already been dropped for this trader
    }

    // Root object handed to StoreModConfig/LoadModConfig (one file per world).
    public class TraderLogData
    {
        public Dictionary<long, TraderRecord> Traders = new Dictionary<long, TraderRecord>();
    }

    // ---- Mod system: capture + storage + /traders command ---------------------

    public class TraderLogModSystem : ModSystem
    {
        internal static TraderLogModSystem Instance;

        private const string HarmonyId = "seraphsledger.traderlog";
        private Harmony harmony;

        private ICoreClientAPI capi;
        private TraderLogData data;
        private bool loaded;
        private GuiDialogTraderLog logDialog;

        // One file per savegame; the location of a trader only means anything
        // within the world it was seen in.
        private string ConfigFile => "seraphsledger/traders-" + capi.World.SavegameIdentifier + ".json";

        // Client-only feature; no server component is needed since the trade
        // inventory is synced to us when the dialog opens.
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);

            // Honour the feature toggle: if the ledger is switched off, register
            // nothing (no patch, hotkey, or commands). Takes effect on restart.
            if (!SeraphsLedgerConfig.Load(api).TraderLedger) return;

            capi = api;
            Instance = this;

            // SavegameIdentifier isn't known until we've joined a world.
            api.Event.LevelFinalize += Load;

            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                harmony = new Harmony(HarmonyId);
                var ctor = AccessTools.Constructor(typeof(GuiDialogTrader), new Type[]
                {
                    typeof(InventoryTrader), typeof(EntityAgent), typeof(ICoreClientAPI), typeof(int), typeof(int)
                });
                if (ctor != null)
                {
                    harmony.Patch(ctor, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(TraderDialogCapturePatch), nameof(TraderDialogCapturePatch.Postfix))));
                }
            }

            // Hotkey to open the browser GUI (rebindable in Controls settings).
            api.Input.RegisterHotKey("seraphsledgertraderlog", "Open trader log", GlKeys.K,
                HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: true);
            api.Input.SetHotKeyHandler("seraphsledgertraderlog", OnToggleLogGui);

            api.ChatCommands.Create("traders")
                .WithDescription("List traders you've visited and their last-seen wares")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("filter"))
                .HandleWith(OnTradersCmd)
                .BeginSubCommand("wp")
                    .WithDescription("Drop map waypoints for any recorded traders that don't have one yet")
                    .HandleWith(OnTradersWpCmd)
                .EndSubCommand()
                .BeginSubCommand("export")
                    .WithDescription("Write a Markdown summary of all recorded traders to the mod data folder")
                    .HandleWith(OnTradersExportCmd)
                .EndSubCommand();
        }

        // ---- Capture ----------------------------------------------------------

        internal void Capture(InventoryTrader inv, EntityAgent entity)
        {
            if (inv == null || entity == null) return;
            EnsureLoaded();

            var nametag = entity.WatchedAttributes?.GetTreeAttribute("nametag");
            var pos = entity.Pos;

            var rec = new TraderRecord
            {
                EntityId = entity.EntityId,
                Name = nametag?.GetString("name") ?? "Unnamed",
                EntityCode = entity.Code?.ToString(),
                Wares = ParseWares(entity.Code?.Path),
                X = (int)pos.X,
                Y = (int)pos.Y,
                Z = (int)pos.Z,
                LastVisitedReal = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                LastVisitedDays = capi.World.Calendar?.TotalDays ?? 0,
            };

            CollectTrades(inv.SellingSlots, rec.Selling);
            CollectTrades(inv.BuyingSlots, rec.Buying);

            // Preserve whether we've already mapped this trader across revisits.
            if (data.Traders.TryGetValue(rec.EntityId, out var existing))
                rec.Waypointed = existing.Waypointed;

            data.Traders[rec.EntityId] = rec;

            if (!rec.Waypointed)
            {
                AddWaypoint(rec);
                rec.Waypointed = true;
            }
            Save();
        }

        private static void CollectTrades(ItemSlotTrade[] slots, List<TradeEntry> into)
        {
            if (slots == null) return;
            foreach (var s in slots)
            {
                if (s?.Itemstack == null) continue;
                var ti = s.TradeItem;
                into.Add(new TradeEntry
                {
                    Code = s.Itemstack.Collectible?.Code?.ToString(),
                    Name = s.Itemstack.GetName(),
                    Price = ti?.Price ?? 0,
                    Stock = ti?.Stock ?? 0,
                });
            }
        }

        // "trader-male-luxuries-temperate" -> "luxuries"
        private static string ParseWares(string codePath)
        {
            if (string.IsNullOrEmpty(codePath)) return "unknown";
            var parts = codePath.Split('-');
            return parts.Length >= 3 ? parts[2] : codePath;
        }

        // ---- Waypoints --------------------------------------------------------

        // A distinct, recognizable color per trader category for the world map.
        private static readonly Dictionary<string, string> WaresColors = new Dictionary<string, string>
        {
            { "agriculture",    "LightGreen" },
            { "artisan",        "Orange" },
            { "buildmaterials", "Gray" },
            { "clothing",       "Magenta" },
            { "commodities",    "Yellow" },
            { "foods",          "LightGreen" },
            { "furniture",      "SaddleBrown" },
            { "luxuries",       "Violet" },
            { "survivalgoods",  "Cyan" },
            { "treasurehunter", "Gold" },
        };

        private static string WaypointColor(string wares)
        {
            return wares != null && WaresColors.TryGetValue(wares, out var c) ? c : "White";
        }

        // Drops a waypoint via the vanilla server command (works on any server).
        // '=' prefixes mark absolute world coords (bare numbers are spawn-relative).
        private void AddWaypoint(TraderRecord t)
        {
            string title = t.Name + " (" + t.Wares + ")";
            string cmd = "/waypoint addati trader ="
                + t.X + " =" + t.Y + " =" + t.Z
                + " false " + WaypointColor(t.Wares) + " " + title;
            capi.SendChatMessage(cmd);
        }

        // ---- Persistence ------------------------------------------------------

        private void Load()
        {
            try { data = capi.LoadModConfig<TraderLogData>(ConfigFile); }
            catch (Exception e) { capi.Logger.Warning("[seraphsledger] trader log load failed: {0}", e.Message); data = null; }
            if (data == null) data = new TraderLogData();
            if (data.Traders == null) data.Traders = new Dictionary<long, TraderRecord>();
            loaded = true;
        }

        private void EnsureLoaded()
        {
            if (!loaded) Load();
        }

        private void Save()
        {
            try { capi.StoreModConfig(data, ConfigFile); }
            catch (Exception e) { capi.Logger.Warning("[seraphsledger] trader log save failed: {0}", e.Message); }
        }

        // ---- Accessors used by the GUI dialog ---------------------------------

        internal List<TraderRecord> GetRecords()
        {
            EnsureLoaded();
            return data.Traders.Values.ToList();
        }

        private bool OnToggleLogGui(KeyCombination comb)
        {
            EnsureLoaded();
            if (logDialog == null) logDialog = new GuiDialogTraderLog(capi, this);
            if (logDialog.IsOpened()) logDialog.TryClose();
            else logDialog.TryOpen();
            return true;
        }

        // ---- /traders command -------------------------------------------------

        private TextCommandResult OnTradersCmd(TextCommandCallingArgs args)
        {
            EnsureLoaded();
            if (data.Traders.Count == 0)
                return TextCommandResult.Success("No traders recorded yet. Open a trader's \"Buy/Sell\" window to log one.");

            string filter = args[0] as string;
            var all = data.Traders.Values
                .OrderBy(t => t.Wares).ThenBy(t => t.Name)
                .ToList();

            // No filter: one-line summary per trader.
            if (string.IsNullOrEmpty(filter))
            {
                var sb = new StringBuilder();
                sb.Append(all.Count).Append(" trader(s) recorded:\n");
                foreach (var t in all) sb.Append(SummaryLine(t)).Append('\n');
                sb.Append("Use /traders <name|type|item> to see full wares.");
                return TextCommandResult.Success(sb.ToString());
            }

            // Filter: full detail for any trader matching name, type, or a ware.
            filter = filter.ToLowerInvariant();
            var matches = all.Where(t => Matches(t, filter)).ToList();
            if (matches.Count == 0)
                return TextCommandResult.Success("No recorded trader matches \"" + filter + "\".");

            var detail = new StringBuilder();
            foreach (var t in matches) detail.Append(DetailBlock(t)).Append('\n');
            return TextCommandResult.Success(detail.ToString().TrimEnd());
        }

        private TextCommandResult OnTradersWpCmd(TextCommandCallingArgs args)
        {
            EnsureLoaded();
            int added = 0;
            foreach (var t in data.Traders.Values)
            {
                if (t.Waypointed) continue;
                AddWaypoint(t);
                t.Waypointed = true;
                added++;
            }
            if (added > 0) Save();
            return TextCommandResult.Success("Added " + added + " trader waypoint(s).");
        }

        private TextCommandResult OnTradersExportCmd(TextCommandCallingArgs args)
        {
            EnsureLoaded();
            if (data.Traders.Count == 0)
                return TextCommandResult.Success("No traders recorded yet — nothing to export.");

            string dir = capi.GetOrCreateDataPath("ModData/seraphsledger");
            string path = System.IO.Path.Combine(dir, "traders-" + capi.World.SavegameIdentifier + ".md");
            try
            {
                System.IO.File.WriteAllText(path, BuildMarkdown());
            }
            catch (Exception e)
            {
                return TextCommandResult.Error("Export failed: " + e.Message);
            }
            return TextCommandResult.Success("Exported " + data.Traders.Count + " trader(s) to " + path);
        }

        private string BuildMarkdown()
        {
            var sb = new StringBuilder();
            sb.Append("# Trader Log\n\n");
            sb.Append("_Exported ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
              .Append(" · ").Append(data.Traders.Count).Append(" trader(s)_\n\n");

            // Grouped by category, then alphabetically by name.
            var byType = data.Traders.Values
                .OrderBy(t => t.Wares).ThenBy(t => t.Name)
                .GroupBy(t => t.Wares);

            foreach (var group in byType)
            {
                sb.Append("## ").Append(group.Key).Append("\n\n");
                foreach (var t in group)
                {
                    sb.Append("### ").Append(t.Name).Append("\n\n");
                    sb.Append("- Location: ").Append(RelCoords(t)).Append("\n");
                    sb.Append("- Last visited: ").Append(t.LastVisitedReal)
                      .Append(" (day ").Append((int)t.LastVisitedDays).Append(")\n\n");

                    sb.Append("**Selling**\n\n");
                    if (t.Selling.Count == 0) sb.Append("_nothing_\n\n");
                    else
                    {
                        sb.Append("| Item | Price | Stock |\n|---|---|---|\n");
                        foreach (var e in t.Selling)
                            sb.Append("| ").Append(e.Name).Append(" | ").Append(e.Price).Append("g | ").Append(e.Stock).Append(" |\n");
                        sb.Append('\n');
                    }

                    sb.Append("**Buying**\n\n");
                    if (t.Buying.Count == 0) sb.Append("_nothing_\n\n");
                    else
                    {
                        sb.Append("| Item | Price |\n|---|---|\n");
                        foreach (var e in t.Buying)
                            sb.Append("| ").Append(e.Name).Append(" | ").Append(e.Price).Append("g |\n");
                        sb.Append('\n');
                    }
                }
            }
            return sb.ToString();
        }

        private static bool Matches(TraderRecord t, string f)
        {
            if ((t.Name ?? "").ToLowerInvariant().Contains(f)) return true;
            if ((t.Wares ?? "").ToLowerInvariant().Contains(f)) return true;
            foreach (var e in t.Selling) if ((e.Name ?? "").ToLowerInvariant().Contains(f) || (e.Code ?? "").ToLowerInvariant().Contains(f)) return true;
            foreach (var e in t.Buying) if ((e.Name ?? "").ToLowerInvariant().Contains(f) || (e.Code ?? "").ToLowerInvariant().Contains(f)) return true;
            return false;
        }

        // Coords shown relative to world spawn, matching the in-game position HUD.
        internal string RelCoords(TraderRecord t)
        {
            var spawn = capi.World.DefaultSpawnPosition?.XYZ;
            if (spawn == null) return t.X + ", " + t.Y + ", " + t.Z;
            return (t.X - (int)spawn.X) + ", " + (t.Y - (int)spawn.Y) + ", " + (t.Z - (int)spawn.Z);
        }

        private string SummaryLine(TraderRecord t)
        {
            return "- " + t.Name + " (" + t.Wares + ") @ " + RelCoords(t)
                 + " — sells " + t.Selling.Count + ", buys " + t.Buying.Count
                 + " [" + t.LastVisitedReal + "]";
        }

        private string DetailBlock(TraderRecord t)
        {
            var sb = new StringBuilder();
            sb.Append(t.Name).Append(" (").Append(t.Wares).Append(") @ ").Append(RelCoords(t))
              .Append("  last seen ").Append(t.LastVisitedReal).Append('\n');
            sb.Append("  Selling:\n");
            if (t.Selling.Count == 0) sb.Append("    (nothing)\n");
            foreach (var e in t.Selling)
                sb.Append("    ").Append(e.Name).Append(" — ").Append(e.Price).Append("g (stock ").Append(e.Stock).Append(")\n");
            sb.Append("  Buying:\n");
            if (t.Buying.Count == 0) sb.Append("    (nothing)\n");
            foreach (var e in t.Buying)
                sb.Append("    ").Append(e.Name).Append(" — ").Append(e.Price).Append("g\n");
            return sb.ToString();
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            Instance = null;
        }
    }

    // Fires every time a trader's Buy/Sell window is constructed client-side.
    public static class TraderDialogCapturePatch
    {
        public static void Postfix(InventoryTrader traderInventory, EntityAgent owningEntity)
        {
            TraderLogModSystem.Instance?.Capture(traderInventory, owningEntity);
        }
    }
}
