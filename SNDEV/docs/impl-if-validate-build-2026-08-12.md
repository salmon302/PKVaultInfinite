Title: Validate &amp; build Infinite Fusion compatibility
Date: 2026-08-12
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: ad-hoc — validate spec docs/technical/infinite-fusion-compatibility.md and build
Summary: Fork PKHeX.Core + PKVault.Backend compile; fixes applied to DexIFService.cs and GenStaticOthers.cs; one spec claim (byte-identical write-back) is false.

## 1. Build results
- **Fork `PKHeX.Core`** (`net10.0`, SDK 10.0.400): `dotnet build PKHeX-master/PKHeX.Core/PKHeX.Core.csproj -c Release` → **Build succeeded, 0 errors**.
- **Fork `IFTool`** (harness): builds; run against `InfiniteSave/File A.rxdata` → `IsMatch: True`, `Detected SAV_InfiniteFusion in 984 ms`.
- **Backend `PKVault.Backend`** (`net10.0`): reference `./PKHeX.Core.dll` updated from rebuilt fork (18 632 192 bytes), `PKHeX.version` already at `26.08.07` (was `26.07.07`). `dotnet build -c Release` → **0 errors** (72 pre-existing nullability warnings, unrelated to IF).

## 2. Spec claims verified TRUE (evidence)
| Claim | Evidence |
|---|---|
| `SAV_InfiniteFusion : SaveFile` detectable via `SaveUtil.TryGetSaveFile` | IFTool: detected in ~1 s; `SAV_InfiniteFusion.cs` registered via `SaveFileType.InfiniteFusion` (`SaveFileType.cs:56,119`). |
| Synthetic PK9 buffer, 41 boxes × 30, party 6 | IFTool: `BoxCount=41 slots/box=30 PartyCount=6`, buffer 405 504 bytes; storage total 373, party 6. |
| `HasPokeDex => true`, standard dex read/write | IFTool: `HasPokeDex=True`, SeenCount=418, CaughtCount=405; `SetSeen/SetCaught` round-trip persisted through `Write()`. |
| `IFSpeciesOrder` (576/577) | `IFSpeciesOrder.cs` static table `Count=577`; 2 trailing slots null (gap at 577 per `#221a` typo). |
| `DexIFService` wired in `DexService` | `DexService.cs:126` `SAV_InfiniteFusion ifSave => new DexIFService(ifSave)`. |
| Fusion-matrix dex surfaced (read + write-back) | IFTool: `fusion seen=1128 caught=139`; `GetFusionDex()` in `DexIFService.cs:66`; route `DexRoute.cs:27` `HttpGet("fusions")`; `DataDTO.FusionDex`; `PUT storage/dex/fusions/sync` (`StorageRoute.cs:202`, `FusionDexSyncAction.cs`); frontend `frontend/src/pokedex/fusion/*.tsx` + `pokedex.tsx` tab. |
| Friendly version `GameVersion.InfiniteFusion` | `GameVersion.cs:526` enum member; `GenStaticOthers.GetVersionName` falls back to "Infinite Fusion" (`GenStaticOthers.cs:734`). |
| Ruby Marshal reader+writer, write-back re-marshal | `RubyMarshal.cs`; IFTool: `Save(Load(x))` deterministic + idempotent over 8 passes. |

## 3. Discrepancies found (must-fix / corrections)
1. **§9.7 claim "Write() is byte-identical to the input" is FALSE.** IFTool: original `File A.rxdata` = 1 722 551 bytes; `sav.Write()` = 1 675 316 bytes (`byte-identical=False`). The re-marshal is **idempotent** (`Save(Load(x))` stable = `w0` across 8 passes, deterministic) and **write-back is correct** (Seen/Caught edits persist and reload), but the output stream is NOT byte-for-byte equal to the input (smaller; structural normalization). Spec §9.7 must be corrected.
2. **§4.4 / §1 cite `DexService.cs:82` for the switch case** — actual location is `DexService.cs:126`. Stale line reference; spec should be updated.
3. **`DexIFService.cs` did NOT compile** despite spec marking it "Done":
   - L99 `r.Types.Length` → `List<byte>` has no `Length` (CS1061). Fixed to `.Count`.
   - L111 local function `BuildRealizedFusionNames()` captured the primary-constructor param `save` (CS9105). Fixed by passing `save` as an argument (`BuildRealizedFusionNames(save)`).
4. **New enum `GameVersion.InfiniteFusion` broke an exhaustive switch** in `GenStaticOthers.cs:792` (CS8509). Added `GameVersion.InfiniteFusion => []` (fangame, no pokeapi version). Now 0 warnings from this.

## 4. Remaining spec "pending" items (consistent with code)
- `PKF` entity (§4.2) — deferred; fusions still borrow head species + `Fusions`/`PartyFusions` pair. Correct.
- Fusion **display** via per-save `GameData::FusedSpecies` derivation + sprites (§4.5(b)) — not yet done (only the fusion-matrix dex tab is surfaced). Correct.
- `PKF` conversion rule (§7.7) — pending. Correct.

## 5. Frontend build (Fusions tab) — additional findings & fixes
The spec/test plan (§8.6, §7.9) requires regenerating the frontend SDK; this was **never done**, so the
frontend did **not compile** before this pass. The Fusions tab (`frontend/src/pokedex/fusion/*`) is the
user's own in-progress code with integration bugs:

1. **Missing generated client** — `fusion-dex-list.tsx` imports `useDexGetFusions` and `FusionDexItemDTO`
   from `data/sdk/dex/dex-fusions`, but that file only contained the sync mutation. Added a hand-written
   `FusionDexItemDTO` (camelCase, matching the API wire format confirmed via `dexItemDTO.gen.ts`) and
   `useDexGetFusions()` GET hook. **TODO:** run `npm run gen:sdk` against a running backend to regenerate
   authoritatively.
2. **`Segmented` removed in Mantine v9** (`@mantine/core` is 9.4.1) — `pokedex.tsx` imported `Segmented`
   (does not exist). Replaced with `SegmentedControl` (same `value`/`onChange`/`data` API).
3. **Wrong sprite component** — `fusion-dex-list.tsx` used the low-level `UISpeciesImg` (requires raw
   `sheetUrl`/`spriteInfos`), not the id-resolving `SpeciesImg`. Switched to `SpeciesImg` with
   `context={EntityContext.Gen9}` `form={0}` (official species ids; Gen9 is a reasonable default for the
   head/body sprites — refine if IF-specific sheets are added later).
4. **Locale drift** — `fr.json`/`pt-br.json`/`de.json` were missing the 7 new fusion keys
   (`dex.tab.fusions`, `dex.tab.species`, `dex.tab.sync-fusions`, `dex.list.loading`,
   `storage.fusion-dex-sync.*`), breaking the `typeof en` structural-assert in `i18n.ts`. Added the keys
   (English placeholder values) to all three.

**Result:** `npm run c:type` (tsgo -b) passes; `npm run build` (vite) completes and emits `dist/`.

## 6. Conclusion
The IF compatibility shim is **functional and builds end-to-end** (fork `PKHeX.Core`, `PKVault.Backend`,
and the frontend Fusions tab). Validation confirmed all major "Done" claims except the byte-identical
write-back (idempotent, not identical) and found compile-blocking bugs in `DexIFService.cs`,
`GenStaticOthers.cs`, and the entire frontend Fusions UI wiring — all now fixed. The rebuilt fork DLL is
shipped into `PKVault.Backend`.
