# The Seraph's Ledger

A personal bundle of small quality-of-life tweaks for Vintage Story.

- **Mod ID:** `seraphsledger`
- **Author:** Feyd
- **Version:** 1.8.0

## Features

### Trader ledger

Automatically records every trader you open the Buy/Sell window of — their
location, name, type, and full buy/sell lists (with prices and stock) as of your
last visit. Stored per-world; revisiting a trader updates their entry in place.

- **Map waypoints** — a color-coded (by type) `trader` waypoint is dropped at
  each trader the first time you visit. `.traders wp` maps any not yet marked.
- **In-game browser** — open with **Ctrl+K** (rebindable). Search by name, type,
  or item; sort by type/name/recent; click **[show on map]** to open the world
  map centered on that trader.
- **Chat** — `.traders` lists everyone; `.traders <text>` shows full wares for
  matches.
- **Export** — `.traders export` writes a Markdown summary to the mod data folder.

### Large storage containers

Bigger variants of the vanilla containers, each crafted from two of the base
container in a shapeless grid recipe:

| Block | Slots | Columns | Crafted from |
| --- | --- | --- | --- |
| Large Trunk | 100 | 10 | 2× Trunk |
| Large Chest | 45 | 9 | 2× Chest |
| Large Labeled Chest | 45 | 9 | 2× Labeled Chest |
| Large Storage Vessel | 35 | 7 | 2× Storage Vessel |
| Large Basket | 21 | 7 | 2× Basket |

They keep the vanilla appearance, footprint, and open/close sounds.

### Inventory quality-of-life

- **Ctrl + left-click** on any slot in an inventory dialog dumps every non-empty
  slot of that inventory into the open container(s), using the engine's own
  shift-click transfer. No-op when only your bare player inventory is open.
- **Sort** button in the player inventory — server-authoritative sort of your
  worn-bag contents (consolidates partial stacks, orders by item).
- **Trash** button in the player inventory — destroys whatever stack is
  currently held on the mouse cursor.

### Combat / loot

- **Hostile creatures vanish 2 seconds after they die and drop their loot
  automatically** — no knife-harvesting required. Applies to anything with the
  vanilla `hostile` spawn group (drifters, locusts, bells, shivers, bowtorn, and
  the hostile animals: wolves, bears, hyenas), plus modded mobs that use the same
  group. The drops are exactly what a full harvest would have yielded (scaled by
  the killer's `animalLootDropRate` for non-mechanical mobs); if you harvest the
  body yourself within the 2 seconds, nothing is duplicated.

### Creative inventory

- The **search box is auto-focused** when the creative inventory opens, so you
  can start typing a search term immediately without clicking it first.
  - Note: while the search box is focused, the **E** key types into the search
    instead of closing the dialog — close with **Escape** or the **X** button.

### Audio

- **Silenced character voice sounds** — both the player/seraph voices and the
  trader voice (the `saxophone` greeting heard when you approach or trade).

### Settings panel

- **Toggle features on or off in-game** — press **Ctrl+Shift+L** (rebindable) or
  run `.slconfig` to open a settings window with a switch per feature: trader
  ledger, ctrl+click move-all, Sort/Trash buttons, creative search auto-focus,
  enemy fast-despawn, large containers, silenced voices, and the distance
  challenge. Choices are saved to `ModConfig/seraphsledger/features.json`.
  - **A restart is required** for changes to take effect — the features are wired
    up at load time (Harmony patches, hotkeys, event handlers, recipe/asset
    gating), so the window warns you of this.
  - Turning **large containers** off disables their crafting recipes; any large
    container already placed in your world keeps working. Turning **silenced
    voices** off restores the vanilla player and trader voice sounds.

## Building & installing

Run `./deploy.sh` — it cleans, builds (`dotnet build -c Debug`), zips the DLL +
assets + `modinfo.json`, and drops `SeraphsLedger.zip` into the Vintage Story
`Mods/` folder.
