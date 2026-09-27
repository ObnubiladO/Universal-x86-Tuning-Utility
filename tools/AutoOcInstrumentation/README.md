# Local AutoOC diagnostic instrumentation

This branch adds observational logging to the local UXTU build. It includes the timer fix from PR #391. It does not change AutoOC decisions, thresholds, offsets, or learned limits.

The detector is distributed as `AutoOC.dll` rather than source in this repository. The tool instruments a **local copy of the audited 26.3.1 DLL**. No AutoOC binary or decompiled detector implementation is included in this branch. Other DLL versions are rejected by SHA-256.

Build the diagnostic UXTU app normally with .NET 10, satisfying its existing AutoOC reference with the installed 26.3.1 dependency. Publish into a separate portable directory, then run:

```powershell
dotnet run --project tools/AutoOcInstrumentation -c Release -- <original-AutoOC.dll> <published-UXTU.dll> <published-AutoOC.dll>
```

The tool preserves all original instructions and semantic operands, widens short branches as necessary, and adds context capture before 14 direct signal calls plus logging at entry of both signal methods. The original flag setters still execute. Event handlers include provider, event ID, record ID, level and timestamp; access violations include exception type/HResult. Direct callers retain literal source-method and original-IL-offset attribution. Signals through external delegates use a best-effort stack fallback and are explicitly classified as unclassified when necessary.

`AutoOC.dll.instrumentation.json` records the input/output hashes and every call site. Keep the instrumented DLL with its matching diagnostic UXTU assembly: it calls public trace hooks in that assembly. Do not replace the installed application's DLL with it.

The trace is `logs/autooc-diagnostics.jsonl` beside the portable executable. It records instability sources, scores, recent performance-counter samples, five-sample heuristic masks/counts, CPU load, controller offsets/holds, state changes, 30-second heartbeats and command dispatch attempts. **Dispatch records are not hardware acceptance/readback.** State is sampled without taking controller locks and is not a cross-thread atomic snapshot.

Records go through a bounded, non-blocking queue to a background writer. The log rotates at 8 MiB and retains one previous file; queue drops and write failures are counted in subsequent records. I/O failures must not change signaling or tuning. The logger does not consume instability flags. An abrupt process termination can lose queued records. The reason for historical signals cannot be recovered retroactively.

To validate the actual patched signal methods without starting hardware monitoring:

```powershell
dotnet build tools/AutoOcDiagnosticsTests -c Release -p:DiagnosticAppDir=<absolute-portable-directory>
dotnet tools/AutoOcDiagnosticsTests/bin/Release/net10.0-windows10.0.22621.0/AutoOcDiagnosticsTests.dll <fresh-test-log-directory>
dotnet tools/AutoOcDiagnosticsTests/bin/Release/net10.0-windows10.0.22621.0/AutoOcDiagnosticsTests.dll <another-fresh-test-log-directory> blocked
```

Tests allocate an uninitialized monitor and invoke its actual private signal/exception-handler methods. They never run its constructor, load hardware drivers, start a tuning controller, or issue hardware commands. They cover attribution/context, flag preservation, captured scores/offsets, nonfinite samples, state deduplication, flushing, and an unwritable log destination. They do not establish hardware stability or detector accuracy.
