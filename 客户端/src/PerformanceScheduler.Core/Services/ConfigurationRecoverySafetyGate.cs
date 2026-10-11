namespace PerformanceScheduler.Core.Services;

/// <summary>
/// Prevents scheduling from continuing after a configuration import or save
/// left a store in an unknown state.
/// </summary>
public sealed class ConfigurationRecoverySafetyGate
{
    private bool _recoveryResolutionRequired;

    public bool RequiresManualAction => _recoveryResolutionRequired;

    public bool CanResume => !RequiresManualAction;

    public void MarkRecoveryRequired()
    {
        _recoveryResolutionRequired = true;
    }

    public void MarkRecoveryResolved()
    {
        _recoveryResolutionRequired = false;
    }
}
