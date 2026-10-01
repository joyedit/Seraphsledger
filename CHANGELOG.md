# Changelog

All notable changes to The Seraph's Ledger are documented here.

## 1.24.1 — 2026-09-30

### Added
- **The large vessel's freshness sort now lays out all of the food, not just the
  crocks.** Fruit, vegetables, eggs, grain, flour, bread, cheese, meat and a
  bowl of a meal now follow the crocks in one queue, soonest to spoil first,
  ordered by the same "fresh for" time their own tooltips show — so the whole
  grid reads in the order you would eat it, not just its first row. Behind that
  queue go the foods that keep, and behind those everything else by item, as
  before.
  - **The foods that keep** are what can be eaten as it is with no spoil time on
    it, like honeycomb, and what food is made with, like salt. Salt is taken
    from the barrel recipes that make food rather than named in the code, so a
    mod's own curing agents and seasonings land with the food too, while
    quicklime and tanning bark stay out of it.
  - A bowl or a pot of a meal is read like a crock is, then queues with the
    loose food rather than with the crocks: a crock is a larder, a bowl is a
    portion.
  - A sort is now purely a look at the food. The "fresh for" figure comes from
    the engine's own transition update, which is a write as well as a read, so
    every reading is taken off a copy and the food in the vessel is left for the
    vessel to tick — the same decay, at the same rate, a few seconds later.
    (Nothing spoiled faster for this before: the sort only ever applied what the
    vessel's own ten-second tick was about to apply anyway.)

## 1.23.0 — 2026-09-26

### Added
- **Move all of one item.** Hold **Shift and press the middle mouse button** on
  a slot and every stack of that same item leaves that inventory — all your
  flax, none of your ore. It rides the engine's own shift-click transfer, so
  each stack lands exactly where shift-clicking it would have put it: into the
  open container from your pack, back into your pack from the container.
  - **Rebindable** under *Settings → Controls → Inventory hotkeys* as "Move all
    of one item". Any mouse button, with or without Ctrl/Shift/Alt — or a plain
    key, which acts on whichever slot the mouse is hovering.
  - Unlike Ctrl + left-click move-all it needs no container open; with just your
    inventory up it gathers an item between hotbar and backpack.
  - It matches the item, not the stack, so durability and freshness are ignored
    — a mixed-wear pile of copper axes moves as one.
  - Clicking an empty slot still does the ordinary thing, since an empty slot
    names no item to match.
- A switch for it in the settings panel (Ctrl+Shift+L), beside Ctrl + click
  move-all.

### Fixed
- **The tally board's breakdown line no longer runs off the edge of the
  tooltip** (`Hen 18 · Rooster 14 · Pullet 2 · Cockerel 1` was losing its last
  character). The breakdown lines were indented with spaces, and the block-info
  box sizes itself to the widest line using ink extents — which leave leading
  spaces out of the width but still draw them, so an indented line overhangs by
  exactly its indent. They're dimmed instead of indented now, which reads the
  same and measures honestly.
- **Binding a tally board to a marked region now puts the marks out.** The
  corner pillars and the violet box stayed lit over the pen after `sneak +
  right-click` bound them, until you thought to run `/tally clear` — which read
  as the binding not having taken. The corners are forgotten the moment a board
  takes the box.
- **The tally board counts eggs laid on the floor.** A hen with no henbox free
  lays where she stands, and that lands as a *block* rather than a dropped
  item — so the board kept reporting `0 loose` with eggs plainly sitting on
  the floorboards. Floor eggs now count, singly or in piles of two and three;
  a broken one counts for nothing, being a mess rather than an egg.
- **A marked region now reaches the ground under its footprint.** Two corners
  marked at the same height grew the box upward to whatever stood in the
  footprint, but pinned its floor at the height you happened to be standing —
  so marking a pen from the wall top, or from the barn floor above it, left the
  animals down in the pen outside the box and the board saying `No animals`. A
  henbox up at marking height still counted, which made it look like the region
  was being ignored rather than floating above the pen. The floor now drops to
  the lowest ground in the footprint, up to 16 blocks below where you marked.
  Corners at different heights are still taken literally, for a loft or a
  stacked coop.
  - **Boards already bound keep their old box** — re-mark and re-bind to pick
    up the new floor.
- **The region's height is now on the label.** `Region: 22 x 37, Y 173-183`
  instead of `22 x 7 x 37`, in the tooltip and in the `/tally mark` reply: when
  a board counts nothing, where the box's floor sits is the first thing worth
  seeing.

## 1.22.1 — 2026-09-22

### Added
- **Marked corners glow.** `/tally mark` now lights the block you marked as a
  pillar — green for the first corner, amber for the second — and once both
  are set the box they describe is outlined in violet, so you can see what
  you're about to bind before you bind it. Same shapes the swatch tool uses,
  so a marked pen reads like a marked build. `/tally clear` puts them out.
- **Ctrl+Shift+X while looking at a tally board** outlines what it counts,
  without having to right-click it. Rebindable under Controls as "Show tally
  board region". Bound boards show the region, unbound ones show the pen walk
  exactly as right-click does, and it clears itself after 25 seconds.

## 1.22.0 — 2026-09-22

### Added
- **The tally board can count a region you mark, instead of working the pen
  out for itself.** `/tally mark` a corner, `/tally mark` the opposite one,
  then sneak + right-click a board to bind it. Sneak + right-click again
  clears it and the board goes back to walking the fence.
  - The walk has to guess where a pen ends, and says so when it can't — a
    gate standing open or a doorway two blocks wide is enough. A marked
    region is simply told, so it counts the same animals every time.
  - Mark **two corners at the same height** and the top rises to whatever
    stands in the footprint, so you walk the corners of a pen at ground level
    rather than flying up to the fence tops. Corners at different heights are
    taken literally, for a loft or a stacked coop.
  - The tooltip shows `Region: 41 x 12 x 26 (marked)` in place of the
    open/closed line, and right-click outlines the region's footprint.
  - It counts a box, so it is honest rather than clever: an L-shaped pen will
    include the notch outside the fence, and a region that overhangs a wall
    will count what's on the other side. Leave a board unbound for the walk.
  - Cheaper than the walk, too: a box test per entity instead of a flood
    fill every ten seconds.

## 1.21.1 — 2026-09-22

### Changed
- **The tally board's tooltip is just the tally now.** It was repeating the
  "hang it inside a pen..." instructions under every count. That text still
  appears on the held item and in the handbook, where it is actually useful.
- **"Pen open" fits on one line**, so the tooltip stops reflowing to two.

### Fixed
- **Right-clicking an open pen now shows the pen and the gap**, rather than a
  trail wandering off into the distance. The walk can't know where an open pen
  ends, so the board takes the doorway-looking points along its escape route -
  places where the walk threaded between two blocks it couldn't stand on -
  seals each in turn and re-walks. The first one that closes the pen is the way
  out: it is marked in blue, and the green outline around it is the pen you'd
  have if you shut it. Falls back to the old trail when no single block closes
  the pen, which means a whole side is missing or the gap is wider than a
  doorway.

## 1.21.0 — 2026-09-22

### Added
- **The tally board counts pregnant animals.** A "Pregnant" line under the
  species breakdown, with a per-species split. It's a separate line rather
  than part of the per-kind breakdown, because only the breeding females can
  be pregnant and folding it in would double-count them against their own
  kind.

### Fixed
- **Wolf and hyena pups were never auto-looted.** Auto-loot recognised a
  hostile by its spawn group, but cubs and pups are spawned by the multiply
  behaviour rather than by world or runtime spawning, so their entity types
  carry no `spawnConditions` at all and the test found nothing to read —
  their bodies just stayed where they fell while every adult vanished. The
  hostile groups are now collected once from the entity registry, so a pup
  is recognised by its family ("wolf", "bear", "hyena", "drifter") instead of
  by a spawn rule it was never given.

## 1.20.1 — 2026-09-22

### Added
- **Auto-loot leaves a carcass** (on by default), so a mass cull doesn't bury
  the pen. The carcass block isn't replaceable, so dead-decay drops one at the
  animal's feet, tries the four horizontal neighbours if that spot is taken,
  and then silently places nothing — culling a pen of two hundred pigs carpets
  the floor with blocks to break one at a time and loses the overflow anyway.
  Turn it off and auto-looted animals are removed like hostiles: loot on the
  ground, no block. Hostiles ignore the setting; their bodies always go.

## 1.20.0 — 2026-09-21

### Added
- **Auto-loot all animals** (off by default). Until now the death auto-loot
  only touched creatures in the "hostile" spawn group — drifters, locusts,
  bells, wolves, bears, hyenas — and left deer, sheep, pigs, hares and the
  rest to be butchered by hand. Turn this on and every animal you kill
  drops its harvest loot on the ground.
  - **The body is not deleted.** A drifter's corpse is litter and is still
    removed outright, but an animal's is handed to the game's own dead-decay
    behaviour, so it leaves the carcass block it would have decayed into —
    the pile of bones you get after harvesting it by hand.
  - The delay is configurable (`AutoLootDelaySeconds`, default 10, clamped
    to 1–300) rather than the hostiles' fixed 2 seconds, because 2 seconds
    is not long enough to change your mind about a kill.
  - **Opening the carcass yourself wins.** If anyone has the carcass window
    open when the timer fires, auto-loot skips that body completely and
    leaves it to vanilla. Emptying an inventory out from under an open GUI
    is exactly how you get items that look like they come back.
  - An animal that died without a killer — fell, drowned, starved, or lost a
    fight with a predator — is left alone, for the reason given in 1.19.4.

## 1.19.4 — 2026-09-20

### Fixed
- **Fast enemy despawn destroyed the loot of any hostile that died without a
  killer.** The 2-second cleanup generated the harvest drops, spat them on the
  ground and removed the body. But generating those drops needs the player who
  landed the kill (the loot is scaled by their `animalLootDropRate`), so when
  there was no such player — a wolf that fell, drowned, burned or lost a fight
  with another predator — that step was skipped while the body was removed
  anyway, taking every hide, scrap of meat and lump of fat with it. The same
  hole swallowed the loot whenever drop generation threw.
  The body is now only removed once its loot is actually on the ground.
  Anything the cleanup cannot roll is left standing to be harvested by hand.

## 1.19.3 — 2026-09-20

### Fixed
- **Animal tally board** still called a closed pen open when hung on the
  outside face of a wall. The 1.19.2 retry stepped to the far side of the
  wall by negating the board's `side` variant, but that variant already
  points from the board into the wall it hangs on, so the second walk
  started two blocks further out on the same side and escaped exactly like
  the first. It now steps toward the wall, and through walls up to two
  blocks thick.

## 1.19.2 — 2026-09-20

### Fixed
- **Animal tally board** called a closed pen open when the board was hung on
  the outside face of a wall - the wall of a gatehouse, say. The walk started
  outside the fence and escaped at once. When the first walk escapes, the
  board now tries again from the far side of the wall it hangs on and keeps
  that result if it closes, so which face you hang it on no longer matters.

### Added
- **Right-click the tally board to mark what it sees.** A closed pen has its
  outline marked for 25 seconds. An open one gets the trail the count followed
  out of the pen, which crosses the fence line exactly at the gap, and a chat
  line with the coordinates it ended at.

## 1.19.1 — 2026-09-19

### Fixed
- **Animal tally board** undercounted chickens. A bird leaning on a fence has
  its centre over the fence block (a fence's collision is a thin rail down the
  middle of its block), so it fell outside the pen; the board now also samples
  around each animal's footprint. A hen sitting in a henbox the walk can't
  reach (a stacked row, a raised shelf) now counts too, and every board scans
  on the same 10-second beat, so two boards in one pen agree instead of
  showing snapshots up to 10s apart.

### Changed
- The settings panel is laid out in two columns. One column of every feature
  had grown taller than a 1080p screen.

## 1.19.0 — 2026-09-18

### Added
- **Animal Tally Board** — a slate chalkboard, hung like a painting, that
  counts the animals in its pen. Look at it and the tooltip shows each species
  inside the fence with a breakdown by kind (Hen 14 · Rooster 2 · Pullet 3 ·
  Chick 3), eggs in henboxes with how many are fertile, and eggs lying loose.
  Crafted from 2 planks and 1 charcoal.
  - Finds the pen by walking the ground outward from the board the way an
    animal could: fences, walls, closed gates and closed doors stop it, open
    doorways let it through, so coops attached to the pen are part of it. One
    board covers a pen with several coops.
  - If the walk reaches 100 blocks out, the pen has a gap or an open gate. The
    board says so and counts everything within 100 blocks instead.
  - Counts any animal (chickens, pigs, goats, sheep, ...), not players,
    traders or drifters.
  - Re-counts every 10 seconds while a player is within 64 blocks; the scan
    runs on the server and only the totals are sent to players.
  - Toggleable in the settings panel (Ctrl+Shift+L). Off removes the recipe
    and stops placed boards counting.

## 1.18.0 — 2026-09-18

### Added
- **Large storage vessel sort** — a "Sort by freshness" button under the
  Large Storage Vessel's slot grid lays it out as a food store:
  1. unsealed crocks holding food (they spoil at the normal rate, so eat these
     first)
  2. sealed crocks holding food
  3. empty crocks
  4. everything else, by item
  - Within each crock group, the crock that will spoil soonest comes first,
    going by the same "fresh for" time its tooltip shows (the seal and the
    vessel/cellar rate included).
  - Partial stacks are merged the game's own way, so perishables average their
    freshness rather than keeping the fresher stack's timer.
  - Sorted on the server, and only for a player who has that vessel open.
  - Hovering the button explains the order; the hover text has its own
    switch in the settings panel.
  - Toggleable in the settings panel (Ctrl+Shift+L).

## 1.17.0 — 2026-08-29

### Added
- **Barrel ingredient portions** — a barrel's item slot now takes only as much
  as the liquid already in it can actually use, and leaves the rest in your
  inventory. Barrel recipes are exact ratios, so a full 50 L barrel of water
  wants exactly 50 lime for limewater and exactly 5 oak/acacia logs for weak
  tannin; drop a whole stack in and vanilla swallows the lot, then matches no
  recipe at all until you work out how much to take back out.
  - Works out the number from the recipes themselves, so it covers every barrel
    recipe (50 L of tannin caps at 25 small hides, 12 medium, 8 large) and any
    added by other mods, rather than a hard-coded list.
  - Applies to every way items go in: click, shift-click, ctrl+click move-all,
    and hopper feeds. The slot simply stops accepting once it holds enough.
  - Only kicks in when the barrel already holds a liquid — that liquid is what
    fixes the ratio. An empty barrel is left alone, so loading the solids first
    still works, and too little liquid for even one batch still lets a single
    portion in rather than refusing everything.
  - Toggleable in the settings panel (Ctrl+Shift+L).

## 1.16.1 — 2026-08-22

- Compatibility release for Vintage Story 1.22.7. Rebuilt against the 1.22.7
  assemblies; no code changes.

## 1.16.0 — 2026-08-12

### Added
- **Material swatch tool** — try a different material on a build before
  committing to it. `/sw mark` two opposite corners, look over the highlighted
  area, then **Ctrl+Shift+M**: the left list holds the materials found in the
  selection, the right list what each can become and how many blocks would
  change. Apply, and Undo if you don't like it (last 25 swaps per player).
  - Swaps are worked out from the block codes themselves — a code is read as
    hyphen-separated tokens and the registry is asked which single-token
    changes exist — so every finish of a material converts in one action
    (cobble, bricks, polished, stairs, slabs), stair facings survive, and
    modded blocks are picked up without a list to maintain.
  - Only blocks somebody placed are listed or swapped. Grass, soil, gravel,
    sand, ore, raw stone, water and plant life stay out of it, so the material
    list holds the build rather than the ground under it, and a wall can be
    restyled without repainting the hillside behind it.
  - Materials are listed above kinds of block, since restyling a build usually
    means the material rather than the shape.
  - Chiselled blocks are re-materialed in place, keeping their carving — and
    are the one place raw stone still counts, since somebody carved it. Blocks
    holding contents or state (chests, doors) are left alone and reported.
  - Also driveable from chat: `/sw list`, `/sw options [material]`,
    `/sw swap <from> <to>`, `/sw undo`, `/sw unmark`. Needs the `controlserver`
    privilege.
  - Available on its own as the standalone **Swatch** mod; install it alongside
    this one and it takes over the feature, the same way Ice Cellar does.

## 1.15.2 — 2026-08-07

### Fixed
- **The ice-cellar hand-off in 1.15.1 never actually triggered.** It looked for
  the standalone mod under the id `icecellar`, but that id was already taken on
  the ModDB, so Ice Cellar had to register as **`icecooledcellars`**. The check
  keys off the modid, so it silently never matched: running 1.15.1 together
  with the released Ice Cellar meant both mods patched the perish rate and
  chilled the cellar twice — roughly halving spoilage twice over, with nothing
  in the log to explain it. **If you run both mods, update to 1.15.2.**

## 1.15.1 — 2026-08-07

### Changed
- **Ice-cooled cellars now defer to the standalone Ice Cellar mod.** The
  feature has been released on its own for players who want it without the rest
  of the bundle; it stays in Seraph's Ledger unchanged for everyone else. Both
  mods patch the same vanilla methods, so with the standalone installed this
  mod leaves the feature to it (logged at startup) rather than chilling cellars
  twice. Ice Cellar picks up any values you tuned in `features.json` on its
  first run.
  - Superseded by 1.15.2 — the check named the wrong modid and never fired.

## 1.15.0 — 2026-08-06

### Changed
- **Ice-cooled cellars ships enabled by default.** Turning it off doesn't
  just remove the chill — cellar ice reverts to vanilla behavior, melting by
  the outdoor climate and flooding the room with water sources — so shipping
  it off would silently cost updating players their stocked cellars. Anyone
  who prefers vanilla behavior can switch it off in the in-game settings
  (Ctrl+Shift+L or `.slconfig`).

## 1.14.1 — 2026-08-02

### Fixed
- **Ice lining a cellar's walls, floor or ceiling now counts.** The ice scan
  covered only the room's interior bounding box, so wall-lining builds (the
  natural way to do it) registered almost no blocks. The scan now includes a
  one-block shell around the room; both wall ice and free-standing ice inside
  count.
- **Wall ice no longer melts at the lonely-single-block rate.** A solid ice
  block isn't part of the room's air space, so the melt check saw a degenerate
  one-cell "room" instead of the cellar and never saw the other ice. The melt
  logic now resolves the real cellar through the ice block's open neighbor
  cells, so a well-stocked cellar protects every block in and around it.
  Fully buried ice (e.g. the outer layer of a double wall) is treated as
  insulated and doesn't melt at all.

### Changed
- Default `IceCellarMinMeltChance` lowered 0.025 → 0.01: a fully chilled
  cellar now loses about one ice block per ~17 hours of real play time
  (was ~7). If your `features.json` already stores the old 0.025, lower it by
  hand or delete the line to pick up the new default.

## 1.14.0 — 2026-08-02

### Added
- **Cellar-aware ice melting** (phase 2 of ice-cooled cellars). Vanilla melts
  ice by the *outdoor* climate — cellar ice melted as fast as ice in the
  summer sun, and every melted block left behind a full water source. Now ice
  in a proper cellar melts based on the ice-chilled room temperature instead:
  the more ice you stock, the colder the room and the slower each block melts.
  A fully iced cellar loses roughly one block per ~7 hours of real play time
  (tunable via `IceCellarMinMeltChance`; set it to 0 to make fully-chilled ice
  last forever), and the loss accelerates as the stock dwindles and the cellar
  warms. Ice melting in any fully enclosed room vanishes instead of turning
  into a water source, so cellars no longer flood. Winter already prevented
  melt rolls entirely (outdoor ≤ 2°C) and still does.

## 1.13.0 — 2026-08-02

### Added
- **Ice-cooled cellars.** Stacking blocks of ice (glacier ice, packed glacier
  ice, lake ice) inside a proper cellar now chills it below the vanilla 5°C
  cellar floor. The effect starts at 4 ice blocks and maxes out at 10, where
  the cellar behaves as if at −3°C — food keeps roughly twice as long as in a
  perfect vanilla cellar. Scales with cellar quality, never raises the rate
  (winter cold passes through untouched), and stays within the game's own
  perish-rate floor, so nothing "freezes". Container tooltips show the ice
  count and chill strength; the perish-speed multiplier reflects it too.
  Thresholds and target temperature are tunable in `features.json`
  (`IceCellarMinBlocks`, `IceCellarFullBlocks`, `IceCellarTemp`).
- **Ice now counts as a cooling cellar-wall material.** Vanilla treats ice
  walls as non-cooling (like wood), which would have penalized the room's
  cellar rating for every ice block placed; with this fix ice walls rate like
  stone or soil.

## 1.12.1 — 2026-07-23

- Compatibility release for Vintage Story 1.22.5. Rebuilt against the 1.22.5
  assemblies; no code changes.
- First deployed build since 1.8.1 — see the 1.9.0 through 1.12.0 entries
  below for what's new, most notably the hidden-lockbox split into the
  standalone Hollowstone mod (install Hollowstone alongside this update).

## 1.12.0

### Changed
- **Hidden lockboxes and the Page of Secrets moved out** into the standalone
  **Hollowstone** mod (feature-identical to 1.11.0). Seraph's Ledger no longer
  contains the blocks, items, recipes, or the settings toggle.

### Migration
- Install **Hollowstone** together with this update, **before the first world
  load** — Hollowstone ships a remap config that converts already-placed
  `seraphsledger:hiddenlockbox-*` blocks and `seraphsledger:secretspage` items
  to its own domain, and the remap only applies cleanly when it's present on
  that first load. Hollowstone also adopts the lockbox registry (owners and
  stone-press combinations) from the old savegame data automatically. Page
  seals (owner attributes) survive the item remap.

## 1.11.0

### Added
- **Stone-press combinations.** A lockbox's face is divided into four unmarked
  "stones" (quadrants). The owner, sneak-clicking with their own sealed Page of
  Secrets in hand, presses four stones in sequence to program the box — a
  stone-slide sound and a chat note confirm it. From then on the box only opens
  after sneak-clicking those four stones in order with an empty hand. Every
  press gives the same soft click and pinch of dust whether right or wrong, so
  a failed guess is indistinguishable from prodding dead stone; attempts use a
  sliding window (just keep pressing) and expire after 15 seconds. Combinations
  live only on the server (never sent to clients), are saved with the world,
  and are removed by breaking the box — break and re-place to clear one.
  A thief with a stolen page can *find* the box but cannot reprogram it: only
  the box's owner can set its combination. Boxes without a combination keep
  opening on a plain sneak-click.

## 1.10.1

### Added
- **Open lockboxes look open.** The drawer's sides are now dark basalt instead
  of matching cobblestone, so a slid-out drawer reads as a stone plug pulled
  from a shadowed cavity (the dark faces are fully hidden while closed, so the
  disguise is unchanged). Opening one also puffs a burst of stone dust from the
  drawer mouth, colored to match the rock — spawned server-side, so nearby
  players can spot someone operating a wall stash.

## 1.10.0

### Changed
- **Page of Secrets reworked into a stealable treasure map.** Each page is
  sealed to its crafter (shown in the item name/tooltip, e.g. "Feyd's Page of
  Secrets"), and whoever *holds* it sees that player's hidden lockboxes —
  steal someone's page and their stashes are yours to find. Pages are live
  keys to the owner's stash registry, so lockboxes placed after the page was
  written show up too. Pages crafted before 1.10.0 seal to the first player
  who holds them.
- **Floating labels replace the locator HUD.** While a page is held, a
  "Hidden Lockbox" label floats over each of the owner's lockboxes, visible
  through walls, fading out toward 30 blocks (same rendering approach as
  Quartermaster's container labels). The block-highlight and the HUD list are
  gone.
- **Server-authoritative reveal.** The server checks about once a second what
  each player is holding and only sends lockbox positions of the page's owner
  within 30 blocks of that player. Distant stashes never cross the wire, so
  modified clients can't dump a stash network, and there is no world scanning
  involved — just a distance filter over the registry.

### Fixed
- **Page of Secrets icon** was a stretched block of text (a texture meant for
  UV-mapping, not an icon). It's now a proper parchment sheet stamped with a
  small red wax seal, fitting the sealed-to-its-owner mechanic.

## 1.9.2

### Fixed
- **Page of Secrets** could not be crafted. The ink and quill was marked as a
  tool ingredient (`isTool`), but the crafting matcher requires a tool to have
  enough durability to pay the tool cost — and ink and quill has no durability
  at all, so the recipe never matched. It is now a non-consumed ingredient
  (`consume: false`): the ink and quill still survives crafting, unchanged.
- **Hidden Lockbox** recipe logged "Output hiddenlockbox-coral cannot be
  resolved" on load when another mod adds extra cobblestone variants. The
  cobblestone ingredient is now pinned to the 20 vanilla rock types the lockbox
  actually exists for.

## 1.9.1

### Fixed
- **Hidden Lockbox** rendered with granite cobblestone textures no matter which
  rock it was crafted from. The blocktype now declares its texture under the
  plain `all` key (the type-prefixed key never resolved and the engine silently
  fell back to the shape's default), and the inventory icon is cached per rock
  (`variantByGroupInventory`), so the placed block, held item, and creative
  entries all show the correct stone.
- **Stone-slide sound** is now the real AI-generated stone-on-stone recording
  (mono OGG, sped up 1.25×) instead of the vanilla placeholder — the 1.9.0
  placeholder note below no longer applies.
- Deploy script now names the zip with the mod version (e.g.
  `SeraphsLedger_1.9.1.zip`) and clears older versions from the Mods folder.

## 1.9.0

### Added
- **Hidden Lockbox**: chisel a small lockbox into any cobblestone block (hammer +
  chisel + cobblestone in a vertical column). Placed, it passes for plain
  cobblestone — the block info HUD reports it as cobblestone, there is no
  mouse-over interaction hint, and a plain right-click behaves like clicking real
  stone (a held block simply gets placed against it). Sneak + right-click with an
  empty hand slides a small stone drawer out (with a stone-on-stone sound) and
  opens its 8-slot inventory. Supports padlocks/reinforcement like other containers.
- **Page of Secrets**: crafted from parchment + ink and quill. While held, a HUD
  lists your hidden lockboxes nearest-first with distance, compass direction and
  height difference, and lockboxes within 24 blocks get an in-world highlight so
  you can pick the right block out of a wall. Ownership is tracked server-side per
  player and saved with the world, so only your own boxes show.
- **Hidden lockboxes** toggle in the settings panel: turning it off disables both
  recipes and the locator HUD; already-placed lockboxes keep working.

### Notes
- The open/close sound ships as a placeholder (a vanilla stone sound). Replace
  `assets/seraphsledger/sounds/block/stoneslide.ogg` with a custom
  stone-sliding-on-stone sound to taste.

## 1.8.1

### Fixed
- **Large Chest** can now be crafted. Its recipe was two normal chests (shapeless,
  2×1), which is identical to vanilla's "2 chests → Trunk" recipe, so the grid always
  resolved to a Trunk instead. The recipe now requires two chests plus a metal nail
  strip (`CNC`), making it unambiguous. Large Trunk and Large Labeled Chest were
  unaffected because their inputs don't collide with any vanilla recipe. Note: only
  chests carrying the `normal-generic` type attribute (crafted, or broken and picked
  up) qualify — very old chests from earlier mod versions may lack it and won't craft.

## 1.8.0

### Added
- **Large containers** and **silenced voices** are now toggleable from the in-game
  settings panel (Ctrl+Shift+L / `.slconfig`), alongside the existing runtime
  features. Like the others, changes take effect after a restart.

### Changed
- **Silenced voices** is now enforced by a client-side Harmony patch that
  suppresses voice playback in `EntityTalkUtil` rather than by overriding the
  voice sounds with silent `.ogg` files. This makes it switchable at runtime and
  covers every entity voice regardless of which instrument it uses. The silent
  `.ogg` override assets were removed.
- The settings panel no longer carries the "these features are baked in and can't
  be switched off" note — every feature is now toggleable.

### Fixed
- Disabling **large containers** now actually blocks crafting. The recipes are
  disabled (`GridRecipe.Enabled = false`) in `AssetsFinalize` after every mod's
  recipes are registered. Removing them from `World.GridRecipes` was ineffective
  because the crafting matcher searches a precomputed ingredient index rather than
  that list; the index honors each recipe's live `Enabled` flag. Blocks stay
  registered, so large containers already placed in a world keep working — only
  crafting new ones is gated.
