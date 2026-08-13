Title: Frontend fusion sprite render (horizontal head/body split)
Date: 2026-08-12T00:00:00Z
Author: Seth Nenninger (opencode Agent)
Contribution Type: Implementation
Ticket/Context: PKVault Infinite Fusion — "full fusion compatibility" render step
Summary: Rendered Infinite Fusion Pokémon as a horizontal head/body sprite split in the storage grid, bank items, and details panel, instead of only the head species. The `PKF` entity already carries `HeadSpecies`/`BodySpecies`/`IsFusion` and `PkmBaseDTO` exposes them (SDK already generated); this wires the frontend to use them.

## Changes
- `frontend/src/img/fusion-sprite.tsx` (NEW): `FusionSprite` renders head species (left, on top) overlapping body species (right) under `EntityContext.Gen9` so SV sprites resolve.
- `frontend/src/storage/item/storage-item.tsx`: added `isFusion`/`headSpecies`/`bodySpecies` to `StorageItemProps`; renders `FusionSprite` when a fusion, else the single `SpeciesImg`.
- `frontend/src/storage/item/save/storage-save-item.tsx` and `.../main/storage-main-item.tsx`: pick the three fields from the pkm DTO and forward to `StorageItem`.
- `frontend/src/storage/details/details-main.tsx`: render `FusionSprite` for fusions in the details panel.

## Verification
- `npx tsc --noEmit` → 0 errors.
- `npx eslint` on the 5 changed files → 0 warnings/errors.

## Notes
- Fusion name already surfaces via `nickname` (IF portmanteau) in the item label.
- Per-save `GameData::FusedSpecies` **type** derivation for storage display is still deferred (see spec §4.5(b)); the Fusions dex tab already shows merged types.
