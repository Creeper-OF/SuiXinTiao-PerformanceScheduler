using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

/// <summary>
/// Keeps startup recovery failures from being bypassed by a later resume action.
/// </summary>
public sealed class StartupRecoverySafetyGate
{
    private bool _stateRepairRequired;
    private bool _rollbackResolutionRequired;
    private bool _gpuRecoveryRequired;

    public bool RequiresManualAction => _stateRepairRequired || _rollbackResolutionRequired || _gpuRecoveryRequired;

    public bool RequiresGpuRecovery => _gpuRecoveryRequired;

    public bool CanResume => !RequiresManualAction;

    public void Apply(StartupRecoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _stateRepairRequired = result.StartupStateReadFailed ||
                               result.FailureStateReadFailed ||
                               result.ProfileDisableFailed;
        _rollbackResolutionRequired = result.RollbackFailed || result.RollbackPending;
        _gpuRecoveryRequired = result.GpuRecoveryFailed;
    }

    public void MarkStateRepairRequired()
    {
        _stateRepairRequired = true;
    }

    public void MarkRollbackRequired()
    {
        _rollbackResolutionRequired = true;
    }

    public void MarkRollbackResolved()
    {
        _rollbackResolutionRequired = false;
    }

    public void MarkGpuRecoveryRequired() => _gpuRecoveryRequired = true;

    public void MarkGpuRecoveryResolved() => _gpuRecoveryRequired = false;
}
