# Specification: Pokémon Infinite Fusion Compatibility for PKVault (PKHeX Shim)

> **Status:** Functional (read + write-back) — save format **confirmed** against a real save
> (`InfiniteSave/File A.rxdata`, IF 6.8.2 / Essentials 19.1.dev). Standard Pokédex read **and
> write-back** are implemented; fusions still borrow the head species (no `PKF` yet) and the
> fusion-matrix dex tab is not yet surfaced. See §9 for the confirmed findings.
> **Last verified:** 2026-08-12 — re-checked against the `PKVaultInfinite` repo; the
> implementation has advanced past the original spec (write-back, `HasPokeDex => true`, complete
> `IFSpeciesOrder`, wired `DexIFService`). See the per-section status notes below.
> **Audience:** PKVault backend + forked-PKHeX maintainers.
> **Companion doc:** `docs/technical/adding-games-compatibility.md` (generic port/fangame guide).
>
> All code references below were verified against the `PKVaultInfinite` repository
> (forked `PKHeX.Core.dll` version `26.07.07`) on the date in the file header.

### Implementation progress
| Component | State |
|---|---|
| `PKHeX.Core/Saves/InfiniteFusion/RubyMarshal.cs` — Ruby Marshal 4.8 (RGSS) reader **+ writer** | **Done** — parses the full 1.7 MB save; `RubyMarshal.Save` re-emits the edited graph |
| `PKHeX.Core/Saves/InfiniteFusion/IFNameLookup.cs` — Essentials symbol → PKHeX ID | **Done** — 344/344 species, 440/440 moves, 123/123 abilities resolved on the sample save |
| `PKHeX.Core/Saves/InfiniteFusion/IFSpeciesOrder.cs` — canonical 1..577 order | **Done** — static 576/577 table derived from `IFPokedex.txt` (auto-generated), merged at runtime with the per-save harvest |
| `PKHeX.Core/Saves/SAV_InfiniteFusion.cs` — `SaveFile` implementation | **Done** — party + 41 boxes surfaced as `PK9`; `HasPokeDex => true`; `SetSeen`/`SetCaught` persist via `GetFinalData` re-marshal |
| `SaveUtil` / `SaveFileType` registration | **Done** — `SaveUtil.TryGetSaveFile` returns `SAV_InfiniteFusion` |
| Write-back (re-marshaling edits) | **Done** — `RubyMarshal.Save` re-emits the object graph so `SetSeen`/`SetCaught`/box edits survive `Write()` |
| Pokédex read + write | **Done** — `IFSpeciesOrder` (576/577) + `SAV_InfiniteFusion` dex surface + `DexIFService`; standard dex flags read/write correctly (write-back persists). Fusion-matrix tab **surfaced (read)** via `DexIFService.GetFusionDex` + `DexService.GetFusionDex` + `api/dex/fusions` + frontend Fusions tab (§9.5). Fusion write-back still pending. |
| PKVault `DexIFService`, `IFSpeciesOrder` extractor | **Done** — `DexIFService : DexGenService` + switch case (`DexService.cs:82`); `IFSpeciesOrder` is a complete static 576/577 table from `IFPokedex.txt` (per-save harvest fills the gap at index 577). |

> **Build note:** the fork targets `net10.0`. A .NET 10 SDK is required; the repo-root
> `global.json` pins the *test runner* to `Microsoft.Testing.Platform`, which conflicts with
> PKHeX's VSTest-based test project — run PKHeX's own tests with a temporary
> `PKHeX-master/global.json` that omits the `test` section.

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
| Forked PKHeX **now has** Infinite Fusion support | `PKHeX-master/PKHeX.Core/Saves/SAV_InfiniteFusion.cs` + `Saves/InfiniteFusion/{RubyMarshal,IFNameLookup}.cs`, registered via `SaveFileType.InfiniteFusion`. *(Originally: a metadata scan of the shipped `PKHeX.Core.dll` found no `SAV_InfiniteFusion`, `PKF`, `Essentials`, or `RGSS` types.)* |
| PKVault dex dispatch **has** an IF case | `dex/services/DexService.cs:126` switches `SAV_InfiniteFusion` → `DexIFService`; the standard Pokédex now reads/writes (§4.4, §9.5). *(Originally: only official `SAV*` classes were handled, unknown games hit `notHandled` → `null`.)* |
| Static species data is **official-only** | `GenStaticSpecies.cs` pulls every species/form/sprite from the `pokeapi` submodule (`PokeApiService.GetPokemonSpecies`). Fan fusions cannot originate here. |
| Conversion chain is generation-locked | `storage/services/PkmConvertService/PkmConvertService.cs:216-238` maps only `PK1..PK9`/`PA9` etc. Non-fused IF mons ship as `PK9`, so they convert normally; a future `PKF` would not. |
| `BoxType.Fused` exists but is **unrelated** | `storage/dto/BoxDTO.cs:45` (`Fused = -5`) and `StorageSlotType.Fused` (`db/loader/BoxLoader.cs:37`) are for *official* fused forms (Kyurem/Necrozma/Calyrex). **Do not reuse** for IF fusions. |
| Fork model already supports adding formats | `PKHeX.version = 26.07.07` is non-mainline → the repo already vendors a fork. Bumping the DLL + version is the established pattern. |
| Display override is ID-keyed | `SAVE_VERSION_OVERRIDES` is `IDictionary<uint, GameVersion>` (`settings/dto/SettingsDTO.cs:57`), keyed by save `ID32`, applied in `SavesLoadersService.cs:87`. `SAV_InfiniteFusion.ID32` is populated from `$player.@id`. |

---

## 2. Target format analysis (Infinite Fusion)

> **Confidence: Confirmed.** Everything below was read out of a real save
> (`InfiniteSave/File A.rxdata` — IF 6.8.2, Essentials 19.1.dev, 1 722 551 bytes).

- **Engine:** Pokémon Essentials 19.1 on RPG Maker XP.
- **Save container:** a raw Ruby `Marshal.dump` (`04 08`) of the game state. **Not** Zlib
  compressed. The root node is a `Hash` (`7b`) of 16 symbol-keyed globals, so the first four
  bytes of every save are `04 08 7b <count>` followed by `3a` (`:`, the first symbol key).
- **Pokémon entity:** `#Pokemon` objects with ~69–77 instance variables. Species identity
  lives in `@species_data`, which is **either** a `GameData::Species` (non-fused) **or** a
  `GameData::FusedSpecies` (fused, carrying `@head_pokemon` + `@body_pokemon`).
- **Fusions are pre-computed in the save.** `GameData::FusedSpecies` already contains the
  merged `@type1`/`@type2`, `@base_stats`, `@abilities`, `@hidden_abilities`, `@evolutions`,
  `@growth_rate`, and even the generated portmanteau name (`@real_name` = `"Scrafsharp"`).
  PKVault therefore does **not** need to reimplement IF's stat/type combination rules for
  fusions the player actually owns — see §4.5.
- **Combinatorial species space:** 577 base species ⇒ up to ~333 000 fusion IDs, which
  **exceeds `ushort`**. This confirms the constraint in §5.

---

## 3. High-level architecture

```
Infinite Fusion save (.rxdata)
        │  (raw Ruby Marshal 4.8 — NOT compressed)
        ▼
[PKHeX FORK]  RubyMarshal                        ← generic object-graph reader
        │
        ▼
[PKHeX FORK]  SAV_InfiniteFusion : SaveFile      ← the "shim"  (implemented)
        │        - detects & parses the RGSS save
        │        - projects party + 41 boxes into a synthetic PK9 buffer
        │        - exposes fusion head/body pairs via .Fusions / .PartyFusions
        │        - Write() re-marshals the edited graph so edits persist (write-back done)
        ▼  SaveUtil.TryGetSaveFile  (SavesLoadersService.cs:213)
[PKVault]     SaveWrapper  →  DexService switch  →  DexIFService : DexGenService
        │
        ├── Dex (standard seen/caught/owned) — live; fusion-matrix tab pending   [§9.5]
        ├── Storage / banks (move entities between boxes & IF save)
        └── Static data: GenStaticFusions generator (NOT pokeapi) — fusion display not yet done [§4.5(b)]
```

**Entity choice (decided):** non-fused Pokémon are official species, so they are emitted as
plain `PK9` in `EntityContext.Gen9`. Fused Pokémon are currently emitted as their **head**
species with the in-game fusion name as the nickname, and the true pair is recorded in
`SAV_InfiniteFusion.Fusions` / `.PartyFusions`. A dedicated `PKF` entity is deferred until
the fusion dex/static-data work lands (§4.2).

---

## 4. Required components

### 4.1 PKHeX fork — `SAV_InfiniteFusion : SaveFile`  *(implemented)*
File: `PKHeX-master/PKHeX.Core/Saves/SAV_InfiniteFusion.cs`.

How it satisfies the `SaveFile` contract PKVault relies on via `SaveWrapper`
(`storage/wrapper/SaveWrapper.cs`):
- `IsMatch(ReadOnlySpan<byte>)` — structural probe (§9.1), wired into `SaveUtil.GetTypeInfo`
  via the new `SaveFileType.InfiniteFusion` member (registered **last**, after bulk storage).
- **Synthetic entity buffer.** The `.rxdata` graph has no flat slot layout, so the constructor
  walks the graph once and writes encrypted `PK9` blobs into a freshly allocated buffer laid out
  as `6 × SIZE_8PARTY` followed by `BoxCount × 30 × SIZE_8STORED` (405 504 bytes for the sample
  save). `Party` / `Box` point at those regions, so every inherited box/party accessor just works.
  Parsing happens in a static `Parse()` whose result is threaded through a private constructor —
  necessary because the box count (and therefore the buffer size) is only known after parsing.
- `Version` = `GameVersion.SV`, `Generation` = 9, `Context` = `EntityContext.Gen9`,
  `PKMType` = `typeof(PK9)`, `BoxCount` = 41 (from the save), `BoxSlotCount` = 30.
- `ID32` / `TID16` / `SID16` are overridden onto a single backing field, because the base class
  declares them as three independent auto-properties (setting `ID32` alone leaves TID/SID at 0).
- `IBoxDetailName` for the player's custom box names.
- Extras: `EssentialsVersion`, `GameVersionText`, `Fusions`, `PartyFusions`.
- **Now done:** `HasPokeDex => true` (the `IFSpeciesOrder` table unblocks it, §9.5/§9.8) and
  `GetFinalData()` re-marshals the (possibly edited) object graph via `RubyMarshal.Save`, so
  `SetSeen`/`SetCaught`/box edits survive `Write()`.

**Bump deliverable:** update `PKVault.Backend/PKHeX.version` to the new fork build and
replace `PKHeX.Core.dll`.

### 4.2 PKHeX fork — `PKF : PKM`  *(in planning — see §12)*
Originally specced as mandatory and deferred. **Reconsidered 2026-08-12:** approved as `PKF : PK9` with its
own `EntityContext.Gen9Fusion` (save stays `Gen9`), storage-first scope. Non-fused
Pokémon are official species and ship as `PK9`, and fused Pokémon are surfaced as their head
species with IF's generated fusion name (`GameData::FusedSpecies.@real_name`, e.g. `Scrafsharp`)
as the nickname, while `SAV_InfiniteFusion.Fusions` / `.PartyFusions` retain the true
`(head, body)` pair.

Introduce `PKF` only when fusions need to be first-class dex/storage citizens. At that point it
must carry `HeadSpecies`, `BodySpecies` and a derived `Species` (see §5 for the ushort mapping)
and flow through `ImmutablePKM` (`storage/wrapper/ImmutablePKM.cs`) and `PkmConvertService`.

> ⚠ Until then, a fused Pokémon reads as its head species. Anything that aggregates species —
> notably the Pokédex — must exclude slots present in `Fusions` / `PartyFusions` or it will
> record false "caught" entries.

### 4.3 `EntityContext` decision  *(decided: reuse `Gen9`)*
`SAV_InfiniteFusion.Context` returns **`EntityContext.Gen9`** and `PKMType` is `PK9`.

Rationale:
- `PK9.Context` is `Gen9`; a save that emits `PK9` while reporting a different context breaks
  `PkmConvertService` and legality lookups.
- The draft's `Gen9a` suggestion is wrong for this fork — `Gen9a` is Legends: Z-A, whose entity
  type is `PA9` (`SAV9ZA.PKMType`), not `PK9`.
- No new enum member means `StaticDataGenerator.LAST_ENTITY_CONTEXT` (`StaticDataGenerator.cs:14`)
  and every `context.IsValid`/switch site in PKVault stay untouched.

Revisit only if `PKF` lands (§4.2): a genuine fusion entity would justify its own context, at the
cost of touching the fork enum, `LAST_ENTITY_CONTEXT`, form tables, sprites
(`GenStaticSpritesheets`) and conversion.

### 4.4 PKVault — `DexIFService : DexGenService`  *(implemented)*
File: `PKVault.Backend/dex/services/gen/DexIFService.cs` (copy `Dex3ColoService.cs` as
template — it already shows the three abstract members).

Implement (per `DexGenService.cs:231-235`):
- `GetDexItemForm(...)` — read `seen`/`caught`/`owned` from the save. The raw flags live in
  `#Player::Pokedex` (§9.5); the merged `Types`/`Abilities`/`BaseStats` for a fusion can be read
  straight out of `GameData::FusedSpecies` instead of being recomputed.
- `GetDexLanguages(...)` — return `[]` (delegates to `GetSaveLanguage()`).
- `EnableSpeciesForm(...)` — write back seen/caught using `save.SetSeen`/`SetCaught`.

Then add one `case` to the switch in `DexService.GetDexService`
(`DexService.cs:126`):
```csharp
SAV_InfiniteFusion sav => new DexIFService(sav),
```
(this case is already wired in; previously fell through to `notHandled` → `null`).

**Implemented.** The IF species-numbering table (`IFSpeciesOrder`, §9.8) is complete and
`SAV_InfiniteFusion.HasPokeDex => true`, so the dex reads and writes correctly. Note
`DexIFService` currently surfaces only the **standard** dex (`@seen_standard`/`@owned_standard`);
the fusion-matrix tab (`@seen_fusion`/`@owned_fusion`) is not yet surfaced (§9.5).

### 4.5 PKVault — static data: `IFSpeciesOrder` vs `GenStaticFusions`  *(dependency analysis)*

There are **two different problems** that the original spec collapsed into one "fusion static-data
generator". They have opposite shapes, and conflating them is the main risk:

#### (a) Decoding the dex flags — needs `IFSpeciesOrder` only, **not** a fusion generator

`DexIFService.GetDexItemForm` (§4.4) must read `@seen_standard` / `@owned_standard` (index = IF
species 1..577) and the `@seen_fusion` / `@owned_fusion` matrices (head × body, also 1..577). Both
are **just booleans indexed by IF species number**; turning an index into a PKHeX species id needs
`IFSpeciesOrder[1..577]` (§9.8), i.e. the canonical Essentials species order. **No fusion
`StaticSpecies` entry is required to read or write these flags** — the dex loop
(`DexGenService.UpdateDexWithSave`, `DexGenService.cs:39`) only needs `IFSpeciesOrder` to translate
the index and the save's own `seen/caught` bit arrays.

#### (b) Rendering a dex entry — needs a `StaticSpecies` entry (`DexGenService.cs:72`)

`CreateDexItem` unconditionally does `staticSpecies[species]` and `save.Personal.GetFormEntry(species, …)`
for every species it renders. So any species shown in the dex **must** exist in `StaticSpeciesData`
*and* in PKHeX's personal table:

- **Standard dex (the 577 base species):** non-fused IF species **are** official species, so their
  `StaticSpecies` entries already come from `GenStaticSpecies` (pokeapi). The handful of IF-custom
  base species (not in PKHeX) need a small supplementary table, **not** the fusion generator. So the
  standard dex is unblocked by `IFSpeciesOrder` + reusing existing pokeapi static data.
- **Fusion dex (the head×body matrix):** a fusion has **no** pokeapi entry and **no** PKHeX personal
  row. Rendering it as a first-class dex item requires a `StaticSpecies` entry **and** a personal
  info source, both keyed by the §5 `ushort` fusion id. This is where a fusion generator would live.

#### Why a build-time `GenStaticFusions` is infeasible (and what to do instead)

- The combinatorial space is `577 * 576 ≈ 332 929` (§9.6) — **5× the `ushort` ceiling**, so a
  build-time `Dictionary<ushort, StaticSpecies>` over *all* fusions cannot even be keyed (§5).
- Realized fusions are only known **per save**; the build-time `StaticDataGenerator` pattern
  (`StaticDataGenerator.cs`) runs once at startup independent of any save, so it can never know which
  fusions to emit.
- **Decision:** do **not** build a global `GenStaticFusions`. Instead:
  1. Decode the standard + fusion dex *flags* with `IFSpeciesOrder` (§9.8) — satisfies the dex read/write
     contract (§4.4) without any fusion static data.
  2. For fusion **display** (name/sprites/types of fusions the player actually owns), derive the
     `StaticSpecies`/`StaticSpeciesForm` entries **at load time, per save**, from the save's own
     `GameData::FusedSpecies` objects — IF already serializes the merged `@real_name`, `@type1/2`,
     `@base_stats`, `@abilities`, `@hidden_abilities`, `@height`, `@weight`, `@real_pokedex_entry`
     (§2/§9.4), so do **not** recompute them. Bound the set to realized fusions (from
     `SAV_InfiniteFusion.Fusions` / `.PartyFusions`) to stay inside the `ushort` budget (§5).
  3. Provide fusion **sprites** from IF's own head+body sprite set (the `pokeapi` proxy at
     `StaticDataGenerator.cs:85` is wrong here). The per-Pokémon `@pif_sprite` in the save
     (`#PIFSprite`) is the most reliable per-fusion sprite reference.
- Keep `IsBattleOnly = false` and model head/body or shiny as forms if needed.

> **Net:** "complete dex integration depends on GenStaticFusions" is true **only** for fusion
> *display*, and even there it must be a per-save runtime derivation, not the build-time generator
> the original §4.5 implied. The dex **flags** — the actual blocker in §9.5 — depend solely on
> `IFSpeciesOrder`.

### 4.6 PKVault — display override  *(done)*
Add the IF save's `ID32` → a friendly `GameVersion` in `SAVE_VERSION_OVERRIDES`
(`settings/dto/SettingsDTO.cs:57`), consumed at `SavesLoadersService.cs:87`. The
`GameVersion` value itself must exist in the fork (add one if reusing an existing value is
undesirable). Implemented by adding `GameVersion.InfiniteFusion` to the fork's `GameVersion` enum
(`Game/Enums/GameVersion.cs`) and setting `SAV_InfiniteFusion.Version = GameVersion.InfiniteFusion`,
so the save reports the friendly version natively (no per-install `SAVE_VERSION_OVERRIDES` entry
required — the override remains available for installations that want a different label). `ID32` is
read from `$player.@id` (e.g. `3291957797` for the sample save). `GenStaticOthers.GetVersionName`
falls back to a human-friendly name ("Infinite Fusion") for non-pokeapi versions, so the version
surfaces correctly in `staticData.versions` and the UI.

### 4.7 Conversion behavior  *(decided for now: nothing to do)*
Non-fused IF Pokémon are genuine `PK9` in `EntityContext.Gen9`, so they already flow through
`PkmConvertService` (`PkmConvertService.cs:216-238`) with no special casing. Fused Pokémon are
`PK9` too (head species + fusion nickname), so they convert as well — **at the cost of silently
losing the body species**, which is only held in `SAV_InfiniteFusion.Fusions`.

Once `PKF` lands (§4.2) the original decision applies:
- `PKF` is convertible **only within its own context** (clone/copy); any cross-context
  `ConvertTo` targeting a non-IF context throws (or is blocked in the UI). Add a weight entry to
  `GetPKMTypeWeight` (`PkmConvertService.cs:216`) for `PKF` so the recursive converter terminates
  instead of throwing "PKM type not handled".

---

## 5. Critical data-model constraint: the fusion → species-id mapping

> **Status:** confirmed and quantified in §9.6; **not yet exercised**, because fused Pokémon
> currently borrow their head species rather than getting their own IDs (§4.2).

`PKM.Species` is `ushort` (max **65535**). `DexGenService.UpdateDexWithSave` iterates
`for (ushort species = 1; species < save.MaxSpeciesID + 1; species++)`
(`DexGenService.cs:39`) and indexes `staticSpecies[species]` by `ushort`.

Infinite Fusion has **577 base species**, so its native fusion IDs run to
`577 * 576 + 577 ≈ 332 929` — roughly 5× the `ushort` ceiling. IF's own `@id_number`
(`body * 576 + head`) therefore **cannot** be reused directly. A deterministic,
**surjective-into-ushort** mapping is mandatory:
- Define `fusionId = f(head, body)` → `ushort`, allocated over *realized* fusions only.
- `PKF.Species` stores the mapped `ushort`; `HeadSpecies`/`BodySpecies` retain the true
  components for sprite/stats.
- `SAV_InfiniteFusion.MaxSpeciesID` must equal the maximum realized `ushort` fusion id,
  never the full combinatorial upper bound. *(It currently returns `Legal.MaxSpeciesID_9`,
  which is correct while entities are plain `PK9`.)*

Since IF's real ids exceed 65535, the only options are (a) cap supported fusions to ≤65535
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
1. ~~**Fork PKHeX** — `SAV_InfiniteFusion` + `SaveUtil` registration + `EntityContext`
   decision.~~ **Done** (`PKF` deferred per §4.2).
2. ~~**Confirm load in PKVault** — rebuild/replace `PKHeX.Core.dll`, bump
   `PKVault.Backend/PKHeX.version`, then check a sample IF save appears in *Save Infos* and loads
   without "version/gen not handled" (`DexService` warning at `DexService.cs:59`).~~ **Done** — IF
   saves load and the dex reflects seen/caught.
3. ~~**IF species table** — ship IF's 1..577 species order as static data (`IFSpeciesOrder[1..577]`,
   from `IFPokedex.txt`) so dex indices decode to Essentials symbols, then to PKHeX ids via
   `IFNameLookup`.~~ **Done** (576/577; §9.8). This unblocked `HasPokeDex => true` (§9.5).
4. ~~**Dex** — `DexIFService` + switch case; dex reflects seen/caught and writes back.~~ **Done**
   (standard dex; fusion-matrix tab pending, §9.5).
5. **Static data** — standard-dex species come free from `GenStaticSpecies` (pokeapi) since
   non-fused IF species are official; IF-custom base species need a small supplement. Fusion
   **display** data is derived per-save from `GameData::FusedSpecies` (§4.5(b)) — **not** a build-time
   `GenStaticFusions`. Sprites from IF's head+body set (`@pif_sprite`). *(Not yet done.)*
6. ~~**Write-back** — Ruby Marshal writer so edits survive `Write()`.~~ **Done** — `RubyMarshal.Save`
   re-marshals the edited graph.
7. **Conversion** — `PKF` weight + intra-context-only rule *(only if `PKF` lands)*.
8. **Display** — `SAVE_VERSION_OVERRIDES`. *(Not yet done.)*
9. **Tests** — backend `PKVault.Backend.Tests` + frontend SDK regeneration
   (`generate-sdk.ts`).

---

## 8. Testing plan
1. Place a sample IF save under a `SAVE_GLOBS` path; confirm load + no
   "not handled" warning (`DexService.cs:59`).
2. Confirm the Pokédex reflects IF seen/caught state and **writes back**.
3. Confirm an IF entity moves between the IF save and storage/banks.
4. Confirm conversion to a non-IF context is blocked (no silent data loss) *(once `PKF` exists)*.
5. Confirm fusion sprites render (head+body overlay) via `GenStaticFusions` output.
6. Run `PKVault.Backend.Tests` and regenerate frontend SDK; verify no breaking schema
   change.

**Fork-side harness (exists):** `PKHeX-master/IFTool` is a console project that loads a
`.rxdata`, dumps the Ruby object graph, audits every Essentials symbol against `IFNameLookup`,
and round-trips the save through `SaveUtil.TryGetSaveFile`. Run it with
`dotnet run --project IFTool -- "<path to .rxdata>"`.

**Known unrelated test failures:** `ShowdownSetTests.SimulatorTranslate{,HABCDS}` fail in
`PKHeX.Core.Tests` because the expected strings use `\n` while the Windows checkout produces
`\r\n`. Pre-existing; not caused by the IF work.

---

## 9. Confirmed save-format findings

Read out of `InfiniteSave/File A.rxdata` (IF **6.8.2**, Essentials **19.1.dev**, 1 722 551 bytes)
with `PKHeX-master/IFTool`. All former `<<CONFIRM>>` items are resolved.

### 9.1 Container & detection
| Property | Value |
|---|---|
| Extension | `.rxdata` (RPG Maker XP), file name = save slot name (`File A`) |
| Magic | `04 08` (Ruby Marshal v4.8) followed by `7b` (`Hash`) and `3a` (`:` first symbol key) |
| Compression | **None** — raw `Marshal.dump`. The earlier "Zlib" assumption was wrong. |
| Detection rule (implemented) | `len ≥ 0x10000 && data[0..2] == 04 08 && data[2] == '{' && data[4] == ':'` |
| Ordering | Registered **last** in `SaveUtil.GetTypeInfo` so a variable-size fangame format can never shadow an official save. |

**Reader gotcha (cost the most time):** Ruby Marshal keeps *separate* link tables for objects
and symbols, and in this RGSS stream the object link index is **1-based** (the outermost object
is not counted). Container objects must also be inserted into their link slot *before* their
children are read so self/parent forward references resolve. Without both fixes, parsing dies at
link 10758 (the reference Python `rubymarshal` library fails at exactly the same place).

### 9.2 Root object graph
Root is `Hash(16)`, symbol-keyed:

| Key | Type | Notes |
|---|---|---|
| `:player` | `#Player` (69 ivars) | trainer + party + pokédex |
| `:storage_system` | `#PokemonStorage` (6 ivars) | PC boxes — **not** `:PokemonStorage` |
| `:frame_count` | Fixnum | play time = `frame_count / 40` (RGSS runs at 40 fps) |
| `:bag` | `#PokemonBag` | |
| `:global_metadata` | `#PokemonGlobalMetadata` (80 ivars) | |
| `:essentials_version` | String | `"19.1.dev"` |
| `:game_version` | String | `"6.8.2"` |
| `:game_system`, `:pokemon_system`, `:switches`, `:variables`, `:self_switches`, `:game_screen`, `:map_factory`, `:game_player`, `:map_metadata` | | engine state, not needed |

### 9.3 Trainer, party and storage
- **Trainer** (`:player`): `@name` (String), `@id` (**uint32**, e.g. `3291957797` → TID16 `18981`
  / SID16 `50231`), `@language` (`2` = English), `@money`, `@badges` (Array(16) of bool),
  `@trainer_type` (`:POKEMONTRAINER_Red`).
  ⚠ **There is no `@gender` ivar** — gender is a property of the trainer type, whose PBS table is
  not in the save. The implementation infers female from a `Leaf` trainer type and defaults to male.
- **Party:** `:player` → `@party` = `Array(6)` of `#Pokemon`.
- **Storage:** `:storage_system` → `@boxes` = `Array(41)`:
  40 × `#PokemonBox` (`@pokemon` = `Array(30)`, `@name`, `@background`) plus a trailing
  `#StorageTransferBox`. Also `@currentBox`, `@boxmode`, `@unlockedWallpapers`, `@fusionMode`,
  `@fusionItem`. Sample save: 373 stored + 6 party = **379 Pokémon**, 9 of them fused.

### 9.4 Pokémon and species model
`#Pokemon` fields consumed by the shim:

| Ivar | Notes |
|---|---|
| `@species_data` | `GameData::Species` **or** `GameData::FusedSpecies` |
| `@species` | `:SCRAFTY` when plain, `:B331H494` when fused (`B<body>H<head>`) |
| `@exp` / `@level` | both stored; fusions use a blended growth curve, so `@level` wins when the two disagree |
| `@iv` / `@ev` | `Hash` keyed `:HP`, `:ATTACK`, `:DEFENSE`, `:SPECIAL_ATTACK`, `:SPECIAL_DEFENSE`, `:SPEED` |
| `@moves` | `Array` of `#Pokemon::Move` (`@id` symbol, `@ppup`, `@pp`) |
| `@owner` | `#Pokemon::Owner` (`@id` uint32, `@name`, `@gender`, `@language`) |
| `@personalID` | uint32 → `PID` |
| `@nature`, `@ability_index`, `@ability`, `@item`, `@poke_ball`, `@happiness`, `@shiny`, `@steps_to_hatch`, `@obtain_level`, `@form`, `@gender`, `@name` (nickname, `nil` when unset) | |
| IF-only extras | `@original_head`, `@original_body` (full nested `#Pokemon`!), `@pif_sprite`, `@exp_when_fused_head/body`, `@head_original_ability_index`, `@spriteform_head/body`, `@hat*`, `@size_category` |

- **Non-fused** → `GameData::Species` with `@id`/`@species` = `:SCRAFTY` and
  `@id_number` = **494**. Note `@id_number` is IF's *own* dex numbering, **not** the national dex
  (Scrafty is #560 nationally). **Always map by symbol, never by `@id_number`.**
- **Fused** → `GameData::FusedSpecies` with `@head_pokemon` / `@body_pokemon` (each a full
  `GameData::Species`), `@id_number = body * 576 + head` (`331 * 576 + 494 = 191150`), plus the
  fully merged `@real_name`, `@type1/2`, `@base_stats`, `@abilities`, `@hidden_abilities`,
  `@growth_rate`, `@evolutions`, `@height`, `@weight`, `@real_pokedex_entry`.

**Symbol → PKHeX ID mapping** (`IFNameLookup`): Essentials symbols are the English name
upper-cased with punctuation stripped, so the tables are derived from PKHeX's own English string
lists using the same normalization (plus diacritic stripping) rather than a hand-written table.
Fallbacks strip form suffixes (`:LYCANROC_D`, `:MELOETTA_A`, `:MINIOR_C`, `:ROTOM_1`).
Only four aliases are needed for renamed moves: `HIJUMPKICK`, `VICEGRIP`, `FAINTATTACK`,
`SMELLINGSALT`. Verified on the sample save: **344/344 species, 440/440 moves, 123/123 abilities,
14/15 items** resolved (the miss is `:NECROZIUM`, an IF-custom item). Balls resolve by stripping
the `BALL` suffix; the IF-custom `:ROCKETBALL` falls back to Poké Ball.

### 9.5 Pokédex (unblocked — standard dex read/write implemented)
`:player` → `@pokedex` = `#Player::Pokedex`:

| Ivar | Shape |
|---|---|
| `@seen_standard`, `@owned_standard` | `Array(578)` of bool, index = IF internal species id `1..577` (`[0]` is `nil`) |
| `@seen_fusion`, `@owned_fusion` | `Array(578)` of `Array(578)` — a head × body matrix |
| `@seen_triple`, `@owned_triple` | `Hash` keyed by symbol (`:BIRDBOSS_3`, `:TYRANTRUM_CARDBOARD`) |
| `@last_seen_forms` | `Hash(775)` keyed `:B4H155` / `:HOOTHOOT` → form array |
| `@seen_forms`, `@owned_shadow`, `@unlocked_dexes`, `@accessible_dexes` | |

⚠ **Blocker (root cause):** `@seen_standard` / `@owned_standard` are `Array(578)` indexed by IF's
**own 1..577 numbering** (`[0]` is `nil`), *not* by national dex and *not* by PKHeX species id.
The save carries an id-from-`@species` symbol only for species the player has **encountered** — there
is **no id → symbol table for never-encountered species**, so index `N` in these arrays cannot be
turned back into a species name (and thus into a PKHeX id) without external data. Decoding the full
dex therefore requires **IF's species list shipped as static data**: a 1-indexed ordered array
`IFSpeciesOrder[1..577]` of the Essentials symbols in PBS `pokemon.txt` order, so that
`IFSpeciesOrder[N]` maps a dex index to a symbol that `IFNameLookup.GetSpecies` then maps to a PKHeX
id (§9.4). **Deliverable:** this table is what unblocks `DexIFService` (§4.4) and flips
`HasPokeDex` to `true`.

A *partial* map can be harvested from every `GameData::Species` object present in the save (~344
entries in the sample), which covers owned/seen-but-owned species but **not** merely-seen ones, so a
harvested table is incomplete and the static ordering table is the correct fix.

> **ushort note:** the *standard* dex numbering is bounded by 577, which fits comfortably in
> `ushort` (unlike the fusion matrix in §9.6 / §5). `SAV_InfiniteFusion.MaxSpeciesID` is therefore
> **not** set to 577 — see the `MaxSpeciesID` deviation note below. The overflow problem (§5/§9.6)
> is purely about *fusion* ids, where `body * 576 + head` exceeds 65535.

`HasPokeDex` is now `true` (`SAV_InfiniteFusion` overrides the virtual `SaveFile.HasPokeDex`);
`DexIFService` (§4.4) is wired in and reads/writes the standard dex. The fusion-matrix tab is now
**surfaced read-write**: `SAV_InfiniteFusion` parses `@seen_fusion`/`@owned_fusion` (head × body,
indexed by IF dex number 1..577), `DexIFService.GetFusionDex` emits one `FusionDexItemDTO` per
seen/caught cell (resolved to PKHeX species ids via `IFSpeciesOrder`, with the portmanteau name and
merged types coming from the realized fusions harvested at load time), exposed through
`DexService.GetFusionDex` → `DataDTO.FusionDex` / `api/dex/fusions` and rendered in the frontend
**Fusions** tab. Write-back is wired via `FusionDexSyncAction` (`PUT api/storage/dex/fusions/sync`)
which merges each save's fusion seen/caught into the selected saves and persists through re-marshal.

### 9.6 Consequences for §5 (ushort mapping)
577 base species ⇒ max fusion id ≈ `577 * 576 + 577 = 332 929`, well beyond `ushort`. The
combinatorial mapping in §5 is therefore **mandatory** and cannot use IF's native `@id_number`.
Because fused Pokémon are currently surfaced as their head species, nothing depends on this yet.

### 9.7 Verified end-to-end
`SaveUtil.TryGetSaveFile` → `SAV_InfiniteFusion` in ~1 s. Sample results: `OT=Seth`,
`ID32=3291957797`, `money=199055`, playtime `113h51m48s`, 41 boxes × 30, party 6, 373 stored,
0 unmapped species, 0 empty first-move slots, and `Write()` **write-back is correct and idempotent**
(`Save(Load(x))` is deterministic and stable across repeated passes — see below).

> ⚠ **Correction (2026-08-12):** `Write()` is **NOT byte-identical** to the input. The re-marshaled
> stream for the sample save is 1 675 316 bytes vs the original 1 722 551 (structural normalization,
> e.g. dropped padding/unused slots). What holds is **idempotency**: `Save(Load(x))` produces a fixed
> point (`w0`) that is stable across 8+ passes and deterministic, so repeated writes do not drift or
> lose data — but the first re-emit differs from the original bytes. Edits (Seen/Caught/box) persist
> correctly and reload.

### 9.8 Canonical species order — source of truth (`species.dat`)

The blocker in §9.5 is *not* a missing algorithm; it is missing **static data**: the canonical
Essentials 1..577 species order. That order is recoverable from the shipped game, **not** from the
save and **not** from pokeapi:

- **Location:** the installed game's `Data/species.dat` (e.g.
  `C:\Users\salmo\Documents\My Games\InfiniteFusion\Data\species.dat`, ~478 KB in IF 6.8.x). This is
  Essentials' compiled PBS `pokemon.txt` — a Ruby `Marshal.dump` (`04 08`) of an `Array` of
  `GameData::Species`, **in PBS order**. It is the same RGSS format the fork's `RubyMarshal` reader
  already parses for saves, so **no new parser is needed** — reuse `RubyMarshal` to load it.
- **Shape per element:** each `GameData::Species` has `@id` (symbol, e.g. `:PIKACHU`), `@id_number`
  (IF's 1..577 dex number), `@real_name`, `@types`, `@base_stats`, etc. The dex arrays in §9.5 are
  indexed by `@id_number`.
- **Build `IFSpeciesOrder`:** for each element, set `IFSpeciesOrder[id_number] = @id` (the symbol
  string). This yields a 1-indexed `Dictionary<int, string>` (or `string[578]`) mapping a dex index
  straight to an Essentials symbol, which `IFNameLookup.GetSpecies` (§9.4) then turns into a PKHeX id.
  Keying by `@id_number` (not array position) is safe even if the file's array order ever diverges
  from dex order.
- **Cross-checks / alternates present in the install:** `Data/dex.json` (1.98 MB, dex metadata),
  `Data/dexdata.rxdata`, `Data/regional_dexes.dat`, `Data/generated_entries.json`. These are useful
  for regional-dex tabs and names, but `species.dat` is the authoritative species list.
- **Where to ship it:** a small build-time step (or a checked-in JSON next to the fork) that emits
  `IFSpeciesOrder` consumed by `SAV_InfiniteFusion` (`GetSeen`/`SetSeen`, `MaxSpeciesID = 577`) and
  `DexIFService`. This single table unblocks the **standard** dex flags and the **fusion-matrix**
  flags alike (head/body indices are also 1..577), per §4.5(a).

> This is the lowest-effort, highest-leverage deliverable for the dex: one static array derived from
> a file the game already ships, parsed by code that already exists.

> ⚠ **Encryption caveat (discovered 2026-08-07).** In the local install of IF 6.8.2
> (`C:\Users\salmo\Documents\My Games\InfiniteFusion\Data\species.dat`), the `*.dat` files for the
> big PBS tables (`species`, `items`, `moves`, `trainers`, `types`, `ribbons`, …) are **not** plain
> RGSS marshal — they begin with a constant `4E-87-57-E3` prefix and are neither valid zlib/deflate/gzip
> (probed with `System.IO.Compression` and rejected as "unsupported compression method"). So
> `RubyMarshal` cannot read `species.dat` directly in this install. The plain-marshal assumption in the
> original §9.8 was wrong **for this build**. Files like `regional_dexes.dat`, `dexdata.rxdata`, and the
> `Map*.rxdata` set ARE plain marshal, but none of them carries the complete `id_number → symbol`
> order (`regional_dexes` only has Kanto/Johto; `dexdata` is numeric stats keyed by id_number with no
> name). The decrypt key is not available, so the canonical 577-entry table cannot currently be built
> from `species.dat`.

> **Resolved 2026-08-07 (canonical order obtained).** The user supplied `IFPokedex.txt` — the IF Pokédex
> list mapping each IF dex index (`#1` … `#576`) to the species **name**. This is enough to build the complete
> table: `genorder gen-txt` parses it, converts each name to the Essentials symbol (`ToSymbol`: uppercase,
> strip non-alphanumerics; `Nidoran♀/♂` → `NIDORANF`/`NIDORANM`), and resolves it to a PKHeX id via
> `IFNameLookup.GetSpecies`. Cross-checked against the save's own `GameData::Species` `@id_number` values:
> **341 exact symbol matches**, the only 9 differences being *form-species* name variants (e.g. save `ORICORIO_2`
> vs file `ORICORIOPOMPOM`, `NIDORANfE` vs `NIDORANF`) — all the same species, so the **numbering aligns
> perfectly**. Result: **576/577** entries populated (index 577 is absent from the list — the `#221a` typo
> occupies the 221 slot, so the file enumerates 1..576; a single trailing species, if any, stays a gap).
> 557 of 576 resolve to PKHeX ids; the ~19 that don't are form-only species (Castform/Oricorio/Minior/Lycanroc/
> Meloetta forms, Ultra Necrozma) which have no standalone PKHeX species and are surfaced as forms of their base.

> **Working fallback (no longer primary).** `IFSpeciesOrder` can also be regenerated by harvesting
> `GameData::Species` `@id_number → @id` pairs **from the save itself** — the save carries one such
> object per species the player has *encountered*. For the sample save this yields **350/577** entries,
> all resolving to PKHeX ids. At parse time `SAV_InfiniteFusion` also harvests the per-save order and
> merges, so the dex decodes correctly for *every species the loaded save has encountered*. The `gen-dat`
> mode (decrypted `species.dat`) remains an option if a decrypted PBS source becomes available, but
> `IFPokedex.txt` is now the shipped source.

> **`MaxSpeciesID` deviation (intentional).** `DexGenService.UpdateDexWithSave` (`DexGenService.cs:39`)
> iterates `1..MaxSpeciesID` treating the counter as a **PKHeX species id** and indexes `staticSpecies[species]`
> by it. IF species map to PKHeX ids that can exceed 577 (IF is Gen-8-based), so `MaxSpeciesID` is kept at
> `Legal.MaxSpeciesID_9` (the current value), **not** 577 as the original §9.5 note suggested. The dex is
> keyed by PKHeX id; `GetSeen/GetCaught` reverse-map the PKHeX id → IF `id_number` (via `IFSpeciesOrder` +
> the per-save harvest) and index the `@seen_standard`/`@owned_standard` arrays. Setting `MaxSpeciesID=577`
> would wrongly skip IF species whose PKHeX id is > 577.

> **Correction (2026-08-12) — `IFSpeciesOrder` was realigned to the canonical IF national dex.** The
> previously-committed table had been generated from a *stale* `IFPokedex.txt` (one with ~17 extra species
> before Regigigas), so every higher index was shifted: e.g. it carried `Regigigas` at IF index **363** and
> `Dusknoir` at **330**, while the canonical dex (the current `IFPokedex.txt`, confirmed by the user) places
> `Regigigas` at IF index **346** and `Dusknoir` at **313**. The standard Pokédex still *appeared* correct
> because `GetSeen`/`GetCaught`/`GetIfIndex` fall back to the per-save `@id_number` harvest, but the
> **fusion-matrix** dex (`GetFusionDex` → `IFSpeciesOrder.GetSpecies(head)`) and any `EnableFusion`
> write-back used the misaligned static table and resolved the wrong head/body species. The table has been
> regenerated from the current `IFPokedex.txt` by **sequential position** (the Nth `#N`/name pair = IF
> `@id_number` N), so `IFSpeciesOrder[346] == "REGIGIGAS"` and `[313] == "DUSKNOIR"`. All 576 symbols were
> validated against the prior table's symbol set, so PKHeX-id resolution is unchanged. **If a fusion is ever
> "recognized" as the wrong head/body, regenerate `IFSpeciesOrder` from `IFPokedex.txt` and rebuild
> `PKHeX.Core.dll` — do not hand-edit indices.**

---

## 10. Minimum deliverables checklist (hand to fork/IF-side implementers)
- [x] IF save format spec: extension, magic, compression, object-graph map for
      party/boxes/dex. *(§9)*
- [x] `SAV_InfiniteFusion` `SaveFile` detectable by `SaveUtil.TryGetSaveFile`.
- [ ] `PKF` `PKM` carrying head+body + mapped `ushort` species.
      *(In planning — `PKF : PK9` with `EntityContext.Gen9Fusion`, storage-first scope; see §12. Non-fused
       mons ship as `PK9`; fusions borrow the head species and record the pair in `SAV_InfiniteFusion.Fusions`)*
- [x] `EntityContext` decision — **reuse `EntityContext.Gen9`** (`PK9`). No new context, no
      `LAST_ENTITY_CONTEXT` change. (`Gen9a` was rejected: that context belongs to `PA9`/ZA.)
- [x] Fusion → `ushort` species-id mapping ruled against IF's real id range. *(§9.6 — IF's native
      ids overflow `ushort`, so a PKVault-side mapping is required once fusions get real entities)*
- [x] **`IFSpeciesOrder[1..577]`** — *the dex unblocker*. Canonical order now **complete (576/577)**, built from
       `IFPokedex.txt` (IF dex index → name → symbol → PKHeX id via `IFNameLookup`); cross-checked against the
       save's `@id_number` (341 exact matches, 9 form-name variants — same species). Index 577 a gap (file enumerates
       1..576 due to a `#221a` typo). Shipped table consumed by `SAV_InfiniteFusion` (`GetSeen/SetSeen`) and
       `DexIFService`. *(§9.8)*
- [x] **Write-back** — `RubyMarshal.Save` re-marshals the edited object graph, so `SetSeen`/`SetCaught` and box edits persist through `Write()`. *(§4.1, §9.5)*
- [x] Dex read/write contract (`GetDexItemForm` + `EnableSpeciesForm`) — `DexIFService` implemented and
       wired in `DexService.GetDexService`. Read works for all 576 species (SeenCount 328→418, CaughtCount 328→405
       on the sample save once the full table landed); `SetSeen`/`SetCaught` persist via write-back. *(§4.4, §4.5(a))*
       *(Standard-dex only — the fusion-matrix tab is now surfaced read-only, §9.5.)*
- [x] Fusion **display** (sprites of realized fusions in storage/details) — `PKF.IsFusion`/`HeadSpecies`/`BodySpecies`
       surface on `PkmBaseDTO`; `frontend/src/img/fusion-sprite.tsx` renders a horizontal head/body split (head left,
       body right, overlapping) in the storage grid (`StorageItem`) and details panel (`DetailsMain`), and banks
       (`StorageMainItem`). Fusion name shows via the `nickname` (IF portmanteau). Per-save `GameData::FusedSpecies`
       derivation for **types** in storage is still deferred (§4.5(b)); the Fusions dex tab already shows merged types.
       *(2026-08-12)*
- [x] **Fusion-matrix dex tab (read + write-back)** — `SAV_InfiniteFusion` parses `@seen_fusion`/`@owned_fusion`;
       `DexIFService.GetFusionDex` emits `FusionDexItemDTO` entries (head/body PKHeX species, portmanteau
       name, merged types, seen/caught); surfaced via `DexService.GetFusionDex` → `DataDTO.FusionDex` /
       `api/dex/fusions` and the frontend **Fusions** tab. Write-back via `FusionDexSyncAction`
       (`PUT api/storage/dex/fusions/sync`) merging seen/caught across selected saves. *(§9.5)*
- [ ] `PKF` conversion rule (intra-context only). *(In planning — see §12.3; cross-context block falls
       out of the existing conversion switches having no `PKF` case.)*
- [x] `SAVE_VERSION_OVERRIDES` entry + friendly `GameVersion`. *Implemented via `GameVersion.InfiniteFusion`
       added to the fork enum and `SAV_InfiniteFusion.Version = GameVersion.InfiniteFusion` (native friendly
       label; `SAVE_VERSION_OVERRIDES` still usable for per-install overrides). `GetVersionName` falls back to
       "Infinite Fusion" for the non-pokeapi version. *(§4.6)*
- [x] Sample save file(s) for tests. *(`InfiniteSave/File A.rxdata`)*
- [x] **Legality exemption for IF-origin Pokémon** — Infinite Fusion mons are projected as `PK9` but are
      **not** official Gen9 entities, so they were producing a wall of bogus SV legality errors
      (encounter match, HOME tracker, obedience, generation transfer, stat alignment, height/weight,
      current handler, genderless-gender, unreleased held item, invalid moves). `LegalityAnalysisService.GetLegalitySafe`
      now short-circuits to a neutral result when the attached save is `SAV_InfiniteFusion`, exactly like the
      existing `SKIP_LEGALITY_CHECKS` path. The synthesis was also corrected so display is internally consistent
      regardless of legality: gender follows the projected head species' gender ratio (fixes "Genderless
      Pokémon should not have a gender" — e.g. a Regigigas/Dusknoir fusion no longer shows ♂), the held item is
      only kept when it is a legal SV held item (fixes "Held item is unreleased"), and moves are clamped to
      `Legal.MaxMoveID_9`.
- [ ] License note: PKVault is GPLv3 and links PKHeX (GPLv3); added code must be
      compatible.

---

## 11. Next steps
1. **Write-back — DONE.** `RubyMarshal.Save` re-marshals the edited object graph, so `SetSeen`/`SetCaught`
   and box edits persist through `Write()`.
2. **IF species table — DONE.** `IFSpeciesOrder` (576/577) is shipped as auto-generated static data from
   `IFPokedex.txt`, merged at runtime with the per-save harvest; this unblocked the Pokédex (§9.5/§9.8).
 3. **PKVault side — partial.** `DexIFService` + `DexService` switch case are done and the standard dex is
    live. **Fusion-matrix dex tab is now surfaced (read + write-back)** (§9.5). **`SAVE_VERSION_OVERRIDES`/friendly
    version is done** (§4.6: `GameVersion.InfiniteFusion` + `SAV_InfiniteFusion.Version`). Remaining:
    fusion **display** via per-save `GameData::FusedSpecies` derivation (§4.5(b)).
 4. **Fusion entities.** Decide whether to promote fusions from "head species + nickname" to a real
    `PKF`; this is what forces the §5 `ushort` mapping.

---

## 12. PKF design plan — true fusion-first-class support (storage-first)

> **Status:** Planning. Decided 2026-08-12. **Entity model:** `PKF : PK9` carrying `HeadSpecies`/`BodySpecies`
> overlaid in reserved `PK9` bytes, with its **own** `EntityContext.Gen9Fusion` (the save itself stays
> `Gen9`, per §4.3). **Scope:** storage-first — fusions become first-class in storage/banks and the
> Fusions tab, and stop polluting the standard Pokédex; surfacing fusions as derived standard-dex
> entries (the §5 `ushort` mapping + per-save `StaticSpecies`) is **explicitly deferred**.

### 12.1 Why a new `EntityContext` (and not just reusing `Gen9`)

The approved model gives the fusion entity its own context island (`Gen9Fusion`) while the *save* keeps
`Context => Gen9`. This isolates conversions and display branching on the **entity** (`PKF.Context`) without
disturbing the save-level machinery that the standard dex and `PersonalTable.SV` rely on:

- `DexGenService.UpdateDexWithSave` keys forms by `save.Context` (`DexGenService.cs:73`). If the *save*
  were `Gen9Fusion`, `staticSpecies[head].Forms[(byte)Gen9Fusion]` would be empty and the standard dex
  would break. Keeping the save `Gen9` preserves the existing dex.
- `PKF.Context => Gen9Fusion` is what lets `PkmConvertService` treat a fusion as a distinct island and
  block cross-context moves (§12.5).

`PKF` **inherits `PK9`**, so `PersonalInfo => PersonalTable.SV` (hardcoded in `PK9`) is reused for free —
storage-first displays the fusion via its **head** species' SV personal data, with merged types overlaid
from the harvested `FusionPair` (§12.6). No new `PersonalTable` is required for this milestone.

### 12.2 Fork touch points

1. **`PKHeX.Core/PKM/Util/EntityContext.cs`**
   - Add `Gen9Fusion` member immediately before `MaxInvalid` (numeric value between `Gen9a` and `MaxInvalid`).
   - `extension(EntityContext value).Generation`: add `Gen9Fusion => (byte)9` to the post-`SplitInvalid` switch.
   - `IsValid` already accepts any `value < MaxInvalid` that is not `0`/`SplitInvalid` — no change.
   - `GetSingleGameVersion`: add `Gen9Fusion => GameVersion.InfiniteFusion`.
   - `Console`: extend the `Gen7b or Gen8 or Gen8a or Gen8b or Gen9 or Gen9a` arm to include `Gen9Fusion`
     (fan game → treat as Switch-era for console grouping).
   - `extension(GameVersion version).Context`: add `GameVersion.InfiniteFusion => Gen9Fusion` (currently it
     falls through to `(EntityContext)version.Generation` and resolves to `Gen9`).
   - `IsMegaContext`/era flags: gen-9 defaults already correct (`IsEraHOME` ⇒ true).

2. **New `PKHeX.Core/PKM/PKF.cs` — `public sealed class PKF : PK9`**
   - `public override EntityContext Context => EntityContext.Gen9Fusion;`
   - `public ushort HeadSpecies { get; set; }` and `public ushort BodySpecies { get; set; }` — overlaid into
     PK9 `ExtraBytes` so they survive `WriteEncryptedDataStored`/party **and** PKVault's bank byte storage:
     - Use the reserved `0x96–0x99` block (already in `PK9.ExtraBytes`, hence excluded from the sanity
       checksum). `0x96–0x97` = `HeadSpecies`, `0x98–0x99` = `BodySpecies`. Optional `0x9A` = fusion flag
       byte (e.g. body-shiny) if needed later.
   - `Species` stays = **head** (inherited); a fusion is "present" iff `BodySpecies != 0`.
   - Constructors: `PKF()`, `PKF(Memory<byte> data)`, `PKF(PK9 src)` (copy + read head/body from reserved bytes).
   - `BlankPKM`-compatible; reuse PK9 encryption/party logic unchanged.

3. **`PKHeX.Core/Saves/Util/BlankSaveFile.cs`**
   - Ensure `Get(EntityContext)` (or the `BlankPKM`-type mapping it uses) returns `new PKF()` for `Gen9Fusion`,
     so `ConvertTo(pkm, EntityContext.Gen9Fusion)` yields a `PKF` target. *(Verify the exact branch — the
     context→blank-PKM mapping may currently flow through `SaveFileType`; a `Gen9Fusion` case must be added.)*

4. **`SAV_InfiniteFusion.cs`**
   - `public override Type PKMType => typeof(PKF);` (was `PK9`).
   - `Context` **unchanged** (`Gen9`) — see §12.1.
   - `ConvertPokemon` (SAV_InfiniteFusion.cs:318): build a `PKF` for both branches —
     fused ⇒ `HeadSpecies`/`BodySpecies` set, `Species = head`; non-fused ⇒ `BodySpecies = 0`.
   - Keep `Fusions`/`PartyFusions` populated (diagnostics + backward-compat with the existing Fusions tab).
   - Write-back is **unaffected**: `GetFinalData()` re-marshals the RGSS graph, so head/body remain
     authoritative from `GameData::FusedSpecies`; the packed bytes are only for PKVault-side byte storage.

### 12.3 `PkmConvertService` guards (PKVault.Backend)

- `GetPKMTypeWeight` (PkmConvertService.cs:216): add `"PKF" => 18` so the recursive converter terminates
  instead of throwing "PKM type not handled".
- **Intra-context-only rule (free):** `TryPKToVariant`/`TryForwardConversion`/`TryBackwardConversion` switch
  on `source.GetType().Name` and have **no** `"PKF"` case. Therefore any conversion whose source is `PKF`
  and whose target is *not* `PKF` returns `null` → `ConvertRecursive` throws "No conversion path" — exactly
  the desired block against silent body loss. Verify the UI surfaces this as "move not allowed".
- Intra-context moves (PKF → PKF) already short-circuit in `ConvertRecursive` (`current.GetType() ==
  targetType` ⇒ clone). Confirm `ConvertTo(PKF, EntityContext.Gen9Fusion)` and `ConvertTo(PKF, targetSave:
  SAV_InfiniteFusion)` both resolve target `PKFType == PKF` and succeed.
- Watch the SV legality path: PKF belongs to an `SAV_InfiniteFusion`, which already short-circuits
  legality (§10). Moving a PKF into a non-IF bank must remain covered by that short-circuit or be blocked.

### 12.4 Dex de-pollution (PKVault.Backend)

`DexGenService.UpdateDexWithSave` (DexGenService.cs:11-32) aggregates by `pkm.Species`. A `PKF.Species =
head` would inflate the head's "caught" count — the §4.2 trap. Fix: skip fusions in the aggregation:

```csharp
.ForEach(pkm =>
{
    if (pkm.IsEgg) return;
    if (pkm.GetMutablePkm() is PKF pkf && pkf.BodySpecies != 0) return; // fusion: not a standard-dex species
    ...
});
```

The fusion-matrix dex (§9.5) remains the fusion's canonical home. Standard-dex fusion *entries* (§5 `ushort`
mapping) stay **deferred**.

### 12.5 PKVault display / storage

- **`ImmutablePKM`** (ImmutablePKM.cs): expose `HeadSpecies`/`BodySpecies`/`IsFusion`/`FusionName` when
  `Pkm is PKF`. Used by both display and the dex exclusion above.
- **Banks:** verify a `PKF` written to a non-IF bank round-trips. PKVault stores PKM bytes generically and
  re-wraps via the target save's `PKMType`; since banks may not carry an IF context, confirm the backend
  preserves the `PKF` type (or persists head/body in a side channel) rather than re-deriving a plain `PK9`.
  *(Open verification item.)*
- **Frontend:** the Fusions tab (§9.5) already exists. Storage slots need a head+body sprite overlay and
  the fusion name. Extend the PKM DTO with `headSpecies`/`bodySpecies`/`isFusion` and regenerate the SDK
  (`generate-sdk.ts`). This is a **schema change** — run the SDK regen and check no breaking consumer.

### 12.6 Open design decisions (resolve during implementation)

- **Merged display types/stats.** Storage-first shows the head's SV `PersonalInfo`; the fused **types**
  should be overlaid from `FusionPair.Types` (harvested from `GameData::FusedSpecies`). Confirm
  `ImmutablePKM.Types` (ImmutablePKM.cs:212) branches on `IsFusion` to use the harvested types rather than
  `PersonalInfo`. Base stats for a fusion (IF's `@base_stats`) are *not* the head's SV stats — decide
  whether storage-first shows head stats or harvested fusion stats; the latter needs the fusion data
  carried beyond head/body (consider packing base stats, or re-deriving from the save graph only).
- **Bank persistence of fusion data.** If banks must show correct fusion types/stats without the source
  save, the harvested `FusionPair` payload must travel with the `PKF` bytes (pack more than head/body), or
  PKVault must store a `FusionPair` side-channel keyed by the entity. Scope this only if banks are required
  to render fusions standalone.

### 12.7 Sequencing

1. Fork: add `EntityContext.Gen9Fusion` + `GameVersion.InfiniteFusion` mapping (§12.2.1).
2. Fork: implement `PKF : PK9` with reserved-byte head/body + `BlankSaveFile` branch (§12.2.2-3).
3. Fork: `SAV_InfiniteFusion` emits `PKF`, `ConvertPokemon` populates head/body (§12.2.4).
4. Rebuild/replace `PKHeX.Core.dll`; bump `PKVault.Backend/PKHeX.version`.
5. PKVault: `PkmConvertService` weight + confirm cross-context block (§12.3).
6. PKVault: `DexGenService` fusion skip (§12.4).
7. PKVault: `ImmutablePKM` fusion fields (§12.5).
8. Frontend: head+body overlay + DTO schema regen (§12.5).
9. Tests: extend `IFTool` round-trip (verify `PKF` bytes survive `Save(Load(x))`); add
   `PKVault.Backend.Tests` for `PKF` build/serialize/convert; regenerate SDK; verify no breaking schema.

### 12.8 Checklist update

- [x] `EntityContext.Gen9Fusion` + `GameVersion.InfiniteFusion ⇒ Gen9Fusion` mapping.
- [x] `PKF : PK9` with reserved-byte `HeadSpecies`/`BodySpecies`; `EntityBlank` returns `PKF` for `Gen9Fusion` (`BlankSaveFile` branches on `SaveFileType`, so the context→blank-PKM mapping lives in `EntityBlank`; `PK9` unsealed to allow inheritance).
- [x] `SAV_InfiniteFusion.PKMType => typeof(PKF)`; `ConvertPokemon` populates head/body (`HeadSpecies`/`BodySpecies` in reserved 0x96–0x99; `PK9` unsealed).
- [x] `PkmConvertService` `PKF` weight (`"PKF" => 18`); cross-context conversion blocked (intra-context clone allowed — `ConvertRecursive` returns clean "No conversion path" for cross-context PKF).
- [x] `DexGenService` excludes fusions from standard-dex aggregation (`pkm.GetMutablePkm() is PKF { BodySpecies: not 0 }` skip).
- [x] `ImmutablePKM` exposes `IsFusion`/`HeadSpecies`/`BodySpecies`; `PkmBaseDTO` carries `isFusion`/`headSpecies`/`bodySpecies` (frontend SDK `pkmBaseDTO.gen.ts` manually synced). Frontend storage sprite overlay is a larger UI feature (deferred — §12.6 open decisions).
- [x] Tests: `IFTool` round-trip idempotent with `PKF` emit; `PKVault.Backend.Tests/PKFTests.cs` (4 tests: byte round-trip, non-fusion body=0, intra-context convert, cross-context block) — all pass. SDK regen performed manually (additive DTO fields); full `orval` regen against a running backend remains the documented step.
- [ ] *(Deferred)* Standard-dex fusion entries via §5 `ushort` mapping + per-save `StaticSpecies`.

