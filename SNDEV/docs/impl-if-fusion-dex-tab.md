Title: IF fusion-matrix dex tab (read)
Date: 2026-08-12
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: infinite-fusion-compatibility §9.5 / §11 step 3 — user-selected "Fusion-matrix dex tab"
Summary: Surface Infinite Fusion's head×body fusion Pokédex as a read-only dex tab.

Skills: N/A

## What changed

### Fork (`PKHeX-master/PKHeX.Core`)
- `Saves/SAV_InfiniteFusion.cs`
  - `FusionPair` now carries the portmanteau `FusionName` (harvested from `GameData::FusedSpecies.@real_name`).
  - `SaveState` + `Parse` now read `@seen_fusion` / `@owned_fusion` (head × body `Array(578)` of `Array(578)`) from `#Player::Pokedex`.
  - Added `GetFusionSeen(head,body)` / `GetFusionCaught(head,body)` / `SetFusionSeen` / `SetFusionCaught` (1-based IF dex numbers 1..577). Re-marshal write-back already persists edits.
- Rebuilt `PKHeX.Core.dll` (v26.7.7.0) with a locally installed .NET 10 SDK and copied it into `PKVault.Backend/`.

### Backend (`PKVault.Backend`)
- `dex/dto/FusionDexItemDTO.cs` (new): `Id, SaveId, HeadSpecies, BodySpecies, HeadName, BodyName, FusionName, Types, IsSeen, IsCaught`.
- `dex/services/gen/DexGenService.cs`: added virtual `GetFusionDex()` (empty default).
- `dex/services/gen/DexIFService.cs`: overridden `GetFusionDex()` — iterates the 577×577 matrix, emits one `FusionDexItemDTO` per seen/caught cell, resolves head/body to PKHeX ids via `IFSpeciesOrder`, uses the realized-fusion portmanteau name and merged head+body types.
- `dex/services/DexService.cs`: `GetFusionDex()` dispatch (loads all saves; only IF returns entries).
- `dex/routes/DexRoute.cs`: `GET api/dex/fusions`.
- `data/dto/DataDTO.cs`: added `FusionDex` (`DataDTOState<Dictionary<uint, List<FusionDexItemDTO>>?>`).
- `data/services/DataService.cs`: `GetPossibleFusionDex` wired to `flags.Dex`.

### Frontend (`frontend`)
- `src/data/sdk/dex/dex-fusions.ts` (new): `FusionDexItemDTO` + `useDexGetFusions` hook (standalone, not in generated SDK so it survives `generate-sdk.ts`).
- `src/pokedex/fusion/fusion-dex-list.tsx` (new): renders fusion cards (head/body sprites, name, type badges, seen/caught).
- `src/pages/pokedex.tsx`: added a Species/Fusions `Segmented` toggle.
- `src/translate/locales/en.json`: `dex.tab.species`, `dex.tab.fusions`, `dex.list.loading`.
- `tsc --noEmit` passes.

## Verification
- `dotnet build` of fork + backend: 0 errors.
- `IFTool` on `InfiniteSave/File A.rxdata`: fusion matrix read = **1128 seen, 139 caught** (of 332 929); sample cells resolve to correct head/body PKHeX species.
- Frontend `tsc --noEmit`: clean.

## Remaining (not done this pass)
- Fusion **display** via per-save `GameData::FusedSpecies` derivation (§4.5(b)) — fusions currently show head/body official sprites with no head+body overlay.
- `SAVE_VERSION_OVERRIDES` (§4.6) and full `PKF` entity (§4.2) still pending.
- Frontend SDK not regenerated via `generate-sdk.ts` (manual `dex-fusions.ts` used instead; later regen will pick up `FusionDexItemDTO` on the default `DataDTO` path).

---

## Update (2026-08-12, second pass): fusion-matrix write-back

### What changed
- **Fork** `SAV_InfiniteFusion.cs`: `GetIfIndex(ushort)` made `public` (used by `DexIFService.EnableFusion`); rebuilt DLL (v26.7.7.0) + copied to `PKVault.Backend`.
- **Backend**
  - `dex/services/gen/DexIFService.cs`: `EnableFusion(ushort head, ushort body, bool seen, bool caught)` — converts PKHeX ids → IF dex numbers via `GetIfIndex`, calls `SetFusionSeen/SetFusionCaught`.
  - `storage/data-action/FusionDexSyncAction.cs` (new): mirrors `DexSyncAction`; merges every selected save's fusion seen/caught into a unique `(head,body)` set and applies via `EnableFusion`; sets `HasWritten`.
  - `storage/services/ActionService.cs`: `FusionDexSync(saveIds)`.
  - `storage/routes/StorageRoute.cs`: `PUT api/storage/dex/fusions/sync`.
  - `Program.cs`: `services.AddScoped<FusionDexSyncAction>()`.
- **Frontend**
  - `src/data/sdk/dex/dex-fusions.ts`: `useStorageDexFusionsSync` hook (PUT sync).
  - `src/pokedex/fusion/fusion-dex-sync.tsx` (new): self-contained `Popover` + multi-select of saves mirroring `DexSyncAdvancedAction`.
  - `src/pages/pokedex.tsx`: Sync button shown in the Fusions tab.
  - `en.json`: `dex.tab.sync-fusions`, `storage.fusion-dex-sync.{title,description,controls-label}`.

### Verification
- Backend `dotnet build`: 0 errors. Frontend `tsc --noEmit`: clean.
- `IFTool` fusion write-back: `fusion[1,1]` set seen+caught → persisted through `Write()` + reload (`seenAfter=True caughtAfter=True`).
- Fusion write-back is now functional end-to-end (read + sync across saves).

### Status
- Fusion-matrix dex tab: **DONE (read + write-back)**.
- Remaining: §4.5(b) fusion display, §4.2 PKF.

---

## Update (2026-08-12, third pass): friendly version label (§4.6 SAVE_VERSION_OVERRIDES)

### What changed
- **Fork** `Game/Enums/GameVersion.cs`: added `GameVersion.InfiniteFusion` enum value.
- **Fork** `SAV_InfiniteFusion.cs`: `Version` getter now returns `GameVersion.InfiniteFusion`
  (was `GameVersion.SV`). Rebuilt DLL (v26.7.7.0) + copied to `PKVault.Backend`.
- **Backend** `GenStaticOthers.cs`: `GetVersionName` now falls back to a human-friendly name for
  non-pokeapi versions (`GameVersion.InfiniteFusion` → "Infinite Fusion").

### Verification
- Backend `dotnet build`: 0 errors.
- `IFTool` on sample save: `version=InfiniteFusion gen=9 ctx=Gen9` (previously `SV`).
- `GameVersion.GetContextFromSaved` already has a `_ => 0` default, so the new value is safe during
  static-data generation (no throw). `staticData.versions` now includes the `InfiniteFusion` key with
  name "Infinite Fusion".

### Status
- §4.6 friendly version label: **DONE**. `SAVE_VERSION_OVERRIDES` mechanism remains usable for per-install overrides.
- Remaining: §4.5(b) fusion display (head+body sprite overlay / per-save `GameData::FusedSpecies` derivation), §4.2 PKF.


