How could we improve/allow for (safe) cross gen compatibility?
In PKVault, "cross-gen" happens in two distinct places: (1) runtime conversion via PkmConvertService and (2) persisted storage of raw PKHeX bytes. "Safe" means different things for each. Here are the concrete weak spots and fixes:

1. Persisted PKM bytes have no PKVault-level format envelope PkmFileLoader.cs:229 stores GetDecryptedDataParty() raw bytes keyed only by EntityContext folder + PKHeX extension. The layout is implied by the vendored PKHeX.Core.dll (pinned 26.08.12). If you bump PKHeX.version, stored bytes for the same format could shift and silently fail to re-read.

Fix: wrap stored bytes with a tiny header (PKVAULT:1|<EntityContext>|<PKHeXVersion>|<raw>). On load, if PKHeXVersion differs, run a re-normalization pass through PkmConvertService or quarantine. This is the single biggest "safe compatibility" win.
2. GetPKMTypeWeight is a hardcoded switch that throws on unknown types PkmConvertService.cs:216 throws ArgumentException for any new PKM type. Adding Gen10 = a forced code change + a crash risk if a user loads it before you update.

Fix: derive weight from PKM.Format/Generation numerically, or use a dictionary with a safe default + explicit "conversion not yet supported" result instead of throwing. Decouples the converter from the enum.
3. Lossy down-conversion is silent Gen9→Gen1 drops moves/abilities/forms with no record. "Safe" means the user shouldn't lose data unknowingly.

Fix: keep an immutable original copy (you may already via PkmVersions) and surface a diff/warning of dropped fields before committing a down-conversion. Store both source and result so round-trips are lossless upward.
4. Cross-context "islands" aren't validated on ingest Gen9Fusion/PKF is a conversion island, but loaders (SavesLoadersService.cs:213, PkmFileLoader.cs:210) trust PKHeX detection. A mislabeled file could be stored under the wrong context.

Fix: assert PKM.Context matches the storage folder on write; reject/relocate mismatches.
5. Forward-compat for new generations Add a registry/test that fails fast if a new EntityContext member lacks a conversion weight, a static-data generation entry, and a save loader — rather than discovering it at runtime.