Title: Infinite Fusion — complete canonical species-order table (IFPokedex.txt)
Date: 2026-08-07T13:40:00Z
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: ad-hoc (continuation — resolve the IFSpeciesOrder blocker)
Summary: Built the complete IF species-order table from the user-supplied `IFPokedex.txt` (IF dex index →
species name → Essentials symbol → PKHeX id), replacing the previous partial (save-harvested 350/577) table.
The dex now decodes all standard species correctly.

Evidence:
- `genorder gen-txt` (isolated tool in `C:\Users\salmo\AppData\Local\Temp\opencode\genorder`) parses the
  two-line `#N`/`Name` file, converts each name to the Essentials symbol (`ToSymbol`: uppercase, strip
  non-alphanumerics; `Nidoran♀/♂` → `NIDORANF`/`NIDORANM`), and resolves via `IFNameLookup.GetSpecies`.
- Cross-check against the save's own `GameData::Species` `@id_number`: **341 exact symbol matches**, the only 9
  differences being form-species name variants (e.g. save `ORICORIO_2` vs file `ORICORIOPOMPOM`, `NIDORANfE` vs
  `NIDORANF`) — all the same species. So the IF numbering aligns with the save's `@id_number` exactly.
- Result: **576/577** entries populated (index 577 absent — the file's `#221a` typo occupies the 221 slot, so it
  enumerates 1..576; a single trailing species, if any, is a gap). 557/576 resolve to PKHeX ids; the ~19 that don't
  are form-only species (Castform/Oricorio/Minior/Lycanroc/Meloetta forms, Ultra Necrozma) with no standalone PKHeX
  species (surfaced as forms of their base).
- `PKHeX-master/PKHeX.Core/Saves/InfiniteFusion/IFSpeciesOrder.cs` rewritten from `IFPokedex.txt`; fork rebuilt
  (0 warnings/0 errors); DLL shipped to `PKVault.Backend/PKHeX.Core.dll`.
- IFTool validation on `InfiniteSave/File A.rxdata`: `HasPokeDex=True`; `SeenCount` **328 → 418** and
  `CaughtCount` **328 → 405** (the 227 previously-undetectable indices now decode correctly). Species names resolve
  correctly (Bulbasaur, Venusaur, Charmander…). `PKVault.Backend` builds with 0 errors.

Docs updated: `docs/technical/infinite-fusion-compatibility.md` §9.8 (encryption caveat + resolution via
IFPokedex.txt), top progress table, §10 checklist (IFSpeciesOrder = 576/577 done; DexIFService done).
Note: `species.dat` remains encrypted in this install, but it is no longer needed — `IFPokedex.txt` is the
shipped source (the `gen-dat` mode is retained only as a fallback for a decrypted PBS).

Not committed (no explicit request). Remaining: write-back (`GetFinalData` re-marshal) to persist SetSeen;
GenStaticFusions / per-save fusion display; index 577 gap (single species) if it proves to matter.
