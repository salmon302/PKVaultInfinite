Title: Validate Gen1 "Recomp" slot1.lua save compatibility
Date: 2026-08-12T19:34:00Z
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Conception / Validation
Ticket/Context: ad-hoc — validate whether PKVault can ingest the Gen1 "Recomp" `DevSaves/slot1.lua` save
Summary: The Gen1 "Recomp" slot1.lua save is a Lua-text structured serialization (3270 bytes), not a binary Gen1 save (32768 bytes). PKVault's load pipeline calls `SaveUtil.TryGetSaveFile(data, ...)` (PKHeX), which has zero Lua-format detection. Verdict: Tier B — incompatible as-is; requires a fork-side Lua reader + PKVault-side Lua write-back. Full field-to-offset mapping and integration path documented.

---

## 1. Verdict

**INCOMPATIBLE — as-is, PKVault cannot load or write `DevSaves/slot1.lua`.**

The file is a 3 270-byte UTF-8 Lua text document (`return { ... }` table), not the 32 768-byte
(0x8000) binary Gen1 save that PKHeX's `SAV1` detector requires. Every stage of PKVault's load
pipeline rejects it.

## 2. Evidence

### 2.1 File properties

| Property | Value |
|---|---|
| Path | `DevSaves/slot1.lua` |
| Size | **3 270 bytes** |
| Format | UTF-8 Lua source — a single `return { ... }` table |
| Gen1 raw save size (`SaveUtil.SIZE_G1RAW`) | **0x8000 = 32 768 bytes** |

### 2.2 Load pipeline trace

PKVault discovers saves via `SAVE_GLOBS` (default `./pokemon-emerald-sample.sav`;
`PKVault.Backend/settings/services/SettingsService.cs:316,336`) and reads each as raw bytes:

```csharp
// SavesLoadersService.cs:206-214
var (TooSmall, TooBig) = fileIOService.CheckGameFile(path);   // 3270 > 32 and < 16MB → passes
var data = await fileIOService.ReadBytes(path);               // UTF-8 Lua text bytes
if (!SaveUtil.TryGetSaveFile(data, out var saveRaw, path))   // ← FAILS HERE
    return;                                                   // file silently skipped
```

`SaveUtil.TryGetSaveFile` (`PKHeX-master\PKEHeX.Core\Saves\Util\SaveUtil.cs:500`) runs three
detection stages — **none** match the Lua format:

| Stage | Method | Check | Result for slot1.lua |
|---|---|---|---|
| Custom readers | `TryGetSaveFileCustom` → `CustomSaveReaders` | Only `ZipReader` (`PKHeX-master/PKHeX.Core/Saves/Util/Recognition/ZipReader.cs:11`); probes for `PK\x03\x04` magic | Lua text has no ZIP magic → reject |
| Fixed-size | `GetSaveFileInternal` → `GetTypeInfo` → `IsG1` | `data.Length is not SIZE_G1RAW` (32 768) → **immediate return false** (`SaveUtil.cs:238-239`). Even if size matched, `IsG1INT`/`IsG1JPN` scan for Pokemon-list signatures at offsets 0x2F2C / 0x2ED5 | 3 270 ≠ 32 768 → reject |
| Handlers | `TryGetSaveFileHandler` → `Handlers` | `SaveHandlerFooterRTC`, Dolphin, DeSmuME, ARDS, NSO — all size/magic based | No match |

**Source anchors:**
- `SaveUtil.cs:235-248` — `IsG1` hard-rejects any size ≠ 0x8000
- `SaveUtil.cs:500-513` — `TryGetSaveFile` pipeline (custom → internal → handler)
- `SaveUtil.cs:116-117, 108-111` — `CustomSaveReaders` = `[ZipReader]` only
- `SaveUtil.cs:94` — `SIZE_G1RAW = 0x8000`
- `FileUtil.cs:203` — `IsFileTooSmall` = `length < 0x20` (3270 passes)
- `FileUtil.cs:188` — `IsFileTooBig` = `length > 0x100_0000` (16 MB; 3270 passes)
- `SavesLoadersService.cs:213` — the `TryGetSaveFile` call site
- `DexService.cs:107-128` — PKVault already dispatches `SAV1 ⇒ Dex123Service` (if detection *worked*)

### 2.3 No Lua parsing infrastructure

- PKHeX has **no Lua parser** or Lua-related types anywhere in `PKHeX-master/`. A repo-wide search for `lua`/`Lua` in `.cs` files returns zero matches.
- PKVault (`PKVault.Backend/`) has **no Lua parser** either; `PKVault.Backend.csproj` references PKHeX via `"./PKHeX.Core.dll"` only (`PKVault.Backend/PKVault.Backend.csproj:52`).

### 2.4 Write-back gap

PKVault persists saves via:
```csharp
// SaveWrapper.cs:186-189
public virtual byte[] GetSaveFileData() => Save.Write().ToArray();
// SaveFile.cs:685-689  (base)
public Memory<byte> Write(...) { var data = GetFinalData(); return Metadata.Finalize(data, setting); }
protected virtual Memory<byte> GetFinalData() { SetChecksums(); return Data.ToArray(); }
```

`SAV1.Write()` produces a **binary** 32 768-byte buffer. There is no Lua serializer, so even if
load succeeded, write-back would corrupt the file (binary written into a `.lua` path).

## 3. Field-level mapping (Lua → Gen1 binary)

The `slot1.lua` data is a Gen1 Yellow save in structured-text form. Below is the complete mapping
to PKHeX's `SAV1Offsets` (`PKHeX-master/PKHeX.Core/Saves/Substructures/Gen12/SAV1Offsets.cs`).
"OK" = clean 1:1 mapping; "Partial" = lossy or WRAM-only (no SAV1 accessor); "None" = no SAV1 equivalent.

| slot1.lua field | Gen1 offset (INT) | Effort | Notes |
|---|---|---|---|
| `money` (125) | 0x25F3, 3 B BCD | OK | `SAV1.Money` getter/setter exists |
| `playTime` (720.71) | 0x2CED, 5 B | OK | `SAV1.PlayedHours/Minutes/Seconds/Frames` |
| `player.name` ("YELLOW") | 0x2598, 10 B + trash | OK | `SAV1.OT`; needs StringConverter1 encoding |
| `player.rival` ("GARY") | 0x25F6, 10 B + trash | OK | `SAV1.RivalNameTrash` |
| `player.id` (63884) | 0x2605, 2 B | OK | `SAV1.TID16` |
| `rivalStarter` (2) | 0x29C2 (Starter-1) | OK | `SAV1.RivalStarter` = `Data[Starter-2]` |
| `pokedex.owned`/`seen` | 0x25A3 / 0x25B6 bitfields | OK | `SAV1.GetSeen/GetCaught` — but Lua stores species names as dict keys, need name→bit mapping |
| `inventory` (POKE_BALL=11, etc.) | 0x25C9 item bag | Partial | `SAV1.Inventory` (PlayerBag1) — needs item ID + quantity mapping |
| `pcItems` (POTION=1) | 0x27E6 PC items | Partial | Same item- ID mapping needed |
| `party` (1 PK: PIKACHU) | 0x2F2C party list | Partial | PK1 format fields exist (species, DVs, statExp, moves, level, etc.) — needs PK1 byte-level conversion |
| `flags` (24 EVENT_*) | 0x29F3 event flags | None | Gen1 has 320×8 = 2560 flags at 0x29F3; Lua uses named booleans — requires complete `EVENT_*` → bit-index table |
| `objectToggles` (OAKS_LAB, PALLET_TOWN, etc.) | WRAM object state | None | Not exposed by `SAV1`; requires Recomp-specific name→bit mapping |
| `pikachuHappiness` (97) | 0x271C | OK | `SAV1.PikaFriendship` |
| `pikachuMood` (128) | WRAM 0xDA4D | None | Yellow mood counter, no SAV1 accessor |
| `pikachuWalkSteps` (101) | WRAM 0xDA08 | None | Yellow follow steps, no SAV1 accessor |
| `pikachuInBall` (false) | WRAM 0xDCD7 | None | No SAV1 accessor |
| `poisonSteps` (2) | WRAM | None | No SAV1 accessor |
| `repelSteps` (0) | WRAM | None | No SAV1 accessor |
| `startMenuIndex` (6) | WRAM | None | No SAV1 accessor |
| `usedPokecenter` (true) | WRAM | None | No SAV1 accessor |
| `visited` (PALLET_TOWN, VIRIDIAN_CITY) | WRAM/ event work | None | No SAV1 accessor |
| `lastHeal` / `lastOutdoor` | WRAM location | None | No SAV1 accessor |
| `player.map` / `player.x` / `player.y` / `player.facing` | WRAM 0xD361+ | None | Player coordinates — no SAV1 accessor |
| `meta`, `modData` | N/A | N/A | Engine-specific; not part of vanilla Gen1 |
| `box` (empty) | 0x30C0+ box data | OK | `SAV1.GetBoxSlotAtIndex`/`SetBoxSlotAtIndex` |

**Key lossy fields for round-trip**: `objectToggles`, `pikachuMood`, `pikachuWalkSteps`,
`pikachuInBall`, `poisonSteps`, `repelSteps`, `startMenuIndex`, `usedPokecenter`, `visited`,
`lastHeal`, `lastOutdoor`, `player.map/x/y/facing` — all live in Gen1 WRAM but are **not
exposed** by the `SAV1` class's `SAV1Offsets` table. A converter would lose these on write-back
unless it extends the internal buffer to cover those WRAM regions.

## 4. Integration path

This is a **Tier B** target per `docs/technical/adding-games-compatibility.md §2`. The detection
entry point is PKHeX's `SaveUtil.TryGetSaveFile`
(`PKVault.Backend/db/loader/save/SavesLoadersService.cs:213`); PKVault cannot ingest the save
until PKHeX can parse it.

### Recommended: Approach A — `ISaveReader` + PKVault `LuaSaveWrapper`

Lightest code footprint; reuses all of `SAV1`'s logic.

**PKHeX fork** (`PKHeX-master/PKHeX.Core/`):

1. Add a `LuaSaveReader : ISaveReader` implementation. Register it in
   `SaveUtil.CustomSaveReaders` (`SaveUtil.cs:108-111`).
   - `IsRecognized(long)` → true for files starting with `return` (0x72 0x65 0x74 0x75 0x72 0x6E).
   - `TryRead(data, out result, path)`:
     1. Decode bytes as UTF-8 text.
     2. Parse the Lua table (need a Lua-parser dependency — see §4.3).
     3. Populate a 32 768-byte buffer using the `SAV1Offsets` table + field mapping (§3).
     4. `SaveUtil.TryGetSaveFile(buffer, out result)` → returns a standard `SAV1`.
     5. `result.Metadata.SetExtraInfo(path)` — preserves `.lua` filename in metadata.

**PKVault** (`PKVault.Backend/`):

2. Subclass `SaveWrapper` (it is `virtual` at `SaveWrapper.cs:186`):

```csharp
public class LuaSaveWrapper(SaveFile save) : SaveWrapper(save)
{
    public override byte[] GetSaveFileData()
    {
        // Save.Write() produces binary; re-serialize to Lua text
        var binary = Save.Write().ToArray();
        return LuaSerializer.FromBinary(binary, save.Version); // new: binary → Lua text
    }
}
```

3. In `SavesLoadersService.UpdateSaveFromPath` (`SavesLoadersService.cs:220`), detect Lua saves
   by checking `saveRaw.Metadata.FileName.EndsWith(".lua")` (or by a metadata flag set by the
   reader) and construct `LuaSaveWrapper` instead of `SaveWrapper`.

**PKVault dex** — **no change needed**. `DexService.cs:110` already dispatches
`SAV1 ⇒ Dex123Service`. Since the reader returns a `SAV1`, the dex, storage, and conversion
pipelines work immediately.

### Approach B — `SAV1Lua : SaveFile` subclass (fork-only, like IF)

Per `docs/technical/adding-games-compatibility.md §3.1`, create a full `SaveFile` implementation
in the fork (mirroring `SAV_InfiniteFusion`).

- `IsMatch` detects Lua format (magic bytes + `meta` field).
- Constructor parses Lua and implements all `SaveFile` abstract members (party, boxes, dex, items,
  money, etc.).
- Override `GetFinalData()` → Lua text, and `Extension` → `.lua`.
- Register via new `SaveFileType` + `SaveUtil.GetTypeInfo` case.

**PKVault**: add `case SAV1Lua lua => new Dex123Service(lua)` to `DexService.cs:107`.

**Downside**: `SAV1` is `sealed` (`SAV1.cs:10`), so `SAV1Lua` would either (a) duplicate much
of `SAV1`'s ~580 lines, or (b) require unsealing `SAV1` in the fork. More invasive than
Approach A.

### Approach C — Unseal `SAV1`, add `SAV1Lua : SAV1`

- Fork: unseal `SAV1` (change `sealed` → non-sealed at `SAV1.cs:10`).
- Fork: create `SAV1Lua : SAV1` that parses Lua in its constructor and overrides
  `GetFinalData()` + `Extension`.
- PKVault: add `case SAV1Lua lua => new Dex123Service(lua)` to `DexService.cs`.

**Downside**: modifies `SAV1`'s class design; less conservative than Approach A but cleaner
write-back (no PKVault `SaveWrapper` subclass needed).

## 4.3 Key requirement: a Lua parser

The `slot1.lua` format is a self-contained Lua table (`return { ... }` with nested tables,
strings, numbers, booleans, and comments). Options ranked by effort:

| Option | Effort | Risk | Notes |
|---|---|---|---|
| **MoonSharp** (NuGet) | Low | Low | Full Lua 5.2 interpreter in pure C#. Adds a NuGet dependency to the PKHeX fork. `LuaLoad` → `Table` → traverse. |
| **NLua** (NuGet) | Low | Medium | Wraps native Lua; platform-dependent native binaries needed (Linux/Windows/macOS). |
| **Custom minimal parser** | Medium | Medium | The format is simple enough for a ~150-LOC recursive-descent parser that handles `return {`, nested `{ }`, `"string"`, `number`, `true/false`, `=`, and `-- comments`. No dependency, but must handle edge cases (escaped quotes, multi-line strings). |

For a PKHeX fork, a **custom parser** is most appropriate — the existing `CustomSaveReaders`
(`ZipReader`) already uses `System.IO.Compression` from the BCL; a hand-rolled Lua-table parser
avoids adding a scripting engine to PKHeX's dependency surface. The format is regular: no
functions, no metatables, no `do/end` blocks, no variable references — just nested tables and
scalar values.

## 4.4 Key requirement: EVENT_* / object toggle name → bit mapping

The Lua `flags` and `objectToggles` sections use **human-readable names** (`EVENT_GOT_OAKS_PARCEL`,
`OAKSLAB_OAK1`, etc.). The Gen1 binary save stores these as bit flags in WRAM:
- Event flags: 2560 bits starting at `EventFlag` (0x29F3 for INT).
- Object toggles: object state bytes in WRAM (address varies by map/script).

A complete `EVENT_*` → bit-number table and `objectToggle` → WRAM-byte/bit table must be supplied
by the "Recomp" project. Without it, these fields cannot be mapped and will be lost on round-trip.
This is a data dependency on the external project (cf. `docs/technical/adding-games-compatibility.md §4.1 checklist: "save format spec … box/party offset map, event flag bitfield layout").

## 5. Comparison with Infinite Fusion (working precedent)

| Aspect | Infinite Fusion | Gen1 "Recomp" |
|---|---|---|
| Container | Raw Ruby Marshal blob (.rxdata) | Lua text (.lua) |
| PKHeX detection | `IsMatch`: 4-byte magic `04 08 7B 3A` + size ≥ 0x10000 (`SaveUtil.cs:200,372`) | No magic; size 3 270 ≠ any recognized size |
| PKHeX class | `SAV_InfiniteFusion : SaveFile` (full impl) | No class exists; needs `ISaveReader` or new `SaveFile` |
| Entity type | `PK9` (Gen 9) | `PK1` (Gen 1) — already handled by PKHeX |
| Write-back | Overridden `GetFinalData()` → `RubyMarshal.Save` | Would need binary → Lua serializer |
| Dex service | `DexIFService` (new, custom) | `Dex123Service` (already exists; `DexService.cs:110`) |
| Status | Functional | Not started |

The IF precedent shows the pattern works (fork PKHeX → PKVault dex dispatch). The Gen1 "Recomp"
integration is simpler in some ways (uses existing `PK1`/`SAV1`/Gen1 dex infrastructure) but
harder in others (text format requires a Lua parser; Gen1 event-flag mapping is name-based).

## 6. Minimum deliverables from the "Recomp" project

Hand this to the external maintainers (cf. `adding-games-compatibility.md §4`):

- [ ] **Save format spec**: Lua schema (field names, types, semantics), the complete
  `EVENT_*` → Gen1 event-flag-bit map, `objectToggles` → WRAM byte/bit map, player position
  WRAM layout, and any non-vanilla (`meta.*`, `modData.*`) semantics.
- [ ] **Reference**: which Gen1 engine/ROM this recompiles (`pokeruby`? `pokecrystal`? `pokeyellow`?),
  and the WRAM map offset to `SAV1Offsets` (INT vs JPN vs Yellow).
- [ ] **Sample saves** in `.lua` form (at least one for each starter, one post-E4, etc.) for
  PKVault's test harness.

## 7. Testing plan

Once the fork-side reader + PKVault-side wrapper exist:

1. Add `slot1.lua` (or a glob pointing at `DevSaves/`) to `SAVE_GLOBS` in settings.
2. Confirm the save appears in *Save Infos* (`SavesLoadersService.cs:225` debug log) with
   `G1 / Version=yellow`.
3. Confirm the single party Pikachu (`PEEPER`, species PIKACHU) appears in storage with correct
   level (6), moves (THUNDERSHOCK/GROWL/TAIL_WHIP), DVs, statExp, and catchRate (190).
4. Confirm the Pokédex reflects `seen = {EEVEE, PIDGEY, PIKACHU, RATTATA}` and
   `owned = {PIKACHU}`, and that write-back (editing seen/caught) round-trips to Lua.
5. Confirm money (125), OT ("YELLOW"), TID (63884), and playTime (720.71) are preserved.
6. Confirm a Gen1 Pokémon can be moved between this save and a bank/storage box (converts as
   `PK1`).
7. Run `PKVault.Backend.Tests` and confirm `IFBoxLoadTests` still passes (no regression).
