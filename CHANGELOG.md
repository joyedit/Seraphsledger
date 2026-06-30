# Changelog

All notable changes to The Seraph's Ledger are documented here.

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
