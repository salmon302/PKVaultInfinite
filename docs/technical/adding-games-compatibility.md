# Adding Game Compatibility (Ports & Fangames)

> **Audience:** maintainers of a Pokémon *port* or *fangame* who want their save
> format / Pokémon data to be usable inside PKVault (storage, Pokédex, conversion).
>
> This document explains how PKVault models "a game", where the hard boundaries
> are, and exactly what a port/fangame repository must provide so PKVault can
> support it. It is meant to be handed to the external project as an investigation
> checklist before any code is written.

---

## 1. How PKVault already models games

PKVault is **not** a Pokémon engine. It is a management UI built **entirely on
top of [PKHeX.Core](https://github.com/kwsch/PKHeX)** (vendored as
`PKVault.Backend/PKHeX.Core.dll`, version pinned in `PKVault.Backend/PKHeX.version`).

Two PKHeX concepts drive everything:

| PKHeX concept | Meaning | Used in PKVault for |
|---|---|---|
| `EntityContext` | The "family" a save belongs to (Gen1, Gen3, Gen8, *etc.*) | dex dispatch, form tables, sprites |
| `GameVersion` | The specific game (e.g. `SV`, `ZA`, `Colosseum`) | display, overrides, conversion targets |
| `SaveFile` (concrete subclass, e.g. `SAV9ZA`, `SAV3Colosseum`) | One parsed save + all its Pokémon | storage, dex, editing |
| `PKM` (concrete subclass, e.g. `PK9`, `PB8`) | One Pokémon entity | storage, conversion, variants |

### The load pipeline (read this first)

`PKVault.Backend/db/loader/save/SavesLoadersService.cs` is where a file on disk
becomes a usable save:

1. `ReadSaveFiles()` walks the user's `SAVE_GLOBS` settings
   (`SavesLoadersService.cs:185`).
2. For each path, `UpdateSaveFromPath` calls
   `SaveUtil.TryGetSaveFile(data, out var saveRaw, path)`
   (`SavesLoadersService.cs:213`).
   **This is PKHeX's universal save detector.** If PKHeX does not recognize the
   file, PKVault never sees it.
3. The recognized `SaveFile` is wrapped in `SaveWrapper`
   (`PKVault.Backend/storage/wrapper/SaveWrapper.cs`) and indexed by
   a stable `Id` (the save's `ID32`, or a SHA1 of the path when `ID32 == 0`).

### The dex pipeline

`PKVault.Backend/dex/services/DexService.cs:GetDexService`
(**lines 63-83**) switches on the **concrete PKHeX `SaveFile` type** to pick a
per-game dex service:

```csharp
return save.GetSave() switch
{
    SAV3Colosseum sav3Colo => new Dex3ColoService(sav3Colo),
    SAV9ZA za               => new Dex9ZAService(za),
    _                      => notHandled(save),   // <-- unknown games land here
};
```

Every per-game service extends the abstract
`PKVault.Backend/dex/services/gen/DexGenService.cs`, which already implements the
shared logic (species iteration, forms, genders, types, abilities, base stats).
A new game only overrides:

- `GetDexItemForm(...)` — how "seen/caught/owned" is read for that save.
- `GetDexLanguages(...)` — which languages the dex tracks.
- `EnableSpeciesForm(...)` — how to *write back* seen/caught flags.

See `Dex3ColoService.cs` as the canonical "side game" example (it is a
non-mainline GameCube title, structurally closest to a port).

### The "centralized" dex (storage / banks)

`DexMainService` (`PKVault.Backend/dex/services/gen/DexMainService.cs`) builds a
Pokédex from **stored Pokémon variants**, independent of any physical save, using
a synthetic `FakeSaveFile.Default`. This is what the "banks & boxes" view shows.
It dispatches *per form context* back through `GetDexService`, so a new game
benefits here automatically once its `DexGenService` exists.

### Static data

`PKVault.Backend/static-data/` (esp. `generators/GenStaticSpecies.cs`) derives
species/form/sprites from the **`pokeapi` git submodule**
(`PKVault.Backend/pokeapi/`), which contains **official** Pokémon only.

### Frontend

The TypeScript frontend does **not** hard-code game lists. Its SDK models
(`frontend/src/data/sdk/model/gameVersion.gen.ts`, `entityContext.gen.ts`, …) are
**generated from the backend OpenAPI spec** (NSwag). Adding a game backend-side
propagates to the UI automatically after regeneration.

---

## 2. Two compatibility tiers

Before writing anything, classify the target:

### Tier A — The game's save/PKM format is already handled by PKHeX
Examples: an official title PKHeX supports, a ROM hack that *reuses* a vanilla
Gen3/Gen4 save layout, a fan game built on an existing PKHeX-supported engine.

**Effort: small.** PKHeX already parses it; you only need to:
1. Add a `DexGenService` subclass + register it in `DexService.GetDexService`
   (and optionally a `SAVE_VERSION_OVERRIDES` display mapping in
   `PKVault.Backend/settings/`).
2. Ensure static data covers any new species/forms (see §4).

### Tier B — The game uses a custom save and/or PKM format PKHeX does not know
Examples: a from-scratch fan engine, a heavily modified ROM with new data
structures, a port to a non-Nintendo platform with its own container.

**Effort: large and is mostly *PKHeX work*, not PKVault work.** PKVault cannot
ingest the save until PKHeX can parse it. See §3.

---

## 3. Tier B requirements — what the port/fangame repo must deliver

PKVault vendors PKHeX as a compiled DLL. To support a brand-new format you must
extend PKHeX itself (or provide an equivalent loader). The external repo should
supply **all** of the following:

### 3.1 A PKHeX `SaveFile` implementation (required)
- A class deriving from `PKHeX.Core.SaveFile` that:
  - Detects the game from raw bytes (`IsMatch`, size/checksum/magic).
  - Exposes `Version`, `Generation`, `Context`, `MaxSpeciesID`, `PKMType`,
    box/party layout, and (if applicable) Pokédex seen/caught accessors.
  - Implements `GetAllPKM()`, `SetBoxSlotAtIndex`, `SetPartySlotAtIndex`,
    `Write()`, `Clone()`, `BlankPKM`.
- Register it with PKHeX's `SaveUtil` so `TryGetSaveFile` returns it.
  - If PKHeX supports runtime save registration in the pinned version, use it.
  - Otherwise the format must be added to a **forked PKHeX build** that PKVault
    then vendors (bump `PKHeX.version` + replace `PKHeX.Core.dll`).
- A matching `PKM` subclass **only if** the Pokémon entity layout differs from
  the generation the game is based on. If it reuses Gen3/Gen4/… `PKM`, none
  needed — the existing conversion chain (`PkmConvertService`, see
  `PKVault.Backend/storage/services/PkmConvertService/`) already handles it.

> **Investigation checkpoint:** can the game's save be modeled as an existing
> `EntityContext` (recommended) or does it need a new one? New contexts ripple
> through form tables, sprites, and conversion — avoid unless truly necessary.

### 3.2 A PKVault `DexGenService` subclass (required)
Drop a new `DexXxxService` next to the others in
`PKVault.Backend/dex/services/gen/`, extending `DexGenService`, implementing the
three abstract members (copy `Dex3ColoService.cs` as a template). Then add one
`case` to the switch in `DexService.GetDexService`.

### 3.3 Static data for any non-official species/forms (conditional)
Official species need no work. For fan-original Pokémon:
- Provide a data source describing each new species: dex number, form list,
  types, abilities, base stats, gender ratio, and sprite references.
- Extend `GenStaticSpecies` / `GenStaticOthers` (and the sprite generator
  `GenStaticSpritesheets`) so PKVault can render and index them. The `pokeapi`
  submodule is official-only and **cannot** be the source for fan species.

### 3.4 Display override (optional but recommended)
Add a `SAVE_VERSION_OVERRIDES` entry (`PKVault.Backend/settings/`) so the game
shows a friendly name instead of a raw `GameVersion`.

---

## 4. Minimum deliverables checklist for an external repo

Hand this list to the port/fangame maintainers:

- [ ] **Save format spec**: file size(s), magic/header bytes, checksum scheme,
      box/party offset map, Pokédex bitfield layout.
- [ ] **PKHeX `SaveFile` class** that parses it and is detectable by
      `SaveUtil.TryGetSaveFile` (or a forked PKHeX + `PKHeX.version` bump).
- [ ] **PKHeX `PKM` class** *only if* the entity format is new; otherwise state
      which existing generation's `PKM` it reuses.
- [ ] **`EntityContext` decision**: reuse existing, or justify a new one.
- [ ] **Dex read/write contract**: how `seen` / `caught` / `owned` map to the
      save (for `GetDexItemForm` + `EnableSpeciesForm`).
- [ ] **Species/form table** for any fan-original Pokémon (types, abilities,
      base stats, gender, sprites) — required for the Pokédex to display them.
- [ ] **Sample save file(s)** for PKVault's test/verification suite.
- [ ] **License note**: PKVault is GPLv3 and links PKHeX (GPLv3); the added
      code must be compatible.

---

## 5. Where to look while implementing (PKVault backend)

| Concern | File |
|---|---|
| Save detection / loading | `db/loader/save/SavesLoadersService.cs` |
| Save abstraction | `storage/wrapper/SaveWrapper.cs` |
| Dex dispatch (add your `case` here) | `dex/services/DexService.cs:63` |
| Dex base class | `dex/services/gen/DexGenService.cs` |
| Side-game dex example | `dex/services/gen/Dex3ColoService.cs` |
| Centralized (storage) dex | `dex/services/gen/DexMainService.cs` |
| PKM conversion chain | `storage/services/PkmConvertService/` |
| Static species/data | `static-data/services/generators/GenStaticSpecies.cs` |
| Display overrides | `settings/` (`SAVE_VERSION_OVERRIDES`) |
| Vendored PKHeX version | `PKHeX.version` + `PKHeX.Core.dll` |

---

## 6. Testing

After implementation:
1. Place a sample save under a `SAVE_GLOBS` path; confirm it appears in
   *Save Infos* and loads without "version/gen not handled" warnings
   (logged in `DexService.GetDexService`).
2. Confirm the Pokédex reflects the game's seen/caught state and writes back.
3. Confirm a Pokémon can be moved between the new save and storage/banks, and
   converts to/from neighboring generations if a converter exists.
4. Run the backend test project (`PKVault.Backend.Tests`) and the frontend SDK
   regeneration to verify no breaking schema changes.
