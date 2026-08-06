# Specification: Pokémon Infinite Fusion Compatibility for PKVault (PKHeX Shim)

> **Status:** Draft for development — pending external confirmation of the Infinite Fusion
> save format specifics (see §9 Open Questions).
> **Audience:** PKVault backend + forked-PKHeX maintainers.
> **Companion doc:** `docs/technical/adding-games-compatibility.md` (generic port/fangame guide).
>
> All code references below were verified against the `PKVaultInfinite` repository
> (forked `PKHeX.Core.dll` version `26.07.07`) on the date in the file header.

---

## 1. Verdict (read this first)

Infinite Fusion is a **Tier B** target per `adding-games-compatibility.md §2`: its save and
Pokémon formats are **not** recognized by PKHeX, so PKVault can never ingest them until
PKHeX can parse them.

**A shim is required, and it must live inside the already-forked PKHeX.** It cannot be a
pure PKVault-side adapter because the only entry point PKVault uses is
`SaveUtil.TryGetSaveFile(data, out var saveRaw, path)`
(`db/loader/save/SavesLoadersService.cs:213`), and that detector runs entirely inside
PKHeX. A runtime "translate-then-feed-as-Gen9" shim is therefore not viable: PKHeX would
still not *recognize* the original file, and fusions have no official species to map onto.

So "the shim" **is** a PKHeX `SaveFile` + `PKM` implementation in the fork, plus a
PKVault-side dex service and a fusion static-data generator.

### Verification findings (confirmed against this repo)
| Claim | Evidence |
|---|---|
| Forked PKHeX has **no** Infinite Fusion support | Metadata scan of `PKHeX.Core.dll`: no `SAV_InfiniteFusion`, `PKF`, `Essentials`, or `RGSS` types. Only unrelated `InfiniteRoyale9a` and PKHeX's internal `DuplicateFusionChecker` exist. |
| PKVault dex dispatch has **no** IF case | `dex/services/DexService.cs:63-83` switches only on official `SAV*` classes (incl. fork-added `SAV9ZA`); unknown games hit `notHandled` → `null` (logged warning). |
| Static species data is **official-only** | `GenStaticSpecies.cs` pulls every species/form/sprite from the `pokeapi` submodule (`PokeApiService.GetPokemonSpecies`). Fan fusions cannot originate here. |
| Conversion chain is generation-locked | `storage/services/PkmConvertService/PkmConvertService.cs:216-238` maps only `PK1..PK9`/`PA9` etc. A fusion `PKF` has no conversion path. |
| `BoxType.Fused` exists but is **unrelated** | `storage/dto/BoxDTO.cs:45` (`Fused = -5`) and `StorageSlotType.Fused` (`db/loader/BoxLoader.cs:37`) are for *official* fused forms (Kyurem/Necrozma/Calyrex). **Do not reuse** for IF fusions. |
| Fork model already supports adding formats | `PKHeX.version = 26.07.07` is non-mainline → the repo already vendors a fork. Bumping the DLL + version is the established pattern. |
| Display override is ID-keyed | `SAVE_VERSION_OVERRIDES` is `IDictionary<uint, GameVersion>` (`settings/dto/SettingsDTO.cs:57`), keyed by save `ID32`, applied in `SavesLoadersService.cs:87`. |

---

## 2. Target format analysis (Infinite Fusion)

> **Confidence:** Medium. The architecture below reflects how Pokémon Essentials / RPG
> Maker XP games store data. **Every `<<CONFIRM>>` item must be verified against a real
> IF save before coding** (see §9).

- **Engine:** Pokémon Essentials on RPG Maker XP.
- **Save container:** a Ruby `Marshal.dump` of the entire game state, Zlib-compressed.
  The structure is an object graph (`$game_system`, `$player` party, `$PokemonStorage`
  boxes, switches, variables…), **not** a flat box/party byte layout like PKHeX expects.
  Detection must key off magic/extension + a structural probe, not a fixed offset map.
- **Pokémon entity:** each monster carries a **head species** and a **body species** plus
  a computed **fusion id**. Stats/types/abilities/sprite are *derived* by combining the
  two component species (head drives the front sprite + some stats, body drives the
  back). A non-fused Pokémon is simply head == body (or a flag marking "not fused").
- **Combinatorial species space:** with ~809 base species the fusion space is huge
  (hundreds of thousands of theoretical pairs; only a subset ever realized). This is the
  central challenge — see §5.

---

## 3. High-level architecture

```
Infinite Fusion save (.rxdata / .rvdata2)
        │  (Zlib + Marshal)
        ▼
[PKHeX FORK]  SAV_InfiniteFusion : SaveFile      ← the "shim"
        │        - detects & parses RGSS save
        │        - exposes box/party, dex flags
        │        - yields PKF entities
        ▼
[PKHeX FORK]  PKF : PKM                          ← fusion entity (head+body)
        │
        ▼  SaveUtil.TryGetSaveFile  (SavesLoadersService.cs:213)
[PKVault]     SaveWrapper  →  DexService switch  →  DexIFService : DexGenService
        │
        ├── Dex (seen/caught/owned) from fusion species space
        ├── Storage / banks (move PKF between boxes & IF save)
        └── Static data: GenStaticFusions generator (NOT pokeapi)
```

---

## 4. Required components

### 4.1 PKHeX fork — `SAV_InfiniteFusion : SaveFile`  *(required)*
File: new in forked `PKHeX.Core` (model on `SAV9ZA` which already exists in this fork).

Must implement the abstract `SaveFile` contract that PKVault relies on via `SaveWrapper`
(`storage/wrapper/SaveWrapper.cs`):
- `IsMatch(byte[] data)` — magic/extension/size/structural probe. Register with
  `SaveUtil` so `TryGetSaveFile` returns it.
- `Version`, `Generation`, `Context`, `MaxSpeciesID`, `PKMType` (= `typeof(PKF)`),
  `BoxCount`, `BoxSlotCount`, `HasBox`, `HasPokeDex`.
- `GetAllPKM()`, `SetBoxSlotAtIndex`, `SetPartySlotAtIndex`, `Write()`, `Clone()`,
  `BlankPKM`.
- Box/party accessors used by `SaveWrapper.GetBoxData` / `GetPartyData`.
- Dex accessors: `GetSeen(ushort)`, `SetSeen(ushort,bool)`, `GetCaught(ushort)`,
  `SetCaught(ushort,bool)` (consumed by `Dex3ColoService.GetDexItemForm` pattern).
- `PKMType` must return `PKF` so `SaveWrapper.PKMType` (`SaveWrapper.cs:100`) is correct.

**Bump deliverable:** update `PKVault.Backend/PKHeX.version` to the new fork build and
replace `PKHeX.Core.dll`.

### 4.2 PKHeX fork — `PKF : PKM`  *(required)*
A fusion cannot reuse an existing generation's `PKM` layout because identity is
head+body. `PKF` must carry at minimum: `HeadSpecies`, `BodySpecies`, and a derived
`Species` (see §5 for the ushort mapping), plus the usual PKHeX fields
(OT, level, IVs/EVs, nature, item, etc.). It must satisfy `PKM` so it flows through
`ImmutablePKM` (`storage/wrapper/ImmutablePKM.cs`) and `PkmConvertService`.

### 4.3 `EntityContext` decision  *(required, fork-side)*
`StaticDataGenerator.LAST_ENTITY_CONTEXT = EntityContext.Gen9a` (`StaticDataGenerator.cs:14`)
bounds static-data generation and `GenStaticSpecies` context iteration
(`GenStaticSpecies.cs:90`). 
**Recommendation:** Add a new `EntityContext`** (`GenInfiniteFusion`) — cleanest,
  but requires touching the fork enum **and** updating `LAST_ENTITY_CONTEXT` in
  `StaticDataGenerator.cs:14` plus any `context.IsValid`/switch sites in PKVault.

> **Recommendation:**  A fusion is semantically its own context; reuse
> ripples through form tables, sprites (`GenStaticSpritesheets`), and conversion.

### 4.4 PKVault — `DexIFService : DexGenService`  *(required)*
File: `PKVault.Backend/dex/services/gen/DexIFService.cs` (copy `Dex3ColoService.cs` as
template — it already shows the three abstract members).

Implement (per `DexGenService.cs:231-235`):
- `GetDexItemForm(...)` — read `seen`/`caught`/`owned` from the save via `PKF` fusion id;
  compute `Types`/`Abilities`/`BaseStats` from the **combined** `PersonalInfo` of head+body.
- `GetDexLanguages(...)` — return `[]` (delegates to `GetSaveLanguage()`).
- `EnableSpeciesForm(...)` — write back seen/caught using `save.SetSeen`/`SetCaught`.

Then add one `case` to the switch in `DexService.GetDexService`
(`DexService.cs:63`):
```csharp
SAV_InfiniteFusion if => new DexIFService(if),
```
(this is what currently falls through to `notHandled` → `null`).

### 4.5 PKVault — fusion static-data generator  *(required, largest effort)*
`GenStaticSpecies` (`GenStaticSpecies.cs`) is **pokeapi-only** and cannot source fusions.
Add a sibling generator, e.g. `GenStaticFusions`, that derives a `StaticSpecies`/`StaticSpeciesForm`
entry per realized fusion, with:
- **Species id** → the ushort fusion mapping from §5.
- **Types / abilities / base stats** computed from head+body component data
  (pull component data from the existing `pokeapi`/personal tables; only the *combination*
  is new).
- **Sprite** references to Infinite Fusion's own sprite set (head+body overlay). The
  `pokeapi` GitHub proxy (`GetPokeapiRelativePath`, `StaticDataGenerator.cs:85`) is
  **not** the source; IF ships fusion sprites separately. Provide a sprite URL/map and
  mirror into PKVault's sprite path used by `StaticSpeciesForm.SpriteDefault`.
- **Forms:** model head/body or shiny as forms if needed; keep `IsBattleOnly = false`.

The generated JSON must land under the same `pokeapi/generated/...` path scheme
(`StaticDataGenerator.cs:95-97`) so `StaticDataService` picks it up, and must be indexed
by the fusion `EntityContext`.

> **Scale note:** generating tens of thousands of fusion entries at startup is the main
> performance risk. Gate generation behind a realized-fusion list extracted from the save
> (or a curated master list) rather than the full combinatorial space.

### 4.6 PKVault — display override  *(recommended)*
Add the IF save's `ID32` → a friendly `GameVersion` in `SAVE_VERSION_OVERRIDES`
(`settings/dto/SettingsDTO.cs:57`), consumed at `SavesLoadersService.cs:87`. The
`GameVersion` value itself must exist in the fork (add one if reusing an existing value is
undesirable).

### 4.7 Conversion behavior  *(required decision)*
`PkmConvertService` (`PkmConvertService.cs`) has no path for `PKF`. Fusions cannot
legitimately convert to/from other generations. Decide and implement explicitly:
- **Recommended:** `PKF` is convertible **only within its own context** (clone/copy);
  any cross-context `ConvertTo` targeting a non-IF context throws (or is blocked in the
  UI). Add a weight entry `GetPKMTypeWeight` (`PkmConvertService.cs:216`) for `PKF` so the
  recursive converter terminates instead of throwing "PKM type not handled".

---

## 5. Critical data-model constraint: the fusion → species-id mapping

`PKM.Species` is `ushort` (max **65535**). `DexGenService.UpdateDexWithSave` iterates
`for (ushort species = 1; species < save.MaxSpeciesID + 1; species++)`
(`DexGenService.cs:39`) and indexes `staticSpecies[species]` by `ushort`.

Infinite Fusion's theoretical fusion space (≈ base² ≈ 650k) **exceeds ushort**. Therefore
a deterministic, **surjective-into-ushort** mapping is mandatory:
- Define `fusionId = f(head, body)` → `ushort` (e.g. `(head * BASE_COUNT + body)` capped
  and range-checked, or the game's own internal id if it already fits in ushort — **must
  confirm IF's actual species-id range, §9**).
- `PKF.Species` stores the mapped `ushort`; `HeadSpecies`/`BodySpecies` retain the true
  components for sprite/stats.
- `SAV_InfiniteFusion.MaxSpeciesID` must equal the maximum realized `ushort` fusion id,
  never the full combinatorial upper bound.

If IF's real ids exceed 65535, the only options are (a) cap supported fusions to ≤65535
realized entries, or (b) extend PKVault's dex keying from `ushort species` to a wider key
(a much larger change touching `DexItemDTO`, `StaticSpeciesData`, `DexService` typing).

---

## 6. What NOT to do
- **Do not** reuse `BoxType.Fused` / `StorageSlotType.Fused` (`BoxDTO.cs:45`,
  `BoxLoader.cs:37`) — those are official fused-form slots. Give IF its own box handling
  or reuse generic box storage.
- **Do not** feed fusions through `pokeapi` generation — they are not official.
- **Do not** attempt a PKVault-only translation shim — detection happens in PKHeX
  (`SavesLoadersService.cs:213`).

---

## 7. Sequencing / effort
1. **Fork PKHeX** — `SAV_InfiniteFusion` + `PKF` + `SaveUtil` registration +
   `EntityContext` decision. *(gating; largest unknown)*.
2. **Confirm load** — a sample IF save appears in *Save Infos* and loads without
   "version/gen not handled" (`DexService` warning at `DexService.cs:59`).
3. **Dex** — `DexIFService` + switch case; dex reflects seen/caught and writes back.
4. **Static data** — `GenStaticFusions` + sprite mirror; bounded to realized fusions.
5. **Conversion** — `PKF` weight + intra-context-only rule.
6. **Display** — `SAVE_VERSION_OVERRIDES`.
7. **Tests** — backend `PKVault.Backend.Tests` + frontend SDK regeneration
   (`generate-sdk.ts`).

---

## 8. Testing plan
1. Place a sample IF save under a `SAVE_GLOBS` path; confirm load + no
   "not handled" warning (`DexService.cs:59`).
2. Confirm the Pokédex reflects IF seen/caught state and **writes back**.
3. Confirm a `PKF` moves between the IF save and storage/banks.
4. Confirm conversion to a non-IF context is blocked (no silent data loss).
5. Confirm fusion sprites render (head+body overlay) via `GenStaticFusions` output.
6. Run `PKVault.Backend.Tests` and regenerate frontend SDK; verify no breaking schema
   change.

---

## 9. Open questions — verify against a real Infinite Fusion save before coding
- `<<CONFIRM>>` Save file **extension & magic** (`.rxdata` vs `.rvdata2`?), compression
  (Zlib?), and how to reliably detect it from raw bytes.
- `<<CONFIRM>>` Exact structure of the party/PC-storage object graph and field names
  (`$PokemonStorage`, `$player.able_pokemon`, etc.) in the target IF version.
- `<<CONFIRM>>` How "seen/caught/owned" and the fusion **dex** are stored (bitfields?
  per-species flags? a fusion-specific dex structure?).
- `<<CONFIRM>>` The game's **internal species-id scheme** for fusions — does it already
  fit in `ushort` (≤65535)? This decides §5 approach.
- `<<CONFIRM>>` How head/body stats/types/abilities are combined (averaged? head-biased?)
  so `GetDexItemForm`/`BaseStats` match in-game values.
- `<<CONFIRM>>` Sprite asset source/URL scheme for fusion head+body overlays (for §4.5).
- `<<CONFIRM>>` Sample save file(s) for the test suite (per
  `adding-games-compatibility.md §4` deliverables checklist).

---

## 10. Minimum deliverables checklist (hand to fork/IF-side implementers)
- [ ] IF save format spec: extension, magic, compression, object-graph map for
      party/boxes/dex.
- [ ] `SAV_InfiniteFusion` `SaveFile` detectable by `SaveUtil.TryGetSaveFile`.
- [ ] `PKF` `PKM` carrying head+body + mapped `ushort` species.
- [ ] `EntityContext` decision (reuse `Gen9a` vs new) + `LAST_ENTITY_CONTEXT` update if new.
- [ ] Fusion → `ushort` species-id mapping ruled against IF's real id range.
- [ ] Dex read/write contract (`GetDexItemForm` + `EnableSpeciesForm`).
- [ ] `GenStaticFusions` generator + sprite source (bounded to realized fusions).
- [ ] `PKF` conversion rule (intra-context only).
- [ ] `SAVE_VERSION_OVERRIDES` entry + friendly `GameVersion`.
- [ ] Sample save file(s) for tests.
- [ ] License note: PKVault is GPLv3 and links PKHeX (GPLv3); added code must be
      compatible.
