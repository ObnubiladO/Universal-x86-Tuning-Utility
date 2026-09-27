# Local AutoOC diagnostic instrumentation

The default instrumentation adds observational logging to the local UXTU build. It includes the timer fix from PR #391. Default mode does not change AutoOC decisions, thresholds, offsets, or learned limits. The separately enabled CPU policy described below changes one CPU instability input and uses isolated CPU learned state.

The detector is distributed as `AutoOC.dll` rather than source in this repository. The tool instruments a **local copy of the audited 26.3.1 DLL**. No AutoOC binary or decompiled detector implementation is included in this branch. Other DLL versions are rejected by SHA-256.

Build the diagnostic UXTU app normally with .NET 10, satisfying its existing AutoOC reference with the installed 26.3.1 dependency. Publish into a separate portable directory, then run:

```powershell
dotnet run --project tools/AutoOcInstrumentation -c Release -- <original-AutoOC.dll> <published-UXTU.dll> <published-AutoOC.dll>
```

In default mode the tool preserves all original instructions and semantic operands, widens short branches as necessary, and adds context capture before 14 direct signal calls plus logging at entry of both signal methods. The original flag setters still execute. Event handlers include provider, event ID, record ID, level and timestamp; access violations include exception type/HResult. Direct callers retain literal source-method and original-IL-offset attribution. Signals through external delegates use a best-effort stack fallback and are explicitly classified as unclassified when necessary.

## Optional local CPU policy v1

Append `--cpu-policy-v1` as the fourth argument to enable the local CPU experiment:

```powershell
dotnet run --project tools/AutoOcInstrumentation -c Release -- <original-AutoOC.dll> <published-UXTU.dll> <published-AutoOC.dll> --cpu-policy-v1
```

This requires a host assembly with the public static hook
`AutoOcCpuPolicy.ObserveWorkloadSignal(object monitor)`. Exactly three audited
original operands are replaced; unexpected counts or locations abort output:

- The single `MonitorPerformanceCounters` CPU signal at original IL offset
  `0x0708` calls that host hook, preserving its diagnostic context prelude. The
  hook observes the workload score without setting the CPU instability flag.
  Other signal callers, including WHEA, exceptions and watchdogs, retain their
  original behavior. The iGPU path and host guard are preserved.
- The monitor constructor's WHEA event query matches both
  `Microsoft-Windows-WHEA-Logger` and legacy `WHEA-Logger` provider names.
- Only the CPU state filename changes to `uv_state_cpu_verified_v1.json` under
  the existing `%LOCALAPPDATA%/AutoOC/AdaptiveUndervolt` directory. Its backup
  derives from the new filename. Original CPU learned state and iGPU filenames
  are unchanged. This starts a separate CPU learning history; it does not prove
  that offsets are stable or that prior learned limits were incorrect.

The manifest explicitly records `cpuPolicyEnabled`, the policy version and all
three operand replacements, with `tuningRulesChanged=true` and
`originalInstructionsPreserved=false`. All other original instructions and
semantic operands are checked before output. The input must still be the exact
audited original DLL, not a previously instrumented or policy-modified copy.
Policy mode also adds assembly metadata `AutoOcCpuPolicy=cpu-policy-v1`, which
the host checks before enabling its CPU policy path. Default instrumentation
does not add that marker.
Setter acknowledgements remain distinct from effective-offset readback, and
passing isolated tests does not establish hardware stability.

## Verified CPU host session

The matching host selects its verified session when the policy DLL marker is
present or the portable directory contains `autooc-cpu-policy-v1.enabled`. If
the marker file requests policy mode but the matching DLL is missing, CPU
AutoOC pauses instead of falling back to the original tuning path.

This session supports only the checked **Ryzen 9 9950X with SMT enabled**:
AuthenticAMD, CPUID family `0x1A` / model `0x44`, Granite Ridge, 16 physical
cores and 32 reported threads. The 9950X3D and other processors are rejected.
Monitoring must provide valid CPU samples, an active WHEA watcher and no
watcher errors; recovery-state persistence must also remain available.

Before taking control, the host reads all 16 core offsets and requires every
one to be zero. Pre-existing nonzero offsets are left unchanged. It then
starts a fresh evaluation epoch while retaining the policy state's learned
limits and unclean-exit recovery protections. Tuning is limited to offsets
from **0 through −50** (the original UXTU CPU search range), in steps of one,
after **60 qualifying active evaluations**
per step. Evaluations are at least one second apart; idle periods, cooldowns
and holds can make this take longer. Existing idle handling remains active.

The earlier -5 trial limit has been removed. A shared `AutoOcCpuLimits`
constant supplies both hardware guards and the controller bound. The existing
`uv_state_cpu_verified_v1.json` is retained: its recorded `MinOffset` does not
override the constructor's current range, while learned failure floors and
crash history still load. The controller backs off after a detected failure
and respects the learned limit for the workload band. It does not jump directly
to -50, measure performance per watt, or declare a globally proven optimum.
Absence of a detected failure during a sampling interval is not a complete
stability test. Crash recovery uses the last verified offset; a crash during
the first pending application has not yet recorded that candidate as applied.

After every offset change, the host requires setter acknowledgement and
getter readback matching the request on **all 16 cores**. It repeats the
readback approximately every **15 seconds** while the verified offset is
unchanged. A failed command, failed or mismatched readback, monitoring fault,
or unavailable recovery state pauses the session. Once the session owns the
hardware, it attempts restoration to **0** on failure, when CPU AutoOC is
disabled, and during normal exit, then verifies zero on all cores. If zero
cannot be verified, it reports that failure and preserves unclean-recovery
state rather than declaring a clean shutdown. Abrupt termination cannot
guarantee that an exit-time restoration runs.

Only workload-counter CPU signals become observations. WHEA, exception,
watchdog and other original hard-error signaling and controller backoff
remain in place, with the corrected WHEA provider query above. These checks
do not constitute exhaustive CPU stability testing.

Session headers identify whether CPU policy was requested, whether its DLL
matches, and whether tuning rules differ from observational mode. Actual
baseline, application, periodic and restoration readback outcomes appear in
`cpu_policy` records (`baseline_readback`, `offset_application`,
`periodic_readback`, `restore_zero`). A session header or ordinary dispatch
record never establishes readback by itself.

`AutoOC.dll.instrumentation.json` records the input/output hashes and every call site. Keep the instrumented DLL with its matching diagnostic UXTU assembly: it calls public trace hooks in that assembly. Do not replace the installed application's DLL with it.

The trace is `logs/autooc-diagnostics.jsonl` beside the portable executable. It records instability sources, scores, recent performance-counter samples, five-sample heuristic masks/counts, CPU load, controller offsets/holds, state changes, 30-second heartbeats and command dispatch attempts. **Dispatch records are not hardware acceptance/readback.** State is sampled without taking controller locks and is not a cross-thread atomic snapshot.

CO setters also emit `smu_command_result` records from the existing command path. These retain the original arguments, execution identifier, mailbox, message and returned status, or distinguish initialization errors, unmapped commands and cached skips. `acceptedByFirmware` is true only for `Status.OK`. A `FAILED` status can include transport or mutex failure, so `send_returned` does not necessarily mean firmware replied. Every `smu_command_result` record retains `hardwareReadbackVerified=false`: a setter acknowledgement is not a separate getter result. These logging hooks issue no additional hardware commands and preserve routing, fallback and rejection caching; the optional verified host session performs its separate getter checks described above. Run `tools/AutoOcSmuTests/run.ps1` for isolated tests of the actual command logic with scripted transport substitutes.

Monitor snapshots include `effectiveCpuAnomalyThreshold` as well as the configured `requiredCpuAnomalies`. The original detector lowers its threshold by one at CPU usage of at least 85 percent; the diagnostic value reflects that existing rule without changing it.

Records go through a bounded, non-blocking queue to a background writer. The log rotates at 8 MiB and retains one previous file; queue drops and write failures are counted in subsequent records. I/O failures must not change signaling or tuning. The logger does not consume instability flags. An abrupt process termination can lose queued records. The reason for historical signals cannot be recovered retroactively.

To validate the actual patched signal methods without starting hardware monitoring:

```powershell
dotnet build tools/AutoOcDiagnosticsTests -c Release -p:DiagnosticAppDir=<absolute-portable-directory>
dotnet tools/AutoOcDiagnosticsTests/bin/Release/net10.0-windows10.0.22621.0/AutoOcDiagnosticsTests.dll <fresh-test-log-directory>
dotnet tools/AutoOcDiagnosticsTests/bin/Release/net10.0-windows10.0.22621.0/AutoOcDiagnosticsTests.dll <another-fresh-test-log-directory> blocked
```

Tests allocate an uninitialized monitor and invoke its actual private signal/exception-handler methods. They never run its constructor, load hardware drivers, start a tuning controller, or issue hardware commands. They cover attribution/context, flag preservation, captured scores/offsets, nonfinite samples, state deduplication, flushing, and an unwritable log destination. They do not establish hardware stability or detector accuracy.
