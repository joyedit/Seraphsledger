using System.Collections.Generic;
using Vintagestory.API.Client;

namespace SeraphsLedger
{
    /// <summary>
    /// Left list: the materials found in the marked area. Right list: what each
    /// can become, with how many blocks the swap would touch. Opened with the
    /// seraphsledgerswatch hotkey (default Ctrl+Shift+M), once the area is
    /// marked with /sw mark.
    /// </summary>
    public class GuiDialogSwatch : GuiDialog
    {
        const int RowHeight = 25;
        const int ListHeight = 220;
        // wide enough for the long ones - "slantedroofinghalfright (33)" and its
        // near-namesakes are indistinguishable once they get clipped
        const int ColWidth = 260;
        const int RightColX = 300;
        const int DialogWidth = 580;

        readonly SwatchModSystem modsys;

        bool hasSelection;
        string selectionDesc = "";
        List<string> sources = new();
        List<int> sourceCounts = new();
        List<string> targets = new();
        List<int> targetCounts = new();
        string selectedSource;
        string selectedTarget;
        string status = "";

        readonly List<ElementBounds> sourceRowBounds = new();
        readonly List<ElementBounds> targetRowBounds = new();

        public GuiDialogSwatch(ICoreClientAPI capi, SwatchModSystem modsys) : base(capi)
        {
            this.modsys = modsys;
            Compose();
        }

        public override string ToggleKeyCombinationCode => "seraphsledgerswatch";

        void Compose()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds descBounds = ElementBounds.Fixed(0, 30, DialogWidth, 25);
            ElementBounds leftLabelBounds = ElementBounds.Fixed(0, 58, ColWidth, 20);
            ElementBounds rightLabelBounds = ElementBounds.Fixed(RightColX, 58, ColWidth, 20);

            ElementBounds leftInset = ElementBounds.Fixed(0, 80, ColWidth, ListHeight);
            ElementBounds leftClip = leftInset.ForkContainingChild(3, 3, 3, 3);
            ElementBounds leftScrollbar = ElementBounds.Fixed(ColWidth + 4, 80, 16, ListHeight);

            ElementBounds rightInset = ElementBounds.Fixed(RightColX, 80, ColWidth, ListHeight);
            ElementBounds rightClip = rightInset.ForkContainingChild(3, 3, 3, 3);
            ElementBounds rightScrollbar = ElementBounds.Fixed(RightColX + ColWidth + 4, 80, 16, ListHeight);

            ElementBounds applyBounds = ElementBounds.Fixed(0, 312, 200, 30);
            ElementBounds undoBounds = ElementBounds.Fixed(RightColX, 312, 120, 30);
            ElementBounds clearBounds = ElementBounds.Fixed(RightColX + 130, 312, 130, 30);
            ElementBounds statusBounds = ElementBounds.Fixed(0, 352, DialogWidth, 60);

            var composer = capi.Gui.CreateCompo("seraphsledgerswatch", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Swatch", () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddStaticText(selectionDesc, CairoFont.WhiteSmallText(), descBounds)
                    .AddStaticText("Material", CairoFont.WhiteSmallText(), leftLabelBounds)
                    .AddStaticText("Replace with", CairoFont.WhiteSmallText(), rightLabelBounds)
                    .AddInset(leftInset, 3)
                    .BeginClip(leftClip);

            sourceRowBounds.Clear();
            for (int i = 0; i < sources.Count; i++)
            {
                var rb = ElementBounds.Fixed(0, i * RowHeight, ColWidth - 10, RowHeight - 2);
                sourceRowBounds.Add(rb);
                int idx = i;
                composer.AddToggleButton($"{sources[i]} ({sourceCounts[i]})", CairoFont.WhiteSmallText(),
                    on => OnSourceClick(idx, on), rb, "src-" + i);
            }

            composer
                .EndClip()
                .AddVerticalScrollbar(OnSourceScroll, leftScrollbar, "srcbar")
                .AddInset(rightInset, 3)
                .BeginClip(rightClip);

            targetRowBounds.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                var rb = ElementBounds.Fixed(0, i * RowHeight, ColWidth - 10, RowHeight - 2);
                targetRowBounds.Add(rb);
                int idx = i;
                composer.AddToggleButton($"{targets[i]} ({targetCounts[i]})", CairoFont.WhiteSmallText(),
                    on => OnTargetClick(idx, on), rb, "tgt-" + i);
            }

            SingleComposer = composer
                    .EndClip()
                    .AddVerticalScrollbar(OnTargetScroll, rightScrollbar, "tgtbar")
                    .AddSmallButton("Apply", OnApply, applyBounds)
                    .AddSmallButton("Undo", OnUndo, undoBounds)
                    .AddSmallButton("Clear", OnClear, clearBounds)
                    .AddDynamicText("", CairoFont.WhiteDetailText(), statusBounds, "status")
                .EndChildElements()
                .Compose();

            SingleComposer.GetScrollbar("srcbar").SetHeights(
                ListHeight - 6, System.Math.Max(ListHeight - 6, sources.Count * RowHeight));
            SingleComposer.GetScrollbar("tgtbar").SetHeights(
                ListHeight - 6, System.Math.Max(ListHeight - 6, targets.Count * RowHeight));

            int srcIdx = selectedSource != null ? sources.IndexOf(selectedSource) : -1;
            if (srcIdx >= 0) SingleComposer.GetToggleButton("src-" + srcIdx).SetValue(true);

            int tgtIdx = selectedTarget != null ? targets.IndexOf(selectedTarget) : -1;
            if (tgtIdx >= 0) SingleComposer.GetToggleButton("tgt-" + tgtIdx).SetValue(true);

            SingleComposer.GetDynamicText("status").SetNewText(status);
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Refresh();
        }

        public void Refresh() => modsys.RequestInfo();

        public void SetSelection(SwInfoResponse info)
        {
            hasSelection = info.HasSelection;
            // the header is one line high, so anything longer than "Area 9x5x1"
            // belongs in the roomier status line at the bottom rather than
            // wrapping over the column labels
            selectionDesc = info.HasSelection ? info.SelectionDesc ?? "" : "No area marked";
            sources = info.Sources ?? new List<string>();
            sourceCounts = info.Counts ?? new List<int>();

            if (selectedSource != null && !sources.Contains(selectedSource))
            {
                selectedSource = null;
                selectedTarget = null;
                targets = new List<string>();
                targetCounts = new List<int>();
            }

            if (!hasSelection)
            {
                targets = new List<string>();
                targetCounts = new List<int>();
                // the server's reason is the precise one (nothing marked, or
                // marked too big, with the numbers)
                status = string.IsNullOrEmpty(info.SelectionDesc)
                    ? "Mark two opposite corners with /sw mark."
                    : info.SelectionDesc;
            }
            else if (sources.Count == 0)
            {
                status = "Nothing in this area has an alternative to swap to.";
            }
            else if (selectedSource == null)
            {
                status = "Pick the material you want to replace.";
            }

            Compose();

            // the world changed under the old replacement list, so ask again
            if (selectedSource != null) modsys.RequestTargets(selectedSource);
        }

        public void SetTargets(SwTargetsResponse response)
        {
            if (response.From != selectedSource) return;

            targets = response.Targets ?? new List<string>();
            targetCounts = response.Counts ?? new List<int>();
            if (selectedTarget != null && !targets.Contains(selectedTarget)) selectedTarget = null;

            if (targets.Count == 0) status = $"Nothing to swap '{response.From}' for here.";
            Compose();
        }

        public void SetStatus(string message)
        {
            status = message ?? "";
            SingleComposer?.GetDynamicText("status")?.SetNewText(status);
        }

        void OnSourceClick(int idx, bool on)
        {
            if (!on)
            {
                // clicking the picked row again keeps it picked
                SingleComposer.GetToggleButton("src-" + idx).SetValue(true);
                return;
            }
            for (int i = 0; i < sources.Count; i++)
            {
                if (i != idx) SingleComposer.GetToggleButton("src-" + i).SetValue(false);
            }
            selectedSource = sources[idx];
            selectedTarget = null;
            targets = new List<string>();
            targetCounts = new List<int>();
            SetStatus($"Loading replacements for {selectedSource}...");
            Compose();
            modsys.RequestTargets(selectedSource);
        }

        void OnTargetClick(int idx, bool on)
        {
            if (!on)
            {
                SingleComposer.GetToggleButton("tgt-" + idx).SetValue(true);
                return;
            }
            for (int i = 0; i < targets.Count; i++)
            {
                if (i != idx) SingleComposer.GetToggleButton("tgt-" + i).SetValue(false);
            }
            selectedTarget = targets[idx];
            SetStatus($"{selectedSource} -> {selectedTarget} on {targetCounts[idx]} block(s). Apply to try it.");
        }

        void OnSourceScroll(float value) => Scroll(sourceRowBounds, value);

        void OnTargetScroll(float value) => Scroll(targetRowBounds, value);

        static void Scroll(List<ElementBounds> rows, float value)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].fixedY = i * RowHeight - value;
                rows[i].CalcWorldBounds();
            }
        }

        bool OnApply()
        {
            if (selectedSource == null || selectedTarget == null)
            {
                SetStatus("Pick a material on the left and a replacement on the right.");
                return true;
            }
            modsys.SendApply(selectedSource, selectedTarget);
            return true;
        }

        bool OnUndo()
        {
            modsys.SendUndo();
            return true;
        }

        bool OnClear()
        {
            modsys.SendClear();
            return true;
        }
    }
}
