# AutoOC transport tests

These tests compile the actual `RyzenSMU` class from `RyzenSmu.cs`, with a fake PawnIO implementation and explicitly injected, unnamed Windows mutexes. They never load a driver, query CPU registers, or open the global hardware mutexes.

Run on Windows with the .NET 10 SDK:

```powershell
./tools/AutoOcTransportTests/run.ps1 -DotnetPath /path/to/dotnet.exe
```

Coverage includes recovery after contention, exhaustion of three 100 ms mutex waits, the original single 10 ms wait, mutex exceptions/unavailability/abandonment, actual firmware error responses, native I/O failures at every protocol phase, partial output, polling exhaustion, command-write uncertainty, per-invocation diagnostic isolation, and release of ownership after every failure. Retries apply only before mailbox I/O; the tests assert that commands are never repeated.
