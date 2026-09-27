using AutoOC.Controllers;
using AutoOC.Monitors;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Universal_x86_Tuning_Utility.Scripts.Misc;

public static class AutoOcCpuPolicy
{
    public const int MinimumOffset = -5;
    public const int EvaluationSamples = 60;
    public static bool LibraryMatches => typeof(InstabilityMonitor).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(x => x.Key == "AutoOcCpuPolicy" && x.Value == "cpu-policy-v1");
    // Either half of the portable bundle selects the verified path; a missing
    // matching DLL must pause it rather than silently fall back to the old tuner.
    public static bool Requested => LibraryMatches || File.Exists(Path.Combine(AppContext.BaseDirectory, "autooc-cpu-policy-v1.enabled"));
    public static string Status { get; private set; } = "CPU AutoOC is off.";
    public static event Action<string>? StatusChanged;

    // Called only by the pinned DLL patch at the performance-counter signal site.
    // Workload observations remain visible without setting the CPU failure flag.
    public static void ObserveWorkloadSignal(object monitor) => AutoOcDiagnostics.OnWorkloadSignal(monitor);

    internal static void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        AutoOcDiagnostics.RecordCpuPolicy("status", new { status });
        foreach (var listener in StatusChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action<string>)listener)(status); } catch { }
    }

    internal static void BeginVerifiedEpoch(AdaptiveUndervoltController controller)
    {
        if (!LibraryMatches) throw new InvalidOperationException("The matching CPU policy library is missing.");
        // The constructor has already processed an unclean exit and its learned
        // limits. Reset only the evaluation epoch, retaining those protections.
        var type = controller.GetType();
        FieldInfo Field(string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unsupported controller layout: " + name);
        string[] names = { "stableCount", "cooldownCount", "consecutiveIdleSamples", "consecutiveActiveSamples", "idleResumeOffset", "idleForcedZeroActive", "lastEvaluationTimestamp" };
        var fields = names.Select(Field).ToArray();
        lock (Field("stateLock").GetValue(controller)!)
        {
            object[] values = { 0, 4, 0, 0, 0, false, 0L };
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(controller, values[i]);
            controller.RecordAppliedOffset(0);
        }
    }

    internal static void PreserveUncleanRecovery(AdaptiveUndervoltController controller)
    {
        // A failed rollback must not be relabeled a clean exit by the library's
        // ProcessExit subscription. Successful later restoration uses Stop/Dispose.
        var field = controller.GetType().GetField("unhandledTermination", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unsupported controller recovery layout.");
        field.SetValue(controller, 1);
    }
}
