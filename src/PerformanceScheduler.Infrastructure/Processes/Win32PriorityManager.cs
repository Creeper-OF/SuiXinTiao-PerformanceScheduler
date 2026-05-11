using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Infrastructure.Processes;

public sealed class Win32PriorityManager : IPriorityManager
{
    private readonly BackgroundPolicySelector _backgroundPolicySelector = new();

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

    public async Task<PriorityAdjustmentResult> ApplyForegroundBoostAsync(
        FocusedAppContext app,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var previousPriority = await TryGetPriorityAsync(app.ProcessId, cancellationToken);
        var requestedPriority = profile.Priority.ForegroundPriority;
        var safety = ProcessPrioritySafetyPolicy.Evaluate(app, requestedPriority);
        if (!safety.IsAllowed)
        {
            return new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = previousPriority,
                AppliedPriority = previousPriority,
                Status = SchedulerActionStatus.Unsupported,
                Message = safety.Reason
            };
        }

        if (previousPriority == requestedPriority)
        {
            return new PriorityAdjustmentResult
            {
                ProcessId = app.ProcessId,
                PreviousPriority = previousPriority,
                AppliedPriority = previousPriority,
                Status = SchedulerActionStatus.Skipped,
                Message = $"Priority already {requestedPriority}."
            };
        }

        var success = await SetPriorityAsync(app.ProcessId, requestedPriority, cancellationToken);

        return new PriorityAdjustmentResult
        {
            ProcessId = app.ProcessId,
            PreviousPriority = previousPriority,
            AppliedPriority = success ? requestedPriority : previousPriority,
            Status = success ? SchedulerActionStatus.Applied : SchedulerActionStatus.Failed,
            Message = success
                ? $"Priority set to {requestedPriority}."
                : "Priority change failed or was not permitted."
        };
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

                    var previousPriority = MapPriority(process.PriorityClass);
                    if (previousPriority == policy.TargetPriority)
                    {
                        continue;
                    }

                    baselines.Add(new BackgroundProcessPriorityBaseline
                    {
                        ProcessId = process.Id,
                        ProcessName = processName,
                        Category = classification.ToString(),
                        PreviousPriority = previousPriority,
                        TargetPriority = policy.TargetPriority
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
        if (!profile.Priority.LowerBackgroundProcesses || profile.BackgroundPolicies.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(Array.Empty<BackgroundProcessAdjustmentResult>());
        }

        var adjustments = new List<BackgroundProcessAdjustmentResult>();
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
                        adjustments.Add(new BackgroundProcessAdjustmentResult
                        {
                            ProcessId = process.Id,
                            ProcessName = processName,
                            Category = classification.ToString(),
                            Status = SchedulerActionStatus.Unsupported,
                            Message = safety.Reason
                        });
                        continue;
                    }

                    var previousPriority = MapPriority(process.PriorityClass);
                    if (previousPriority == policy.TargetPriority)
                    {
                        continue;
                    }

                    process.PriorityClass = MapPriority(policy.TargetPriority);
                    adjustments.Add(new BackgroundProcessAdjustmentResult
                    {
                        ProcessId = process.Id,
                        ProcessName = processName,
                        Category = classification.ToString(),
                        PreviousPriority = previousPriority,
                        AppliedPriority = policy.TargetPriority,
                        Status = SchedulerActionStatus.Applied,
                        Message = $"Background policy applied for {classification}."
                    });
                }
                catch
                {
                    adjustments.Add(new BackgroundProcessAdjustmentResult
                    {
                        ProcessId = SafeProcessId(process),
                        ProcessName = SafeProcessName(process),
                        Category = "Unknown",
                        Status = SchedulerActionStatus.Failed,
                        Message = "Background policy could not be applied."
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyList<BackgroundProcessAdjustmentResult>>(adjustments);
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
}
