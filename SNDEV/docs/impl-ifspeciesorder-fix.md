Title: Fix Infinite Fusion species-order misalignment (fusion head/body misrecognition)
Date: 2026-08-12T00:00:00Z
Author: Seth Nenninger (opencode Agent)
Contribution Type: Implementation
Ticket/Context: PKVault Infinite Fusion — full fusion compatibility; user report "save 346.313 [Regigigas/Dusknoir] recognized as wrong head"
Summary: Regenerated `IFSpeciesOrder` (`PKHeX-master/PKHeX.Core/Saves/InfiniteFusion/IFSpeciesOrder.cs`) from the canonical `IFPokedex.txt` by sequential position so IF index N = game `@id_number` N. Previously the table was built from a stale `IFPokedex.txt` with ~17 extra species, shifting all higher indices (Regigigas was at 363 instead of 346; Dusknoir at 330 instead of 313). This corrupted fusion-matrix dex resolution (`GetFusionDex` → `IFSpeciesOrder.GetSpecies(head)`) and `EnableFusion` write-back, mapping fusions to the wrong head/body species. Rebuilt `PKHeX.Core.dll` (copied into `PKVault.Backend`) and verified via IFTool that the fusion matrix resolves head/body correctly; `IFBoxLoadTests` + `PKFTests` still pass.

## Evidence
- User: "346.313 is the IF Dex # for Regigigas/Dusknoir" (canonical `IFPokedex.txt` line 692 = Regigigas = #346; line 626 = Dusknoir = #313).
- Before: `IFSpeciesOrder[346]` = `"AEGISLASH"`, `[313]` = `"LUCARIO"` (shifted).
- After: `IFSpeciesOrder[346]` = `"REGIGIGAS"`, `[313]` = `"DUSKNOIR"`.
- All 576 generated symbols validated against the old symbol set (PKHeX-id resolution unchanged).

## Touch points
- `PKHeX-master/PKHeX.Core/Saves/InfiniteFusion/IFSpeciesOrder.cs` — regenerated array (seq position = IF dex #).
- `PKVault.Backend/PKHeX.Core.dll` + `.pdb` — rebuilt from corrected source.
- `docs/technical/infinite-fusion-compatibility.md` §9.8 — added correction note.

## Verification
- `dotnet build PKHeX.Core -c Release` → 0 errors.
- IFTool on `InfiniteSave/File A.rxdata`: fusion-matrix `head`/`body` resolve to correct species names.
- `IFBoxLoadTests` (1), `PKFTests` (4) → all pass.
