using System.Diagnostics;
using System.Runtime.InteropServices;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Processes;

public sealed class Win32PriorityManager : IPriorityManager
{
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private readonly BackgroundPolicySelector _backgroundPolicySelector = new();

    public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var process = Process.GetProcessById(processId);
            return Task.FromResult<ProcessIdentitySnapshot?>(CreateIdentity(process));
        }
        catch
        {
            return Task.FromResult<ProcessIdentitySnapshot?>(null);
        }
    }

    public async Task<bool> IsSameProcessAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default)
    {
        var current = await TryGetProcessIdentityAsync(identity.ProcessId, cancellationToken);
        return current is not null && IsSameIdentity(identity, current);
    }

    public Task<PriorityLevel?> TryGetPriorityAsync(int processId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Task.FromResult<PriorityLevel?>(MapPriority(process.PriorityClass));
        }
        catch
        {
            return Task.FromResult<PriorityLevel?>(null);
        }
    }

    public Task<bool> SetPriorityAsync(int processId, PriorityLevel priority, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var safety = ProcessPrioritySafetyPolicy.Evaluate(process, priority);
            if (!safety.IsAllowed)
            {
                return Task.FromResult(false);
            }

            process.PriorityClass = MapPriority(priority);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<ProcessEfficiencyModeState?> TryGetEfficiencyModeAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<ProcessEfficiencyModeState?>(null);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion
            };
            var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
            if (!GetProcessInformation(
                    process.Handle,
                    ProcessInformationClass.ProcessPowerThrottling,
                    ref state,
                    size))
            {
                return Task.FromResult<ProcessEfficiencyModeState?>(null);
            }

            return Task.FromResult<ProcessEfficiencyModeState?>(new ProcessEfficiencyModeState
            {
                IsEnabled = (state.StateMask & ProcessPowerThrottlingExecutionSpeed) != 0
            });
        }
        catch
        {
            return Task.FromResult<ProcessEfficiencyModeState?>(null);
        }
    }

    public Task<bool> SetEfficiencyModeAsync(
        int processId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return Task.FromResult(SetEfficiencyMode(process, enabled));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default) =>
        ApplyForegroundBoostCore(app, profile, expectedIdentity: null, cancellationToken);

    public Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        ProcessIdentitySnapshot expectedIdentity,
        CancellationToken cancellationToken = default) =>
        ApplyForegroundBoostCore(app, profile, expectedIdentity, cancellationToken);

    public Task<ConditionalProcessUpdateResult> RestorePriorityIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        PriorityLevel expectedCurrentPriority,
        PriorityLevel originalPriority,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!IsSameIdentity(identity, CreateIdentity(process)))
            {
                return Task.FromResult(ConditionalProcessUpdateResult.IdentityMismatch);
            }

            if (MapPriority(process.PriorityClass) != expectedCurrentPriority)
            {
                return Task.FromResult(ConditionalProcessUpdateResult.CurrentValueMismatch);
            }

            var safety = ProcessPrioritySafetyPolicy.Evaluate(process, originalPriority);
            if (!safety.IsAllowed)
            {
                return Task.FromResult(ConditionalProcessUpdateResult.Failed);
            }

            process.PriorityClass = MapPriority(originalPriority);
            return Task.FromResult(ConditionalProcessUpdateResult.Applied);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ConditionalProcessUpdateResult.IdentityMismatch);
        }
        catch
        {
            return Task.FromResult(ConditionalProcessUpdateResult.Failed);
        }
    }

    public Task<ConditionalProcessUpdateResult> RestoreEfficiencyModeIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        bool expectedCurrentState,
        bool originalState,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!IsSameIdentity(identity, CreateIdentity(process)))
            {
                return Task.FromResult(ConditionalProcessUpdateResult.IdentityMismatch);
            }

            var currentState = TryGetEfficiencyMode(process);
            if (currentState is null)
            {
                return Task.FromResult(ConditionalProcessUpdateResult.Failed);
            }

            if (currentState.IsEnabled != expectedCurrentState)
            {
                return Task.FromResult(ConditionalProcessUpdateResult.CurrentValueMismatch);
            }

            return Task.FromResult(SetEfficiencyMode(process, originalState)
                ? ConditionalProcessUpdateResult.Applied
                : ConditionalProcessUpdateResult.Failed);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ConditionalProcessUpdateResult.IdentityMismatch);
        }
        catch
        {
            return Task.FromResult(ConditionalProcessUpdateResult.Failed);
        }
    }

    public Task<IReadOnlyList<BackgroundProcessPriorityBaseline>> CaptureBackgroundPolicyBaselinesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (!profile.Priority.LowerBackgroundProcesses || profile.BackgroundPolicies.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<BackgroundProcessPriorityBaseline>>(Array.Empty<BackgroundProcessPriorityBaseline>());
        }

        var baselines = new List<BackgroundProcessPriorityBaseline>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == foregroundApp.ProcessId || process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    var processName = process.ProcessName;
                    var executablePath = TryReadExecutablePath(process);
                    var classification = ProcessClassificationHelper.Classify(processName, executablePath, process.MainWindowTitle);
                    var policy = _backgroundPolicySelector.Select(classification, profile.BackgroundPolicies);
                    if (policy is null)
                    {
                        continue;
                    }

                    var safety = ProcessPrioritySafetyPolicy.Evaluate(process, policy.TargetPriority);
                    if (!safety.IsAllowed)
                    {
                        continue;
                    }

                    var identity = CreateIdentity(process);
                    var previousPriority = MapPriority(process.PriorityClass);
                    var targetEfficiencyMode = profile.Priority.PreferEfficiencyModeForBackground ||
                                               policy.PreferEfficiencyMode;
                    var previousEfficiencyMode = targetEfficiencyMode
                        ? TryGetEfficiencyMode(process)
                        : null;
                    var priorityChanges = previousPriority != policy.TargetPriority;
                    var efficiencyChanges = previousEfficiencyMode is not null &&
                                            previousEfficiencyMode.IsEnabled != targetEfficiencyMode;
                    if (!priorityChanges && !efficiencyChanges)
                    {
                        continue;
                    }

                    baselines.Add(new BackgroundProcessPriorityBaseline
                    {
                        ProcessId = process.Id,
                        Identity = identity,
                        ProcessName = processName,
                        Category = classification.ToString(),
                        PreviousPriority = previousPriority,
                        TargetPriority = policy.TargetPriority,
                        PreviousEfficiencyMode = previousEfficiencyMode,
                        TargetEfficiencyMode = targetEfficiencyMode
                    });
                }
                catch
                {
                }
            }
        }

        return Task.FromResult<IReadOnlyList<BackgroundProcessPriorityBaseline>>(baselines);
    }

    public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        return ApplyCapturedBackgroundPoliciesAsync(foregroundApp, profile, cancellationToken);
    }

    public Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyBackgroundPoliciesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        IReadOnlyList<BackgroundProcessPriorityBaseline> preparedBaselines,
        CancellationToken cancellationToken = default)
    {
        var adjustments = new List<BackgroundProcessAdjustmentResult>(preparedBaselines.Count);
        foreach (var baseline in preparedBaselines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(baseline.ProcessId);
                if (!IsSameIdentity(baseline.Identity, CreateIdentity(process)))
                {
                    adjustments.Add(CreateBackgroundResult(
                        baseline,
                        SchedulerActionStatus.Failed,
                        "Process identity changed before the background policy could be applied."));
                    continue;
                }

                var safety = ProcessPrioritySafetyPolicy.Evaluate(process, baseline.TargetPriority);
                var priorityApplied = baseline.PreviousPriority == baseline.TargetPriority;
                if (!priorityApplied && safety.IsAllowed)
                {
                    process.PriorityClass = MapPriority(baseline.TargetPriority);
                    priorityApplied = true;
                }

                var efficiencyApplied = baseline.PreviousEfficiencyMode is null ||
                                        baseline.PreviousEfficiencyMode.IsEnabled == baseline.TargetEfficiencyMode ||
                                        SetEfficiencyMode(process, baseline.TargetEfficiencyMode);

                var status = priorityApplied && efficiencyApplied
                    ? SchedulerActionStatus.Applied
                    : SchedulerActionStatus.Failed;
                adjustments.Add(CreateBackgroundResult(
                    baseline,
                    status,
                    status == SchedulerActionStatus.Applied
                        ? $"Background policy applied for {baseline.Category}."
                        : "One or more background policy adjustments could not be applied."));
            }
            catch
            {
                adjustments.Add(CreateBackgroundResult(
                    baseline,
                    SchedulerActionStatus.Failed,
                    "Background policy could not be applied."));
            }
        }

        return Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(adjustments);
    }

    private async Task<IReadOnlyList<BackgroundProcessAdjustmentResult>> ApplyCapturedBackgroundPoliciesAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken)
    {
        var baselines = await CaptureBackgroundPolicyBaselinesAsync(foregroundApp, profile, cancellationToken);
        return await ApplyBackgroundPoliciesAsync(foregroundApp, profile, baselines, cancellationToken);
    }

    private static BackgroundProcessAdjustmentResult CreateBackgroundResult(
        BackgroundProcessPriorityBaseline baseline,
        SchedulerActionStatus status,
        string message) =>
        new()
        {
            ProcessId = baseline.ProcessId,
            ProcessName = baseline.ProcessName,
            Category = baseline.Category,
            PreviousPriority = baseline.PreviousPriority,
            AppliedPriority = status == SchedulerActionStatus.Applied
                ? baseline.TargetPriority
                : baseline.PreviousPriority,
            PreviousEfficiencyMode = baseline.PreviousEfficiencyMode?.IsEnabled,
            AppliedEfficiencyMode = status == SchedulerActionStatus.Applied
                ? baseline.TargetEfficiencyMode
                : baseline.PreviousEfficiencyMode?.IsEnabled,
            Status = status,
            Message = message
        };

    private static Task<PriorityAdjustmentResult> ApplyForegroundBoostCore(
        FocusedAppContext app,
        PerformanceProfile profile,
        ProcessIdentitySnapshot? expectedIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedPriority = profile.Priority.ForegroundPriority;

        try
        {
            using var process = Process.GetProcessById(app.ProcessId);
            if (expectedIdentity is not null &&
                !IsSameIdentity(expectedIdentity, CreateIdentity(process)))
            {
                return Task.FromResult(new PriorityAdjustmentResult
                {
                    ProcessId = app.ProcessId,
                    Status = SchedulerActionStatus.Failed,
                    Message = "Foreground process identity changed before the priority adjustment could be applied."
                });
            }

            var previousPriority = MapPriority(process.PriorityClass);
            var safety = ProcessPrioritySafetyPolicy.Evaluate(process, requestedPriority);
            if (!safety.IsAllowed)
            {
                return Task.FromResult(new PriorityAdjustmentResult
                {
                    ProcessId = app.ProcessId,
                    PreviousPriority = previousPriority,
                    AppliedPriority = previousPriority,
                    Status = SchedulerActionStatus.Unsupported,
                    Message = safety.Reason
                });
            }

            if (previousPriority == requestedPriority)
            {
                return Task.FromResult(new PriorityAdjustmentResult
                {
                    ProcessId = app.ProcessId,
                    PreviousPriority = previousPriority,
                    AppliedPriority = previousPriority,
                    Status = SchedulerActionStatus.Skipped,
                    Message = $"Priority already {requestedPriority}."
                });
            }

            process.PriorityClass = MapPriority(requestedPriority);
            return Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = previousPriority,
                AppliedPriority = requestedPriority,
                Status = SchedulerActionStatus.Applied,
                Message = $"Priority set to {requestedPriority}."
            });
        }
        catch
        {
            return Task.FromResult(new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                Status = SchedulerActionStatus.Failed,
                Message = "Priority change failed or was not permitted."
            });
        }
    }

    private static ProcessIdentitySnapshot CreateIdentity(Process process) =>
        new()
        {
            ProcessId = process.Id,
            ProcessName = process.ProcessName,
            StartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            ExecutablePath = TryReadExecutablePath(process) ?? string.Empty
        };

    private static bool IsSameIdentity(ProcessIdentitySnapshot expected, ProcessIdentitySnapshot current) =>
        expected.ProcessId == current.ProcessId &&
        expected.StartedAt == current.StartedAt &&
        string.Equals(expected.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(expected.ExecutablePath) ||
         string.IsNullOrWhiteSpace(current.ExecutablePath) ||
         string.Equals(expected.ExecutablePath, current.ExecutablePath, StringComparison.OrdinalIgnoreCase));

    private static ProcessEfficiencyModeState? TryGetEfficiencyMode(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion
        };
        return GetProcessInformation(
            process.Handle,
            ProcessInformationClass.ProcessPowerThrottling,
            ref state,
            (uint)Marshal.SizeOf<ProcessPowerThrottlingState>())
            ? new ProcessEfficiencyModeState
            {
                IsEnabled = (state.StateMask & ProcessPowerThrottlingExecutionSpeed) != 0
            }
            : null;
    }

    private static bool SetEfficiencyMode(Process process, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingExecutionSpeed,
            StateMask = enabled ? ProcessPowerThrottlingExecutionSpeed : 0
        };
        return SetProcessInformation(
            process.Handle,
            ProcessInformationClass.ProcessPowerThrottling,
            ref state,
            (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
    }

    private static PriorityLevel MapPriority(ProcessPriorityClass priorityClass) =>
        priorityClass switch
        {
            ProcessPriorityClass.Idle => PriorityLevel.Idle,
            ProcessPriorityClass.BelowNormal => PriorityLevel.BelowNormal,
            ProcessPriorityClass.Normal => PriorityLevel.Normal,
            ProcessPriorityClass.AboveNormal => PriorityLevel.AboveNormal,
            ProcessPriorityClass.High => PriorityLevel.High,
            ProcessPriorityClass.RealTime => PriorityLevel.RealTime,
            _ => PriorityLevel.Normal
        };

    private static ProcessPriorityClass MapPriority(PriorityLevel priority) =>
        priority switch
        {
            PriorityLevel.Idle => ProcessPriorityClass.Idle,
            PriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
            PriorityLevel.Normal => ProcessPriorityClass.Normal,
            PriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
            PriorityLevel.High => ProcessPriorityClass.High,
            PriorityLevel.RealTime => ProcessPriorityClass.RealTime,
            _ => ProcessPriorityClass.Normal
        };

    private static string? TryReadExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    private enum ProcessInformationClass
    {
        ProcessPowerThrottling = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        nint processHandle,
        ProcessInformationClass processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        nint processHandle,
        ProcessInformationClass processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);
}
