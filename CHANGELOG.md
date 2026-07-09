# Changelog

All notable changes to The Seraph's Ledger are documented here.

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
