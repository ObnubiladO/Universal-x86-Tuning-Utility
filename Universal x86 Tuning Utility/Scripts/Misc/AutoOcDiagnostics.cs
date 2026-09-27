using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Universal_x86_Tuning_Utility.Scripts.Misc;

// Used only by the separate, locally instrumented diagnostic build of AutoOC.
// Never consumes instability flags, calls a tuning API, or modifies a controller.
public static class AutoOcDiagnostics
{
    private static readonly Channel<Dictionary<string, object?>> Queue = Channel.CreateBounded<Dictionary<string, object?>>(
        new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly ConcurrentDictionary<string, (string Key, DateTime At)> LastStates = new();
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static object? cpuController, igpuController;
    private static Task? writer;
    private static int initialized;
    private static long sequence, dropped, writeFailures;
    [ThreadStatic] private static SignalContext? pendingContext;

    private sealed record SignalContext(object Monitor, string Source, Dictionary<string, object?> Details);

    public static void Initialize(string? directory = null)
    {
        try
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) return;
            string path = Path.Combine(directory ?? Path.Combine(AppContext.BaseDirectory, "logs"), "autooc-diagnostics.jsonl");
            writer = Task.Run(() => WriteLoop(path));
            Enqueue("session_start", new()
            {
                ["appVersion"] = typeof(AutoOcDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                ["hardwareReadbackVerified"] = false,
                ["description"] = "Observational trace; thresholds and learned state are unchanged."
            });
        }
        catch { /* Diagnostics must not stop or change tuning. */ }
    }

    public static void AttachController(object controller, bool isIgpu)
    {
        if (isIgpu) Volatile.Write(ref igpuController, controller);
        else Volatile.Write(ref cpuController, controller);
    }

    // Injected immediately before a direct call to the existing signal method.
    public static void SetSignalContext(object monitor, string source, object? args)
    {
        try { pendingContext = new(monitor, source, EventDetails(args)); }
        catch { pendingContext = null; }
    }

    // Injected at entry of the existing signal method, before its original IL.
    public static void OnSignal(object monitor, string channel)
    {
        var context = pendingContext;
        pendingContext = null;
        try
        {
            bool matched = context != null && ReferenceEquals(context.Monitor, monitor);
            string source = matched ? context!.Source : FindCaller();
            Enqueue("instability_signal", new()
            {
                ["channel"] = channel,
                ["source"] = source,
                ["reason"] = Reason(source),
                ["event"] = matched ? context!.Details : null,
                ["monitor"] = MonitorSnapshot(monitor),
                ["controller"] = ControllerSnapshot(channel == "cpu" ? Volatile.Read(ref cpuController) : Volatile.Read(ref igpuController)),
                ["snapshotNote"] = "Observed fields are not a cross-thread atomic snapshot; no stability verdict or hardware readback is inferred."
            });
        }
        catch { }
    }

    public static void ObserveController(object controller, bool isIgpu, object monitor)
    {
        try
        {
            string channel = isIgpu ? "igpu" : "cpu";
            var state = ControllerSnapshot(controller);
            string key = JsonSerializer.Serialize(new object?[] {
                Read(controller, "currentOffset"), Read(controller, "lastAppliedOffset"),
                Read(controller, "idleForcedZeroActive"), Read(controller, "holdUntilUtc"),
                Read(controller, "lastUnstableOffset"), Read(controller, "learnedSafeFloorHeavy"),
                Read(controller, "learnedSafeFloorMedium"), Read(controller, "learnedSafeFloorLight") });
            DateTime now = DateTime.UtcNow;
            if (LastStates.TryGetValue(channel, out var previous) && previous.Key == key && (now - previous.At).TotalSeconds < 30) return;
            LastStates[channel] = (key, now);
            Enqueue("controller_state", new() { ["channel"] = channel, ["controller"] = state, ["monitor"] = MonitorSnapshot(monitor) });
        }
        catch { }
    }

    public static void RecordCommand(bool isIgpu, int offset, string stage)
    {
        try { Enqueue("offset_command", new() { ["channel"] = isIgpu ? "igpu" : "cpu", ["offset"] = offset, ["stage"] = stage, ["hardwareReadbackVerified"] = false }); }
        catch { }
    }

    // Firmware acceptance of a setter is distinct from reading the effective CO
    // back from the CPU. Cached skips and initialization failures are not replies.
    public static void RecordSmuCommand(string executionId, string commandName, uint[]? originalArguments,
        string outcome, string? mailbox, uint? message, string? status, uint? statusCode,
        bool acceptedByFirmware, Exception? exception = null)
    {
        try
        {
            Enqueue("smu_command_result", new()
            {
                ["executionId"] = executionId,
                ["commandName"] = commandName,
                ["originalArguments"] = originalArguments == null ? null : (uint[])originalArguments.Clone(),
                ["outcome"] = outcome,
                ["mailbox"] = mailbox,
                ["message"] = message,
                ["messageHex"] = message.HasValue ? $"0x{message.Value:X}" : null,
                ["status"] = status,
                ["statusCode"] = statusCode,
                ["acceptedByFirmware"] = acceptedByFirmware,
                ["hardwareReadbackVerified"] = false,
                ["exceptionType"] = exception?.GetType().FullName,
                ["exceptionHResult"] = exception?.HResult
            });
        }
        catch { }
    }

    public static void Shutdown()
    {
        try
        {
            Enqueue("session_end", new());
            Queue.Writer.TryComplete();
            writer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    private static string Reason(string source)
    {
        if (source.StartsWith("MonitorPerformanceCounters", StringComparison.Ordinal)) return "performance_counter_score";
        if (source.StartsWith("WatchdogLoop", StringComparison.Ordinal)) return "scheduler_watchdog_delay";
        if (source.StartsWith("OnWheaEvent:1@", StringComparison.Ordinal)) return "hard_cpu_or_memory_whea";
        if (source.StartsWith("OnWheaEvent:2@", StringComparison.Ordinal)) return "cpu_or_memory_whea_rate";
        if (source.StartsWith("OnFirstChanceException", StringComparison.Ordinal)) return "first_chance_access_violation";
        if (source.StartsWith("OnUnhandledException", StringComparison.Ordinal)) return "unhandled_access_violation";
        if (source.StartsWith("OnBugcheckEvent", StringComparison.Ordinal)) return "bugcheck_event";
        if (source.StartsWith("OnKernelPowerEvent", StringComparison.Ordinal)) return "kernel_power_41";
        if (source.StartsWith("OnHypervisorEvent", StringComparison.Ordinal)) return "hypervisor_error_event";
        if (source.StartsWith("OnLiveKernelEvent", StringComparison.Ordinal)) return "live_kernel_event";
        if (source.StartsWith("OnDisplayEvent", StringComparison.Ordinal)) return "display_driver_recovery";
        if (source.StartsWith("OnVendorGpuEvent", StringComparison.Ordinal)) return "vendor_gpu_event_score";
        return "external_or_unclassified_signal";
    }

    private static string FindCaller()
    {
        foreach (var frame in new StackTrace().GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType?.FullName == "AutoOC.Monitors.InstabilityMonitor" && method.Name != "SignalCpuInstability" && method.Name != "SignalGpuInstability")
                return method.Name + ":stack";
        }
        return "unknown";
    }

    private static Dictionary<string, object?> EventDetails(object? args)
    {
        var result = new Dictionary<string, object?>();
        if (args == null) return result;
        object? record = Property(args, "EventRecord");
        if (record != null)
            foreach (string name in new[] { "ProviderName", "Id", "Level", "RecordId", "TimeCreated", "LogName" }) result[name] = Property(record, name);
        object? exception = Property(args, "Exception") ?? Property(args, "ExceptionObject");
        if (exception != null)
        {
            result["exceptionType"] = exception.GetType().FullName;
            result["hresult"] = Property(exception, "HResult");
        }
        return result;
    }

    private static Dictionary<string, object?> ControllerSnapshot(object? controller)
    {
        var result = new Dictionary<string, object?>();
        foreach (string field in new[] { "currentOffset", "lastAppliedOffset", "stableCount", "cooldownCount", "idleForcedZeroActive", "holdUntilUtc", "lastUnstableOffset", "lastUnstableBand", "learnedSafeFloorLight", "learnedSafeFloorMedium", "learnedSafeFloorHeavy", "crashCount" })
            result[field] = Read(controller, field);
        return result;
    }

    private static Dictionary<string, object?> MonitorSnapshot(object monitor)
    {
        var result = new Dictionary<string, object?>();
        foreach (string field in new[] { "lastCpuUsagePercent", "smoothedCpuUsagePercent", "cpuScore", "gpuScore", "requiredCpuAnomalies", "spikeMultiplier", "absoluteThreshold", "watchdogStallCount", "performanceCountersAvailable", "consecutiveCounterReadFailures", "eventWatcherErrorCount" })
            result[field] = Read(monitor, field);
        // Mirror only the threshold adjustment, not the detector's score or verdict.
        // These sampled fields can change concurrently; this is diagnostic context.
        if (result["requiredCpuAnomalies"] is int baseThreshold && result["lastCpuUsagePercent"] is float usage)
            result["effectiveCpuAnomalyThreshold"] = usage >= 85 ? Math.Max(6, baseThreshold - 1)
                : usage >= 60 ? Math.Max(6, baseThreshold) : baseThreshold;
        var bits = new Dictionary<string, object?>();
        foreach (string field in new[] { "interruptSpikeBits", "contextSpikeBits", "transitionSpikeBits", "queueSpikeBits", "sysCallsSpikeBits", "pagesInSpikeBits", "pagesPerSecSpikeBits", "pageFaultsPerSecSpikeBits", "availDropBits", "interruptTimeSpikeBits", "dpcTimeSpikeBits", "dpcsQueuedSpikeBits", "exceptionDispatchesSpikeBits" })
        {
            var value = Read(monitor, field);
            bits[field] = value;
            if (value is ushort mask)
            {
                int hits = 0;
                for (int n = mask; n != 0; n &= n - 1) hits++;
                bits[field + "HitsOf5"] = hits;
            }
        }
        result["heuristicWindows"] = bits;
        var latest = new Dictionary<string, object?>();
        foreach (string field in new[] { "interruptHistory", "contextSwitchHistory", "transitionFaultHistory", "processorQueueHistory", "systemCallsHistory", "pagesInputHistory", "pagesPerSecHistory", "pageFaultsPerSecHistory", "availableMBytesHistory", "interruptTimeHistory", "dpcTimeHistory", "dpcsQueuedHistory", "exceptionDispatchesHistory" })
        {
            try
            {
                object? last = null;
                if (Read(monitor, field) is IEnumerable history) foreach (object sample in history) last = sample;
                latest[field] = Finite(last);
            }
            catch { latest[field] = "unavailable_during_concurrent_update"; }
        }
        result["latestCounterSamples"] = latest;
        return result;
    }

    private static object? Read(object? instance, string field)
    {
        try { return Finite(instance?.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)); }
        catch { return null; }
    }

    private static object? Property(object instance, string name)
    {
        try { return Finite(instance.GetType().GetProperty(name)?.GetValue(instance)); }
        catch { return null; }
    }

    private static object? Finite(object? value) => value switch
    {
        float f when !float.IsFinite(f) => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d when !double.IsFinite(d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value
    };

    private static void Enqueue(string kind, Dictionary<string, object?> data)
    {
        data["kind"] = kind;
        data["utc"] = DateTime.UtcNow;
        data["session"] = Session;
        data["sequence"] = Interlocked.Increment(ref sequence);
        data["threadId"] = Environment.CurrentManagedThreadId;
        data["droppedRecords"] = Interlocked.Read(ref dropped);
        data["writeFailures"] = Interlocked.Read(ref writeFailures);
        if (!Queue.Writer.TryWrite(data)) Interlocked.Increment(ref dropped);
    }

    private static async Task WriteLoop(string path)
    {
        await foreach (var record in Queue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= 8 * 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(record) + Environment.NewLine, Encoding.UTF8);
            }
            catch { Interlocked.Increment(ref writeFailures); }
        }
    }
}
