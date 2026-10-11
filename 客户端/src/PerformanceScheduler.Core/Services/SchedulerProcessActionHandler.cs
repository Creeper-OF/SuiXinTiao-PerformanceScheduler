using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Core.Services;

internal sealed class SchedulerProcessActionHandler
{
    private readonly IPriorityManager _priorityManager;
    private readonly ICpuAffinityManager? _cpuAffinityManager;
    private readonly ICpuSetManager? _cpuSetManager;
    private readonly IProcessMemoryIoManager? _processMemoryIoManager;
    private readonly IProcessJobObjectManager? _processJobObjectManager;
    private readonly IAppLogger _logger;
    private readonly ResourceArbitrationActionGate _resourceArbitrationActionGate = new();

    public SchedulerProcessActionHandler(
        IPriorityManager priorityManager,
        IAppLogger logger,
        ICpuAffinityManager? cpuAffinityManager = null,
        ICpuSetManager? cpuSetManager = null,
        IProcessMemoryIoManager? processMemoryIoManager = null,
        IProcessJobObjectManager? processJobObjectManager = null)
    {
        _priorityManager = priorityManager;
        _logger = logger;
        _cpuAffinityManager = cpuAffinityManager;
        _cpuSetManager = cpuSetManager;
        _processMemoryIoManager = processMemoryIoManager;
        _processJobObjectManager = processJobObjectManager;
    }

    public async Task<ProcessActionPreparation> PrepareAsync(
        SchedulerActionExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var supportsAffinity = request.Capabilities.SupportsCpuAffinity && _cpuAffinityManager is not null;
        var supportsCpuSets = request.Capabilities.SupportsCpuSets && _cpuSetManager is not null;
        var supportsMemoryIo = request.Capabilities.SupportsMemoryIoPriority && _processMemoryIoManager is not null;
        var supportsJobObjects = request.Capabilities.SupportsJobObjectLimits && _processJobObjectManager is not null;
        if (!request.Capabilities.SupportsRollback ||
            (!request.Capabilities.SupportsPriorityBoost && !request.Capabilities.SupportsEfficiencyModeHint && !supportsAffinity && !supportsCpuSets && !supportsMemoryIo && !supportsJobObjects))
        {
            return ProcessActionPreparation.Empty;
        }

        var app = request.ActiveApp;
        var profile = request.Match.Profile;
        var foregroundIdentity = request.Capabilities.SupportsPriorityBoost
            ? await _priorityManager.TryGetProcessIdentityAsync(app.ProcessId, cancellationToken)
            : (supportsAffinity
                ? await _cpuAffinityManager!.TryGetProcessIdentityAsync(app.ProcessId, cancellationToken)
                : (supportsCpuSets
                    ? await _cpuSetManager!.TryGetProcessIdentityAsync(app.ProcessId, cancellationToken)
                    : (supportsMemoryIo ? await _processMemoryIoManager!.TryGetProcessIdentityAsync(app.ProcessId, cancellationToken) : null)));
        var originalForegroundPriority = request.Capabilities.SupportsPriorityBoost
            ? await _priorityManager.TryGetPriorityAsync(app.ProcessId, cancellationToken)
            : null;
        var originalPriorities = new Dictionary<int, PriorityLevel>();
        var appliedPriorities = new Dictionary<int, PriorityLevel>();
        var processIdentities = new Dictionary<int, ProcessIdentitySnapshot>();
        var originalEfficiencyModes = new Dictionary<int, ProcessEfficiencyModeState>();
        var appliedEfficiencyModes = new Dictionary<int, bool>();
        var originalCpuAffinities = new Dictionary<int, long>();
        var appliedCpuAffinities = new Dictionary<int, long>();
        var originalCpuSets = new Dictionary<int, IReadOnlyList<uint>>();
        var appliedCpuSets = new Dictionary<int, IReadOnlyList<uint>>();
        var originalIoPriorities = new Dictionary<int, IoPriorityLevel>();
        var appliedIoPriorities = new Dictionary<int, IoPriorityLevel>();
        var originalMemoryPriorities = new Dictionary<int, MemoryPriorityLevel>();
        var appliedMemoryPriorities = new Dictionary<int, MemoryPriorityLevel>();
        var originalJobObjectLimits = new Dictionary<int, JobObjectLimitSnapshot>();
        var appliedJobObjectLimits = new Dictionary<int, JobObjectLimitSnapshot>();
        var preparedJobObjectLimits = new Dictionary<int, JobObjectLimitPreference>();

        if (foregroundIdentity is not null &&
            originalForegroundPriority is { } foregroundPriority &&
            foregroundPriority != profile.Priority.ForegroundPriority)
        {
            originalPriorities[app.ProcessId] = foregroundPriority;
            appliedPriorities[app.ProcessId] = profile.Priority.ForegroundPriority;
            processIdentities[app.ProcessId] = foregroundIdentity;
        }

        if (supportsAffinity && profile.CpuAffinity.Enabled && profile.CpuAffinity.ForegroundAffinityMask is { } fgTargetMask && fgTargetMask > 0)
        {
            var originalFgAffinity = await _cpuAffinityManager!.TryGetAffinityAsync(app.ProcessId, cancellationToken);
            if (originalFgAffinity is { } fgOrig && fgOrig != fgTargetMask)
            {
                originalCpuAffinities[app.ProcessId] = fgOrig;
                appliedCpuAffinities[app.ProcessId] = fgTargetMask;
                if (foregroundIdentity is not null)
                {
                    processIdentities.TryAdd(app.ProcessId, foregroundIdentity);
                }
            }
        }

        if (supportsCpuSets && profile.CpuSets.Enabled && profile.CpuSets.ForegroundCpuSets is { } fgTargetCpuSets && fgTargetCpuSets.Count > 0)
        {
            var originalFgCpuSets = await _cpuSetManager!.TryGetCpuSetsAsync(app.ProcessId, cancellationToken);
            if (originalFgCpuSets is not null && !AreEquivalentSets(originalFgCpuSets, fgTargetCpuSets))
            {
                originalCpuSets[app.ProcessId] = originalFgCpuSets;
                appliedCpuSets[app.ProcessId] = fgTargetCpuSets;
                if (foregroundIdentity is not null)
                {
                    processIdentities.TryAdd(app.ProcessId, foregroundIdentity);
                }
            }
        }

        if (supportsMemoryIo && profile.MemoryIoPriority.Enabled)
        {
            if (profile.MemoryIoPriority.ForegroundIoPriority is { } fgTargetIo)
            {
                var origFgIo = await _processMemoryIoManager!.TryGetIoPriorityAsync(app.ProcessId, cancellationToken);
                if (origFgIo is { } origIo && origIo != fgTargetIo)
                {
                    originalIoPriorities[app.ProcessId] = origIo;
                    appliedIoPriorities[app.ProcessId] = fgTargetIo;
                    if (foregroundIdentity is not null)
                    {
                        processIdentities.TryAdd(app.ProcessId, foregroundIdentity);
                    }
                }
            }

            if (profile.MemoryIoPriority.ForegroundMemoryPriority is { } fgTargetMem)
            {
                var origFgMem = await _processMemoryIoManager!.TryGetMemoryPriorityAsync(app.ProcessId, cancellationToken);
                if (origFgMem is { } origMem && origMem != fgTargetMem)
                {
                    originalMemoryPriorities[app.ProcessId] = origMem;
                    appliedMemoryPriorities[app.ProcessId] = fgTargetMem;
                    if (foregroundIdentity is not null)
                    {
                        processIdentities.TryAdd(app.ProcessId, foregroundIdentity);
                    }
                }
            }
        }

        var capturedBackgroundBaselines = await _priorityManager.CaptureBackgroundPolicyBaselinesAsync(
            app,
            profile,
            cancellationToken);
        var backgroundBaselines = FilterBackgroundBaselines(
            capturedBackgroundBaselines,
            request.Capabilities);
        var arbitrationDecision = _resourceArbitrationActionGate.Evaluate(
            request.UseResourceArbitration,
            request.ResourceArbitration);
        if (arbitrationDecision.IsEnabled)
        {
            var beforeArbitrationCount = backgroundBaselines.Count;
            backgroundBaselines = backgroundBaselines
                .Where(baseline => IsAllowedByArbitration(baseline, arbitrationDecision))
                .ToArray();
            if (beforeArbitrationCount != backgroundBaselines.Count)
            {
                _logger.Info(
                    $"Resource arbitration filtered {beforeArbitrationCount - backgroundBaselines.Count} background actions " +
                    $"({arbitrationDecision.ReasonKey}).");
            }
        }
        foreach (var baseline in backgroundBaselines)
        {
            processIdentities.TryAdd(baseline.ProcessId, baseline.Identity);
            if (baseline.PreviousPriority != baseline.TargetPriority)
            {
                originalPriorities.TryAdd(baseline.ProcessId, baseline.PreviousPriority);
                appliedPriorities.TryAdd(baseline.ProcessId, baseline.TargetPriority);
            }

            if (baseline.PreviousEfficiencyMode is not null &&
                baseline.PreviousEfficiencyMode.IsEnabled != baseline.TargetEfficiencyMode)
            {
                originalEfficiencyModes.TryAdd(baseline.ProcessId, baseline.PreviousEfficiencyMode);
                appliedEfficiencyModes.TryAdd(baseline.ProcessId, baseline.TargetEfficiencyMode);
            }

            if (supportsAffinity && profile.CpuAffinity.Enabled)
            {
                var targetBgMask = baseline.TargetAffinityMask ?? profile.CpuAffinity.BackgroundAffinityMask;
                if (targetBgMask is { } bgMask && bgMask > 0)
                {
                    var origBgAffinity = await _cpuAffinityManager!.TryGetAffinityAsync(baseline.ProcessId, cancellationToken);
                    if (origBgAffinity is { } bgOrig && bgOrig != bgMask)
                    {
                        originalCpuAffinities.TryAdd(baseline.ProcessId, bgOrig);
                        appliedCpuAffinities.TryAdd(baseline.ProcessId, bgMask);
                    }
                }
            }

            if (supportsCpuSets && profile.CpuSets.Enabled)
            {
                var targetBgCpuSets = baseline.TargetCpuSets ?? profile.CpuSets.BackgroundCpuSets;
                if (targetBgCpuSets is { Count: > 0 } bgSets)
                {
                    var origBgCpuSets = await _cpuSetManager!.TryGetCpuSetsAsync(baseline.ProcessId, cancellationToken);
                    if (origBgCpuSets is not null && !AreEquivalentSets(origBgCpuSets, bgSets))
                    {
                        originalCpuSets.TryAdd(baseline.ProcessId, origBgCpuSets);
                        appliedCpuSets.TryAdd(baseline.ProcessId, bgSets);
                    }
                }
            }

            if (supportsMemoryIo && profile.MemoryIoPriority.Enabled)
            {
                var targetBgIo = baseline.TargetIoPriority ?? profile.MemoryIoPriority.BackgroundIoPriority;
                if (targetBgIo is { } bgIo)
                {
                    var origBgIo = await _processMemoryIoManager!.TryGetIoPriorityAsync(baseline.ProcessId, cancellationToken);
                    if (origBgIo is { } origIo && origIo != bgIo)
                    {
                        originalIoPriorities.TryAdd(baseline.ProcessId, origIo);
                        appliedIoPriorities.TryAdd(baseline.ProcessId, bgIo);
                    }
                }

                var targetBgMem = baseline.TargetMemoryPriority ?? profile.MemoryIoPriority.BackgroundMemoryPriority;
                if (targetBgMem is { } bgMem)
                {
                    var origBgMem = await _processMemoryIoManager!.TryGetMemoryPriorityAsync(baseline.ProcessId, cancellationToken);
                    if (origBgMem is { } origMem && origMem != bgMem)
                    {
                        originalMemoryPriorities.TryAdd(baseline.ProcessId, origMem);
                        appliedMemoryPriorities.TryAdd(baseline.ProcessId, bgMem);
                    }
                }
            }

            if (supportsJobObjects)
            {
                var policy = profile.BackgroundPolicies.FirstOrDefault(p =>
                    string.Equals(p.Category, baseline.Category, StringComparison.OrdinalIgnoreCase));
                var targetJobLimits = baseline.TargetJobLimits ?? policy?.GetEffectiveJobObjectLimits();
                if (targetJobLimits is not null && (targetJobLimits.CpuRate.Enabled || targetJobLimits.MemoryLimit.Enabled))
                {
                    var origLimits = await _processJobObjectManager!.GetCurrentLimitsAsync(baseline.ProcessId, cancellationToken);
                    originalJobObjectLimits.TryAdd(baseline.ProcessId, origLimits ?? new JobObjectLimitSnapshot { ProcessId = baseline.ProcessId });
                    preparedJobObjectLimits.TryAdd(baseline.ProcessId, targetJobLimits);
                    appliedJobObjectLimits.TryAdd(baseline.ProcessId, new JobObjectLimitSnapshot
                    {
                        ProcessId = baseline.ProcessId,
                        HasCpuRateControl = targetJobLimits.CpuRate.Enabled,
                        CpuRatePercent = targetJobLimits.CpuRate.Enabled ? targetJobLimits.CpuRate.MaxCpuRatePercent : null,
                        CpuRateHardCap = targetJobLimits.CpuRate.Enabled && targetJobLimits.CpuRate.HardCap,
                        HasMemoryLimit = targetJobLimits.MemoryLimit.Enabled,
                        MaxCommitMemoryBytes = targetJobLimits.MemoryLimit.Enabled ? targetJobLimits.MemoryLimit.MaxCommitMemoryBytes : null
                    });
                }
            }
        }

        return new ProcessActionPreparation
        {
            ForegroundIdentity = foregroundIdentity,
            OriginalForegroundPriority = originalForegroundPriority,
            BackgroundBaselines = backgroundBaselines,
            OriginalPriorities = originalPriorities,
            AppliedPriorities = appliedPriorities,
            ProcessIdentities = processIdentities,
            OriginalEfficiencyModes = originalEfficiencyModes,
            AppliedEfficiencyModes = appliedEfficiencyModes,
            OriginalCpuAffinities = originalCpuAffinities,
            AppliedCpuAffinities = appliedCpuAffinities,
            OriginalCpuSets = originalCpuSets,
            AppliedCpuSets = appliedCpuSets,
            OriginalIoPriorities = originalIoPriorities,
            AppliedIoPriorities = appliedIoPriorities,
            OriginalMemoryPriorities = originalMemoryPriorities,
            AppliedMemoryPriorities = appliedMemoryPriorities,
            OriginalJobObjectLimits = originalJobObjectLimits,
            AppliedJobObjectLimits = appliedJobObjectLimits,
            PreparedJobObjectLimits = preparedJobObjectLimits
        };
    }

    public async Task<ProcessActionExecution> ApplyAsync(
        SchedulerActionExecutionRequest request,
        ProcessActionPreparation preparation,
        CancellationToken cancellationToken)
    {
        var app = request.ActiveApp;
        var profile = request.Match.Profile;
        PriorityAdjustmentResult? priorityResult;
        SchedulerActionReasonCode foregroundReasonCode;
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments =
            Array.Empty<BackgroundProcessAdjustmentResult>();

        if (!request.Capabilities.SupportsRollback)
        {
            const string message = "Profile matched, but adjustments were blocked because rollback is not supported on this device.";
            _logger.Warn(message);
            priorityResult = SchedulerResultSummaryBuilder.CreateUnsupportedPriorityResult(app.ProcessId, message);
            foregroundReasonCode = SchedulerActionReasonCode.SafeRollbackUnavailable;
        }
        else
        {
            if (!request.Capabilities.SupportsPriorityBoost)
            {
                const string message = "Process priority adjustments are not supported on this device.";
                priorityResult = SchedulerResultSummaryBuilder.CreateUnsupportedPriorityResult(app.ProcessId, message);
                foregroundReasonCode = SchedulerActionReasonCode.CapabilityUnavailable;
            }
            else if (preparation.ForegroundIdentity is null ||
                preparation.OriginalForegroundPriority is null)
            {
                priorityResult = new PriorityAdjustmentResult
                {
                    ProcessId = app.ProcessId,
                    Status = SchedulerActionStatus.Failed,
                    Message = "Foreground process identity or original priority could not be captured safely; priority adjustment was skipped."
                };
                foregroundReasonCode = SchedulerActionReasonCode.ProcessStateUnavailable;
            }
            else
            {
                priorityResult = await _priorityManager.ApplyForegroundBoostAsync(
                    app,
                    profile,
                    preparation.ForegroundIdentity,
                    cancellationToken);
                foregroundReasonCode = ResolveProviderReasonCode(priorityResult.Status);
            }

            if (request.Capabilities.SupportsPriorityBoost || request.Capabilities.SupportsEfficiencyModeHint)
            {
                backgroundAdjustments = await _priorityManager.ApplyBackgroundPoliciesAsync(
                    app,
                    profile,
                    preparation.BackgroundBaselines,
                    cancellationToken);
            }
        }

        var actionResults = new List<SchedulerActionResult>();
        var foregroundPlan = SchedulerActionPlanFactory.CreateForegroundPriority(
            profile,
            app,
            preparation.OriginalForegroundPriority ?? priorityResult?.PreviousPriority);
        actionResults.Add(SchedulerActionPlanFactory.Complete(
            foregroundPlan,
            priorityResult?.Status ?? SchedulerActionStatus.Unsupported,
            priorityResult?.Message ?? "Foreground priority adjustment was not available.",
            priorityResult is null
                ? SchedulerActionReasonCode.ProviderResultMissing
                : foregroundReasonCode));

        if (preparation.OriginalCpuAffinities.TryGetValue(app.ProcessId, out var origFgAffinity) &&
            preparation.AppliedCpuAffinities.TryGetValue(app.ProcessId, out var targetFgAffinity))
        {
            var plan = SchedulerActionPlanFactory.CreateForegroundCpuAffinity(
                profile,
                app,
                origFgAffinity,
                targetFgAffinity);
            if (request.Capabilities.SupportsCpuAffinity && _cpuAffinityManager is not null)
            {
                var applied = await _cpuAffinityManager.SetAffinityAsync(app.ProcessId, targetFgAffinity, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Foreground CPU affinity set to 0x{targetFgAffinity:X}." : "Failed to set foreground CPU affinity.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "CPU affinity is not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        if (preparation.OriginalCpuSets.TryGetValue(app.ProcessId, out var origFgCpuSets) &&
            preparation.AppliedCpuSets.TryGetValue(app.ProcessId, out var targetFgCpuSets))
        {
            var plan = SchedulerActionPlanFactory.CreateForegroundCpuSet(
                profile,
                app,
                origFgCpuSets,
                targetFgCpuSets);
            if (request.Capabilities.SupportsCpuSets && _cpuSetManager is not null)
            {
                var applied = await _cpuSetManager.SetCpuSetsAsync(app.ProcessId, targetFgCpuSets, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Foreground CPU sets set to {string.Join(",", targetFgCpuSets)}." : "Failed to set foreground CPU sets.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "CPU sets are not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        if (preparation.OriginalIoPriorities.TryGetValue(app.ProcessId, out var origFgIo) &&
            preparation.AppliedIoPriorities.TryGetValue(app.ProcessId, out var targetFgIo))
        {
            var plan = SchedulerActionPlanFactory.CreateForegroundIoPriority(
                profile,
                app,
                origFgIo,
                targetFgIo);
            if (request.Capabilities.SupportsMemoryIoPriority && _processMemoryIoManager is not null)
            {
                var applied = await _processMemoryIoManager.SetIoPriorityAsync(app.ProcessId, targetFgIo, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Foreground I/O priority set to {targetFgIo}." : "Failed to set foreground I/O priority.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "Process memory and I/O priority adjustments are not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        if (preparation.OriginalMemoryPriorities.TryGetValue(app.ProcessId, out var origFgMem) &&
            preparation.AppliedMemoryPriorities.TryGetValue(app.ProcessId, out var targetFgMem))
        {
            var plan = SchedulerActionPlanFactory.CreateForegroundMemoryPriority(
                profile,
                app,
                origFgMem,
                targetFgMem);
            if (request.Capabilities.SupportsMemoryIoPriority && _processMemoryIoManager is not null)
            {
                var applied = await _processMemoryIoManager.SetMemoryPriorityAsync(app.ProcessId, targetFgMem, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Foreground memory priority set to {targetFgMem}." : "Failed to set foreground memory priority.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "Process memory and I/O priority adjustments are not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        AddBackgroundActionResults(
            actionResults,
            profile,
            preparation.BackgroundBaselines,
            backgroundAdjustments);

        foreach (var (bgPid, targetBgAffinity) in preparation.AppliedCpuAffinities)
        {
            if (bgPid == app.ProcessId)
            {
                continue;
            }

            var origBgAffinity = preparation.OriginalCpuAffinities.GetValueOrDefault(bgPid);
            var baseline = preparation.BackgroundBaselines.FirstOrDefault(b => b.ProcessId == bgPid);
            var procName = baseline?.ProcessName ?? $"PID {bgPid}";
            var category = baseline?.Category ?? "default";
            var plan = SchedulerActionPlanFactory.CreateBackgroundCpuAffinity(
                profile,
                procName,
                bgPid,
                category,
                origBgAffinity,
                targetBgAffinity);
            if (request.Capabilities.SupportsCpuAffinity && _cpuAffinityManager is not null)
            {
                var applied = await _cpuAffinityManager.SetAffinityAsync(bgPid, targetBgAffinity, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Background CPU affinity set to 0x{targetBgAffinity:X}." : "Failed to set background CPU affinity.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "CPU affinity is not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        foreach (var (bgPid, targetBgCpuSets) in preparation.AppliedCpuSets)
        {
            if (bgPid == app.ProcessId)
            {
                continue;
            }

            var origBgCpuSets = preparation.OriginalCpuSets.GetValueOrDefault(bgPid);
            var baseline = preparation.BackgroundBaselines.FirstOrDefault(b => b.ProcessId == bgPid);
            var procName = baseline?.ProcessName ?? $"PID {bgPid}";
            var category = baseline?.Category ?? "default";
            var plan = SchedulerActionPlanFactory.CreateBackgroundCpuSet(
                profile,
                procName,
                bgPid,
                category,
                origBgCpuSets,
                targetBgCpuSets);
            if (request.Capabilities.SupportsCpuSets && _cpuSetManager is not null)
            {
                var applied = await _cpuSetManager.SetCpuSetsAsync(bgPid, targetBgCpuSets, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Background CPU sets set to {string.Join(",", targetBgCpuSets)}." : "Failed to set background CPU sets.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "CPU sets are not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        foreach (var (bgPid, targetBgIo) in preparation.AppliedIoPriorities)
        {
            if (bgPid == app.ProcessId)
            {
                continue;
            }

            var origBgIo = preparation.OriginalIoPriorities.GetValueOrDefault(bgPid);
            var baseline = preparation.BackgroundBaselines.FirstOrDefault(b => b.ProcessId == bgPid);
            var procName = baseline?.ProcessName ?? $"PID {bgPid}";
            var category = baseline?.Category ?? "default";
            var plan = SchedulerActionPlanFactory.CreateBackgroundIoPriority(
                profile,
                procName,
                bgPid,
                category,
                origBgIo,
                targetBgIo);
            if (request.Capabilities.SupportsMemoryIoPriority && _processMemoryIoManager is not null)
            {
                var applied = await _processMemoryIoManager.SetIoPriorityAsync(bgPid, targetBgIo, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Background I/O priority set to {targetBgIo}." : "Failed to set background I/O priority.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "Process memory and I/O priority adjustments are not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        foreach (var (bgPid, targetBgMem) in preparation.AppliedMemoryPriorities)
        {
            if (bgPid == app.ProcessId)
            {
                continue;
            }

            var origBgMem = preparation.OriginalMemoryPriorities.GetValueOrDefault(bgPid);
            var baseline = preparation.BackgroundBaselines.FirstOrDefault(b => b.ProcessId == bgPid);
            var procName = baseline?.ProcessName ?? $"PID {bgPid}";
            var category = baseline?.Category ?? "default";
            var plan = SchedulerActionPlanFactory.CreateBackgroundMemoryPriority(
                profile,
                procName,
                bgPid,
                category,
                origBgMem,
                targetBgMem);
            if (request.Capabilities.SupportsMemoryIoPriority && _processMemoryIoManager is not null)
            {
                var applied = await _processMemoryIoManager.SetMemoryPriorityAsync(bgPid, targetBgMem, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Background memory priority set to {targetBgMem}." : "Failed to set background memory priority.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "Process memory and I/O priority adjustments are not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        foreach (var (bgPid, targetJobLimits) in preparation.PreparedJobObjectLimits)
        {
            if (bgPid == app.ProcessId)
            {
                continue;
            }

            var origLimits = preparation.OriginalJobObjectLimits.GetValueOrDefault(bgPid);
            var baseline = preparation.BackgroundBaselines.FirstOrDefault(b => b.ProcessId == bgPid);
            var procName = baseline?.ProcessName ?? $"PID {bgPid}";
            var category = baseline?.Category ?? "default";
            var plan = SchedulerActionPlanFactory.CreateBackgroundJobObjectLimit(
                profile,
                procName,
                bgPid,
                category,
                origLimits,
                targetJobLimits);
            if (request.Capabilities.SupportsJobObjectLimits && _processJobObjectManager is not null)
            {
                var applied = await _processJobObjectManager.ApplyProcessLimitsAsync(bgPid, targetJobLimits, cancellationToken);
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    applied ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
                    applied ? $"Background Job Object limits applied for PID {bgPid}." : "Failed to apply background Job Object limits.",
                    applied ? SchedulerActionReasonCode.None : SchedulerActionReasonCode.ProviderApplyFailed));
            }
            else
            {
                actionResults.Add(SchedulerActionPlanFactory.Complete(
                    plan,
                    SchedulerActionStatus.Unsupported,
                    "Job Object limits are not supported on this device.",
                    SchedulerActionReasonCode.CapabilityUnavailable));
            }
        }

        foreach (var adjustment in backgroundAdjustments.Where(
                     result => result.Status == SchedulerActionStatus.Applied))
        {
            _logger.Info(
                $"Background policy applied: {adjustment.ProcessName} ({adjustment.ProcessId}) -> {adjustment.AppliedPriority} [{adjustment.Category}]");
        }

        return new ProcessActionExecution
        {
            PriorityChange = priorityResult,
            BackgroundAdjustments = backgroundAdjustments,
            Actions = actionResults
        };
    }

    private static void AddBackgroundActionResults(
        ICollection<SchedulerActionResult> results,
        PerformanceProfile profile,
        IReadOnlyList<BackgroundProcessPriorityBaseline> baselines,
        IReadOnlyList<BackgroundProcessAdjustmentResult> adjustments)
    {
        var adjustmentByProcessId = adjustments
            .GroupBy(item => item.ProcessId)
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var baseline in baselines)
        {
            adjustmentByProcessId.TryGetValue(baseline.ProcessId, out var adjustment);
            var status = adjustment?.Status ?? SchedulerActionStatus.Failed;
            var message = adjustment?.Message ?? "No result was returned for the prepared background action.";
            var reasonCode = adjustment is null
                ? SchedulerActionReasonCode.ProviderResultMissing
                : ResolveProviderReasonCode(adjustment.Status);
            foreach (var plan in SchedulerActionPlanFactory.CreateBackgroundPlans(profile, baseline))
            {
                results.Add(SchedulerActionPlanFactory.Complete(plan, status, message, reasonCode));
            }
        }
    }

    private static SchedulerActionReasonCode ResolveProviderReasonCode(SchedulerActionStatus status) =>
        status switch
        {
            SchedulerActionStatus.Applied => SchedulerActionReasonCode.None,
            SchedulerActionStatus.Unsupported => SchedulerActionReasonCode.SafetyPolicyBlocked,
            SchedulerActionStatus.Failed => SchedulerActionReasonCode.ProviderApplyFailed,
            _ => SchedulerActionReasonCode.None
        };

    private static IReadOnlyList<BackgroundProcessPriorityBaseline> FilterBackgroundBaselines(
        IReadOnlyList<BackgroundProcessPriorityBaseline> baselines,
        CapabilitySnapshot capabilities)
    {
        if (capabilities.SupportsPriorityBoost && capabilities.SupportsEfficiencyModeHint)
        {
            return baselines;
        }

        return baselines
            .Select(baseline => baseline with
            {
                TargetPriority = capabilities.SupportsPriorityBoost
                    ? baseline.TargetPriority
                    : baseline.PreviousPriority,
                PreviousEfficiencyMode = capabilities.SupportsEfficiencyModeHint
                    ? baseline.PreviousEfficiencyMode
                    : null,
                TargetEfficiencyMode = capabilities.SupportsEfficiencyModeHint
                    ? baseline.TargetEfficiencyMode
                    : false
            })
            .Where(baseline =>
                baseline.PreviousPriority != baseline.TargetPriority ||
                baseline.PreviousEfficiencyMode is { } previousEfficiency &&
                previousEfficiency.IsEnabled != baseline.TargetEfficiencyMode ||
                baseline.TargetAffinityMask is not null ||
                baseline.TargetCpuSets is { Count: > 0 } ||
                baseline.TargetIoPriority is not null ||
                baseline.TargetMemoryPriority is not null ||
                baseline.TargetJobLimits is not null)
            .ToArray();
    }

    private static bool IsAllowedByArbitration(
        BackgroundProcessPriorityBaseline baseline,
        ResourceArbitrationActionDecision decision)
    {
        if (!decision.IsEnabled || !decision.AllowBackgroundActions ||
            !decision.AllowedProcessIds.Contains(baseline.ProcessId))
        {
            return false;
        }

        return decision.AllowedProcessStartTimes.TryGetValue(
                   baseline.ProcessId,
                   out var expectedStartedAt) &&
               baseline.Identity.StartedAt == expectedStartedAt;
    }

    private static bool AreEquivalentSets(IReadOnlyList<uint> first, IReadOnlyList<uint> second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first.Count != second.Count) return false;
        return new HashSet<uint>(first).SetEquals(second);
    }
}

internal sealed record ProcessActionPreparation
{
    public static ProcessActionPreparation Empty { get; } = new();

    public ProcessIdentitySnapshot? ForegroundIdentity { get; init; }

    public PriorityLevel? OriginalForegroundPriority { get; init; }

    public IReadOnlyList<BackgroundProcessPriorityBaseline> BackgroundBaselines { get; init; } =
        Array.Empty<BackgroundProcessPriorityBaseline>();

    public IReadOnlyDictionary<int, PriorityLevel> OriginalPriorities { get; init; } =
        new Dictionary<int, PriorityLevel>();

    public IReadOnlyDictionary<int, PriorityLevel> AppliedPriorities { get; init; } =
        new Dictionary<int, PriorityLevel>();

    public IReadOnlyDictionary<int, ProcessIdentitySnapshot> ProcessIdentities { get; init; } =
        new Dictionary<int, ProcessIdentitySnapshot>();

    public IReadOnlyDictionary<int, ProcessEfficiencyModeState> OriginalEfficiencyModes { get; init; } =
        new Dictionary<int, ProcessEfficiencyModeState>();

    public IReadOnlyDictionary<int, bool> AppliedEfficiencyModes { get; init; } =
        new Dictionary<int, bool>();

    public IReadOnlyDictionary<int, long> OriginalCpuAffinities { get; init; } =
        new Dictionary<int, long>();

    public IReadOnlyDictionary<int, long> AppliedCpuAffinities { get; init; } =
        new Dictionary<int, long>();

    public IReadOnlyDictionary<int, IReadOnlyList<uint>> OriginalCpuSets { get; init; } =
        new Dictionary<int, IReadOnlyList<uint>>();

    public IReadOnlyDictionary<int, IReadOnlyList<uint>> AppliedCpuSets { get; init; } =
        new Dictionary<int, IReadOnlyList<uint>>();

    public IReadOnlyDictionary<int, IoPriorityLevel> OriginalIoPriorities { get; init; } =
        new Dictionary<int, IoPriorityLevel>();

    public IReadOnlyDictionary<int, IoPriorityLevel> AppliedIoPriorities { get; init; } =
        new Dictionary<int, IoPriorityLevel>();

    public IReadOnlyDictionary<int, MemoryPriorityLevel> OriginalMemoryPriorities { get; init; } =
        new Dictionary<int, MemoryPriorityLevel>();

    public IReadOnlyDictionary<int, MemoryPriorityLevel> AppliedMemoryPriorities { get; init; } =
        new Dictionary<int, MemoryPriorityLevel>();

    public IReadOnlyDictionary<int, JobObjectLimitSnapshot> OriginalJobObjectLimits { get; init; } =
        new Dictionary<int, JobObjectLimitSnapshot>();

    public IReadOnlyDictionary<int, JobObjectLimitSnapshot> AppliedJobObjectLimits { get; init; } =
        new Dictionary<int, JobObjectLimitSnapshot>();

    public IReadOnlyDictionary<int, JobObjectLimitPreference> PreparedJobObjectLimits { get; init; } =
        new Dictionary<int, JobObjectLimitPreference>();
}

internal sealed record ProcessActionExecution
{
    public PriorityAdjustmentResult? PriorityChange { get; init; }

    public IReadOnlyList<BackgroundProcessAdjustmentResult> BackgroundAdjustments { get; init; } =
        Array.Empty<BackgroundProcessAdjustmentResult>();

    public IReadOnlyList<SchedulerActionResult> Actions { get; init; } =
        Array.Empty<SchedulerActionResult>();
}