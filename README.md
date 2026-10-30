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

> **Do not load a world holding large containers without this mod.** Their slot
> count lives in the block, so with the mod absent the game falls back to the
> vanilla block entity's 16 slots, reads only the first 16, and writes the
> container back without the rest — silently, with no error, for every chunk
> that loads in that session. This is the game's own behaviour, before any mod
> gets a say, and it is the same for every mod that widens a container.
>
> The mod keeps a safety net against exactly this: a copy of every large
> container's contents in the savegame's own mod data, which survives the mod
> being gone. A container that comes back trimmed is **refilled automatically**,
> with a chat line to say so — but only when the shortfall carries the engine's
> fingerprint (nothing left at or past slot 16, where the trimming cuts). A
> container that is merely emptier than the record is held instead: `/sl rescue`
> lists those and puts one back on request, `/sl forget <n>` drops an entry, and
> `/sl status` reports what is on record. Restoring only ever fills empty slots,
> never over what is there now. It is switchable in the settings panel as
> **Large container rescue**, and it only protects what it has already recorded:
> a container trimmed before the mod was updated to 1.25.0 cannot be recovered.
>
> Turning the **large containers** feature off in the settings panel is safe —
> it only stops the recipes; placed containers keep working.

### Inventory quality-of-life

- **Ctrl + left-click** on any slot in an inventory dialog dumps every non-empty
  slot of that inventory into the open container(s), using the engine's own
  shift-click transfer. No-op when only your bare player inventory is open.
- **Shift + middle mouse** on a slot moves every stack of *that one item* out of
  that inventory — all your flax, none of your ore — again through the engine's
  shift-click transfer, so stacks land exactly where shift-clicking each of them
  would have put them.
  - **Rebindable** under *Settings → Controls → Inventory hotkeys* as
    "Move all of one item". Bind it to any mouse button, with or without
    Ctrl/Shift/Alt, or to a plain key — a key binding acts on the slot the mouse
    is hovering.
  - Works with nothing else open, where it gathers an item between your hotbar
    and backpack. Matches on the item itself, ignoring durability and freshness,
    so a mixed-wear pile of copper axes still moves as one.
- **Crate sweep** — one gesture puts *every* stack of what a crate holds into
  it, out of your hotbar and your worn bags. A crate takes one kind of thing and
  names that kind itself, so a sweep can never put the wrong item in — hold
  nothing and it reads the kind off the crate's own contents.
  - **Ctrl + Shift + right-click with an empty hand.** Walk up to the flint
    crate, click, and every flint stack you are carrying goes in. Vanilla does
    nothing at all for that gesture, so this adds a behaviour rather than
    replacing one.
  - **Ctrl + Shift + right-click holding the item** — vanilla puts the held
    stack; the sweep carries on past it and empties the rest. This one *does*
    change a vanilla interaction, so it has its own switch in the settings panel.
  - **Shift + middle mouse** (the "move all of one item" binding above) while
    looking at a crate does the same thing, so the one shortcut means "move all
    of this" whether it is pointed at a slot or at a crate. Aim at anything that
    isn't a crate with something in it and the click still picks the block, as
    it always did.
  - An **empty** crate has no kind yet, so a sweep leaves it alone; put the first
    stack in — Ctrl + Shift + right-click holding it, as above — and the sweep
    follows that stack in. Crates that are **retrieve-only** (the pre-filled ones
    in ruins) are never swept into.
  - When the crate fills up partway through, what is left over stays in your
    **hotbar** rather than in a bag, where you can see it.
- **Sort** button in the player inventory — server-authoritative sort of your
  worn-bag contents (consolidates partial stacks, orders by item).
- **Trash** button in the player inventory — destroys whatever stack is
  currently held on the mouse cursor.
- **Sort by freshness** button on the Large Storage Vessel — lays the vessel out as a food
  store: unsealed crocks first, then sealed crocks, each ordered by the "fresh
  for" time in its tooltip, soonest to spoil first, then empty crocks. Behind
  them comes every other food that spoils — fruit, vegetables, eggs, grain,
  flour, bread, cheese, meat, a bowl of a meal — in one queue, again soonest to
  spoil first, so the grid reads in the order you would eat it. Then the foods
  that keep: what can be eaten as it is, like honeycomb, and what food is made
  with, like salt. Everything else follows by item. Partial stacks are merged the
  game's own way, so perishables average their freshness.

### Barrels

- **Barrel ingredient portions** — the item slot of a barrel only takes as many
  items as the liquid already in it can turn into something; the rest stays in
  your inventory. Barrel recipes are exact ratios, so a full 50 L barrel of water
  takes exactly **50 lime** (limewater) or exactly **5 oak/acacia logs** (weak
  tannin), and 50 L of tannin takes at most **25 small hides**. Vanilla accepts
  the whole stack and then matches no recipe at all.
  - The numbers come from the barrel recipes themselves, so modded recipes are
    covered too.
  - Applies to click, shift-click, ctrl+click move-all and hopper feeds alike.
  - Only applies once the barrel holds a liquid — that liquid is what fixes the
    ratio; an empty barrel behaves as before.

### Animal husbandry

- **Animal Tally Board** — a chalkboard (2 planks + 1 charcoal) you hang on a
  wall inside a pen. Look at it and the tooltip lists the animals inside the
  fence, grouped by species and broken down by kind (hens, roosters, pullets,
  cockerels, chicks; rams, ewes, lambs; ...), plus eggs in henboxes (and how
  many are fertile) and eggs lying loose — both the ones laid on the floor when
  every henbox is taken and any dropped on the ground.
  - The pen is found by walking the ground outward from the board the way an
    animal could: fences, walls, closed gates and closed doors stop it, open
    doorways don't, so coops attached to the pen count as part of it.
  - If that walk reaches 100 blocks out, the pen isn't closed; the board says
    so and counts everything within 100 blocks instead, which makes it a handy
    gap finder too.
  - Re-counts every 10 seconds while a player is within 64 blocks.
  - **Right-click the board** to mark the pen it counts: the outline of a
    closed pen, or, when it isn't closed, the trail it followed out - which
    crosses the fence line right at the gap.
  - **Or tell it the region outright** — `/tally mark` one corner, `/tally mark`
    the opposite one (each lights up as a pillar, with the box between them
    outlined), then **sneak + right-click** a board to bind it. Binding clears
    the marks and their highlights; `/tally clear` forgets them without binding,
    and sneak + right-click again unbinds the board back to walking the fence.
    - Two corners at the same height read as a footprint: the box grows up to
      whatever stands in it and down to the ground beneath it (up to 16 blocks
      below where you marked), so you can mark a pen from its wall top without
      leaving the animals underneath outside the box. Corners at different
      heights are taken literally, for a loft or a stacked coop.
    - The board's tooltip names the footprint and the height range it ended up
      with — `Region: 22 x 37, Y 173-183 (marked)`.
  - Hang it on either face of a wall; if the walk escapes from the side it
    faces, the board tries the other side of that wall.

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

### Mod list browser

**Ctrl+Shift+N** (rebindable) or `.slmods` opens a sortable list of every mod
installed, which the game's own mod manager only ever shows in file-name order.
Sort by any of these, ascending or descending:

| Sort | Ascending order |
| --- | --- |
| Name | A → Z by display name |
| Install date | Oldest file first; unreadable dates last |
| Game version | Lowest `game` dependency first; "any version" last |
| Update available | Not checked, then up to date, then updates |
| Load order | The order the game loads them in, dependencies first |
| Load problems | Loads fine first, then whatever failed and why |
| Enabled | Disabled first, then enabled |
| Author | A → Z; mods with no author recorded last |
| Description | A → Z; mods with no description last |

Descending reverses the chosen key but keeps the A → Z tiebreak, so rows that
are equal stay in a readable order, and the value you sorted by is highlighted
on every row. The search box filters on name, mod id, file name, author and
description. `.slmods <sort> [desc]` opens the list already in that order.

- **Rescan** re-reads the mod folders, so a mod dropped in while the game runs
  shows up without a restart.
- **Check updates** asks the mod database for the newest release of each mod
  tagged for this game's version series, and flags the ones you are behind on.
  Mods built locally and ahead of anything published read as *ahead of x.y.z*
  rather than up to date. It is one request per mod, so it only ever runs when
  you press the button, and the answers are kept for the session.
- **Enable or disable** a mod with the `[enable]` / `[disable]` link on its row.
  The game keeps that state in the client settings as `modid@version` entries,
  and editing that list is the whole of what its own mod manager does on a
  click — so it works just as well from in-game. It takes a **restart** either
  way, so a row toggled this session reads *[enabled on restart]* or
  *[disabled on restart]*, and the summary counts how many are waiting.
  - **Disabling asks twice.** The confirmation names the mods that depend on
    this one and will stop loading with it, warns if the mod declares itself
    part of the base game, and reminds you that blocks and items it adds read
    as unknown in worlds built with it. Enabling takes one click.
  - Installing and deleting mods are left to the game's mod manager — those
    move files the running game is reading.
  - The setting is pinned to the version, so updating a disabled mod brings it
    back enabled. That is the game's own behaviour, kept as-is.

> It is a window of its own rather than sort buttons on the game's mod manager
> because that screen (`GuiScreenMods`) exists only in the main menu, and no mod
> code is running there — the game reads mod *infos* at startup but does not
> create mod systems until a world loads, and the escape menu has no Mods
> button. So there is no moment at which a mod could patch that list. What sits
> *behind* it is reachable from in-game, which is what this uses: the same
> mod-info + dependency pass the main menu runs, against a mod loader of its
> own, and the same `ClientSettings.DisabledMods` list for the switches.

### Settings panel

- **Toggle features on or off in-game** — press **Ctrl+Shift+L** (rebindable) or
  run `.slconfig` to open a settings window with a switch per feature: trader
  ledger, ctrl+click move-all, move all of one item, the three crate sweeps,
  Sort/Trash buttons, creative search auto-focus,
  enemy fast-despawn, large containers, silenced voices, barrel ingredient
  portions, the mod list browser, and the distance challenge. Choices are saved to `ModConfig/seraphsledger/features.json`.
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
