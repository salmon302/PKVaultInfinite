Title: cross-gen item 2 - decouple GetPKMTypeWeight from PKM enum
Date: 2026-08-12T00:00:00Z
Author: Seth Nenninger (tencent/hy3 Agent)
Contribution Type: Implementation
Ticket/Context: plan-cross-gen.md item 2
Summary: GetPKMTypeWeight no longer throws ArgumentException on unknown/future PKM types; it derives a stable numeric weight from the type name's embedded generation, and unsupported conversions fail with an explicit NotSupportedException instead of a crash.

## Changes

- `PKVault.Backend/storage/services/PkmConvertService/PkmConvertService.cs:216` `GetPKMTypeWeight` replaced the throwing `switch` with a `Dictionary<string,int>` of the same pinned weights (PK1=0 .. PKF=18) plus a safe fallback: unknown types get `GetGenerationFromTypeName(name) * 100`, so ordering is preserved and no exception is thrown. `GetGenerationFromTypeName` parses the embedded generation digits from the type name (no LINQ dependency).
- `ConvertRecursive` (same file) now detects an unknown source/target type up front and throws `NotSupportedException("Conversion not yet supported for X -> Y")` with a `log.LogWarning`, replacing the previous silent fall-through that ended in a generic "No conversion path" `InvalidOperationException`.

## Evidence

- `dotnet build PKVault.Backend` => 0 errors.
- `PKVault.Backend.Tests` `PkmConvertServiceTests` added 2 tests: `GetPKMTypeWeight_KnownType_ReturnsPinnedWeight` (PK9 => 16) and `GetPKMTypeWeight_UnknownFutureType_DoesNotThrowAndDerivesStableWeight` (unknown type => 9900; type with digit "10" => 1000). Both pass.
- Note: 3 pre-existing RK4 conversion failures in `PkmConvertServiceTests` exist on `main` before this change (verified via `git stash`); they are unrelated to item 2.

## Notes / risk

- Pinned weights for currently supported types are byte-for-byte identical to the old `switch`, so conversion ordering/direction is unchanged.
- A future generation (e.g. Gen10) will no longer crash the converter; it will instead cleanly report "not yet supported" and can be wired in later by adding one dictionary entry + the relevant Try*Conversion arm.
