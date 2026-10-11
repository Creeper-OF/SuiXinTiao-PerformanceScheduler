using System.Diagnostics;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;

namespace PerformanceScheduler.Infrastructure.Processes;

/// <summary>
/// Collects a conservative, read-only inventory of background processes.
/// CPU usage is available after a second sample; GPU usage is queried for eligible candidates;
/// no process state is changed.
/// </summary>
public sealed class WindowsResourceArbitrationCandidateProvider : IResourceArbitrationCandidateProvider
{
    private readonly IWindowsGpuPerformanceCounterSource? _gpuSource;
    private readonly BackgroundPolicySelector _backgroundPolicySelector = new();
    private readonly object _sync = new();
    private Dictionary<int, CpuSample> _cpuSamples = new();

    public WindowsResourceArbitrationCandidateProvider(IWindowsGpuPerformanceCounterSource? gpuSource = null)
    {
        _gpuSource = gpuSource;
    }

    public async Task<IReadOnlyList<ResourceArbitrationCandidate>> CollectAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(foregroundApp);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var observedAt = DateTimeOffset.UtcNow;
        var currentSamples = new Dictionary<int, CpuSample>();
        var candidates = new List<ResourceArbitrationCandidate>();
        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (process)
            {
                try
                {
                    if (process.Id == foregroundApp.ProcessId || process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var processName = process.ProcessName;
                    var executablePath = TryReadExecutablePath(process);
                    var windowTitle = TryReadWindowTitle(process);
                    var classification = ProcessClassificationHelper.Classify(
                        processName,
                        executablePath,
                        windowTitle);
                    var safety = ProcessPrioritySafetyPolicy.Evaluate(process, PriorityLevel.Normal);
                    var isProtected = !safety.IsAllowed;
                    var startedAt = TryReadStartTime(process);
                    var policy = profile is null
                        ? null
                        : _backgroundPolicySelector.Select(classification, profile.BackgroundPolicies);
                    var hasApplicablePolicy = policy is not null &&
                        (profile!.Priority.LowerBackgroundProcesses ||
                         profile.Priority.PreferEfficiencyModeForBackground ||
                         policy.PreferEfficiencyMode);
                    var cpuUsage = TryCalculateCpuUsage(process, observedAt, currentSamples);

                    double? gpuUsage = null;
                    if (_gpuSource is not null && !isProtected && hasApplicablePolicy)
                    {
                        gpuUsage = await TryCollectGpuUsageAsync(process.Id, cancellationToken).ConfigureAwait(false);
                    }

                    candidates.Add(new ResourceArbitrationCandidate
                    {
                        ProcessId = process.Id,
                        ProcessName = processName,
                        StartedAt = startedAt,
                        Classification = classification,
                        IsProtected = isProtected,
                        HasApplicablePolicy = hasApplicablePolicy,
                        CpuUsagePercent = cpuUsage,
                        GpuUsagePercent = gpuUsage
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Process enumeration is best effort. One inaccessible process
                    // must not hide the rest of the read-only inventory.
                }
            }
        }

        lock (_sync)
        {
            _cpuSamples = currentSamples;
        }

        return candidates;
    }

    private async Task<double?> TryCollectGpuUsageAsync(int processId, CancellationToken cancellationToken)
    {
        if (_gpuSource is null)
        {
            return null;
        }

        try
        {
            var snapshot = await _gpuSource.CollectProcessAsync(processId, cancellationToken).ConfigureAwait(false);
            if (snapshot.UsagePercent is { } usage && double.IsFinite(usage) && usage >= 0)
            {
                return Math.Clamp(usage, 0, 100);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private double? TryCalculateCpuUsage(
        Process process,
        DateTimeOffset observedAt,
        IDictionary<int, CpuSample> currentSamples)
    {
        try
        {
            var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var totalProcessorTime = process.TotalProcessorTime;
            var current = new CpuSample(startedAt, totalProcessorTime, observedAt);
            CpuSample? previous;
            lock (_sync)
            {
                _cpuSamples.TryGetValue(process.Id, out previous);
            }

            currentSamples[process.Id] = current;
            if (previous is null || previous.StartedAt != startedAt)
            {
                return null;
            }

            var wallClockSeconds = (observedAt - previous.ObservedAt).TotalSeconds;
            var cpuSeconds = (totalProcessorTime - previous.TotalProcessorTime).TotalSeconds;
            if (wallClockSeconds <= 0 || cpuSeconds < 0)
            {
                return null;
            }

            var usage = cpuSeconds / wallClockSeconds / Math.Max(1, Environment.ProcessorCount) * 100;
            return double.IsFinite(usage) ? Math.Clamp(usage, 0, 100) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadExecutablePath(Process process)
    {
        try
        {
            return WindowsProcessStateReader.TryReadImagePath(process);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static string TryReadWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record CpuSample(
        DateTimeOffset StartedAt,
        TimeSpan TotalProcessorTime,
        DateTimeOffset ObservedAt);
}
