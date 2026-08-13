Title: PKF entity plan — fusion-first-class (storage-first)
Date: 2026-08-12T00:00:00Z
Author: Seth Nenninger (opencode Agent)
Contribution Type: Conception
Ticket/Context: PKVault Infinite Fusion — §4.2/§12 of infinite-fusion-compatibility.md
Summary: Decided PKF model (PKF : PK9, new EntityContext.Gen9Fusion; save stays Gen9) and storage-first scope; authored §12 plan + checklist updates in docs/technical/infinite-fusion-compatibility.md.

## Decisions
- Entity model: `PKF : PK9`, head/body overlaid in PK9 reserved bytes (0x96-0x99, checksum-safe).
- New `EntityContext.Gen9Fusion` for the *entity*; `SAV_InfiniteFusion.Context` stays `Gen9` to preserve dex/PersonalTable.SV.
- Scope: storage-first. Standard-dex fusion entries (§5 ushort mapping + per-save StaticSpecies) deferred.

## Key evidence / touch points
- SAV_InfiniteFusion.cs:318 ConvertPokemon — currently emits PK9, stashes fusions in Fusions/PartyFusions.
- PkmConvertService.cs:216 GetPKMTypeWeight — must add "PKF" weight; cross-context block is free (no PKF case in switches).
- DexGenService.cs:11-32 / :73 — aggregate by pkm.Species; skip PKF with Body!=0 to avoid false head "caught"; forms keyed by save.Context (kept Gen9).
- EntityContext.cs — add Gen9Fusion member + extension branches; GameVersion.InfiniteFusion => Gen9Fusion.
- ImmutablePKM.cs:212 Types + dex exclusion need fusion fields.

## Open items
- Bank persistence of fusion payload beyond head/body (merged types/stats standalone).
- Frontend DTO schema change + SDK regen (generate-sdk.ts).
- Verify backend preserves PKF type when a fusion moves to a non-IF bank.
