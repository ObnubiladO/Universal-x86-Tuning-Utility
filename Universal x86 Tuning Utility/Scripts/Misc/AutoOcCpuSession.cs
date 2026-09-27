using AutoOC.Controllers;
using AutoOC.Monitors;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Universal_x86_Tuning_Utility.Scripts.Misc;

// Serializes the controller, readback and shutdown. All awaits are independent
// of the UI dispatcher so normal application exit can drain and restore safely.
internal sealed class AutoOcCpuSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AutoOcCpuHardware hardware = AutoOcCpuHardware.CreateForCurrentCpu();
    private InstabilityMonitor? monitor;
    private AdaptiveUndervoltController? controller;
    private bool ownsHardware, faulted;
    private volatile bool stopping;
    private int verifiedOffset;
    private DateTime monitorStartedUtc, lastReadUtc;

    public async Task TickAsync(bool enabled)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stopping) return;
            if (!enabled)
            {
                if (await DisableCoreAsync().ConfigureAwait(false))
                {
                    faulted = false;
                    AutoOcCpuPolicy.SetStatus("CPU AutoOC is off.");
                }
                return;
            }
            if (faulted) return;
            if (!AutoOcCpuPolicy.LibraryMatches) throw new InvalidOperationException("The matching CPU policy library is missing.");
            if (!hardware.Support.Supported) throw new InvalidOperationException(hardware.Support.Reason);
            if (monitor == null)
            {
                monitor = new InstabilityMonitor();
                monitorStartedUtc = DateTime.UtcNow;
                AutoOcDiagnostics.RecordCpuPolicy("monitor_started", new { monitor.WheaWatcherActive, monitor.EventWatcherErrorCount, hardware.Support,
                    stateDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoOC", "AdaptiveUndervolt") });
            }
            if (!monitor.WheaWatcherActive || monitor.EventWatcherErrorCount != 0)
                throw new InvalidOperationException("Hardware-error monitoring is unavailable or reported an error.");
            if (!monitor.PerformanceCountersAvailable || !float.IsFinite(monitor.GetCpuUsagePercent()) || !float.IsFinite(monitor.GetSmoothedCpuUsagePercent()))
            {
                if (controller != null || DateTime.UtcNow - monitorStartedUtc > TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException("CPU monitoring stopped providing valid samples.");
                AutoOcCpuPolicy.SetStatus("CPU AutoOC is waiting for monitoring to start.");
                return;
            }
            if (controller == null)
            {
                var baseline = await hardware.ReadAsync().ConfigureAwait(false);
                AutoOcDiagnostics.RecordCpuPolicy("baseline_readback", baseline);
                if (!baseline.Matches(0)) throw new InvalidOperationException("Start CPU AutoOC with all core offsets at 0; existing offsets were left unchanged.");
                ownsHardware = true;
                verifiedOffset = 0;
                controller = new AdaptiveUndervoltController(monitor, AutoOcCpuPolicy.MinimumOffset,
                    stepSize: 1, stableThreshold: AutoOcCpuPolicy.EvaluationSamples, cooldownThreshold: 4,
                    isIgpu: false, minimumEvaluationIntervalMilliseconds: 1000,
                    idleEntrySamples: 3, idleExitSamples: 2, idleExitMarginPercent: 2f);
                AutoOcDiagnostics.AttachController(controller, false);
                AutoOcCpuPolicy.BeginVerifiedEpoch(controller);
                lastReadUtc = DateTime.UtcNow;
            }
            if (!controller.IsPersistenceAvailable()) throw new InvalidOperationException("CPU AutoOC cannot save its recovery state.");
            int requested = controller.UpdateOffset();
            if (requested < AutoOcCpuPolicy.MinimumOffset || requested > 0)
                throw new InvalidOperationException("The controller requested an offset outside this build's limits.");
            if (requested != verifiedOffset)
            {
                var applied = await hardware.ApplyAsync(requested).ConfigureAwait(false);
                AutoOcDiagnostics.RecordCpuPolicy("offset_application", applied);
                if (!applied.Success)
                {
                    if (applied.Rollback?.ReadbackVerified == true)
                    {
                        verifiedOffset = 0;
                        controller.RecordAppliedOffset(0);
                    }
                    throw new InvalidOperationException(applied.Error ?? "The CPU offset could not be verified.");
                }
                verifiedOffset = requested;
                controller.RecordAppliedOffset(requested);
                lastReadUtc = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastReadUtc >= TimeSpan.FromSeconds(15))
            {
                var observed = await hardware.ReadAsync().ConfigureAwait(false);
                AutoOcDiagnostics.RecordCpuPolicy("periodic_readback", observed);
                if (!observed.Matches(verifiedOffset)) throw new InvalidOperationException("CPU offsets changed unexpectedly or could not be read.");
                lastReadUtc = DateTime.UtcNow;
            }
            AutoOcDiagnostics.ObserveController(controller, false, monitor);
            AutoOcCpuPolicy.SetStatus(verifiedOffset == 0
                ? "CPU offset 0 verified. AutoOC waits during idle and checks active workloads before tuning. Limit −5."
                : $"CPU offset {verifiedOffset} verified on all 16 cores. AutoOC active; limit −5.");
        }
        catch (Exception ex)
        {
            faulted = true;
            AutoOcDiagnostics.RecordCpuPolicy("paused", new { error = ex.ToString(), ownsHardware, verifiedOffset });
            bool restored = await DisableCoreAsync().ConfigureAwait(false);
            AutoOcCpuPolicy.SetStatus($"CPU AutoOC paused: {ex.Message} " +
                (restored && ownsHardware == false ? "Enable it again after resolving the cause." : "Restoration to 0 could not be verified."));
        }
        finally { gate.Release(); }
    }

    private async Task<bool> DisableCoreAsync()
    {
        if (ownsHardware)
        {
            var restored = await hardware.RestoreZeroAsync().ConfigureAwait(false);
            AutoOcDiagnostics.RecordCpuPolicy("restore_zero", restored);
            if (!restored.ReadbackVerified)
            {
                faulted = true;
                if (controller != null) AutoOcCpuPolicy.PreserveUncleanRecovery(controller);
                AutoOcCpuPolicy.SetStatus("CPU AutoOC paused: restoration to 0 could not be verified.");
                return false;
            }
            verifiedOffset = 0;
            controller?.RecordAppliedOffset(0);
            ownsHardware = false;
        }
        controller?.Stop();
        controller?.Dispose();
        controller = null;
        monitor?.Dispose();
        monitor = null;
        return true;
    }

    public async Task StopAsync()
    {
        stopping = true;
        await gate.WaitAsync().ConfigureAwait(false);
        try { await DisableCoreAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
