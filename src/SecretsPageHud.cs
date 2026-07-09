using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SeraphsLedger
{
    // HUD readout shown only while the player holds a Page of Secrets in the
    // active hotbar slot: lists their hidden lockboxes nearest-first with
    // distance, compass direction and height difference, and highlights any
    // within a short range so the exact block in a wall is findable. The
    // positions come from the server via LockboxListPacket (owner's boxes only).
    public class SecretsPageHud : HudElement
    {
        private const string TextKey = "seraphsledgersecretstext";
        private const int RefreshMs = 250;
        private const int MaxLines = 5;

        // Arbitrary mod-unique id for the engine's block-highlight slot, plus the
        // range (in blocks) within which lockboxes get an in-world highlight.
        private const int HighlightSlotId = 9451;
        private const double HighlightRange = 24;

        // Latest owner-lockbox list, replaced wholesale by the packet handler.
        internal static List<BlockPos> Positions = new List<BlockPos>();

        private string lastText;
        private long listenerId;
        private bool highlighting;

        public SecretsPageHud(ICoreClientAPI capi) : base(capi)
        {
            Compose();
            listenerId = capi.Event.RegisterGameTickListener(_ => Update(), RefreshMs);
        }

        public override double DrawOrder => 0.1;

        private void Compose()
        {
            var textBounds = ElementBounds.Fixed(0, 0, 250, 20 * (MaxLines + 1));

            var bgBounds = ElementBounds.Fill.WithFixedPadding(2, 4);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            // Sits under the minimap, below the distance-challenge HUD's slot so
            // the two never overlap when both features are enabled.
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightTop)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 310);

            var font = CairoFont.WhiteSmallText()
                .WithOrientation(EnumTextOrientation.Center)
                .WithColor(new double[] { 0.85, 0.75, 0.55, 1 });

            SingleComposer = capi.Gui.CreateCompo("seraphsledgersecretshud", dialogBounds)
                .AddShadedDialogBG(bgBounds, false, 3, 0.6f)
                .BeginChildElements(bgBounds)
                    .AddDynamicText("", font, textBounds, TextKey)
                .EndChildElements()
                .Compose();
        }

        private void Update()
        {
            var plr = capi.World?.Player;
            var stack = plr?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            bool holdingPage = stack?.Collectible?.Code != null
                && stack.Collectible.Code.Domain == "seraphsledger"
                && stack.Collectible.Code.Path == "secretspage";

            if (!holdingPage)
            {
                Hide();
                return;
            }

            var playerPos = plr.Entity.Pos;
            var nearby = new List<BlockPos>();
            var sorted = new List<(double distSq, BlockPos pos)>();

            foreach (var pos in Positions)
            {
                double dx = pos.X + 0.5 - playerPos.X;
                double dy = pos.Y + 0.5 - playerPos.Y;
                double dz = pos.Z + 0.5 - playerPos.Z;
                double distSq = dx * dx + dy * dy + dz * dz;
                sorted.Add((distSq, pos));
                if (distSq <= HighlightRange * HighlightRange) nearby.Add(pos);
            }
            sorted.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            string text;
            if (sorted.Count == 0)
            {
                text = "No hidden lockboxes yet";
            }
            else
            {
                var lines = new List<string> { "Hidden lockboxes: " + sorted.Count };
                for (int i = 0; i < sorted.Count && i < MaxLines; i++)
                {
                    var (distSq, pos) = sorted[i];
                    lines.Add(Describe(Math.Sqrt(distSq), pos, playerPos));
                }
                text = string.Join("\n", lines);
            }

            if (!IsOpened()) TryOpen();
            if (text != lastText)
            {
                lastText = text;
                SingleComposer?.GetDynamicText(TextKey)?.SetNewText(text);
            }

            // Highlight the nearby ones through the engine's block-highlight API;
            // an empty list clears the highlight slot again.
            if (nearby.Count > 0 || highlighting)
            {
                capi.World.HighlightBlocks(plr, HighlightSlotId, nearby);
                highlighting = nearby.Count > 0;
            }
        }

        private void Hide()
        {
            if (IsOpened()) TryClose();
            if (highlighting)
            {
                var plr = capi.World?.Player;
                if (plr != null) capi.World.HighlightBlocks(plr, HighlightSlotId, new List<BlockPos>());
                highlighting = false;
            }
        }

        // "37m NE, 4 up" - distance, compass octant, height difference.
        private static string Describe(double dist, BlockPos pos, Vintagestory.API.Common.Entities.EntityPos playerPos)
        {
            double dx = pos.X + 0.5 - playerPos.X;
            double dz = pos.Z + 0.5 - playerPos.Z;
            int dy = (int)Math.Round(pos.Y + 0.5 - playerPos.Y);

            // North is -Z in Vintage Story; octants clockwise from north.
            string[] octants = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            double bearing = Math.Atan2(dx, -dz) * GameMath.RAD2DEG;
            if (bearing < 0) bearing += 360;
            string dir = octants[(int)Math.Round(bearing / 45.0) % 8];

            string vertical = dy == 0 ? "" : dy > 0 ? ", " + dy + " up" : ", " + (-dy) + " down";
            return (int)dist + "m " + dir + vertical;
        }

        public override bool ShouldReceiveKeyboardEvents() => false;
        public override bool Focusable => false;

        public override void Dispose()
        {
            base.Dispose();
            capi.Event.UnregisterGameTickListener(listenerId);
        }
    }
}
