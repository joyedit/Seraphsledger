using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;

namespace SeraphsLedger
{
    // The mod list browser's window: a search box, a sort key, an ascending /
    // descending switch, and the list itself. See ModList.cs for where the rows
    // come from and why this isn't bolted onto the game's mod manager.
    //
    // Opened by the "seraphsledgermodlist" hotkey (Ctrl+Shift+N by default) or the
    // .slmods chat command, both registered by ModListModSystem.
    public class GuiDialogModList : GuiDialog
    {
        private readonly ModListModSystem owner;

        private string searchText = "";
        private ModSortKey sortKey = ModSortKey.Name;
        private bool ascending = true;
        private float sinceProgressRedraw;

        // The row whose [disable] has been clicked but not yet confirmed, by
        // token. Disabling is the direction that can cost something, so it always
        // takes the second click; enabling happens on the first.
        private string confirmToken;

        private const double ListW = 780;
        private const double ListH = 460;

        // Parallel to ModSortKey, in the same order.
        private static readonly string[] SortLabels =
        {
            "Name",
            "Install date",
            "Game version",
            "Update available",
            "Load order",
            "Load problems",
            "Enabled",
            "Author",
            "Description",
        };

        public GuiDialogModList(ICoreClientAPI capi, ModListModSystem owner) : base(capi)
        {
            this.owner = owner;
        }

        public override string ToggleKeyCombinationCode => "seraphsledgermodlist";

        public override void OnGuiOpened()
        {
            Compose();
            base.OnGuiOpened();
        }

        // Used by .slmods <sort> [desc] so the dialog opens in the asked-for
        // order; takes effect on the next compose either way.
        internal void SetSort(ModSortKey key, bool asc)
        {
            sortKey = key;
            ascending = asc;
            if (IsOpened()) Compose();
        }

        private void Compose()
        {
            double top = GuiStyle.TitleBarHeight + 5;

            ElementBounds searchBounds = ElementBounds.Fixed(0, top, 230, 30);
            ElementBounds sortBounds = ElementBounds.Fixed(240, top, 230, 30);
            ElementBounds orderBounds = ElementBounds.Fixed(480, top, 130, 30);
            ElementBounds updateBounds = ElementBounds.Fixed(620, top, 160, 30);

            double row2 = top + 38;
            ElementBounds summaryBounds = ElementBounds.Fixed(0, row2 + 4, 620, 24);
            ElementBounds rescanBounds = ElementBounds.Fixed(680, row2, 100, 30);

            // Same shape as the trader log: a fixed window with the richtext as a
            // grandchild that grows to the content height and scrolls inside it.
            double listTop = row2 + 42;
            ElementBounds clipBounds = ElementBounds.Fixed(0, listTop, ListW, ListH);
            ElementBounds insetBounds = clipBounds.FlatCopy().FixedGrow(3);
            ElementBounds textBounds = ElementBounds.Fixed(0, 0, ListW - 10, ListH);
            // Hung off the inset, not the clip: the inset is the clip grown by 3
            // on every side, so a bar sized to the clip stops 3px short of the
            // frame's top and bottom corners. The engine's own helper puts it
            // beside the box it is given and matches its height exactly.
            ElementBounds scrollBounds = ElementStdBounds.VerticalScrollbar(insetBounds);

            ElementBounds bgBounds = ElementBounds.FixedSize(0, 0);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            // textBounds is deliberately left out: its height balloons to the
            // content size while composing, and the dialog must size to the clip.
            bgBounds.WithChildren(searchBounds, sortBounds, orderBounds, updateBounds,
                summaryBounds, rescanBounds, insetBounds, clipBounds, scrollBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            string[] values = Enum.GetNames(typeof(ModSortKey));

            SingleComposer = capi.Gui.CreateCompo("seraphsledgermodlist", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("The Seraph's Ledger — Installed mods", () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddTextInput(searchBounds, OnSearchChanged, CairoFont.WhiteSmallishText(), "search")
                    .AddDropDown(values, SortLabels, (int)sortKey, OnSortPicked, sortBounds, "sortkey")
                    .AddSmallButton(ascending ? "Ascending" : "Descending", OnToggleOrder, orderBounds,
                        EnumButtonStyle.Normal, "orderbtn")
                    .AddSmallButton(owner.Checking ? "Checking…" : "Check updates", OnCheckUpdates, updateBounds,
                        EnumButtonStyle.Normal, "updatebtn")
                    .AddDynamicText(Summary(), CairoFont.WhiteDetailText(), summaryBounds, "summary")
                    .AddSmallButton("Rescan", OnRescan, rescanBounds, EnumButtonStyle.Normal, "rescanbtn")
                    .AddInset(insetBounds, 3)
                    .BeginClip(clipBounds)
                        .AddRichtext(BuildVtml(), CairoFont.WhiteSmallText(), textBounds, "list")
                    .EndClip()
                    .AddVerticalScrollbar(OnNewScrollbar, scrollBounds, "scrollbar")
                .EndChildElements()
                .Compose();

            SingleComposer.GetTextInput("search").SetValue(searchText, false);
            UpdateScrollbarHeight();
        }

        // ---- Data shaping -----------------------------------------------------

        private List<ModListEntry> FilteredSorted()
        {
            var rows = new List<ModListEntry>(owner.Entries());

            string f = searchText?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(f)) rows = rows.Where(e => Matches(e, f)).ToList();

            ModListModSystem.Sort(rows, sortKey, ascending);
            return rows;
        }

        private static bool Matches(ModListEntry e, string f)
        {
            return Has(e.Name, f) || Has(e.ModId, f) || Has(e.FileName, f)
                || Has(e.Authors, f) || Has(e.Description, f);
        }

        private static bool Has(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string Summary()
        {
            List<ModListEntry> all = owner.Entries();
            int disabled = all.Count(e => !e.Enabled && !e.Failed);
            int failed = all.Count(e => e.Failed);
            int updates = all.Count(e => e.UpdateState == 1);
            int known = all.Count(e => e.UpdateState >= 0);
            int pending = all.Count(e => owner.PendingRestart(e));

            var sb = new StringBuilder();
            sb.Append(all.Count).Append(" mods");
            if (disabled > 0) sb.Append(" · ").Append(disabled).Append(" disabled");
            if (failed > 0) sb.Append(" · ").Append(failed).Append(" won't load");
            if (pending > 0) sb.Append(" · ").Append(pending).Append(" awaiting restart");
            if (owner.Checking)
            {
                sb.Append(" · asking the mod database… ")
                  .Append(owner.CheckDone).Append('/').Append(owner.CheckTotal);
            }
            else if (known > 0)
            {
                sb.Append(" · ").Append(updates).Append(updates == 1 ? " update" : " updates");
                if (known < all.Count) sb.Append(" · ").Append(all.Count - known).Append(" not on the database");
            }
            return sb.ToString();
        }

        // Keeps the "x of y" count moving while a check is running. The summary is
        // the only thing that changes, and only while waiting on the network.
        public override void OnFinalizeFrame(float dt)
        {
            base.OnFinalizeFrame(dt);
            if (!owner.Checking || SingleComposer == null) return;

            sinceProgressRedraw += dt;
            if (sinceProgressRedraw < 0.5f) return;
            sinceProgressRedraw = 0;
            SingleComposer.GetDynamicText("summary").SetNewText(Summary());
        }

        // One mod per block: a title line, a line of the sortable details, and the
        // description when there is one. The value the list is currently ordered by
        // is highlighted, so the order on screen always has something to read it
        // against.
        private string BuildVtml()
        {
            List<ModListEntry> rows = FilteredSorted();
            if (rows.Count == 0)
            {
                return owner.Entries().Count == 0
                    ? "<font color=\"#cccccc\">No mods found in the mod folders.</font>"
                    : "<font color=\"#cccccc\">No mod matches that search.</font>";
            }

            var sb = new StringBuilder();
            foreach (ModListEntry e in rows)
            {
                // Title: name, version, and whatever is worth flagging.
                string nameColor = sortKey == ModSortKey.Name ? "#e8c060" : (e.Enabled ? null : "#8a8a8a");
                sb.Append("<font weight=\"bold\"");
                if (nameColor != null) sb.Append(" color=\"").Append(nameColor).Append('"');
                sb.Append('>').Append(San(e.Name)).Append("</font>");

                if (e.Version.Length > 0)
                    sb.Append("<font color=\"#9a9a9a\">  v").Append(San(e.Version)).Append("</font>");

                if (e.Failed)
                    sb.Append(Key(ModSortKey.Problem, "  [" + San(e.Problem ?? "won't load") + "]", "#d88c76"));
                else if (!e.Enabled)
                    sb.Append(Key(ModSortKey.Enabled, "  [disabled]", "#9a9a9a"));

                if (owner.PendingRestart(e))
                {
                    sb.Append("<font color=\"#ffa342\">  [")
                      .Append(e.DisabledBySetting ? "disabled" : "enabled")
                      .Append(" on restart]</font>");
                }

                if (e.UpdateState == 1)
                    sb.Append(Key(ModSortKey.Update, "  [update: " + San(e.LatestVersion) + "]", "#7ec96f"));
                else if (e.AheadOfDb)
                    sb.Append(Key(ModSortKey.Update, "  [ahead of " + San(e.LatestVersion) + "]", "#6f8a6a"));
                else if (e.UpdateState == 0)
                    sb.Append(Key(ModSortKey.Update, "  [up to date]", "#6f8a6a"));
                sb.Append('\n');

                // Details, in the order they appear in the sort dropdown.
                var bits = new List<string>(5);
                bits.Add(Key(ModSortKey.Author, e.Authors.Length > 0 ? San(e.Authors) : "author unknown", "#8a8a8a"));
                bits.Add(Key(ModSortKey.Installed,
                    e.Installed == default ? "date unknown" : e.Installed.ToString("yyyy-MM-dd"), "#8a8a8a"));
                bits.Add(Key(ModSortKey.GameVersion,
                    e.GameVersion.Length > 0 ? "game " + San(e.GameVersion) : "any game version", "#8a8a8a"));
                bits.Add(Key(ModSortKey.LoadOrder,
                    e.LoadOrder == int.MaxValue ? "not loaded" : "load #" + (e.LoadOrder + 1), "#8a8a8a"));
                if (e.TypeName.Length > 0)
                    bits.Add("<font color=\"#8a8a8a\">" + San(e.TypeName) + "</font>");
                if (ModListModSystem.CanToggle(e))
                {
                    bits.Add(e.DisabledBySetting
                        ? Link("on", e.Token, "[enable]")
                        : Link("ask", e.Token, "[disable]"));
                }

                sb.Append("  ").Append(string.Join("<font color=\"#5f5f5f\"> · </font>", bits)).Append('\n');

                if (e.Description.Length > 0)
                {
                    sb.Append("  ").Append(Key(ModSortKey.Description, San(Clip(e.Description, 140)), "#777777"));
                    sb.Append('\n');
                }

                if (confirmToken == e.Token) AppendConfirm(sb, e);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // The second half of a disable: what it will cost, then the two links.
        // Shown in the row itself rather than in a dialog over the top, so the
        // mod being talked about stays on screen next to the warning.
        private void AppendConfirm(StringBuilder sb, ModListEntry e)
        {
            sb.Append("  <font color=\"#e8c060\">Disable ").Append(San(e.Name)).Append("?</font>\n");

            if (e.CoreMod)
            {
                sb.Append("    <font color=\"#d87a5c\">It is part of the base game. Disabling it may stop the game starting.</font>\n");
            }
            if (e.Dependents.Count > 0)
            {
                sb.Append("    <font color=\"#d87a5c\">").Append(San(DependentLine(e))).Append("</font>\n");
            }
            sb.Append("    <font color=\"#9a9a9a\">Blocks and items it adds read as unknown in worlds built with it.</font>\n");
            sb.Append("    <font color=\"#9a9a9a\">Takes effect when you restart the game.</font>\n");

            sb.Append("    ").Append(Link("do", e.Token, "[disable anyway]"))
              .Append("    ").Append(Link("cancel", "", "[cancel]")).Append('\n');
        }

        // "Carry On and Gatewright depend on it and will not load either."
        private static string DependentLine(ModListEntry e)
        {
            const int Max = 6;
            List<string> names = e.Dependents.Count <= Max
                ? e.Dependents
                : e.Dependents.Take(Max).Append((e.Dependents.Count - Max) + " more").ToList();

            string list = names.Count == 1
                ? names[0]
                : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];

            return list + (e.Dependents.Count == 1 ? " depends on it and will not load either."
                                                   : " depend on it and will not load either.");
        }

        private static string Link(string verb, string token, string label)
        {
            return "<a href=\"slmod://" + verb + "/" + San(token) + "\">" + label + "</a>";
        }

        // Wraps one detail in its colour, or in the highlight colour when the list
        // is sorted by it.
        private string Key(ModSortKey key, string text, string color)
        {
            return "<font color=\"" + (key == sortKey ? "#e8c060" : color) + "\">" + text + "</font>";
        }

        private static string Clip(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";
        }

        // Keep dynamic text from breaking the VTML markup.
        private static string San(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("<", "(").Replace(">", ")");
        }

        // ---- Events -----------------------------------------------------------

        private void OnSearchChanged(string text)
        {
            searchText = text;
            confirmToken = null; // the row being asked about may not survive the filter
            RefreshList();
        }

        private void OnSortPicked(string code, bool selected)
        {
            if (!selected) return;
            if (Enum.TryParse(code, out ModSortKey picked)) sortKey = picked;
            confirmToken = null;
            RefreshList();
        }

        private bool OnToggleOrder()
        {
            ascending = !ascending;
            confirmToken = null;
            Compose(); // a button's label is drawn into its static surface, so relabelling means recomposing
            return true;
        }

        private bool OnRescan()
        {
            owner.Entries(rescan: true);
            confirmToken = null;
            RefreshList();
            return true;
        }

        // A click on one of the list's [enable] / [disable] links, routed here by
        // the "slmod" link protocol the mod system registers. The href is
        // slmod://<verb>/<modid@version>.
        internal void OnLink(string href)
        {
            if (!IsOpened() || !ParseLink(href, out string verb, out string token)) return;

            switch (verb)
            {
                case "ask": confirmToken = token; break;
                case "cancel": confirmToken = null; break;
                case "on": Apply(token, true); break;
                case "do": Apply(token, false); break;
                default: return;
            }

            // The row that was clicked has to stay where it was: jumping back to
            // the top of a fifty-mod list would lose it.
            RefreshList(keepScroll: true);
        }

        // slmod://<verb>/<modid@version> -> verb, token. The token is the rest of
        // the href as it stands, so a version with a dash or dots comes back whole.
        internal static bool ParseLink(string href, out string verb, out string token)
        {
            verb = token = "";
            const string Prefix = "slmod://";
            if (href == null || !href.StartsWith(Prefix, StringComparison.Ordinal)) return false;

            string rest = href.Substring(Prefix.Length);
            int slash = rest.IndexOf('/');
            verb = slash < 0 ? rest : rest.Substring(0, slash);
            token = slash < 0 ? "" : rest.Substring(slash + 1);
            return verb.Length > 0;
        }

        private void Apply(string token, bool enable)
        {
            ModListEntry e = owner.Entries().FirstOrDefault(r => r.Token == token);
            if (e != null) owner.SetEnabled(e, enable);
            confirmToken = null;
        }

        private bool OnCheckUpdates()
        {
            if (owner.Checking) return true;
            owner.CheckUpdates(() =>
            {
                // Back on the main thread, and the dialog may have been closed in
                // the meantime - the rows are kept on the mod system either way.
                if (IsOpened()) Compose();
            });
            Compose(); // picks up the "Checking…" label on the button
            return true;
        }

        private void RefreshList(bool keepScroll = false)
        {
            float was = keepScroll ? SingleComposer.GetScrollbar("scrollbar").CurrentYPosition : 0;

            SingleComposer.GetRichtext("list").SetNewText(BuildVtml(), CairoFont.WhiteSmallText());
            SingleComposer.GetDynamicText("summary").SetNewText(Summary());
            UpdateScrollbarHeight();

            // Opening or closing a confirmation changes the content height, so
            // the old offset can now be past the end of the list.
            var rt = SingleComposer.GetRichtext("list");
            float max = (float)Math.Max(0, rt.Bounds.fixedHeight - ListH);
            float now = Math.Min(was, max);

            SingleComposer.GetScrollbar("scrollbar").CurrentYPosition = now;
            OnNewScrollbar(now);
        }

        private void OnNewScrollbar(float value)
        {
            var rt = SingleComposer.GetRichtext("list");
            rt.Bounds.fixedY = 3 - value;
            rt.Bounds.CalcWorldBounds();
        }

        private void UpdateScrollbarHeight()
        {
            var rt = SingleComposer.GetRichtext("list");
            SingleComposer.GetScrollbar("scrollbar").SetHeights((float)ListH, (float)rt.Bounds.fixedHeight);
        }

    }
}
