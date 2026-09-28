using Universal_x86_Tuning_Utility.Scripts.Misc;

int assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    assertions++;
}
AutoOcCpuSupport Supported() => new(true, "fake supported", "test9950X", "AuthenticAMD", 26, 68, 16, 2, 32);
AutoOcCpuHardware Service(FakeBackend backend, bool supported = true) => new(backend,
    supported ? Supported() : Supported() with { Supported = false, Reason = "unsupported test" });

var unsupportedBackend = new FakeBackend();
var unsupported = Service(unsupportedBackend, false);
Check(!(await unsupported.ReadAsync()).Success, "unsupported read is rejected");
Check(!(await unsupported.ApplyAsync(-1)).Success, "unsupported apply is rejected");
Check(!(await unsupported.RestoreZeroAsync()).Success, "unsupported restore is rejected");
Check(unsupportedBackend.Events.Count == 0, "unsupported operations never access backend");

var boundsBackend = new FakeBackend();
var bounds = Service(boundsBackend);
Check(!(await bounds.ApplyAsync(-51)).Success && !(await bounds.ApplyAsync(1)).Success, "allowed offset range");
Check(boundsBackend.Events.Count == 0, "out-of-range operations never access backend");

var successBackend = new FakeBackend();
var success = await Service(successBackend).ApplyAsync(-5);
Check(success.Success && success.CommandAccepted && success.ReadbackVerified && success.Rollback is null, "accepted and matching offset succeeds");
Check(success.Readback!.Cores.Select(core => core.Offset).All(value => value == -5), "actual signed readback");
Check(success.Readback.Cores.Select(core => core.Selector).Distinct().Count() == 16 &&
    success.Readback.Cores[8].Selector == 0x10000000 && success.Readback.Cores[15].Selector == 0x10700000,
    "all sixteen distinct physical cores are queried");
Check(successBackend.Events.Count(e => e.StartsWith("set:")) == 1, "success does not restore away requested offset");

foreach (int requested in new[] { -6, -50 })
{
    var backend = new FakeBackend();
    var applied = await Service(backend).ApplyAsync(requested);
    Check(applied.Success && applied.Readback!.Matches(requested), requested + " within original search range is accepted with signed readback");
    Check(backend.Events.First() == "set:" + requested && !backend.Events.Contains("set:0"), requested + " successful application remains applied");
}
Check(AutoOcCpuLimits.MinimumOffset == -50, "hardware tests use the production original search bound");

foreach (string cause in new[] { "mutex_timeout", "firmware_response", "driver_io", "inconsistent_ok" })
{
    var backend = new FakeBackend { Scenario = "transport_" + cause };
    var applied = await Service(backend).ApplyAsync(-50);
    Check(!applied.Success && applied.Rollback?.Success == true, cause + " retains verified-zero rollback");
    Check(applied.Readback!.Cores.Count == 4 && applied.Readback.Cores[3].Offset is null,
        cause + " never interprets failed or inconsistent transport arguments as an offset");
    Check(backend.Events.Where(e => e.StartsWith("set:")).SequenceEqual(new[] { "set:-50", "set:0" }),
        cause + " does not retry setters");
    Check(backend.Events.Count(e => e == "read:requested:3145728") == 1,
        cause + " is not blindly retried by hardware service");
    var captured = applied.Readback.Cores[3].Transport!;
    Check(captured.FailureKind == (cause == "inconsistent_ok" ? "driver_io" : cause), cause + " diagnostic preserved");
    if (cause == "mutex_timeout")
    {
        Check(applied.Error!.Contains("busy after 3 lock attempts") && applied.Error.Contains("No offset query was sent"),
            "mutex exhaustion does not masquerade as a firmware rejection");
        Check(captured.MutexAttempts == 3 && !captured.CommandMayHaveBeenSent, "exhaustion includes attempts and command state");
    }
    if (cause == "firmware_response")
        Check(applied.Error!.Contains("firmware returned FAILED (0xFF)"), "actual firmware rejection is labeled separately");
}

var recoveredBackend = new FakeBackend { Scenario = "transport_recovered" };
var recovered = await Service(recoveredBackend).ApplyAsync(-50);
Check(recovered.Success && recovered.Readback!.Matches(-50), "recovered lock contention remains applied after full verification");
Check(recovered.Readback!.Cores[3].Transport is { FailureKind: "none", MutexAttempts: 2 },
    "successful lock retry remains visible in core diagnostics");
Check(recoveredBackend.Events.Count(e => e.StartsWith("set:")) == 1 && recoveredBackend.Events.Count(e => e.StartsWith("read:")) == 16,
    "recovered lock contention does not duplicate setters or per-core backend calls");
Check(System.Text.Json.JsonSerializer.Serialize(recovered.Readback).Contains("\"MutexAttempts\":2"),
    "readback logging retains recovered retry details");

foreach (string scenario in new[] { "set_false", "set_throws", "read_failed", "read_throws", "read_mismatch", "malformed", "implausible" })
{
    var backend = new FakeBackend { Scenario = scenario };
    var result = await Service(backend).ApplyAsync(-1);
    Check(!result.Success && result.Rollback?.Success == true, scenario + " returns failure and verified rollback");
    Check(backend.Events.Where(e => e.StartsWith("set:")).SequenceEqual(new[] { "set:-1", "set:0" }), scenario + " restores after attempted setter");
    Check(backend.Events.IndexOf("reset") < backend.Events.IndexOf("set:0"), scenario + " clears rejection cache before restoring");
    Check(backend.Offset == 0, scenario + " leaves fake hardware at zero");
    if (scenario == "read_failed") Check(result.Readback?.Cores.Single().StatusCode == 0xFD, "raw rejected status preserved");
    if (scenario == "read_mismatch") Check(result.CommandAccepted && !result.ReadbackVerified, "acknowledgement is separate from readback");
}

foreach (string scenario in new[] { "restore_false", "restore_throws", "restore_read_failed", "reset_throws" })
{
    var backend = new FakeBackend { Scenario = scenario };
    var result = await Service(backend).ApplyAsync(-1);
    Check(!result.Success && result.Rollback is { Success: false }, scenario + " does not claim successful restoration");
    Check(backend.Events.Contains("set:0"), scenario + " restoration attempted");
    Check(backend.Events.Any(e => e.StartsWith("read:restored")), scenario + " restoration readback attempted");
    if (scenario == "restore_false") Check(result.Rollback!.ReadbackVerified, "failed restore acknowledgement can still have verified zero readback");
}

var restoreBackend = new FakeBackend { Offset = -1 };
var restore = await Service(restoreBackend).RestoreZeroAsync();
Check(restore.Success && restore.Readback!.Matches(0), "explicit restore verifies all zero");
Check(restoreBackend.Events[0] == "reset" && restoreBackend.Events[1] == "set:0", "explicit restore bypasses prior rejection cache");

// Serialized behavior across two service instances: the second operation is queued
// while the first setter is held. No hardware sleeps or driver calls are involved.
using var entered = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
var firstBackend = new FakeBackend { SetterHook = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); } };
var secondBackend = new FakeBackend();
Task<AutoOcCpuApplyResult> first = Service(firstBackend).ApplyAsync(-1);
Check(entered.Wait(TimeSpan.FromSeconds(5)), "first setter entered");
Task<AutoOcCpuReadback> second = Service(secondBackend).ReadAsync();
Check(!second.IsCompleted && secondBackend.Events.Count == 0, "second service cannot enter during an operation");
release.Set();
Check((await first).Success && (await second).Success, "queued operations complete after gate release");

Console.WriteLine($"PASS {assertions} assertions; actual service linked with fake backend only; no hardware access.");

internal sealed class FakeBackend : IAutoOcCpuHardwareBackend
{
    public readonly List<string> Events = new();
    public int Offset;
    public string Scenario = "";
    public Action? SetterHook;
    private bool restored;
    public bool SetAllCoreOffset(int offset)
    {
        Events.Add("set:" + offset);
        SetterHook?.Invoke();
        Offset = offset; // Even a rejected/throwing call may have partially applied.
        restored = offset == 0;
        if (offset < 0 && Scenario == "set_throws") throw new Exception("setter failed after partial application");
        if (offset == 0 && Scenario == "restore_throws") throw new Exception("restore acknowledgement exception");
        return !(offset < 0 && Scenario == "set_false") && !(offset == 0 && Scenario == "restore_false");
    }
    public void ResetRejectedCommands()
    {
        Events.Add("reset");
        if (Scenario == "reset_throws") throw new Exception("cache reset failed");
    }
    public AutoOcCpuSmuReply ReadCoreOffset(uint selector)
    {
        Events.Add("read:" + (restored ? "restored:" : "requested:") + selector);
        if (!restored && Offset < 0)
        {
            if (Scenario.StartsWith("transport_") && selector == 0x00300000)
            {
                string cause = Scenario.Substring("transport_".Length);
                bool recovered = cause == "recovered";
                bool lockTimeout = cause == "mutex_timeout";
                var diagnostic = new RyzenSmu.RyzenSMU.CommandDiagnostics(
                    recovered ? "none" : cause == "inconsistent_ok" ? "driver_io" : cause,
                    lockTimeout ? "mutex_acquire" : "response", !lockTimeout,
                    lockTimeout ? 3 : recovered ? 2 : 1, lockTimeout ? 300 : recovered ? 130 : 0,
                    301, lockTimeout ? null : cause == "firmware_response" ? 255u : 1u,
                    cause is "driver_io" or "inconsistent_ok" ? unchecked((int)0x80070006) : null,
                    null, null);
                return new(recovered || cause == "inconsistent_ok" ? 1u : 255u,
                    recovered || cause == "inconsistent_ok" ? "OK" : "FAILED",
                    new uint[] { unchecked((uint)Offset), 0, 0, 0, 0, 0 }, diagnostic);
            }
            if (Scenario == "read_throws") throw new Exception("getter failed");
            if (Scenario == "read_failed") return new(0xFD, "CMD_REJECTED_PREREQ", new uint[6]);
            if (Scenario == "malformed") return new(1, "OK", new uint[2]);
            if (Scenario == "implausible") return new(1, "OK", new uint[] { 200, 0, 0, 0, 0, 0 });
            if (Scenario is "read_mismatch" or "restore_false" or "restore_throws" or "restore_read_failed" or "reset_throws")
                return new(1, "OK", new uint[6]);
        }
        if (restored && Scenario == "restore_read_failed") return new(0xFF, "FAILED", new uint[6]);
        return new(1, "OK", new uint[] { unchecked((uint)Offset), 0, 0, 0, 0, 0 });
    }
}
