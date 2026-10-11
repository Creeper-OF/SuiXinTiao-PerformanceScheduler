namespace PerformanceScheduler.Core.Models;

public static class CapabilityReasonCodes
{
    public const string NoPowerPlans = "Capability.NoPowerPlans";

    public const string EfficiencyModeUnavailable = "Capability.EfficiencyModeUnavailable";

    public const string CpuAffinityUnavailable = "Capability.CpuAffinityUnavailable";
    public const string CpuSetsUnavailable = "Capability.CpuSetsUnavailable";
    public const string MemoryIoPriorityUnavailable = "Capability.MemoryIoPriorityUnavailable";
    public const string JobObjectLimitsUnavailable = "Capability.JobObjectLimitsUnavailable";

    public const string GpuAdapterUnavailable = "Capability.GpuAdapterUnavailable";

    public const string GpuVendorExtensionUnavailable = "Capability.GpuVendorExtensionUnavailable";

    public const string GpuProviderApplyUnavailable = "Capability.GpuProviderApplyUnavailable";

    public const string GpuDetectionUnavailable = "Capability.GpuDetectionUnavailable";

    public const string GpuProviderUnavailable = "Capability.GpuProviderUnavailable";
}
