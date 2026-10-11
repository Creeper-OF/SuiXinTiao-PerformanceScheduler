using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Coordinates runtime closed-loop auto-tuning for the active foreground process.
/// It verifies capabilities, tracks baseline state, applies bounded step adjustments,
/// and restores the baseline when the session ends or transitions.
/// </summary>
public sealed class TargetExperienceAdaptiveCoordinator : ITargetExperienceAdaptiveCoordinator
{
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IRollbackManager _rollbackManager;
    private readonly TargetExperienceAutoTuner _tuner;
    private readonly IAppLogger? _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _sync = new();

    private int? _activeProcessId;
    private Guid? _activeSchemeGuid;
    private PowerSourceMode _activePowerSourceMode;
    private int? _baselineProcessorMaxPercent;
    private int? _currentProcessorMaxPercent;
    private DateTimeOffset? _lastAdjustedAt;

    public TargetExperienceAdaptiveCoordinator(
        IPowerPlanManager powerPlanManager,
        IRollbackManager rollbackManager,
        TargetExperienceAutoTuner? tuner = null,
        IAppLogger? logger = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _powerPlanManager = powerPlanManager ?? throw new ArgumentNullException(nameof(powerPlanManager));
        _rollbackManager = rollbackManager ?? throw new ArgumentNullException(nameof(rollbackManager));
        _tuner = tuner ?? new TargetExperienceAutoTuner();
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<TargetExperienceAdaptiveResult?> EvaluateAndApplyAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        CapabilitySnapshot capabilities,
        PowerSourceMode powerSourceMode,
        PowerPlanInfo? activePowerPlan,
        TargetExperienceControlDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        if (!capabilities.SupportsRollback || !capabilities.SupportsPowerPlanSwitching)
        {
            return null;
        }

        if (activePowerPlan is null)
        {
            return null;
        }

        var preference = profile.TargetExperience;
        if (!preference.Enabled || preference.ControlMode != TargetExperienceControlMode.Automatic)
        {
            return null;
        }

        var now = _utcNow();
        var targetPowerSourceMode = profile.PowerSourceMode == PowerSourceMode.Any
            ? powerSourceMode
            : profile.PowerSourceMode;

        int currentPercent;
        lock (_sync)
        {
            if (_activeProcessId != app.ProcessId || _activeSchemeGuid != activePowerPlan.SchemeGuid)
            {
                // New session started; reset old session tracking
                _activeProcessId = app.ProcessId;
                _activeSchemeGuid = activePowerPlan.SchemeGuid;
                _activePowerSourceMode = targetPowerSourceMode;
                _baselineProcessorMaxPercent = null;
                _currentProcessorMaxPercent = null;
                _lastAdjustedAt = null;
            }
        }

        if (_baselineProcessorMaxPercent is null)
        {
            try
            {
                var advanced = await _powerPlanManager.GetAdvancedSettingsAsync(
                    activePowerPlan.SchemeGuid,
                    targetPowerSourceMode,
                    cancellationToken);
                var initialVal = advanced?.ProcessorMaxStatePercent ?? 100;
                lock (_sync)
                {
                    _baselineProcessorMaxPercent = initialVal;
                    _currentProcessorMaxPercent = initialVal;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Failed to query baseline advanced power settings for {app.ProcessName}.", ex);
                return null;
            }
        }

        lock (_sync)
        {
            currentPercent = _currentProcessorMaxPercent ?? 100;
        }

        var plan = _tuner.PlanAdjustment(
            preference,
            decision,
            currentPercent,
            _lastAdjustedAt,
            now);

        if (plan is null)
        {
            return null;
        }

        if (!plan.HasChange)
        {
            return new TargetExperienceAdaptiveResult
            {
                Applied = false,
                Plan = plan,
                AppliedProcessorMaxPercent = currentPercent,
                ReasonKey = plan.ReasonKey
            };
        }

        try
        {
            var updatePreference = new PowerPlanAdvancedPreference
            {
                ProcessorMaxStatePercent = plan.TargetProcessorMaxPercent
            };

            var applied = await _powerPlanManager.ApplyAdvancedSettingsAsync(
                activePowerPlan.SchemeGuid,
                targetPowerSourceMode,
                updatePreference,
                cancellationToken);

            if (!applied)
            {
                _logger?.Warn($"Adaptive auto-tuning power setting apply failed for {app.ProcessName}.");
                return new TargetExperienceAdaptiveResult
                {
                    Applied = false,
                    Plan = plan,
                    AppliedProcessorMaxPercent = currentPercent,
                    ReasonKey = "SchedulerActionReasonCode.ProviderApplyFailed"
                };
            }

            lock (_sync)
            {
                _currentProcessorMaxPercent = plan.TargetProcessorMaxPercent;
                _lastAdjustedAt = now;
            }

            _logger?.Info(
                $"Target experience adaptive auto-tuning applied for {app.ProcessName}: " +
                $"{plan.CurrentProcessorMaxPercent}% -> {plan.TargetProcessorMaxPercent}% ({plan.Kind}).");

            return new TargetExperienceAdaptiveResult
            {
                Applied = true,
                Plan = plan,
                AppliedProcessorMaxPercent = plan.TargetProcessorMaxPercent,
                ReasonKey = "TargetExperience.AdaptiveApplied"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error($"Target experience adaptive auto-tuning execution threw for {app.ProcessName}.", ex);
            return new TargetExperienceAdaptiveResult
            {
                Applied = false,
                Plan = plan,
                AppliedProcessorMaxPercent = currentPercent,
                ReasonKey = "SchedulerActionReasonCode.ProviderApplyFailed"
            };
        }
    }

    public async Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        Guid? schemeGuid;
        PowerSourceMode mode;
        int? baseline;
        int? current;

        lock (_sync)
        {
            schemeGuid = _activeSchemeGuid;
            mode = _activePowerSourceMode;
            baseline = _baselineProcessorMaxPercent;
            current = _currentProcessorMaxPercent;

            _activeProcessId = null;
            _activeSchemeGuid = null;
            _baselineProcessorMaxPercent = null;
            _currentProcessorMaxPercent = null;
            _lastAdjustedAt = null;
        }

        if (schemeGuid is { } scheme && baseline is { } baselineVal && current is { } currentVal && currentVal != baselineVal)
        {
            try
            {
                var restorePref = new PowerPlanAdvancedPreference
                {
                    ProcessorMaxStatePercent = baselineVal
                };
                await _powerPlanManager.ApplyAdvancedSettingsAsync(
                    scheme,
                    mode,
                    restorePref,
                    cancellationToken);
                _logger?.Info($"Target experience adaptive session reset; restored baseline {baselineVal}%.");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Failed to restore baseline advanced power setting during adaptive session reset: {ex.Message}");
            }
        }
    }
}
