// Compile the actual service source, but make accidental production backend use fail.
// This executable has no driver reference and cannot send a hardware command.
namespace Universal_x86_Tuning_Utility.Scripts
{
    internal static class Family
    {
        internal enum RyzenFamily { Unknown, GraniteRidge }
        internal static RyzenFamily FAM = RyzenFamily.Unknown;
        internal static string CPUName = "test process; production backend prohibited";
    }
}
namespace RyzenSmu
{
    internal sealed class RyzenSMU
    {
        internal enum Status : byte { OK = 1, FAILED = 255 }
        internal sealed record CommandDiagnostics(string FailureKind, string Phase,
            bool CommandMayHaveBeenSent, int MutexAttempts, double MutexWaitMilliseconds,
            double ElapsedMilliseconds, uint? FirmwareResponse, int? NativeErrorCode,
            uint? FailedRegister, string? ExceptionType);
        internal sealed record CommandResult(Status Status, CommandDiagnostics Diagnostics);
    }
    internal static class SMUCommands
    {
        internal static bool UseHsmp => throw new InvalidOperationException("Production backend prohibited in tests.");
        internal static Smu RyzenAccess => throw new InvalidOperationException("Production backend prohibited in tests.");
        internal static bool applySettings(string command, uint value) => throw new InvalidOperationException("Production backend prohibited in tests.");
        internal static void ResetRejectedPrereqCommands() => throw new InvalidOperationException("Production backend prohibited in tests.");
    }
    internal sealed class Smu
    {
        internal static uint PSMU_ADDR_MSG => throw new InvalidOperationException("Production backend prohibited in tests.");
        internal static uint PSMU_ADDR_RSP => throw new InvalidOperationException("Production backend prohibited in tests.");
        internal static uint PSMU_ADDR_ARG => throw new InvalidOperationException("Production backend prohibited in tests.");
        internal RyzenSMU.CommandResult SendRsmuDetailed(uint command, ref uint[] arguments, bool retryMutexTimeout = false)
            => throw new InvalidOperationException("Production backend prohibited in tests.");
    }
}
