namespace AutoOC.Monitors
{
    internal sealed class InstabilityMonitor : IDisposable
    {
        public static Action<InstabilityMonitor>? Configure;
        public static readonly List<InstabilityMonitor> Instances = new();
        public bool WheaWatcherActive { get; set; } = true;
        public int EventWatcherErrorCount { get; set; }
        public bool PerformanceCountersAvailable { get; set; } = true;
        public float Usage = 20, Smoothed = 20;
        public bool Disposed;
        public InstabilityMonitor() { Configure?.Invoke(this); Instances.Add(this); }
        public float GetCpuUsagePercent() => Usage;
        public float GetSmoothedCpuUsagePercent() => Smoothed;
        public void Dispose() => Disposed = true;
    }
}
namespace AutoOC.Controllers
{
    using AutoOC.Monitors;
    internal sealed class AdaptiveUndervoltController : IDisposable
    {
        public static readonly List<AdaptiveUndervoltController> Instances = new();
        public static int InitialRequest;
        public int Request;
        public bool PersistenceAvailable = true;
        public readonly List<int> Recorded = new();
        public int Updates;
        public bool Stopped, Disposed;
        public AdaptiveUndervoltController(InstabilityMonitor monitor, int minimumOffset, int stepSize,
            int stableThreshold, int cooldownThreshold, bool isIgpu, int minimumEvaluationIntervalMilliseconds,
            int idleEntrySamples, int idleExitSamples, float idleExitMarginPercent)
        {
            Request = InitialRequest;
            Instances.Add(this);
        }
        public bool IsPersistenceAvailable() => PersistenceAvailable;
        public int UpdateOffset() { Updates++; return Request; }
        public void RecordAppliedOffset(int offset) => Recorded.Add(offset);
        public void Stop() => Stopped = true;
        public void Dispose() => Disposed = true;
    }
}
namespace Universal_x86_Tuning_Utility.Scripts.Misc
{
    using AutoOC.Controllers;
    using AutoOC.Monitors;
    internal sealed record FakeSupport(bool Supported, string Reason);
    internal sealed record FakeReadback(bool Success, int Offset)
    {
        public bool Matches(int offset) => Success && Offset == offset;
    }
    internal sealed record FakeRollback(bool ReadbackVerified);
    internal sealed record FakeApply(bool Success, bool ReadbackVerified, string? Error = null, FakeRollback? Rollback = null);
    internal sealed class AutoOcCpuHardware
    {
        public static AutoOcCpuHardware Current = new();
        public FakeSupport Support = new(true, "fake supported");
        public FakeReadback NextRead = new(true, 0);
        public FakeApply NextApply = new(true, true);
        public FakeApply NextRestore = new(true, true);
        public Func<int, Task<FakeApply>>? ApplyHandler;
        public readonly List<string> Events = new();
        public static AutoOcCpuHardware CreateForCurrentCpu() => Current;
        public Task<FakeReadback> ReadAsync() { Events.Add("read"); return Task.FromResult(NextRead); }
        public Task<FakeApply> ApplyAsync(int offset)
        {
            Events.Add("apply:" + offset);
            return ApplyHandler?.Invoke(offset) ?? Task.FromResult(NextApply);
        }
        public Task<FakeApply> RestoreZeroAsync()
        {
            Events.Add("restore");
            return Task.FromResult(NextRestore);
        }
    }
    internal static class AutoOcCpuPolicy
    {
        public static bool LibraryMatches = true;
        public const int MinimumOffset = -5, EvaluationSamples = 60;
        public static readonly List<string> Status = new();
        public static int Epochs;
        public static int UncleanRecoveryPreserved;
        public static void SetStatus(string status) => Status.Add(status);
        public static void BeginVerifiedEpoch(AdaptiveUndervoltController controller) => Epochs++;
        public static void PreserveUncleanRecovery(AdaptiveUndervoltController controller) => UncleanRecoveryPreserved++;
    }
    internal static class AutoOcDiagnostics
    {
        public static readonly List<string> Events = new();
        public static void RecordCpuPolicy(string name, object details) => Events.Add(name);
        public static void AttachController(AdaptiveUndervoltController controller, bool isIgpu) { }
        public static void ObserveController(AdaptiveUndervoltController controller, bool isIgpu, InstabilityMonitor monitor) { }
    }
}
