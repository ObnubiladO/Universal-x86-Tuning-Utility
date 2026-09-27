namespace Universal_x86_Tuning_Utility.Scripts.Misc;

// Preserve UXTU's original CPU AutoOC search range. This is a search bound,
// not a target to apply immediately or evidence that an offset is stable.
internal static class AutoOcCpuLimits
{
    internal const int MinimumOffset = -50;
}
