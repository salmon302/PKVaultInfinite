Title: Gen1 "Recomp" slot1.lua save compatibility — implementation
Date: 2026-08-12T20:00:00Z
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: ad-hoc — add Gen1 "Recomp" Lua save (.lua) load + write-back support
Summary: Added a `LuaSaveReader` (ISaveReader) to the PKHeX fork, registered in `SaveUtil.CustomSaveReaders`, that detects and parses Gen1 "Recomp" project Lua saves into a standard `SAV1`. Added `LuaSaveConverter` for Lua↔SAV1 conversion, `LuaSaveRegistry` for unmapped-field preservation, and a PKVault `LuaSaveWrapper` that overrides `GetSaveFileData()` to serialize back to Lua text. All 7 Gen1 tests pass; no regressions.

---

## Implementation overview

### Architecture (Approach A from validation doc)

```
slot1.lua (Lua text, 3270 bytes)
        │
        ▼
[PKHeX fork]  LuaSaveReader : ISaveReader
        │  - IsRecognized(length): length > 4
        │  - TryRead: checks "return" magic + "yellow"/"red"/"engine" markers
        │    → LuaParser.Parse(text) → LuaTable
        │    → LuaSaveConverter.LuaToSAV1(table) → SAV1 (32768-byte binary)
        │    → stores original Lua text in LuaSaveRegistry (keyed by file path)
        │
        ▼  SaveUtil.TryGetSaveFile  → returns SAV1
[PKVault]     SaveWrapper (LuaSaveWrapper when .lua)  →  DexService switch → Dex123Service
        │
        ├── Dex/Storage: works via standard SAV1 + Dex123Service (no PKVault dex changes)
        │
        ▼
[PKVault]  write-back: LuaSaveWrapper.GetSaveFileData()
        │  → Save.Write() → binary SAV1
        │  → LuaSaveConverter.SAV1ToLua(sav) → Lua text bytes
        │  → writes UTF-8 Lua to original .lua path
```

### Files added

**PKHeX fork** (`PKHeX-master/PKHeX.Core/Saves/Gen1/Lua/`):

| File | Purpose |
|---|---|
| `LuaParser.cs` | Minimal Lua table parser: `LuaValue` (readonly struct), `LuaTable` (ordered entry list with `Serialize()`), `LuaParser` (recursive-descent parser for `return { ... }` with nested tables, `[n]=`, `key=`, strings, numbers, booleans, comments). |
| `LuaSaveReader.cs` | `ISaveReader` implementation. `IsRecognized` → true for data > 4 bytes. `TryRead` checks `return` magic prefix + `"yellow"`/`"red"`/`engine` markers, then parses and delegates to `LuaSaveConverter.LuaToSAV1`. |
| `LuaSaveConverter.cs` | Core conversion logic. `LuaToSAV1` maps Lua fields → SAV1 properties (money, OT, TID, playtime, player name, rival, pokedex bitfields, event flags, bag items, PC items, party PK1, boxes). `SAV1ToLua` reads SAV1 properties and updates the original Lua table in-place (preserving unmapped fields), then serializes. Species/move/item name resolution via Unicode-normalized lookup built from PKHeX's English string tables. |
| `LuaSaveRegistry.cs` | `ConcurrentDictionary<string, string>` stores original Lua text keyed by file path. `Store`, `TryGetOriginalLua`, `Remove`. Used by `SAV1ToLua` to retrieve the original table for unmapped-field preservation (objectToggles, player position, pikachu mood, etc.). |
| `Gen1EventFlagMap.cs` | Maps 12 Gen1 Yellow `EVENT_*` flag names → bit numbers (pokeyellow `event_flags.asm` ordering: 0–11). |

**PKHeX fork modification**:
- `SaveUtil.cs:110` — added `new LuaSaveReader()` to `CustomSaveReaders` list (before `ZipReader`).
- `SaveUtil.cs:7` — added `using PKHeX.Core.Saves.Gen1.Lua;`.

**PKVault.Backend**:
| File | Purpose |
|---|---|
| `storage/wrapper/LuaSaveWrapper.cs` | `LuaSaveWrapper(SAV1 save) : SaveWrapper(save)`. Overrides `GetSaveFileData()` to call `LuaSaveConverter.SAV1ToLua()` instead of `Save.Write()`. |
| `db/loader/save/SavesLoadersService.cs:220` | Added `.lua` detection: if `Path.GetExtension(path) == ".lua"` and save is SAV1 → `new LuaSaveWrapper((SAV1)saveRaw)` instead of `new SaveWrapper(saveRaw)`. |

**PKVault.Backend.Tests**:
| File | Purpose |
|---|---|
| `db/loader/save/Gen1LuaSaveLoadTests.cs` | 7 xUnit tests: detection, money/OT, party Pokemon, pokedex, event flags, inventory, round-trip write-back. |

### Field-level mapping (verified)

| slot1.lua field | SAV1 accessor | Test |
|---|---|---|
| `money` (125) | `SAV1.Money` (`Offsets.Money 0x25F3`, BCD) | ✓ `LuaSaveMoneyAndOT` |
| `playTime` (720.71) | `SAV1.PlayedHours/Minutes/Seconds` (`0x2CED`) | ✓ |
| `player.name` ("YELLOW") | `SAV1.OT` (`0x2598`, StringConverter1) | ✓ |
| `player.id` (63884) | `SAV1.TID16` (`0x2605`, BE) | ✓ |
| `player.rival` ("GARY") | `SAV1.RivalNameTrash` (`0x25F6`) | ✓ |
| `rivalStarter` (2) | `SAV1.RivalStarter` (`0x29C1`) | ✓ |
| `pikachuHappiness` (97) | `SAV1.PikaFriendship` (`0x271C`) | ✓ |
| `pokedex.owned/seen` | SAV1 dex bitfields (`0x25A3`/`0x25B6`) via `SetSeen/SetCaught` | ✓ `LuaSavePokedex` |
| `flags` (12 EVENT_*) | `SAV1.SetEventFlag` (`0x29F3` bit array) | ✓ `LuaSaveEventFlags` |
| `inventory`/`bagOrder` | `SAV1.Inventory.Pouches[0]` (ItemStorage1) | ✓ `LuaSaveInventory` |
| `pcItems` | `SAV1.Inventory.Pouches[1]` | ✓ |
| `party` (1 PK1) | `SAV1.SetPartySlotAtIndex` → PK1 | ✓ `LuaSavePartyPokemon` |
| `box` (empty) | `SAV1.SetBoxSlotAtIndex` | ✓ (empty, handled) |

### Unmapped fields (preserved via LuaSaveRegistry round-trip)
Fields that SAV1 does not expose but are preserved from the original Lua text:
- `objectToggles` (OAKS_LAB, PALLET_TOWN, ROUTE_22, VIRIDIAN_CITY)
- `player.map`, `player.x`, `player.y`, `player.facing`, `player.surfing`
- `lastHeal`, `lastOutdoor`
- `pikachuMood`, `pikachuWalkSteps`, `pikachuInBall`
- `poisonSteps`, `repelSteps`, `startMenuIndex`, `usedPokecenter`, `visited`
- `meta`, `modData`

These are read from the original Lua table on write-back and merged into the output, ensuring the "Recomp" game doesn't lose game state when PKVault writes edits.

### Known limitations
1. **Event flag mapping** — only the 12 flags present in the sample `slot1.lua` are mapped. Additional flags from other "Recomp" saves would need to be added to `Gen1EventFlagMap`. Unknown flags are silently dropped on write-back (they remain in the original table but are not written to the SAV1 binary).
2. **Item name aliases** — `PARLYZ_HEAL` is mapped to `PARALYZEHEAL` via an explicit alias. Other abbreviations may need similar treatment.
3. **Player position** — The Lua format stores player coordinates and map as named fields, but these are not exposed by SAV1 and are only preserved via the registry round-trip (not written to binary on first load).

### Build results
- **PKHeX.Core**: `dotnet build PKHeX-master/PKHeX.Core/PKHeX.Core.csproj -c Release` → **0 errors, 0 warnings**
- **PKVault.Backend**: `dotnet build PKVault.Backend/PKVault.Backend.csproj -c Release` → **0 errors** (72 pre-existing warnings, unchanged)
- **Tests**: `Gen1LuaSaveLoadTests` → **7/7 passed**
- **No regressions**: existing tests unaffected (IF test fails only due to missing `InfiniteSave/` directory, pre-existing)

### How to use
1. Add the save path to PKVault settings → `SAVE_GLOBS`:
   ```
   ./DevSaves/slot1.lua
   ```
2. PKVault will auto-detect the Lua format via `LuaSaveReader` and load it as a `SAV1`.
3. The save appears in *Save Infos* with version `YW` (Yellow) and generation `G1`.
4. The party Pikachu (`PEEPER`) appears in storage and the Pokedex reflects seen/caught state.
5. On write-back (save edits), `LuaSaveWrapper` serializes to Lua text, preserving unmapped fields from the original.
