# AutoOC SMU result diagnostics tests

Run `./run.ps1` with a .NET 8 or newer SDK, or pass `-DotnetPath`.

The runner extracts the current `SMUCommands` class directly from `RyzenSmu.cs`.
The test project compiles it against a scripted SMU (no real driver), a diagnostic
sink that can throw, and the production `AutoOcDiagnostics` serializer. It checks
command routing and short-circuiting, argument preservation across fallback,
prerequisite caching, false results, original exception objects, HSMP routing,
all three CO setters, and failure isolation. It also checks sampled effective
CPU thresholds and that every result retains `hardwareReadbackVerified=false`.

These tests do not apply CPU settings or prove hardware acceptance. Live
`smu_command_result` records distinguish successful firmware status from a
dispatch return; `FAILED` may include driver/transport failure. Cached skips
have no fresh status. An `OK` setter response still is not effective-CO readback.
