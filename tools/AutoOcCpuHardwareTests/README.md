# CPU hardware service tests

This console test harness links the application's actual `AutoOcCpuHardware.cs` source and injects a fake backend. It does not reference AutoOC, load PawnIO, or send firmware commands. Stub production types throw if the hardware backend is accidentally selected.

## Run

From the repository root, using a .NET 10 SDK:

```powershell
dotnet run --project tools/AutoOcCpuHardwareTests/AutoOcCpuHardwareTests.csproj -c Release
```

No elevation, installed UXTU copy, or AMD CPU is required. A failed assertion produces a nonzero exit code. The current harness contains 63 assertions.

## Coverage

- Unsupported processors and offsets outside the shared -50 through 0 search range cause no backend access; -51 and +1 are rejected.
- Fake applications at -6 and -50 require matching signed readback. The test project links the production `AutoOcCpuLimits.cs` rather than copying its bound.
- Acknowledgement alone is insufficient: success requires matching readback from all 16 distinct core selectors.
- Signed offsets and raw rejected status values are retained.
- Rejected or throwing setters, failed/malformed/implausible queries, and mismatched readback restore zero before returning failure.
- Restoration clears the rejection cache, still runs if clearing fails, and reads back even if the restore setter rejects or throws.
- Explicit restoration verifies zero, and concurrent operations across service instances remain serialized.

## Limits

These tests verify the service's decisions and ordering, not real firmware, driver behavior, CPU detection on every platform, electrical voltage, or undervolt stability. The -50 limit is the original AutoOC search bound, not a target applied by these tests or evidence that -50 is stable. The production backend's CPUID gate and actual SMU transport require separate validation on supported hardware. The serialization gate covers this service; integration must prevent competing offset writers and require a verified zero baseline before taking ownership. A managed rollback cannot survive process termination or an OS crash.
