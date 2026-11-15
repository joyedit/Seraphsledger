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

        // Crate sweeps: three ways to empty every stack of what a crate holds out
        // of your hotbar and bags in one gesture. A crate holds one kind of thing
        // and names that kind itself, so a sweep can never put the wrong item in.
        //
        // The empty-handed one fills a gesture vanilla does nothing with at all
        // (Ctrl + Shift + right-click with nothing in hand), so it adds a
        // behaviour rather than replacing one, and ships on.
        public bool CrateSweepEmptyHand = true;

        // This one does change a vanilla interaction: Ctrl + Shift + right-click
        // holding the item puts the held stack in, and with this on the sweep
        // carries on past it. Off leaves that gesture exactly as vanilla has it.
        public bool CrateSweepHeldStack = true;

        // The move-matching binding above (Shift + middle mouse by default),
        // pressed while looking at a crate, sweeps it - the same "move all of one
        // item" idea pointed at a block instead of a slot. Registers that hotkey
        // even when MoveMatchingItems itself is off.
        //
        // A mouse binding is watched on Event.MouseDown rather than through the
        // hotkey, because vanilla's "pickblock" consumes a bare middle-mouse
        // press before any mod hotkey is reached. See CrateSweep.InitClient.
        public bool CrateSweepHotkey = true;

        public bool SortTrashButtons = true;
        public bool LargeVesselSort = true;
        public bool LargeVesselSortTooltip = true;
        public bool CreativeSearchAutofocus = true;
        public bool EnemyFastDespawn = true;
        public bool LargeContainers = true;

        // The large containers' safety net. They hold more slots than the vanilla
        // block entity defaults to, and that count lives in the block - so a save
        // loaded without this mod trims every one of them to 16 slots and drops
        // the rest for good. The ledger keeps a copy of their contents in the
        // savegame's own mod data, which outlives the mod, spots the gap when a
        // chunk comes back short, and holds what is missing until /sl rescue asks
        // for it. It never writes to a container by itself.
        public bool ContainerRescue = true;
        public bool SilencedVoices = true;

        // Bigger fuel stacks, for firepits that can be loaded for longer. A
        // firepit has no fuel limit of its own - the cap is the fuel's own
        // stack size, and vanilla is uneven about it: charcoal, coal and coke
        // stack to 64 while firewood, aged firewood and peat bricks stop at
        // 32. On, the short ones are levelled up: a wood or peat load burns
        // ~26 minutes instead of ~13.
        //
        // It is a change to the item, not to the firepit, so these stack the
        // same way everywhere - in your bags, in a chest, in a crate. Piles on
        // the ground are unaffected: a firewood or peat pile's size comes from
        // the GroundStorable behaviour's own stackingCapacity, still 32.
        //
        // In multiplayer the server's setting is the one that applies: it
        // sends its block and item definitions, stack size included, to every
        // client on connect.
        public bool BiggerFuelStacks = true;

        // How big. Nothing is ever lowered, so 64 lifts the fuels that sit
        // below it and leaves the rest at vanilla; 128 takes the whole list
        // there. Clamped to 32..999.
        public int FuelStackSize = 64;

        // Which codes count as fuel, matched on the first part of the code, so
        // every variant follows (coal-contaminated with coal) in any domain (a
        // modded firewood with the vanilla one). A listed code still has to
        // have a burn temperature to be touched, so a name collision with some
        // other mod's item can't be caught up in it.
        //
        // Everything here is something you would feed a firepit. It is a list
        // and not "everything that burns" on purpose: 197 vanilla assets have
        // a burn temperature, among them beds, barrels, baskets, armour
        // stands, candles, mannequins, waterwheels, windmill rotors and every
        // wooden gear, which stack to 1, 4 or 16 by design.
        //
        // Add to it freely. 'log' (16) and 'planks' (32) are the two obvious
        // ones left out - both are burnable, but they are building stock
        // first, and quadrupling what a trip can carry is a bigger change than
        // fuel.
        public string[] FuelStackCodes =
        {
            "firewood", "agedfirewood", "peatbrick", "charcoal", "coal", "coke",
        };

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
        // below). 7s reads as prompt while still leaving that window open; it
        // was 10s through 1.28.1, which felt like standing around. Clamped to
        // 1..300.
        public int AutoLootDelaySeconds = 7;

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

        // Floating markers over the animals a tally board counts as ready to
        // milk, shown while you are standing inside what that board counts.
        //
        // A milk-white plume over each one, not an outline around it: the
        // engine has no way to outline an entity (block highlights are the
        // only shaded highlight it offers), and faking one means re-rendering
        // the animal's mesh through a render patch, which breaks on engine
        // updates for the sake of a cosmetic.
        //
        // Off by default - it draws particles in your pen whenever you walk
        // into it, which is a thing to opt into rather than to discover. The
        // board's own "Ready to milk" count is part of the board and is not
        // affected by this.
        public bool AnimalTallyMilkMarkers = false;

        // Material swatch tool: /sw mark two corners, then Ctrl+Shift+M to swap
        // one building material for another across the selection, with undo.
        // Costs nothing until used, so it ships on. Needs the controlserver
        // privilege, and stands aside for the standalone Swatch mod when that
        // is installed (both claim the same command and hotkey).
        public bool MaterialSwatch = true;

        // Mod list browser: Ctrl+Shift+N (or .slmods) opens a sortable list of
        // every installed mod - by install date, declared game version, available
        // update, load order, load failures, enabled state, author or description,
        // either way round, and enables or disables them. Costs nothing until it
        // is opened, so it ships on.
        //
        // It is a dialog of our own rather than sort buttons on the game's mod
        // manager because that screen only exists in the main menu, where no mod
        // code is running - see the note at the top of ModList.cs.
        public bool ModListBrowser = true;

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
