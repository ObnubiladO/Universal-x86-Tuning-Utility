using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AutoOC.Monitors;
using Universal_x86_Tuning_Utility.Scripts.Misc;

// Do not construct the monitor or controller: constructors start real monitoring.
// These tests execute the actual instrumented signal/exception-handler methods on
// an uninitialized object, without loading drivers or issuing hardware commands.
string directory = Path.GetFullPath(args[0]);
bool blocked = args.Length > 1 && args[1] == "blocked";
Directory.CreateDirectory(directory);
string destination = directory;
if (blocked) { destination = Path.Combine(directory, "not-a-directory"); File.WriteAllText(destination, "test"); }
AutoOcDiagnostics.Initialize(destination);
var monitor = (InstabilityMonitor)RuntimeHelpers.GetUninitializedObject(typeof(InstabilityMonitor));
Set("cpuScore", 13); Set("requiredCpuAnomalies", 12); Set("lastCpuUsagePercent", 10f); Set("smoothedCpuUsagePercent", 8f);
Set("interruptSpikeBits", (ushort)3); Set("contextSpikeBits", (ushort)7);
var controller = new FakeController();
AutoOcDiagnostics.AttachController(controller, false);
Invoke("OnFirstChanceException", null, new FirstChanceExceptionEventArgs(new AccessViolationException("synthetic, never thrown")));
Check((int)Get("cpuInstabilityFlag")! == 1, "original CPU signal still sets flag");
Check((int)Get("cpuScore")! == 13, "logging does not mutate score");
Check(controller.lastAppliedOffset == -1, "logging does not mutate offset");
Set("cpuInstabilityFlag", 0);
AutoOcDiagnostics.SetSignalContext(monitor, "MonitorPerformanceCounters:1@IL_test", null);
Invoke("SignalCpuInstability");
Check((int)Get("cpuInstabilityFlag")! == 1, "heuristic signal preserves flag behavior");
AutoOcDiagnostics.SetSignalContext(monitor, "OnWheaEvent:1@IL_test", new FakeEventArgs());
Invoke("SignalCpuInstability");
Invoke("SignalGpuInstability");
Check((int)Get("gpuInstabilityFlag")! == 1, "original GPU signal still sets flag");
AutoOcDiagnostics.ObserveController(controller, false, monitor);
AutoOcDiagnostics.ObserveController(controller, false, monitor);
controller.currentOffset = -2;
AutoOcDiagnostics.ObserveController(controller, false, monitor);
Set("lastCpuUsagePercent", float.NaN);
Invoke("OnFirstChanceException", null, new FirstChanceExceptionEventArgs(new AccessViolationException()));
AutoOcDiagnostics.RecordCommand(false, -1, "dispatch_requested");
AutoOcDiagnostics.Shutdown();
if (blocked)
{
    Check((int)Get("cpuInstabilityFlag")! == 1, "unwritable log destination does not suppress signals");
    Console.WriteLine("PASS: logging I/O failure leaves original signaling intact.");
    return;
}
var records = File.ReadAllLines(Path.Combine(directory, "autooc-diagnostics.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
var signals = records.Where(r => r.GetProperty("kind").GetString() == "instability_signal").ToArray();
Check(signals.Length == 5, "all five real signal invocations logged");
var access = signals.First(r => r.GetProperty("reason").GetString() == "first_chance_access_violation");
Check(access.GetProperty("source").GetString()!.StartsWith("OnFirstChanceException:1@IL_"), "actual injected caller identity captured");
Check(access.GetProperty("event").GetProperty("exceptionType").GetString() == typeof(AccessViolationException).FullName, "exception context captured");
Check(access.GetProperty("controller").GetProperty("lastAppliedOffset").GetInt32() == -1, "offset at signal captured");
var heuristic = signals.Single(r => r.GetProperty("reason").GetString() == "performance_counter_score");
Check(heuristic.GetProperty("monitor").GetProperty("cpuScore").GetInt32() == 13, "pre-reset score captured");
Check(heuristic.GetProperty("monitor").GetProperty("heuristicWindows").GetProperty("contextSpikeBitsHitsOf5").GetInt32() == 3, "heuristic evidence captured");
var whea = signals.Single(r => r.GetProperty("reason").GetString() == "hard_cpu_or_memory_whea");
Check(whea.GetProperty("event").GetProperty("Id").GetInt32() == 18, "event identity captured");
Check(signals.Single(r => r.GetProperty("channel").GetString() == "igpu").GetProperty("event").ValueKind == JsonValueKind.Null, "context does not leak into later signals");
Check(signals.Last().GetProperty("monitor").GetProperty("lastCpuUsagePercent").GetString() == "NaN", "nonfinite counters do not lose log records");
Check(records.Count(r => r.GetProperty("kind").GetString() == "controller_state") == 2, "unchanged state deduplicated; offset change retained");
Check(records.Last().GetProperty("kind").GetString() == "session_end", "queued records flushed at shutdown");
Console.WriteLine("PASS: actual instrumented methods preserve signaling and produce correctly attributed diagnostics.");

void Invoke(string method, params object?[] arguments) => typeof(InstabilityMonitor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(monitor, arguments);
object? Get(string field) => typeof(InstabilityMonitor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor);
void Set(string field, object value) => typeof(InstabilityMonitor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, value);
static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }

public sealed class FakeController { public int currentOffset = -1; public int lastAppliedOffset = -1; public bool idleForcedZeroActive = false; }
public sealed class FakeEventArgs { public FakeEventRecord EventRecord { get; } = new(); }
public sealed class FakeEventRecord { public string ProviderName => "SyntheticWHEA"; public int Id => 18; public byte Level => 2; public long RecordId => 123; public DateTime TimeCreated => DateTime.UtcNow; public string LogName => "Synthetic"; }
