# Changelog

All notable changes to The Seraph's Ledger are documented here.

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
