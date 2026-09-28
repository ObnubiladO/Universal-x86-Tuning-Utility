using RyzenSmu;
using System.Diagnostics;
using System.Text.Json;
using Universal_x86_Tuning_Utility.Scripts.AMD_Backend;
using static RyzenSmu.RyzenSMU;

internal static class Program
{
    private static int assertions;
    private static readonly List<string> cases = new();
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Case(string name, Action action) { action(); cases.Add(name); }

    public static void Main()
    {
        Case("success_single_command", () =>
        {
            using var f = new Fixture();
            uint[] args = new uint[6];
            var result = f.Smu.SendSmuCommandDetailed(f.Mailbox, 0xD5, ref args, true);
            Check(result.Status == Status.OK && result.Diagnostics.FailureKind == "none", "success status");
            Check(result.Diagnostics.Phase == "complete" && result.Diagnostics.CommandMayHaveBeenSent, "success phase");
            Check(result.Diagnostics.FirmwareResponse == 1 && result.Diagnostics.MutexAttempts == 1, "success detail");
            Check(result.Diagnostics.FailedRegister == null && result.Diagnostics.NativeErrorCode == null, "success has no failure");
            Check(f.Io.CommandWrites == 1 && f.Io.Calls == 16 && args.All(x => x == 0xFFCE), "one exact flow and values");
            f.AssertReleased();
        });

        Case("contention_recovers_before_any_io", () =>
        {
            using var f = new Fixture();
            using var held = new HeldMutex(f.Pci, 140);
            var result = f.Send();
            held.Join();
            Check(result.Status == Status.OK, "recovered contention");
            Check(result.Diagnostics.MutexAttempts is >= 2 and <= 3, "actual timeout preceded acquisition");
            Check(result.Diagnostics.MutexWaitMilliseconds >= 100, "wait measured across attempts");
            Check(f.Io.CommandWrites == 1 && f.Io.Calls == 16, "recovery did not duplicate protocol");
            f.AssertReleased();
        });

        Case("contention_exhausts_without_io", () =>
        {
            using var f = new Fixture();
            using var held = new HeldMutex(f.Pci);
            var result = f.Send();
            Check(result.Status == Status.FAILED && result.Diagnostics.FailureKind == "mutex_timeout", "timeout classified");
            Check(result.Diagnostics.MutexAttempts == 3 && result.Diagnostics.MutexWaitMilliseconds >= 250, "bounded retry budget");
            Check(result.Diagnostics.Phase == "mutex_wait" && !result.Diagnostics.CommandMayHaveBeenSent, "timeout cannot send");
            Check(result.Diagnostics.FirmwareResponse == null && f.Io.Calls == 0, "no firmware response and no IO");
            held.Release(); held.Join(); f.AssertReleased();
        });

        Case("legacy_wait_unchanged", () =>
        {
            using var f = new Fixture();
            using var held = new HeldMutex(f.Pci);
            var result = f.Send(false);
            Check(result.Diagnostics.FailureKind == "mutex_timeout" && result.Diagnostics.MutexAttempts == 1, "legacy waits once");
            Check(result.Diagnostics.MutexWaitMilliseconds >= 5 && result.Diagnostics.MutexWaitMilliseconds < 200, "legacy wait remains short");
            uint[] args = new uint[6];
            Check(f.Smu.SendSmuCommand(f.Mailbox, 0xD5, ref args) == Status.FAILED && f.Io.Calls == 0, "legacy enum wrapper does not retry or send");
        });

        Case("mutex_unavailable", () =>
        {
            using var f = new Fixture(unavailable: true);
            var result = f.Send();
            Check(result.Diagnostics.FailureKind == "mutex_unavailable" && result.Diagnostics.Phase == "mutex_open", "unavailable classified");
            Check(result.Diagnostics.MutexAttempts == 0 && f.Io.Calls == 0 && !result.Diagnostics.CommandMayHaveBeenSent, "unavailable cannot access hardware");
        });

        Case("mutex_exception_is_not_timeout", () =>
        {
            using var f = new Fixture();
            f.Smu.Open(); f.Pci.Dispose();
            var result = f.Send();
            Check(result.Diagnostics.FailureKind == "mutex_error" && result.Diagnostics.MutexAttempts == 1, "disposed mutex not retried");
            Check(result.Diagnostics.ExceptionType == typeof(ObjectDisposedException).FullName && f.Io.Calls == 0, "mutex exception retained");
        });

        Case("mutex_open_exception", () =>
        {
            var io = new AMDPawnIo();
            using var smu = new RyzenSMU(io, _ => throw new UnauthorizedAccessException("test"));
            var mb = smu.RegisterMailbox("test", 0x100, 0x200, 0x300);
            uint[] args = new uint[6];
            var result = smu.SendSmuCommandDetailed(mb, 0xD5, ref args, true);
            Check(result.Diagnostics.FailureKind == "mutex_error" && result.Diagnostics.Phase == "mutex_open", "open exception classified");
            Check(result.Diagnostics.MutexAttempts == 0 && io.Calls == 0, "open exception no retry/no IO");
        });

        Case("abandoned_mutex_is_acquired_and_released", () =>
        {
            using var f = new Fixture();
            var abandoned = new Thread(() => { f.Pci.WaitOne(); });
            abandoned.Start(); abandoned.Join();
            var result = f.Send();
            Check(result.Status == Status.OK && result.Diagnostics.MutexAttempts == 1, "abandoned acquired once");
            Check(f.Io.CommandWrites == 1, "abandoned not duplicated");
            f.AssertReleased();
        });

        foreach (uint response in new uint[] { 0xFF, 0xFE, 0xFD, 0xFC, 0x42 })
            Case($"firmware_response_{response:X}", () =>
            {
                using var f = new Fixture();
                f.Io.FirmwareResponse = response;
                var result = f.Send();
                Check((uint)result.Status == response && result.Diagnostics.FailureKind == "firmware_response", "firmware error distinct from transport");
                Check(result.Diagnostics.FirmwareResponse == response && result.Diagnostics.NativeErrorCode == null, "raw firmware response retained");
                Check(result.Diagnostics.MutexAttempts == 1 && result.Diagnostics.CommandMayHaveBeenSent, "firmware error not retried");
                Check(f.Io.CommandWrites == 1 && f.Io.Calls == 10, "firmware error no readback or resubmit");
                f.AssertReleased();
            });

        Case("malformed_firmware_response", () =>
        {
            using var f = new Fixture(); f.Io.FirmwareResponse = 0x100;
            var result = f.Send();
            Check(result.Status == Status.FAILED && result.Diagnostics.FailureKind == "malformed_response", "large status rejected");
            Check(result.Diagnostics.FirmwareResponse == 0x100 && result.Diagnostics.FailedRegister == 0x200, "malformed status retained");
            Check(f.Io.CommandWrites == 1, "malformed status not retried"); f.AssertReleased();
        });

        for (int call = 1; call <= 16; call++)
        {
            int failedCall = call;
            Case($"driver_failure_at_operation_{failedCall}", () =>
            {
                using var f = new Fixture(); f.Io.FailAt = failedCall;
                var result = f.Send();
                Check(result.Status == Status.FAILED && result.Diagnostics.FailureKind == "driver_io", "driver failure classified");
                Check(result.Diagnostics.NativeErrorCode == AMDPawnIo.DriverError && result.Diagnostics.FailedRegister != null, "driver details retained");
                Check(result.Diagnostics.CommandMayHaveBeenSent == (failedCall >= 9), "write uncertainty conservative");
                Check(result.Diagnostics.MutexAttempts == 1 && f.Io.Calls == failedCall, "no native operation retry");
                Check(f.Io.CommandWrites == (failedCall >= 9 ? 1 : 0), "command attempted at most once");
                Check(result.Diagnostics.FirmwareResponse == (failedCall >= 11 ? 1u : null), "driver failure cannot fabricate firmware response");
                f.AssertReleased();
            });
        }

        foreach (int call in new[] { 1, 2, 9, 10, 11 })
            Case($"native_exception_at_operation_{call}", () =>
            {
                using var f = new Fixture(); f.Io.ThrowAt = call;
                var result = f.Send();
                Check(result.Status == Status.FAILED && result.Diagnostics.FailureKind == "exception", "native exception classified");
                Check(result.Diagnostics.ExceptionType == typeof(InvalidOperationException).FullName && result.Diagnostics.FailedRegister != null, "native exception evidence");
                Check(result.Diagnostics.CommandMayHaveBeenSent == (call >= 9), "throwing command-write uncertain");
                Check(f.Io.Calls == call && f.Io.CommandWrites == (call >= 9 ? 1 : 0), "throwing operation not retried");
                f.AssertReleased();
            });

        foreach (int call in new[] { 1, 10, 11 })
            foreach (uint size in new uint[] { 0, 2 })
                Case($"malformed_native_output_{call}_{size}", () =>
                {
                    using var f = new Fixture(); f.Io.WrongSizeAt = call; f.Io.ReturnSize = size;
                    var result = f.Send();
                    Check(result.Status == Status.FAILED && result.Diagnostics.FailureKind == "malformed_response", "partial native output rejected");
                    Check(result.Diagnostics.FailedRegister != null && f.Io.Calls == call, "invalid output fails immediately");
                    Check(f.Io.CommandWrites == (call >= 9 ? 1 : 0), "invalid output not resent");
                    f.AssertReleased();
                });

        foreach (bool afterCommand in new[] { false, true })
            Case($"polling_timeout_after_command_{afterCommand}", () =>
            {
                using var f = new Fixture();
                f.Io.ReadyZero = !afterCommand; f.Io.CompletionZero = afterCommand;
                var result = f.Send();
                Check(result.Status == Status.FAILED && result.Diagnostics.FailureKind == "polling_timeout", "poll exhaustion distinct");
                Check(result.Diagnostics.Phase == (afterCommand ? "completion_wait" : "mailbox_ready"), "poll phase retained");
                Check(result.Diagnostics.CommandMayHaveBeenSent == afterCommand && result.Diagnostics.FirmwareResponse == null, "poll timeout cannot fabricate response");
                Check(f.Io.Calls == (afterCommand ? 8201 : 8192) && f.Io.CommandWrites == (afterCommand ? 1 : 0), "bounded poll no resubmit");
                f.AssertReleased();
            });

        Case("invalid_request_no_io", () =>
        {
            using var f = new Fixture(); uint[] args = new uint[6];
            var result = f.Smu.SendSmuCommandDetailed(f.Mailbox, 0, ref args, true);
            Check(result.Status == Status.UNKNOWN_CMD && result.Diagnostics.FailureKind == "invalid_request", "invalid request classified");
            Check(result.Diagnostics.MutexAttempts == 0 && f.Io.Calls == 0, "invalid request no IO");
        });

        Case("per_invocation_diagnostics_survive_following_command", () =>
        {
            using var f = new Fixture(); f.Io.FirmwareResponse = 255;
            var failed = f.Send(); var snapshot = JsonSerializer.Serialize(failed);
            f.Io.Reset(); var success = f.Send();
            Check(success.Status == Status.OK && success.Diagnostics.FailureKind == "none", "subsequent command succeeds");
            Check(JsonSerializer.Serialize(failed) == snapshot && failed.Diagnostics.FirmwareResponse == 255, "previous diagnostic immutable");
            Check(!ReferenceEquals(failed.Diagnostics, success.Diagnostics), "diagnostics are per-call");
        });

        Case("concurrent_results_do_not_use_shared_last_status", () =>
        {
            using var f = new Fixture(); f.Io.ResponseForMessage = message => message == 0xD5 ? 1u : 255u;
            CommandResult? a = null, b = null;
            var first = new Thread(() => { uint[] x = new uint[6]; a = f.Smu.SendSmuCommandDetailed(f.Mailbox, 0xD5, ref x, true); });
            var second = new Thread(() => { uint[] x = new uint[6]; b = f.Smu.SendSmuCommandDetailed(f.Mailbox, 0xD6, ref x, true); });
            f.Smu.Open(); first.Start(); second.Start(); first.Join(); second.Join();
            Check(a!.Status == Status.OK && a.Diagnostics.FailureKind == "none", "success kept on correct call");
            Check(b!.Status == Status.FAILED && b.Diagnostics.FailureKind == "firmware_response" && b.Diagnostics.FirmwareResponse == 255, "error kept on correct call");
            Check(f.Io.CommandWrites == 2, "exactly two commands for two callers"); f.AssertReleased();
        });

        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, assertions, cases }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Mutex Pci = new(false);
        public readonly AMDPawnIo Io = new();
        public readonly RyzenSMU Smu;
        public readonly Mailbox Mailbox;
        public Fixture(bool unavailable = false)
        {
            Smu = new RyzenSMU(Io, name => unavailable ? null : name.EndsWith("Access_PCI") ? Pci : new Mutex(false));
            Mailbox = Smu.RegisterMailbox("isolated-test", 0x100, 0x200, 0x300);
        }
        public CommandResult Send(bool retry = true)
        {
            uint[] args = new uint[6]; return Smu.SendSmuCommandDetailed(Mailbox, 0xD5, ref args, retry);
        }
        public void AssertReleased()
        {
            bool acquired = false;
            var probe = new Thread(() => { acquired = Pci.WaitOne(100); if (acquired) Pci.ReleaseMutex(); });
            probe.Start(); probe.Join(); Check(acquired, "mutex released on return");
        }
        public void Dispose() { Smu.Dispose(); Pci.Dispose(); }
    }

    private sealed class HeldMutex : IDisposable
    {
        private readonly ManualResetEventSlim ready = new();
        private readonly ManualResetEventSlim release = new();
        private readonly Thread owner;
        public HeldMutex(Mutex mutex, int? releaseAfterMs = null)
        {
            owner = new Thread(() =>
            {
                mutex.WaitOne(); ready.Set();
                if (releaseAfterMs.HasValue) release.Wait(releaseAfterMs.Value); else release.Wait();
                mutex.ReleaseMutex();
            });
            owner.Start(); ready.Wait();
        }
        public void Release() => release.Set();
        public void Join() => owner.Join();
        public void Dispose() { Release(); Join(); ready.Dispose(); release.Dispose(); }
    }
}

namespace Universal_x86_Tuning_Utility.Scripts.AMD_Backend
{
    // No driver handles or P/Invoke exist in this fake. The real transport protocol is compiled unchanged.
    public sealed class AMDPawnIo
    {
        public const int DriverError = unchecked((int)0x80070005);
        public int Calls, CommandWrites, FailAt, ThrowAt, WrongSizeAt;
        public uint FirmwareResponse = 1, ReturnSize;
        public bool ReadyZero, CompletionZero;
        public Func<uint, uint>? ResponseForMessage;
        private bool sent;
        private uint message;
        public void Reset()
        {
            Calls = CommandWrites = FailAt = ThrowAt = WrongSizeAt = 0;
            FirmwareResponse = 1; ReadyZero = CompletionZero = sent = false;
        }
        public int ExecuteHr(string name, long[] input, uint inputSize, long[] output, uint outputSize, out uint returned)
        {
            int call = ++Calls; uint register = unchecked((uint)input[0]);
            bool write = name == "ioctl_write_smu_register";
            if (write && register == 0x100) CommandWrites++;
            if (call == ThrowAt) throw new InvalidOperationException("Injected native failure");
            returned = 0;
            if (call == FailAt) return DriverError;
            if (write)
            {
                if (register == 0x200) sent = false;
                if (register == 0x100) { sent = true; message = unchecked((uint)input[1]); }
            }
            else
            {
                output[0] = register == 0x200
                    ? sent ? CompletionZero ? 0 : ResponseForMessage?.Invoke(message) ?? FirmwareResponse : ReadyZero ? 0 : 1
                    : 0xFFCE;
                returned = call == WrongSizeAt ? ReturnSize : 1;
            }
            return 0;
        }
    }
}
