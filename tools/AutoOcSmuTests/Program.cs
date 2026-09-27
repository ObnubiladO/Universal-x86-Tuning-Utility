using System.Reflection;
using System.Text.Json;
using RyzenSmu;
using RealDiagnostics = Universal_x86_Tuning_Utility.Scripts.Misc.AutoOcDiagnostics;
using static RyzenSmu.RyzenSMU;

int passed = 0;
string logDirectory = Path.Combine(Path.GetTempPath(), "uxtu-smu-tests-" + Guid.NewGuid().ToString("N"));
RealDiagnostics.Initialize(logDirectory);

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Test(string name, Action test)
{
    SMUCommands.commands = [("set-coall", true, 0x4c), ("set-coall", false, 0x5d)];
    SMUCommands.UseHsmp = false;
    SMUCommands.RyzenAccess = new();
    TestDiagnostics.Records.Clear(); TestDiagnostics.Throw = false;
    test();
    passed++; Console.WriteLine("PASS " + name);
}
Exception Catch(Action action)
{
    try { action(); } catch (Exception ex) { return ex; }
    throw new Exception("Expected exception was not thrown.");
}
const uint input = 0xfffff;

Test("OK stops at first target and retains original input despite firmware output", () =>
{
    SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
    Check(SMUCommands.applySettings("set-coall", input), "OK must return true.");
    Check(SMUCommands.RyzenAccess.Calls.Count == 1, "No extra commands allowed.");
    var record = TestDiagnostics.Records.Single();
    Check(record.Accepted && record.Status == "OK" && record.StatusCode == 1, "Missing acceptance.");
    Check(record.Mailbox == "MP1" && record.Message == 0x4c, "Incorrect target.");
    Check(record.Arguments!.SequenceEqual(new uint[] { input, 0, 0, 0, 0, 0 }), "Input was overwritten.");
});
Test("UNKNOWN_CMD fallback restores input and shares execution id", () =>
{
    SMUCommands.RyzenAccess.Results.Enqueue(Status.UNKNOWN_CMD);
    SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
    Check(SMUCommands.applySettings("set-coall", input), "Fallback must succeed.");
    var calls = SMUCommands.RyzenAccess.Calls;
    Check(calls.Select(x => x.Mailbox).SequenceEqual(new[] { "MP1", "RSMU" }), "Changed routing/order.");
    Check(calls.All(x => x.Arguments[0] == input), "Fallback reused modified output.");
    Check(TestDiagnostics.Records.Select(x => x.ExecutionId).Distinct().Count() == 1, "Lost correlation.");
    Check(TestDiagnostics.Records[0].Status == "UNKNOWN_CMD" && !TestDiagnostics.Records[0].Accepted, "Unknown was accepted.");
});
Test("prerequisite refusal is cached, then only uncached fallback is attempted", () =>
{
    SMUCommands.RyzenAccess.Results.Enqueue(Status.CMD_REJECTED_PREREQ);
    SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
    Check(SMUCommands.applySettings("set-coall", input), "First fallback must succeed.");
    SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
    Check(SMUCommands.applySettings("set-coall", input), "Second fallback must succeed.");
    Check(SMUCommands.RyzenAccess.Calls.Count == 3 && SMUCommands.RyzenAccess.Calls[2].Mailbox == "RSMU", "Cache bypassed.");
    var skip = TestDiagnostics.Records.Single(x => x.Outcome == "cached_prerequisite_rejection");
    Check(skip.Mailbox == "MP1" && skip.Status == null && !skip.Accepted, "Skip masquerades as current reply.");
});
Test("fully cached refusal returns false without initialization or command", () =>
{
    SMUCommands.RyzenAccess.Results.Enqueue(Status.CMD_REJECTED_PREREQ);
    SMUCommands.RyzenAccess.Results.Enqueue(Status.CMD_REJECTED_PREREQ);
    Check(Catch(() => SMUCommands.applySettings("set-coall", input)).Message.EndsWith("CMD_REJECTED_PREREQ."), "Changed rejection exception.");
    int initializations = SMUCommands.RyzenAccess.Initializations;
    Check(!SMUCommands.applySettings("set-coall", input), "All cached should return false.");
    Check(SMUCommands.RyzenAccess.Initializations == initializations && SMUCommands.RyzenAccess.Calls.Count == 2, "Cached path accessed driver.");
    Check(TestDiagnostics.Records.Count(x => x.Outcome == "cached_prerequisite_rejection") == 2, "Missing skipped targets.");
});
Test("all unknown returns false and last nonunknown failure remains exception", () =>
{
    SMUCommands.RyzenAccess.Results.Enqueue(Status.UNKNOWN_CMD);
    SMUCommands.RyzenAccess.Results.Enqueue(Status.UNKNOWN_CMD);
    Check(!SMUCommands.applySettings("set-coall", input), "Unknown path changed.");
    SMUCommands.RyzenAccess.Results.Enqueue(Status.CMD_REJECTED_BUSY);
    SMUCommands.RyzenAccess.Results.Enqueue(Status.UNKNOWN_CMD);
    Check(Catch(() => SMUCommands.applySettings("set-coall", input)).Message.EndsWith("CMD_REJECTED_BUSY."), "Lost last nonunknown failure.");
});
Test("initialization false preserves exception and emits no attempt", () =>
{
    SMUCommands.RyzenAccess.InitializationResult = false;
    Check(Catch(() => SMUCommands.applySettings("set-coall", input)).Message == "AMD PawnIO failed to initialise.", "Changed initialization failure.");
    Check(TestDiagnostics.Records.Single().Outcome == "initialization_failed", "Missing initialization result.");
    Check(SMUCommands.RyzenAccess.Calls.Count == 0, "Failure sent commands.");
});
Test("initialization and send exceptions retain their original object and routing", () =>
{
    var sentinel = new IOException("sentinel");
    SMUCommands.RyzenAccess.InitializationException = sentinel;
    Check(ReferenceEquals(Catch(() => SMUCommands.applySettings("set-coall", input)), sentinel), "Initialization exception replaced.");
    Check(TestDiagnostics.Records.Single().Outcome == "initialization_exception", "Missing initialization exception.");
    SMUCommands.RyzenAccess.InitializationException = null;
    SMUCommands.RyzenAccess.Results.Enqueue(sentinel);
    Check(ReferenceEquals(Catch(() => SMUCommands.applySettings("set-coall", input)), sentinel), "Send exception replaced.");
    Check(SMUCommands.RyzenAccess.Calls.Count == 1, "Exception changed fallback.");
    Check(TestDiagnostics.Records.Last().Outcome == "send_exception" && TestDiagnostics.Records.Last().Mailbox == "MP1", "Missing send exception.");
});
Test("HSMP overrides target mailbox exactly as before", () =>
{
    SMUCommands.UseHsmp = true;
    SMUCommands.RyzenAccess.Results.Enqueue(Status.UNKNOWN_CMD);
    SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
    Check(SMUCommands.applySettings("set-coall", input), "HSMP fallback failed.");
    Check(SMUCommands.RyzenAccess.Calls.All(x => x.Mailbox == "HSMP"), "Wrong HSMP routing.");
    Check(TestDiagnostics.Records.All(x => x.Mailbox == "HSMP"), "Wrong HSMP logging.");
});
Test("unmapped CO emits no hardware access and other commands are not traced", () =>
{
    Check(!SMUCommands.applySettings("set-coper", input), "Unmapped should return false.");
    Check(TestDiagnostics.Records.Single().Outcome == "unmapped_command", "Missing unmapped record.");
    Check(SMUCommands.RyzenAccess.Initializations == 0, "Unmapped initialized driver.");
    SMUCommands.commands = [("ppt-limit", false, 1)];
    SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
    Check(SMUCommands.applySettings("ppt-limit", input), "NonCO behavior changed.");
    Check(TestDiagnostics.Records.Count == 1, "Traced nonCO command.");
});
Test("all three CO setters traced, diagnostic throws cannot alter results or exceptions", () =>
{
    foreach (string name in new[] { "set-coall", "set-coper", "set-cogfx" })
    {
        SMUCommands.commands = [(name, false, 1)];
        SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
        Check(SMUCommands.applySettings(name, input), name + " failed.");
        Check(TestDiagnostics.Records.Last().CommandName == name, name + " was not traced.");
    }
    TestDiagnostics.Throw = true;
    SMUCommands.RyzenAccess.Results.Enqueue(Status.OK);
    Check(SMUCommands.applySettings("set-cogfx", input), "Logging throw changed OK.");
    SMUCommands.RyzenAccess.Results.Enqueue(Status.FAILED);
    Check(Catch(() => SMUCommands.applySettings("set-cogfx", input)).Message.EndsWith("status FAILED."), "Logging throw replaced failure.");
});

// Exercise the real serializer and the diagnostic-only sampled threshold without
// constructing the real monitor, loading a driver, or invoking tuning hardware.
var snapshotMethod = typeof(RealDiagnostics).GetMethod("MonitorSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var (usage, threshold) in new[] { (10f, 12), (60f, 12), (84.9f, 12), (85f, 11), (100f, 11) })
{
    var snapshot = (Dictionary<string, object?>)snapshotMethod.Invoke(null, [new FakeMonitor { lastCpuUsagePercent = usage }])!;
    Check((int)snapshot["effectiveCpuAnomalyThreshold"]! == threshold, "Incorrect effective threshold.");
}
RealDiagnostics.Shutdown();
var records = File.ReadAllLines(Path.Combine(logDirectory, "autooc-diagnostics.jsonl"))
    .Select(line => JsonDocument.Parse(line)).ToArray();
var session = records.Single(x => x.RootElement.GetProperty("kind").GetString() == "session_start").RootElement;
Check(!session.GetProperty("cpuPolicyRequested").GetBoolean() && !session.GetProperty("cpuPolicyLibraryMatches").GetBoolean()
    && !session.GetProperty("tuningRulesChanged").GetBoolean(), "Observational test session was labeled as CPU policy mode.");
var smu = records.Where(x => x.RootElement.GetProperty("kind").GetString() == "smu_command_result").Select(x => x.RootElement).ToArray();
Check(smu.Length > 0 && smu.All(x => !x.GetProperty("hardwareReadbackVerified").GetBoolean()), "Serializer overclaims readback.");
Check(smu.Any(x => x.GetProperty("acceptedByFirmware").GetBoolean() && x.GetProperty("statusCode").GetUInt32() == 1), "Serializer lost acceptance.");
Check(smu.All(x => x.GetProperty("originalArguments")[0].GetUInt32() == input), "Serialized input was mutated.");
Console.WriteLine($"PASS real logger schema and effective threshold; {passed + 1} checks passed. No drivers or hardware commands used.");
Console.WriteLine("Test log: " + logDirectory);

public sealed class FakeMonitor
{
    public float lastCpuUsagePercent;
    public int requiredCpuAnomalies = 12;
}
public sealed record Trace(string ExecutionId, string CommandName, uint[]? Arguments, string Outcome,
    string? Mailbox, uint? Message, string? Status, uint? StatusCode, bool Accepted, Exception? Exception);
public static class TestDiagnostics
{
    public static readonly List<Trace> Records = new();
    public static bool Throw;
    public static void RecordSmuCommand(string executionId, string commandName, uint[]? originalArguments,
        string outcome, string? mailbox, uint? message, string? status, uint? statusCode,
        bool acceptedByFirmware, Exception? exception = null)
    {
        if (Throw) throw new IOException("Diagnostic sink failure");
        Records.Add(new(executionId, commandName, originalArguments, outcome, mailbox, message, status, statusCode, acceptedByFirmware, exception));
        RealDiagnostics.RecordSmuCommand(executionId, commandName, originalArguments, outcome, mailbox, message, status, statusCode, acceptedByFirmware, exception);
    }
}

namespace RyzenSmu
{
    public class Smu
    {
        public readonly Queue<object> Results = new();
        public readonly List<(string Mailbox, uint Message, uint[] Arguments)> Calls = new();
        public int Initializations;
        public bool InitializationResult = true;
        public Exception? InitializationException;
        public bool EnsureInitialised()
        {
            Initializations++;
            if (InitializationException != null) throw InitializationException;
            return InitializationResult;
        }
        public Status SendMp1(uint message, ref uint[] args) => Send("MP1", message, ref args);
        public Status SendRsmu(uint message, ref uint[] args) => Send("RSMU", message, ref args);
        public Status SendHsmp(uint message, ref uint[] args) => Send("HSMP", message, ref args);
        private Status Send(string mailbox, uint message, ref uint[] args)
        {
            Calls.Add((mailbox, message, (uint[])args.Clone()));
            args[0] = 123; // Firmware reply buffers can overwrite command inputs.
            object result = Results.Dequeue();
            if (result is Exception exception) throw exception;
            return (Status)result;
        }
    }
    public class RyzenSMU
    {
        public enum Status : byte { OK = 1, FAILED = 0xff, UNKNOWN_CMD = 0xfe, CMD_REJECTED_PREREQ = 0xfd, CMD_REJECTED_BUSY = 0xfc }
    }
}
namespace Universal_x86_Tuning_Utility.Scripts.Misc
{
    public static class DiagnosticLogger { public static void LogDebug(string message) { } }
    // This isolated harness has neither a policy DLL nor a portable marker file.
    // The production policy type remains unchanged and is not source-linked here.
    public static class AutoOcCpuPolicy
    {
        public static bool Requested => false;
        public static bool LibraryMatches => false;
    }
}
