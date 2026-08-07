using System;
using Vintagestory.API.Common;

namespace SeraphsLedger
{
    // Per-feature on/off switches for the mod, edited through the in-game settings
    // dialog (GuiDialogFeatureConfig) and read once at startup by each ModSystem to
    // decide what to wire up. Because the features are applied via Harmony patches,
    // hotkey/command registration and event subscriptions - all of which happen at
    // load time - changes only take effect after a restart.
    //
    // Stored as a single JSON file in the ModConfig folder. In singleplayer the
    // client and the integrated server share that folder, so one file drives both.
    // (On a dedicated server the server reads its own copy; the server-only feature
    // here, EnemyFastDespawn, is governed by the server's file.)
    //
    // Two of these gate asset-derived features that can't simply be toggled by
    // adding/removing an asset at runtime, so each is enforced in code instead:
    //   - SilencedVoices: a client Harmony patch suppresses voice playback in
    //     EntityTalkUtil (the silent-ogg overrides have been removed).
    //   - LargeContainers: the crafting recipes for the large containers are
    //     stripped from the registered grid recipes when off. The blocks stay
    //     registered, so containers already placed in a save keep working - they
    //     just can't be crafted anymore.
    public class SeraphsLedgerConfig
    {
        public bool TraderLedger = true;
        public bool CtrlClickMoveAll = true;
        public bool SortTrashButtons = true;
        public bool CreativeSearchAutofocus = true;
        public bool EnemyFastDespawn = true;
        public bool LargeContainers = true;
        public bool SilencedVoices = true;

        // Ice-cooled cellars: enabled by default deliberately - turning it off
        // reverts cellar ice to vanilla behavior (melts by outdoor climate and
        // floods the room), so a default-off update would silently cost
        // existing worlds their stocked ice. Ice blocks placed in a proper cellar
        // chill it below the vanilla 5°C floor, slowing food spoilage (about 2x shelf
        // life at FullBlocks). MinBlocks is where the effect starts, FullBlocks
        // where it maxes out, Temp the effective temperature at full effect
        // (clamped to -5..5; below -5 the game's own perish floor applies).
        // MinMeltChance is the floor on the per-random-tick melt roll for ice
        // in a cellar (a roll lands on a given block about every ~10 real
        // minutes), so even a fully iced cellar slowly loses blocks - at the
        // default about one block per ~17 hours of real play time. 0 makes
        // fully-chilled cellar ice last forever.
        public bool IceCooledCellars = true;
        public int IceCellarMinBlocks = 4;
        public int IceCellarFullBlocks = 10;
        public float IceCellarTemp = -3f;
        public float IceCellarMinMeltChance = 0.01f;

        // Distance-challenge mode is off by default - it's an opt-in challenge run,
        // not a tweak everyone wants. StartMeters seeds a player's budget the first
        // time the feature ever sees them, so they aren't instantly frozen.
        public bool StepCountdown = false;
        public double StepCountdownStartMeters = 1000;

        public const string FileName = "seraphsledger/features.json";

        // Loads the config, writing out a default file the first time so the player
        // has something to find on disk. Never returns null.
        public static SeraphsLedgerConfig Load(ICoreAPI api)
        {
            SeraphsLedgerConfig cfg = null;
            try
            {
                cfg = api.LoadModConfig<SeraphsLedgerConfig>(FileName);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[seraphsledger] config load failed, using defaults: {0}", e.Message);
            }

            if (cfg == null)
            {
                cfg = new SeraphsLedgerConfig();
                cfg.Save(api);
            }
            return cfg;
        }

        public void Save(ICoreAPI api)
        {
            try
            {
                api.StoreModConfig(this, FileName);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[seraphsledger] config save failed: {0}", e.Message);
            }
        }
    }
}
