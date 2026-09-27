# CPU AutoOC session tests

This console harness links the application's actual `AutoOcCpuSession.cs` and replaces its controller, monitor, hardware service, policy, and diagnostic sink with fakes. It has no driver reference and performs no CPU tuning.

## Run

From the repository root, using a .NET 8 SDK or a newer compatible SDK:

```powershell
dotnet run --project tools/AutoOcCpuSessionTests/AutoOcCpuSessionTests.csproj -c Release
```

No elevation, UXTU installation, or AMD CPU is required. A failed assertion produces a nonzero exit code. The current harness contains 69 assertions.

## Coverage

- Nonzero or unreadable baseline offsets remain untouched and prevent controller creation.
- Library identity, processor support, WHEA watcher health, and valid monitoring samples gate startup; sample startup has a bounded grace period.
- Only verified application or verified zero restoration is recorded as applied.
- The controller receives the shared production -50 search bound; fake -6 and -50 requests proceed to verified application, while -51 requests restore and pause.
- Failed or throwing applications restore zero and remain paused on subsequent enabled ticks.
- Lost monitoring, persistence failure, out-of-range requests, and conflicting or failed periodic readback restore and pause.
- Failed restoration preserves unclean recovery state and prevents an off/on toggle from restarting until zero restoration is verified.
- A fresh verified session starts a new policy epoch.
- Shutdown waits for a pending application, restores afterward, and prevents queued or future ticks from writing offsets.

## Limits

The tests verify session orchestration, not the real AutoOC controller's dwell timing or stability detector, WPF event wiring, firmware, driver behavior, or long-term CPU stability. Accepting -50 in a fake test does not establish real-world stability or make it an immediate tuning target. Separate hardware-service and actual-controller tests cover those components' software behavior. Controlled `TaskCompletionSource` instances exercise shutdown ordering without hardware or workload sleeps. The test-only timestamp adjustment reaches periodic-read and startup-timeout paths immediately; production timestamps are not changed.
