using System.Reflection;
using AutoOC.Controllers;
using AutoOC.Monitors;
using Universal_x86_Tuning_Utility.Scripts.Misc;

int assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    assertions++;
}
AutoOcCpuSession Fresh(int request = -1)
{
    AutoOcCpuHardware.Current = new();
    AutoOcCpuPolicy.LibraryMatches = true;
    AutoOcCpuPolicy.Epochs = 0;
    AutoOcCpuPolicy.UncleanRecoveryPreserved = 0;
    AutoOcCpuPolicy.Status.Clear();
    AutoOcDiagnostics.Events.Clear();
    InstabilityMonitor.Configure = null;
    InstabilityMonitor.Instances.Clear();
    AdaptiveUndervoltController.Instances.Clear();
    AdaptiveUndervoltController.InitialRequest = request;
    return new();
}
void Age(AutoOcCpuSession session, string field) => typeof(AutoOcCpuSession)
    .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, DateTime.UtcNow.AddMinutes(-1));
bool Paused() => AutoOcDiagnostics.Events.Contains("paused");

foreach (var baseline in new[] { new FakeReadback(true, -3), new FakeReadback(false, 0) })
{
    var session = Fresh();
    var hardware = AutoOcCpuHardware.Current;
    hardware.NextRead = baseline;
    await session.TickAsync(true);
    Check(hardware.Events.SequenceEqual(new[] { "read" }), "nonzero/unreadable baseline produces no hardware write");
    Check(AdaptiveUndervoltController.Instances.Count == 0 && Paused(), "baseline failure prevents controller and pauses");
    await session.TickAsync(true);
    Check(hardware.Events.Count == 1, "baseline fault is latched while enabled");
    await session.StopAsync();
}

foreach (string gate in new[] { "library", "support", "whea", "watcher_error", "counter_unavailable", "nan_usage", "nan_smoothed" })
{
    var session = Fresh();
    var hardware = AutoOcCpuHardware.Current;
    if (gate == "library") AutoOcCpuPolicy.LibraryMatches = false;
    if (gate == "support") hardware.Support = new(false, "unsupported");
    InstabilityMonitor.Configure = monitor => {
        if (gate == "whea") monitor.WheaWatcherActive = false;
        if (gate == "watcher_error") monitor.EventWatcherErrorCount = 1;
        if (gate == "counter_unavailable") monitor.PerformanceCountersAvailable = false;
        if (gate == "nan_usage") monitor.Usage = float.NaN;
        if (gate == "nan_smoothed") monitor.Smoothed = float.NaN;
    };
    await session.TickAsync(true);
    Check(hardware.Events.Count == 0 && AdaptiveUndervoltController.Instances.Count == 0, gate + " prevents all writes and baseline ownership");
    if (gate is "counter_unavailable" or "nan_usage" or "nan_smoothed")
    {
        Check(!Paused(), gate + " first allows bounded monitoring startup");
        Age(session, "monitorStartedUtc");
        await session.TickAsync(true);
    }
    Check(Paused(), gate + " results in pause after required gate");
    await session.StopAsync();
}

{
    var session = Fresh();
    await session.TickAsync(true);
    var controller = AdaptiveUndervoltController.Instances.Single();
    Check(controller.Recorded.SequenceEqual(new[] { -1 }), "verified successful application records offset");
    Check(AutoOcCpuPolicy.Epochs == 1, "zero baseline begins fresh controller epoch");
    Check(AutoOcCpuHardware.Current.Events.SequenceEqual(new[] { "read", "apply:-1" }), "baseline read precedes offset application");
    await session.StopAsync();
    Check(controller.Recorded.SequenceEqual(new[] { -1, 0 }), "verified stop restoration records zero");
    Check(controller.Stopped && controller.Disposed && InstabilityMonitor.Instances.Single().Disposed, "stop disposes controller and monitor");
}

foreach (bool rollbackVerified in new[] { false, true })
{
    var session = Fresh();
    var hardware = AutoOcCpuHardware.Current;
    hardware.NextApply = new(false, false, "application failed", new(rollbackVerified));
    await session.TickAsync(true);
    var controller = AdaptiveUndervoltController.Instances.Single();
    Check(!controller.Recorded.Contains(-1) && controller.Recorded.All(value => value == 0), "unverified offset is never recorded as applied");
    Check(hardware.Events.SequenceEqual(new[] { "read", "apply:-1", "restore" }) && Paused(), "application failure restores and pauses");
    int events = hardware.Events.Count;
    await session.TickAsync(true);
    await session.TickAsync(true);
    Check(hardware.Events.Count == events, "application failure prevents subsequent negative writes while enabled");
    await session.StopAsync();
}

{
    var session = Fresh();
    var hardware = AutoOcCpuHardware.Current;
    hardware.ApplyHandler = _ => Task.FromException<FakeApply>(new InvalidOperationException("unexpected backend failure"));
    await session.TickAsync(true);
    Check(hardware.Events.SequenceEqual(new[] { "read", "apply:-1", "restore" }) && Paused(), "unexpected application exception restores and pauses");
    Check(!AdaptiveUndervoltController.Instances.Single().Recorded.Contains(-1), "throwing application never records requested offset");
    int count = hardware.Events.Count;
    await session.TickAsync(true);
    Check(hardware.Events.Count == count, "unexpected application exception is latched");
    await session.StopAsync();
}

foreach (string failure in new[] { "monitor", "whea", "persistence", "conflicting_readback", "failed_readback", "request_bounds" })
{
    var session = Fresh();
    var hardware = AutoOcCpuHardware.Current;
    await session.TickAsync(true);
    var controller = AdaptiveUndervoltController.Instances.Single();
    if (failure == "monitor") InstabilityMonitor.Instances.Single().PerformanceCountersAvailable = false;
    if (failure == "whea") InstabilityMonitor.Instances.Single().WheaWatcherActive = false;
    if (failure == "persistence") controller.PersistenceAvailable = false;
    if (failure == "request_bounds") controller.Request = -6;
    if (failure is "conflicting_readback" or "failed_readback")
    {
        Age(session, "lastReadUtc");
        hardware.NextRead = new(failure != "failed_readback", 0);
    }
    await session.TickAsync(true);
    Check(hardware.Events.Last() == "restore" && Paused(), failure + " restores zero and pauses");
    Check(controller.Recorded.Last() == 0, failure + " records verified restoration");
    int before = hardware.Events.Count;
    await session.TickAsync(true);
    Check(hardware.Events.Count == before, failure + " fault stays latched");
    await session.StopAsync();
}

{
    var session = Fresh();
    var hardware = AutoOcCpuHardware.Current;
    await session.TickAsync(true);
    AdaptiveUndervoltController.Instances.Single().PersistenceAvailable = false;
    hardware.NextRestore = new(false, false, "restore failed");
    await session.TickAsync(true);
    Check(hardware.Events.Count(e => e == "restore") == 1 && Paused(), "failed restoration leaves session faulted");
    Check(AutoOcCpuPolicy.UncleanRecoveryPreserved == 1, "failed restoration preserves unclean recovery state");
    await session.TickAsync(false);
    int afterFailedOff = hardware.Events.Count;
    await session.TickAsync(true);
    Check(hardware.Events.Count == afterFailedOff && AdaptiveUndervoltController.Instances.Count == 1, "off/on cannot clear fault before verified restoration");
    hardware.NextRestore = new(true, true);
    await session.TickAsync(false);
    Check(hardware.Events.Last() == "restore", "toggle off retries required restoration");
    await session.TickAsync(true);
    Check(AdaptiveUndervoltController.Instances.Count == 2 && hardware.Events.Last() == "apply:-1", "verified restoration and off/on permit a fresh session");
    Check(AutoOcCpuPolicy.Epochs == 2, "re-enabled session starts another fresh epoch");
    await session.StopAsync();
}

{
    var session = Fresh();
    var hardware = AutoOcCpuHardware.Current;
    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource<FakeApply>(TaskCreationOptions.RunContinuationsAsynchronously);
    hardware.ApplyHandler = _ => { entered.TrySetResult(true); return finish.Task; };
    Task tick = session.TickAsync(true);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task stopped = session.StopAsync();
    Task queuedTick = session.TickAsync(true);
    Check(!stopped.IsCompleted && hardware.Events.Last() == "apply:-1", "stop waits for active application");
    finish.SetResult(new(true, true));
    await Task.WhenAll(tick, stopped, queuedTick).WaitAsync(TimeSpan.FromSeconds(5));
    Check(hardware.Events.SequenceEqual(new[] { "read", "apply:-1", "restore" }), "stop restores after active application without queued negative writes");
    int count = hardware.Events.Count;
    await session.TickAsync(true);
    await session.TickAsync(false);
    Check(hardware.Events.Count == count, "no further hardware writes after stopping");
}

Console.WriteLine($"PASS {assertions} assertions; actual AutoOcCpuSession linked with fake controller, monitor, policy and hardware. No driver or CPU tuning access.");
