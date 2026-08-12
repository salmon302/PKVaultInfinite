Title: Verify IF compatibility status and reconcile spec doc
Date: 2026-08-12T00:00:00Z
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: ad-hoc
Summary: Re-verified `docs/technical/infinite-fusion-compatibility.md` against the actual fork/PKVault code and corrected stale "blocked / not started / HasPokeDex = false" claims.

## Findings (verified against repo)
- `SAV_InfiniteFusion.HasPokeDex => true` (SAV_InfiniteFusion.cs:594) — doc previously claimed `false`.
- Write-back implemented: `RubyMarshal.Save` (RubyMarshal.cs:34) + `GetFinalData()` re-marshals the edited graph (SAV_InfiniteFusion.cs:701); `SetSeen`/`SetCaught` (lines 608-620) now persist. Doc previously claimed "Not started".
- `IFSpeciesOrder` ships as a complete auto-generated static 576/577 table from `IFPokedex.txt` (IFSpeciesOrder.cs, merged with per-save harvest). Doc previously said harvested 350/577 only.
- `DexIFService` is implemented (GetDexItemForm/GetDexLanguages/EnableSpeciesForm) and wired into `DexService.GetDexService` at DexService.cs:82. Doc previously said "blocked on §9.5".
- No `PKF` entity exists (still deferred, matches doc). `SAVE_VERSION_OVERRIDES` still defaults to null (still not done, matches doc).

## Remaining gaps (still open)
- Fusion-matrix dex tab (`@seen_fusion`/`@owned_fusion`) not surfaced — `DexIFService` only reads the standard dex.
- Fusion display (per-save `GameData::FusedSpecies` derivation) not done.
- `SAVE_VERSION_OVERRIDES` IF entry not added.

## Doc changes
Updated header status, progress table, §1 verification table, §3/§4.1/§4.4/§7/§9.5/§10/§11 to reflect the completed work; added a "Last verified" dateline.
