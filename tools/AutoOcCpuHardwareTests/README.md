# CPU hardware service tests

This console test harness links the application's actual `AutoOcCpuHardware.cs` source and injects a fake backend. It does not reference AutoOC, load PawnIO, or send firmware commands. Stub production types throw if the hardware backend is accidentally selected.

## Run

From the repository root, using a .NET 10 SDK:

```powershell
dotnet run --project tools/AutoOcCpuHardwareTests/AutoOcCpuHardwareTests.csproj -c Release
```

No elevation, installed UXTU copy, or AMD CPU is required. A failed assertion produces a nonzero exit code. The current harness contains 58 assertions.

## Coverage

- Unsupported processors and offsets outside -5 through 0 cause no backend access.
- Acknowledgement alone is insufficient: success requires matching readback from all 16 distinct core selectors.
- Signed offsets and raw rejected status values are retained.
- Rejected or throwing setters, failed/malformed/implausible queries, and mismatched readback restore zero before returning failure.
- Restoration clears the rejection cache, still runs if clearing fails, and reads back even if the restore setter rejects or throws.
- Explicit restoration verifies zero, and concurrent operations across service instances remain serialized.

## Limits

These tests verify the service's decisions and ordering, not real firmware, driver behavior, CPU detection on every platform, electrical voltage, or undervolt stability. The production backend's CPUID gate and actual SMU transport require separate validation on supported hardware. The serialization gate covers this service; integration must prevent competing offset writers and require a verified zero baseline before taking ownership. A managed rollback cannot survive process termination or an OS crash.
