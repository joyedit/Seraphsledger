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

        // Move-matching: press the bound combination on a slot and every stack of
        // that same item moves out of that inventory, the way a shift-click moves
        // one. Default binding Shift + middle mouse, rebindable under
        // Controls > Inventory hotkeys (a mouse button or a plain key both work).
        // Unlike CtrlClickMoveAll it is useful with no container open - there it
        // gathers an item between hotbar and backpack - so it carries no
        // container guard.
        public bool MoveMatchingItems = true;
        public bool SortTrashButtons = true;
        public bool LargeVesselSort = true;
        public bool LargeVesselSortTooltip = true;
        public bool CreativeSearchAutofocus = true;
        public bool EnemyFastDespawn = true;
        public bool LargeContainers = true;
        public bool SilencedVoices = true;

        // Auto-loot for the rest of the animals: deer, sheep, pigs, hares and
        // anything else that isn't in the "hostile" spawn group, which is all
        // EnemyFastDespawn above ever touches.
        //
        // Deliberately not the same behavior as the hostile path. A drifter's
        // body is litter, so it is deleted; a deer's is a carcass you expect to
        // see. So this generates the harvest drops, spills them on the ground
        // and then hands the body to the game's own EntityBehaviorDeadDecay,
        // which leaves the carcass block the animal would have decayed into -
        // the pile of bones you get after harvesting it by hand.
        //
        // Off by default: it changes how every hunt ends, and the drops land as
        // ground items, which the game despawns on a timer. Someone who kills a
        // moose (20+ meat) and walks away loses it, where a carcass would have
        // kept it for days.
        public bool AutoLootAllCreatures = false;

        // Whether an auto-looted animal leaves behind the carcass block it
        // would have decayed into. On suits hunting: you skip the butchering
        // and the carcass still marks the kill.
        //
        // Turn it off before a mass cull. The carcass block is not replaceable,
        // so DecayNow puts one at the animal's feet, tries the four horizontal
        // neighbours if that spot is taken, and then silently places nothing -
        // so culling a pen full of pigs carpets the floor with blocks you have
        // to break one at a time, and loses the rest anyway.
        //
        // Ignored for hostiles, whose bodies are always removed outright.
        public bool AutoLootLeaveCarcass = true;

        // How long a non-hostile body lies there before it is looted, in
        // seconds. Longer than the hostiles' fixed 2s on purpose: 2 seconds is
        // not enough time to change your mind about a kill, and the delay is
        // also the window in which you can still open the carcass yourself (see
        // below). Clamped to 1..300.
        public int AutoLootDelaySeconds = 10;

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

        // Barrel ingredient portioning: a barrel's input slot only takes as many
        // items as the liquid already in it can actually use (50 lime or 5 oak
        // logs for a full 50L water barrel, 25 small hides for 50L of tannin);
        // the rest stays in your inventory instead of being swallowed into a
        // barrel that then matches no recipe at all. Purely a guard rail - it
        // never lets a barrel hold a combination vanilla wouldn't - so it ships
        // on.
        public bool BarrelIngredientPortions = true;

        // Animal tally board: a craftable chalkboard that counts the animals,
        // henbox eggs and loose eggs inside the fence it hangs within. Off
        // disables the recipe and stops placed boards from scanning.
        public bool AnimalTally = true;

        // Material swatch tool: /sw mark two corners, then Ctrl+Shift+M to swap
        // one building material for another across the selection, with undo.
        // Costs nothing until used, so it ships on. Needs the controlserver
        // privilege, and stands aside for the standalone Swatch mod when that
        // is installed (both claim the same command and hotkey).
        public bool MaterialSwatch = true;

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
