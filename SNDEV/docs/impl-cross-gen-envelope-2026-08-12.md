Title: cross-gen item 1 - PKVault persisted PKM envelope
Date: 2026-08-12T00:00:00Z
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: plan-cross-gen.md item 1
Summary: Wrap persisted raw PKM bytes with a PKVault envelope (PKVAULT:1|<EntityContext>|<PKHeXVersion>|<raw>) so a PKHeX version bump is detectable on load instead of silently failing; mismatch triggers re-normalization or quarantine.

## Changes

- `PKVault.Backend/db/loader/PkmVaultEnvelope.cs` (NEW): `Wrap`/`TryUnwrap`/`IsEnveloped`/`GetPkhexVersion`. Header boundary is located by the 3rd `|` delimiter so raw bytes containing delimiter bytes are preserved. Legacy (no prefix) bytes are returned untouched for backward compatibility. Malformed header or unsupported format version => `error` out param (caller quarantines).
- `PKVault.Backend/db/loader/PkmFileLoader.cs:229` `GetPKMBytes` now returns `PkmVaultEnvelope.Wrap(pkm.GetDecryptedDataParty(), pkm.Context)`.
- `PkmFileLoader.cs:195` `CreatePKM` now `TryUnwrap`s `entity.Data`. On malformed envelope or on a `TryGetPKM` null when a PKHeX version drift is the likely cause, returns `PKMLoadError.QUARANTINE` (new enum value in `ImmutablePKM.cs:485`). On stored `PKHeXVersion != current`, it re-reads the pkm, re-writes current-layout party bytes, re-stamps the envelope and marks `entity.Updated = true` for one-time migration.
- `PKVault.Backend/storage/wrapper/ImmutablePKM.cs:485` added `PKMLoadError.QUARANTINE`.
- `PKVault.Backend.Tests/db/loader/PkmVaultEnvelopeTests.cs` (NEW): 5 tests (round-trip, delimiter-in-raw, legacy passthrough, malformed header, unsupported version). All pass.

## Evidence

- `dotnet build PKVault.Backend` => 0 errors.
- `dotnet run --project PKVault.Backend.Tests -class "PkmVaultEnvelopeTests"` => Total: 5, Errors: 0, Failed: 0.

## Notes / risk

- Envelope adds ~27 bytes; well above PKHeX `IsFileTooSmall` minima, so the size guard in `FileIOService.CheckGameFile` is unaffected.
- Re-normalization only mutates/stamps enveloped files whose stored PKHeX version differs from current; legacy files are left untouched (no mass rewrite). External-preview loads (EntityContext.None) are unaffected since `GetPKMBytes` is not used there.
- Cross-context mismatch (envelope context != folder context) is logged as a warning but parsing still uses the folder context; full reject/relocate is deferred to plan item 4.
