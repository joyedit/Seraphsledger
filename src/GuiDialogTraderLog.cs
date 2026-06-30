using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;

namespace SeraphsLedger
{
    // Searchable, scrollable in-game browser for the recorded traders. Opened by
    // a hotkey (Ctrl+K by default). Read-only apart from the per-row [map] link,
    // which ensures a waypoint exists for that trader.
    public class GuiDialogTraderLog : GuiDialog
    {
        private readonly TraderLogModSystem owner;

        private string searchText = "";
        private int sortMode; // 0 = type, 1 = name, 2 = recent

        private const double ListW = 560;
        private const double ListH = 420;

        private static readonly string[] SortLabels = { "Sort: Type", "Sort: Name", "Sort: Recent" };

        public GuiDialogTraderLog(ICoreClientAPI capi, TraderLogModSystem owner) : base(capi)
        {
            this.owner = owner;
        }

        public override string ToggleKeyCombinationCode => "seraphsledgertraderlog";

        public override void OnGuiOpened()
        {
            Compose();
            base.OnGuiOpened();
        }

        private void Compose()
        {
            // Push content below the dialog's title bar.
            double top = GuiStyle.TitleBarHeight + 5;
            ElementBounds searchBounds = ElementBounds.Fixed(0, top, 360, 30);
            ElementBounds sortBounds = ElementBounds.Fixed(0, top, 170, 30).FixedRightOf(searchBounds, 10);

            // Fixed visible window; the richtext inside is a grandchild that grows
            // to the full content height and is clipped/scrolled within it.
            double listTop = top + 44;
            ElementBounds clipBounds = ElementBounds.Fixed(0, listTop, ListW, ListH);
            ElementBounds insetBounds = clipBounds.FlatCopy().FixedGrow(3);
            ElementBounds textBounds = ElementBounds.Fixed(0, 0, ListW - 10, ListH);
            ElementBounds scrollBounds = ElementBounds.Fixed(ListW + 6, listTop, 20, ListH);

            ElementBounds bgBounds = ElementBounds.FixedSize(0, 0);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            // Note: textBounds is deliberately NOT listed here — its height balloons
            // to the content size during compose; the dialog must size to the clip.
            bgBounds.WithChildren(searchBounds, sortBounds, insetBounds, clipBounds, scrollBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            SingleComposer = capi.Gui.CreateCompo("seraphsledgertraderlog", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("The Seraph's Ledger", () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddTextInput(searchBounds, OnSearchChanged, CairoFont.WhiteSmallishText(), "search")
                    .AddSmallButton(SortLabels[sortMode], OnToggleSort, sortBounds, EnumButtonStyle.Normal, "sortbtn")
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

        private List<TraderRecord> FilteredSorted()
        {
            var records = owner.GetRecords().AsEnumerable();

            string f = searchText?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(f)) records = records.Where(t => RecordMatches(t, f));

            switch (sortMode)
            {
                case 1: records = records.OrderBy(t => t.Name); break;
                case 2: records = records.OrderByDescending(t => t.LastVisitedDays); break;
                default: records = records.OrderBy(t => t.Wares).ThenBy(t => t.Name); break;
            }
            return records.ToList();
        }

        private static bool RecordMatches(TraderRecord t, string f)
        {
            if ((t.Name ?? "").ToLowerInvariant().Contains(f)) return true;
            if ((t.Wares ?? "").ToLowerInvariant().Contains(f)) return true;
            foreach (var e in t.Selling) if ((e.Name ?? "").ToLowerInvariant().Contains(f)) return true;
            foreach (var e in t.Buying) if ((e.Name ?? "").ToLowerInvariant().Contains(f)) return true;
            return false;
        }

        private string BuildVtml()
        {
            var list = FilteredSorted();
            if (list.Count == 0)
                return "<font color=\"#cccccc\">No traders match. Open a trader's Buy/Sell window to record one.</font>";

            var sb = new StringBuilder();
            foreach (var t in list)
            {
                sb.Append("<font weight=\"bold\">").Append(San(t.Name)).Append("</font>  ");
                sb.Append("<font color=\"#9a9a9a\">(").Append(San(t.Wares)).Append(")  ")
                  .Append(owner.RelCoords(t)).Append("</font>  ");
                // Built-in "worldmap" link protocol: opens the world map and
                // centers it on these (absolute) coords, reusing our existing
                // trader waypoint marker. Coords must be absolute, not relative.
                sb.Append("  <a href=\"worldmap://t=").Append(t.X).Append('=').Append(t.Y).Append('=').Append(t.Z)
                  .Append("\">[show on map]</a>\n");

                sb.Append("  <font color=\"#88cc88\">Sells:</font>\n");
                AppendBullets(sb, t.Selling, true);
                sb.Append("  <font color=\"#cc9988\">Buys:</font>\n");
                AppendBullets(sb, t.Buying, false);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void AppendBullets(StringBuilder sb, List<TradeEntry> entries, bool withStock)
        {
            if (entries == null || entries.Count == 0)
            {
                sb.Append("    <font color=\"#777777\">• nothing</font>\n");
                return;
            }
            foreach (var e in entries)
            {
                sb.Append("    • ").Append(San(e.Name)).Append(" — ").Append(e.Price).Append('g');
                if (withStock) sb.Append(" ×").Append(e.Stock);
                sb.Append('\n');
            }
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
            var rt = SingleComposer.GetRichtext("list");
            rt.SetNewText(BuildVtml(), CairoFont.WhiteSmallText());
            ResetScroll();
        }

        private bool OnToggleSort()
        {
            sortMode = (sortMode + 1) % SortLabels.Length;
            Compose(); // simplest reliable way to relabel the button + rebuild list
            return true;
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

        private void ResetScroll()
        {
            UpdateScrollbarHeight();
            var sb = SingleComposer.GetScrollbar("scrollbar");
            sb.CurrentYPosition = 0;
            OnNewScrollbar(0);
        }
    }
}
