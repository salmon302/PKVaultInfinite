Title: Ship rebuilt PKHeX fork DLL into PKVault.Backend with IF support
Date: 2026-08-07T12:10:00Z
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: ad-hoc (handoff: "Next up — ship rebuilt PKHeX.Core.dll")
Summary: Rebuilt forked PKHeX.Core (net10.0) with SAV_InfiniteFusion shim and copied it over PKVault.Backend/PKHeX.Core.dll; bumped PKHeX.version 26.07.07 → 26.08.07.

Evidence:
- Built PKHeX-master/PKHeX.Core (Release, net10.0) with SDK 10.0.302 at C:\Users\salmo\.dotnet (DOTNET_ROOT set). 0 warnings / 0 errors.
- Copied bin\Release\net10.0\PKHeX.Core.dll → PKVault.Backend/PKHeX.Core.dll (LastWriteTime 2026-08-07 12:01:50).
- PKHeX.version updated to 26.08.07. Note: this file is human-readable metadata only; the backend references the DLL directly via <Reference Include="./PKHeX.Core.dll" /> (PKVault.Backend.csproj:52). The assembly's own AssemblyVersion remains 26.7.7.0 (defined by PKHeX.Core's own version props, unchanged).
- Verified detection end-to-end via PKHeX-master/IFTool on InfiniteSave/File A.rxdata: IsMatch=True, SaveUtil detected SAV_InfiniteFusion in 763ms, OT=Seth ID32=3291957797, BoxCount=41, Write() byte-identical to input (1722551 bytes).
- PKVault.Backend builds clean (0 errors; only pre-existing CS8602/CS8619 nullability warnings) after killing the locking dev instance (PID 31272) so the bin DLL could be overwritten.

Notes:
- The fork source (SAV_InfiniteFusion.cs, IFNameLookup.cs, SaveFileType.cs, SaveUtil.cs) was already modified 2026-08-06 but the shipped DLL was stale (Aug 5). This task closed that gap.
- Not committed (no explicit request). Remaining "Next up" items (Pokédex species-order table, Marshal write-back, DexIFService) are still blocked/deferred per the handoff.
