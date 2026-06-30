using Vintagestory.API.Client;

namespace SeraphsLedger
{
    // Always-on HUD readout for the distance-challenge budget. It simply reads the
    // remaining-distance value that the server keeps on the player entity's
    // WatchedAttributes (see StepCountdown) and renders it near the top of the
    // screen, turning red when the budget is exhausted and the player is locked.
    public class StepCountdownHud : HudElement
    {
        private const string TextKey = "seraphsledgerstepstext";
        private const int RefreshMs = 200;

        // Cache so we only rebuild the texture when the displayed string changes.
        private string lastText;
        private long listenerId;

        public StepCountdownHud(ICoreClientAPI capi) : base(capi)
        {
            Compose();
            listenerId = capi.Event.RegisterGameTickListener(_ => UpdateText(), RefreshMs);
        }

        public override double DrawOrder => 0.1;

        private void Compose()
        {
            // Match the built-in minimap's footprint so we can sit centered right
            // under it: it's a 250-wide box with 2px padding (254 total), anchored
            // RightTop at offset (-10, +10). We mirror that width and X offset, then
            // drop down past the minimap's height (254) plus a small gap. The text
            // is centered within the box, so it lands dead-centre under the minimap.
            var textBounds = ElementBounds.Fixed(0, 0, 250, 20);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(2, 4);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightTop)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 272);

            var font = CairoFont.WhiteSmallText()
                .WithOrientation(EnumTextOrientation.Center)
                .WithColor(new double[] { 1, 0.85, 0.4, 1 });

            SingleComposer = capi.Gui.CreateCompo("seraphsledgerstepshud", dialogBounds)
                .AddShadedDialogBG(bgBounds, false, 3, 0.6f)
                .BeginChildElements(bgBounds)
                    .AddDynamicText("", font, textBounds, TextKey)
                .EndChildElements()
                .Compose();
        }

        private void UpdateText()
        {
            var ep = capi.World?.Player?.Entity;
            if (ep == null) return;

            double remaining = ep.WatchedAttributes.GetDouble(StepCountdown.AttrKey, 0);

            // 1 block = 1 metre in VS, so show whole blocks (floored, so it never
            // reads 1 while you're already locked).
            int blocks = (int)remaining;
            string text = remaining <= 0
                ? "0 blocks — locked"
                : $"Blocks left: {blocks}";

            if (text == lastText) return;
            lastText = text;
            SingleComposer?.GetDynamicText(TextKey)?.SetNewText(text);
        }

        // HUD dialogs open themselves and stay open; never grab the mouse.
        public override bool ShouldReceiveKeyboardEvents() => false;
        public override bool Focusable => false;

        public override void Dispose()
        {
            base.Dispose();
            capi.Event.UnregisterGameTickListener(listenerId);
        }
    }
}
