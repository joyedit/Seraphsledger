using System;
using Vintagestory.API.Client;

namespace SeraphsLedger
{
    // In-game settings panel for The Seraph's Ledger: one toggle switch per
    // runtime feature. Toggling a switch updates the config and saves it
    // immediately; because every feature is wired up at load time, a banner makes
    // clear the change only applies after restarting the game.
    //
    // Opened via the "seraphsledgerconfig" hotkey (Ctrl+Shift+K by default) or the
    // .slconfig chat command, both registered by SeraphsLedgerModSystem.
    public class GuiDialogFeatureConfig : GuiDialog
    {
        private readonly SeraphsLedgerConfig config;

        // Title, blurb, and the getter/setter bound to a config field, in display
        // order. Adding a feature here adds a row to the dialog automatically.
        private readonly Feature[] features;

        private const double DialogW = 540;
        private const double RowH = 52;
        private const double SwitchSize = 28;
        private const double TextX = 42;
        private const double TextW = DialogW - TextX;

        private struct Feature
        {
            public string Title;
            public string Blurb;
            public Func<bool> Get;
            public Action<bool> Set;
        }

        public GuiDialogFeatureConfig(ICoreClientAPI capi) : base(capi)
        {
            config = SeraphsLedgerConfig.Load(capi);
            features = new[]
            {
                new Feature
                {
                    Title = "Trader ledger",
                    Blurb = "Record visited traders, map waypoints, Ctrl+K browser, .traders command.",
                    Get = () => config.TraderLedger,
                    Set = v => config.TraderLedger = v,
                },
                new Feature
                {
                    Title = "Ctrl + click move-all",
                    Blurb = "Ctrl + left-click dumps an inventory into the open container(s).",
                    Get = () => config.CtrlClickMoveAll,
                    Set = v => config.CtrlClickMoveAll = v,
                },
                new Feature
                {
                    Title = "Sort & Trash buttons",
                    Blurb = "Adds Sort and Trash buttons to your player inventory.",
                    Get = () => config.SortTrashButtons,
                    Set = v => config.SortTrashButtons = v,
                },
                new Feature
                {
                    Title = "Creative search auto-focus",
                    Blurb = "Focuses the search box when the creative inventory opens.",
                    Get = () => config.CreativeSearchAutofocus,
                    Set = v => config.CreativeSearchAutofocus = v,
                },
                new Feature
                {
                    Title = "Enemy fast-despawn & loot",
                    Blurb = "Hostile creatures vanish 2s after death, dropping their loot.",
                    Get = () => config.EnemyFastDespawn,
                    Set = v => config.EnemyFastDespawn = v,
                },
                new Feature
                {
                    Title = "Large containers",
                    Blurb = "Recipes for the oversized trunk, chest, labeled chest, vessel & basket.",
                    Get = () => config.LargeContainers,
                    Set = v => config.LargeContainers = v,
                },
                new Feature
                {
                    Title = "Hidden lockboxes",
                    Blurb = "Chisel a lockbox into cobblestone; a Page of Secrets finds them again.",
                    Get = () => config.HiddenLockboxes,
                    Set = v => config.HiddenLockboxes = v,
                },
                new Feature
                {
                    Title = "Silenced voices",
                    Blurb = "Mutes the chattering voice sounds of the player and traders.",
                    Get = () => config.SilencedVoices,
                    Set = v => config.SilencedVoices = v,
                },
                new Feature
                {
                    Title = "Distance challenge",
                    Blurb = "HUD travel budget that drains as you walk; can't move at 0. /steps add|set.",
                    Get = () => config.StepCountdown,
                    Set = v => config.StepCountdown = v,
                },
            };
        }

        public override string ToggleKeyCombinationCode => "seraphsledgerconfig";

        public override void OnGuiOpened()
        {
            Compose();
            base.OnGuiOpened();
        }

        private void Compose()
        {
            double top = GuiStyle.TitleBarHeight + 8;

            var bgBounds = ElementBounds.FixedSize(0, 0);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithFixedPadding(GuiStyle.ElementToDialogPadding);

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            var composer = capi.Gui.CreateCompo("seraphsledgerconfig", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("The Seraph's Ledger — Settings", () => TryClose())
                .BeginChildElements(bgBounds);

            var titleFont = CairoFont.WhiteSmallishText();
            var blurbFont = CairoFont.WhiteDetailText().WithColor(new double[] { 0.62, 0.62, 0.62, 1 });

            for (int i = 0; i < features.Length; i++)
            {
                double rowY = top + i * RowH;
                Feature feat = features[i]; // capture a copy for the closure

                var switchBounds = ElementBounds.Fixed(0, rowY, SwitchSize, SwitchSize);
                var titleBounds = ElementBounds.Fixed(TextX, rowY - 1, TextW, 24);
                var blurbBounds = ElementBounds.Fixed(TextX, rowY + 22, TextW, 22);

                composer
                    .AddSwitch(on => OnToggle(feat, on), switchBounds, "sw" + i, SwitchSize)
                    .AddStaticText(feat.Title, titleFont, titleBounds)
                    .AddStaticText(feat.Blurb, blurbFont, blurbBounds);
            }

            double warnY = top + features.Length * RowH + 8;
            var warnBounds = ElementBounds.Fixed(0, warnY, DialogW, 26);
            var warnFont = CairoFont.WhiteSmallishText().WithColor(new double[] { 1.0, 0.64, 0.26, 1 });

            composer
                .AddStaticText("Changes take effect after you restart the game.", warnFont, warnBounds)
                .EndChildElements();

            SingleComposer = composer.Compose();

            // Reflect the persisted state onto each switch (SetValue does not fire
            // the toggle handler, so this won't trigger a redundant save).
            for (int i = 0; i < features.Length; i++)
            {
                SingleComposer.GetSwitch("sw" + i).SetValue(features[i].Get());
            }
        }

        private void OnToggle(Feature feat, bool on)
        {
            feat.Set(on);
            config.Save(capi);
        }
    }
}
