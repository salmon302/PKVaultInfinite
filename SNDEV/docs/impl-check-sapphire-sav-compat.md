Title: Verify + restore Sapphire (USA) GBA save loading
Date: 2026-08-12T00:00:00Z
Author: Seth Nenninger (poolside/laguna-s-2.1 Agent)
Contribution Type: Implementation
Ticket/Context: ad-hoc
Summary: Confirmed the Sapphire save is PKHeX-compatible; diagnosed why PKVault was not loading it (configured glob pointed at the live mGBA save, which is exclusively locked by the running mGBA process); repointed the glob to the verified DevSaves copy and re-scanned; Sapphire now loads as `SAV3RS`/Version S.

## Target file
`C:\Users\salmo\Documents\GitHub\PKVaultInfinite\DevSaves\Pokemon - Sapphire Version (USA).sav`
- Size: 131088 bytes (0x20010) = 131072 (0x20000) GBA save + 16-byte RTC footer (emulator style).

## Verification 1 — PKHeX load path (compatible)
Temp console project referencing the backend's `PKHeX.Core.dll` (v26.08.12), replaying PKVault's exact pipeline
(`SavesLoadersService.UpdateSaveFromPath`, PKVault.Backend/db/loader/save/SavesLoadersService.cs:197-233):
```
File: Pokemon - Sapphire Version (USA).sav
Size: 131088 bytes (0x20010)
IsFileTooSmall: False | IsFileTooBig: False
Before TryRevise: Type=SAV3RS Gen=3 Ver=RS Lang=0
SaveLanguage.TryRevise returned: True
RESULT: SUCCESS - save is compatible
  SaveType: SAV3RS
  Generation: 3
  Version: S          <-- inferred as Sapphire from filename
  LanguageID: 2        <-- English
  OT: SETH             Boxes: 14   PartyCount: 6
```
- `FileUtil.IsFileTooSmall/IsFileTooBig` (PKHeX-master/PKHeX.Core/Util/FileUtil.cs:203,186) PASS.
- `SaveHandlerFooterRTC` (PKHeX-master/PKHeX.Core/Saves/Util/Recognition/SaveHandlerFooterRTC.cs:13) strips the 16-byte footer (131088 & 0x3F = 0x10; noFooter = 0x20000 = SIZE_G3RAW).
- `IsG3` (SaveUtil.cs:285-299) + `SAV3.IsAllMainSectorsPresent` (SAV3.cs:119) validates 14 sectors; `GetVersionG3SAV` (SaveUtil.cs:306-325) reads offset 0xAC == 0 -> RS -> `new SAV3RS` (SaveUtil.cs:647). Sapphire is handled by SAV3RS (SAV3.cs:200: `GameVersion.R or GameVersion.S or GameVersion.RS`).
- `SaveLanguage.TryRevise` (SavesLoadersService.cs:217-218) infers English + GameVersion.S from the filename ("sapp").

## Verification 2 — runtime load (why data was missing)
PKVault.Backend/bin/Debug/net10.0/logs/pkvault-20260812.log showed that on session start the Sapphire save FAILED to load:
```
[ERR] System.IO.IOException: The process cannot access the file
  'C:\Users\salmo\Documents\mGBA-0.10.5-win32\Pokemon - Sapphire Version (USA).sav'
  because it is being used by another process.
  ... FileIOService.ReadBytes (...) FileIOService.cs:96
  ... SavesLoadersService.UpdateSaveFromPath (...) SavesLoadersService.cs:212
```
- Root cause: `config/pkvault.json` `SAVE_GLOBS` pointed the Sapphire entry at the **live mGBA save path** (`mGBA-0.10.5-win32\...sav`). The mGBA emulator was running (PID 20256) and held that file with an **exclusive lock** (OS-level; not overridable by PKVault). `File.ReadAllBytesAsync` (FileIOService.cs:96) threw `IOException`, caught & logged at `SavesLoadersService.cs:229` (logged as `[ERR]`, not surfaced to the UI), so the save was silently skipped -> "2 saves" loaded instead of 3; no Sapphire data.
- The DevSaves copy (identical content/size, same mtime) was NOT being read by PKVault.
- Note: this is not a code compatibility problem — PKVault simply cannot read a file another process holds exclusively.

## Fix applied
Repointed the Sapphire entry in the runtime config `PKVault.Backend/bin\Debug\net10.0\config\pkvault.json`
(git-ignored, not committed) from the locked mGBA path to the verified DevSaves copy, then triggered an in-process re-scan via the app's own endpoint `PUT /api/save-infos` (`SaveInfosController.Scan`, SaveInfosRoute.cs:21 -> `RefreshSettings` + `Clear` + `StartNewSession` -> `ReadSaveFiles`).

## Verification 3 — post-fix load (PASS)
`PUT /api/save-infos` -> HTTP 200. Log after re-scan:
```
[DBG] Save 1713453007 1713453007 1713453007 - G3 - Version S - play-time 4E?48E?05
```
GET `/api/save-infos` now lists Sapphire (id 1713453007):
```
generation=3 context=3 version=1(S) language=2(English) trainerName=SETH
dexSeen=29 dexCaught=19 owned=18 partyCount=6 boxCount=14 boxSlotCount=30
path=C:/Users/salmo/Documents/GitHub/PKVaultInfinite/DevSaves/Pokemon - Sapphire Version (USA).sav
```
No `IOException`/`ArgumentException` for Sapphire in the post-fix scan -> PASS.

## Tradeoff / note for the user
- Sapphire is now read from the DevSaves snapshot. If you keep playing in mGBA and want the *live* (up-to-date) save reflected in PKVault, close mGBA first (it releases the exclusive lock), then re-scan (`PUT /api/save-infos` / restart) so PKVault can read the unlocked file from its original path. Re-adding the mGBA glob while mGBA is closed will pick up live progress.
- If both the mGBA path and DevSaves path resolve to the same save (same TID/SID/OT), PKVault dedupes by save ID, but loading two identical-content paths concurrently can hit a TOCTOU race in `UpdateGlobalsWithSave` (SavesLoadersService.cs:235-254, `ConcurrentDictionary.TryGetValue` + `Add`). To avoid that, keep a single Sapphire glob (current state: DevSaves only).
