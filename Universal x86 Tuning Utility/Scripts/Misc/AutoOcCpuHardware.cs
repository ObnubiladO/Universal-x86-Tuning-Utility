using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RyzenSmu;

namespace Universal_x86_Tuning_Utility.Scripts.Misc;

internal sealed record AutoOcCpuSupport(bool Supported, string Reason, string CpuName,
    string Vendor, int Family, int Model, int PhysicalCores, int ThreadsPerCore, int ReportedCpuCount);

internal sealed record AutoOcCpuCoreReadback(int Core, int Ccd, int CoreWithinCcd,
    uint Selector, DateTime StartedUtc, DateTime CompletedUtc, uint? StatusCode,
    string? Status, uint[] RawArguments, int? Offset, string? Error);

internal sealed record AutoOcCpuReadback(bool Success, string? Error, DateTime StartedUtc,
    DateTime CompletedUtc, IReadOnlyList<AutoOcCpuCoreReadback> Cores)
{
    public bool Matches(int offset) => Success && Cores.Count == 16 && Cores.All(core => core.Offset == offset);
}

internal sealed record AutoOcCpuRestoreResult(bool CommandAccepted, bool ReadbackVerified,
    AutoOcCpuReadback Readback, string? Error)
{
    public bool Success => CommandAccepted && ReadbackVerified && Error is null;
}

internal sealed record AutoOcCpuApplyResult(int RequestedOffset, bool CommandAccepted,
    bool ReadbackVerified, AutoOcCpuReadback? Readback, string? Error,
    AutoOcCpuRestoreResult? Rollback)
{
    public bool Success => CommandAccepted && ReadbackVerified && Error is null && Rollback is null;
}

internal sealed record AutoOcCpuSmuReply(uint StatusCode, string Status, uint[] Arguments);

internal interface IAutoOcCpuHardwareBackend
{
    bool SetAllCoreOffset(int offset);
    AutoOcCpuSmuReply ReadCoreOffset(uint selector);
    void ResetRejectedCommands();
}

/// <summary>
/// Narrow hardware path validated on this 9950X. All operations are serialized,
/// and an accepted setter is never reported as applied without per-core readback.
/// </summary>
internal sealed class AutoOcCpuHardware
{
    // Static so two service instances cannot interleave an application and its rollback.
    private static readonly SemaphoreSlim HardwareGate = new(1, 1);
    private readonly IAutoOcCpuHardwareBackend backend;
    public AutoOcCpuSupport Support { get; }

    internal AutoOcCpuHardware(IAutoOcCpuHardwareBackend backend, AutoOcCpuSupport support)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Support = support ?? throw new ArgumentNullException(nameof(support));
    }

    public static AutoOcCpuHardware CreateForCurrentCpu() => new(new UxtuBackend(), DetectSupport());

    public Task<AutoOcCpuReadback> ReadAsync() => Serialized(ReadCoreOffsets);

    public Task<AutoOcCpuApplyResult> ApplyAsync(int offset) => Serialized(() => Apply(offset));

    public Task<AutoOcCpuApplyResult> RestoreZeroAsync() => Serialized(() =>
    {
        if (!Support.Supported) return UnsupportedApply(0);
        var restored = RestoreZero();
        return new AutoOcCpuApplyResult(0, restored.CommandAccepted, restored.ReadbackVerified,
            restored.Readback, restored.Error, null);
    });

    private static Task<T> Serialized<T>(Func<T> operation) => Task.Run(() =>
    {
        HardwareGate.Wait();
        try { return operation(); }
        finally { HardwareGate.Release(); }
    });

    private AutoOcCpuApplyResult Apply(int offset)
    {
        if (!Support.Supported) return UnsupportedApply(offset);
        if (offset < -5 || offset > 0)
            return new(offset, false, false, null, "Only offsets from -5 through 0 are allowed by this patch.", null);

        bool accepted = false;
        AutoOcCpuReadback? readback = null;
        string? error = null;
        try
        {
            accepted = backend.SetAllCoreOffset(offset);
            if (!accepted) error = "The SMU did not acknowledge the offset command.";
            else
            {
                readback = ReadCoreOffsets();
                if (!readback.Matches(offset))
                    error = readback.Error ?? "The requested offset did not read back on all 16 cores.";
            }
        }
        catch (Exception exception) { error = exception.ToString(); }

        // A failed/throwing setter may still have partially applied. Every unsuccessful
        // attempt therefore restores zero before returning to the controller.
        AutoOcCpuRestoreResult? rollback = error is null ? null : RestoreZero();
        return new(offset, accepted, readback?.Matches(offset) == true, readback, error, rollback);
    }

    private AutoOcCpuRestoreResult RestoreZero()
    {
        bool accepted = false;
        string? error = null;
        try { backend.ResetRejectedCommands(); }
        catch (Exception exception) { error = "Could not clear the rejected-command cache: " + exception; }

        // Cache-reset failure must not prevent the restoration attempt.
        try
        {
            accepted = backend.SetAllCoreOffset(0);
            if (!accepted) error = Join(error, "The SMU did not acknowledge restoration to zero.");
        }
        catch (Exception exception) { error = Join(error, exception.ToString()); }

        // Even a rejected or throwing setter might have restored the firmware value.
        // Record what the hardware returns, rather than assuming either outcome.
        var readback = ReadCoreOffsets();
        bool verified = readback.Matches(0);
        if (!verified) error = Join(error, readback.Error ?? "Zero did not read back on all 16 cores after restoration.");
        return new(accepted, verified, readback, error);
    }

    private AutoOcCpuReadback ReadCoreOffsets()
    {
        DateTime started = DateTime.UtcNow;
        var cores = new List<AutoOcCpuCoreReadback>(16);
        if (!Support.Supported)
            return new(false, Support.Reason, started, DateTime.UtcNow, cores.AsReadOnly());
        for (int core = 0; core < 16; core++)
        {
            DateTime coreStarted = DateTime.UtcNow;
            uint selector = (uint)((core / 8) << 28 | (core % 8) << 20);
            uint? statusCode = null;
            string? status = null, error = null;
            uint[] raw = Array.Empty<uint>();
            int? offset = null;
            try
            {
                var reply = backend.ReadCoreOffset(selector);
                statusCode = reply.StatusCode;
                status = reply.Status;
                raw = reply.Arguments?.ToArray() ?? Array.Empty<uint>();
                if (statusCode != 1) error = "The firmware offset query was rejected or failed.";
                else if (raw.Length != 6) error = "The offset query returned a malformed argument buffer.";
                else
                {
                    offset = unchecked((short)(raw[0] & 0xFFFF));
                    if (offset < -100 || offset > 100)
                        error = "The returned offset is outside the supported interpretation range.";
                }
            }
            catch (Exception exception) { error = exception.ToString(); }
            cores.Add(new(core, core / 8, core % 8, selector, coreStarted, DateTime.UtcNow,
                statusCode, status, raw, offset, error));
            if (error is not null)
                return new(false, $"Core {core}: {error}", started, DateTime.UtcNow, cores.AsReadOnly());
        }
        return new(true, null, started, DateTime.UtcNow, cores.AsReadOnly());
    }

    private AutoOcCpuApplyResult UnsupportedApply(int offset) => new(offset, false, false, null, Support.Reason, null);
    private static string Join(string? first, string second) => first is null ? second : first + " " + second;

    private static AutoOcCpuSupport DetectSupport()
    {
        string name = Scripts.Family.CPUName;
        string vendor = "";
        int family = 0, model = 0, physical = 0, smt = 0, reported = 0;
        try
        {
            if (!X86Base.IsSupported) throw new PlatformNotSupportedException("CPUID is unavailable.");
            var vendorId = X86Base.CpuId(0, 0);
            byte[] vendorBytes = new byte[12];
            BitConverter.GetBytes(vendorId.Ebx).CopyTo(vendorBytes, 0);
            BitConverter.GetBytes(vendorId.Edx).CopyTo(vendorBytes, 4);
            BitConverter.GetBytes(vendorId.Ecx).CopyTo(vendorBytes, 8);
            vendor = Encoding.ASCII.GetString(vendorBytes);
            uint signature = unchecked((uint)X86Base.CpuId(1, 0).Eax);
            family = (int)((signature >> 8) & 15);
            model = (int)((signature >> 4) & 15) | (int)((signature >> 12) & 0xF0);
            if (family == 15) family += (int)((signature >> 20) & 0xFF);
            uint maximum = unchecked((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax);
            if (maximum >= 0x80000008)
                reported = (X86Base.CpuId(unchecked((int)0x80000008), 0).Ecx & 0xFF) + 1;
            if (maximum >= 0x8000001E)
                smt = ((X86Base.CpuId(unchecked((int)0x8000001E), 0).Ebx >> 8) & 0xFF) + 1;
            physical = smt > 0 && reported % smt == 0 ? reported / smt : 0;
            bool supported = vendor == "AuthenticAMD" && family == 0x1A && model == 0x44 &&
                Scripts.Family.FAM == Scripts.Family.RyzenFamily.GraniteRidge && physical == 16 && smt == 2 && reported == 32 &&
                Regex.IsMatch(name, @"\bAMD Ryzen 9 9950X\b") && !name.Contains("9950X3D", StringComparison.OrdinalIgnoreCase);
            return new(supported, supported ? "Validated Ryzen 9 9950X protocol." : "This readback patch supports only the validated 16-core Ryzen 9 9950X with SMT enabled.",
                name, vendor, family, model, physical, smt, reported);
        }
        catch (Exception exception)
        {
            return new(false, "CPU support detection failed: " + exception.Message, name, vendor, family, model, physical, smt, reported);
        }
    }

    private sealed class UxtuBackend : IAutoOcCpuHardwareBackend
    {
        public bool SetAllCoreOffset(int offset)
        {
            ValidateProtocol();
            if (offset < -5 || offset > 0) throw new ArgumentOutOfRangeException(nameof(offset));
            uint encoded = offset < 0 ? unchecked((uint)(0x100000 + offset)) : 0u;
            return SMUCommands.applySettings("set-coall", encoded);
        }

        public AutoOcCpuSmuReply ReadCoreOffset(uint selector)
        {
            ValidateProtocol();
            // Only two CCDs, eight cores each; no margin/payload bits on getter requests.
            if ((selector & ~0x10700000u) != 0) throw new ArgumentOutOfRangeException(nameof(selector));
            uint[] args = { selector, 0, 0, 0, 0, 0 };
            var status = SMUCommands.RyzenAccess.SendRsmu(0xD5, ref args);
            return new((uint)status, status.ToString(), args);
        }

        public void ResetRejectedCommands() => SMUCommands.ResetRejectedPrereqCommands();

        private static void ValidateProtocol()
        {
            if (Scripts.Family.FAM != Scripts.Family.RyzenFamily.GraniteRidge || SMUCommands.UseHsmp ||
                Smu.PSMU_ADDR_MSG != 0x03B10524 || Smu.PSMU_ADDR_RSP != 0x03B10570 || Smu.PSMU_ADDR_ARG != 0x03B10A40)
                throw new InvalidOperationException("The active SMU mailbox does not match the validated Granite Ridge protocol.");
        }
    }
}
