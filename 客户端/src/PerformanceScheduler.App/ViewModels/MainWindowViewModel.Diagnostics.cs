using System.Collections.ObjectModel;
using Microsoft.Win32;
using PerformanceScheduler.App.Commands;
using PerformanceScheduler.App.Services.Telemetry;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isSelfTestRunning;
    private string _selfTestStatusSummary = string.Empty;
    private bool _hasSelfTestResults;

    public bool IsSelfTestRunning
    {
        get => _isSelfTestRunning;
        private set
        {
            if (SetProperty(ref _isSelfTestRunning, value))
            {
                (RunFullSelfTestCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                (RunGpuSelfTestCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                (RunPowerEmergencySelfTestCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelfTestStatusSummary
    {
        get => string.IsNullOrEmpty(_selfTestStatusSummary) ? T("Status.SelfTestReady") : _selfTestStatusSummary;
        private set => SetProperty(ref _selfTestStatusSummary, value);
    }

    public bool HasSelfTestResults
    {
        get => _hasSelfTestResults;
        private set => SetProperty(ref _hasSelfTestResults, value);
    }

    public ObservableCollection<string> SelfTestResultLines { get; } = new();

    private async Task RunFullSelfTestAsync()
    {
        if (_selfTestService == null || IsSelfTestRunning) return;
        IsSelfTestRunning = true;
        SelfTestResultLines.Clear();
        SelfTestStatusSummary = T("Status.SelfTestRunning");
        HasSelfTestResults = true;

        try
        {
            var report = await _selfTestService.RunFullDiagnosticSelfTestAsync();
            SelfTestStatusSummary = $"{T("Status.SelfTestCompleted")} [{report.OverallStatus}] ({report.TotalDurationMilliseconds}ms)";
            SelfTestResultLines.Add($"⚡ {T("Section.SelfTestGpu")}: {(report.GpuResult.Skipped ? "SKIPPED" : report.GpuResult.Supported ? (report.GpuResult.IsSuccess ? "PASS" : "FAIL") : "UNSUPPORTED")} - {report.GpuResult.Message}");
            SelfTestResultLines.Add($"🛡️ {T("Section.SelfTestEmergency")}: {(report.EmergencyGateResult.IsSuccess ? "PASS" : "FAIL")} - {report.EmergencyGateResult.Message}");
            SelfTestResultLines.Add($"🔋 {T("Section.SelfTestPowerPlan")}: {(report.PowerPlanResult.IsSuccess ? "PASS" : "FAIL")} - {report.PowerPlanResult.Message}");
            SelfTestResultLines.Add($"🔒 {T("Section.SelfTestSafety")}: {(report.ProcessSafetyResult.IsSuccess ? "PASS" : "FAIL")} - {report.ProcessSafetyResult.Message}");
        }
        catch (Exception ex)
        {
            SelfTestStatusSummary = $"{T("Status.SelfTestFailed")}: {ex.Message}";
            SelfTestResultLines.Add($"ERROR: {ex.Message}");
        }
        finally
        {
            IsSelfTestRunning = false;
        }
    }

    private async Task RunGpuSelfTestAsync()
    {
        if (_selfTestService == null || IsSelfTestRunning) return;
        IsSelfTestRunning = true;
        SelfTestStatusSummary = T("Status.SelfTestRunning");
        HasSelfTestResults = true;

        try
        {
            var res = await _selfTestService.TestGpuClockLockAndResetAsync();
            SelfTestStatusSummary = $"GPU: {(res.Skipped ? "SKIPPED" : res.Supported ? (res.IsSuccess ? "PASS" : "FAIL") : "UNSUPPORTED")} ({res.DurationMilliseconds}ms)";
            SelfTestResultLines.Insert(0, $"⚡ [{DateTime.Now:HH:mm:ss}] GPU: {res.Message}");
        }
        catch (Exception ex)
        {
            SelfTestStatusSummary = $"{T("Status.SelfTestFailed")}: {ex.Message}";
            SelfTestResultLines.Insert(0, $"ERROR: {ex.Message}");
        }
        finally
        {
            IsSelfTestRunning = false;
        }
    }

    private async Task RunPowerEmergencySelfTestAsync()
    {
        if (_selfTestService == null || IsSelfTestRunning) return;
        IsSelfTestRunning = true;
        SelfTestStatusSummary = T("Status.SelfTestRunning");
        HasSelfTestResults = true;

        try
        {
            var res = await _selfTestService.TestPowerEmergencyGateSimulationAsync();
            SelfTestStatusSummary = $"Emergency Gate: {(res.IsSuccess ? "PASS" : "FAIL")} ({res.DurationMilliseconds}ms)";
            SelfTestResultLines.Insert(0, $"🛡️ [{DateTime.Now:HH:mm:ss}] Emergency Gate: {res.Message}");
        }
        catch (Exception ex)
        {
            SelfTestStatusSummary = $"{T("Status.SelfTestFailed")}: {ex.Message}";
            SelfTestResultLines.Insert(0, $"ERROR: {ex.Message}");
        }
        finally
        {
            IsSelfTestRunning = false;
        }
    }

    private const int RuntimeHistoryLimit = 50;
    private const int TelemetryHistoryLoadLimit = 50;
    private const int TelemetryHistoryDisplayLimit = 5;
    private string _selectedRuntimeHistoryDetail = string.Empty;
    private string _diagnosticPackageStatus = string.Empty;
    private string _diagnosticPackageStatusKey = "Status.DiagnosticPackageReady";
    private object[] _diagnosticPackageStatusArgs = [];
    private string _telemetryObservationSummary = string.Empty;
    private string _telemetryObservationSummaryKey = "Status.TelemetryObservationReady";
    private object[] _telemetryObservationSummaryArgs = [];
    private IReadOnlyList<TelemetryObservationHistoryEntry> _telemetryHistoryEntries =
        Array.Empty<TelemetryObservationHistoryEntry>();
    private bool _telemetryHistoryLoadFailed;
    private IReadOnlyList<TelemetryProviderCapabilities> _telemetryProviderCapabilities =
        Array.Empty<TelemetryProviderCapabilities>();
    private bool _telemetryCapabilityLoadFailed;
    private IReadOnlyList<TelemetryTrendSummary> _lastTelemetryTrends =
        Array.Empty<TelemetryTrendSummary>();
    private readonly TelemetryAnalysisEvaluator _telemetryAnalysisEvaluator = new();

    public string SelectedRuntimeHistoryDetail
    {
        get => _selectedRuntimeHistoryDetail;
        private set => SetProperty(ref _selectedRuntimeHistoryDetail, value);
    }

    public string DiagnosticPackageStatus
    {
        get => _diagnosticPackageStatus;
        private set => SetProperty(ref _diagnosticPackageStatus, value);
    }

    public string TelemetryObservationSummary
    {
        get => _telemetryObservationSummary;
        private set => SetProperty(ref _telemetryObservationSummary, value);
    }

    private async Task RefreshTelemetryCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _telemetryProviderCapabilities = await _telemetryObservationService
                .GetProviderCapabilitiesAsync(cancellationToken);
            _telemetryCapabilityLoadFailed = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Telemetry capability detection could not be refreshed.", exception);
            _telemetryProviderCapabilities = Array.Empty<TelemetryProviderCapabilities>();
            _telemetryCapabilityLoadFailed = true;
        }

        RebuildTelemetryCapabilityLines();
    }

    private void RebuildTelemetryCapabilityLines()
    {
        TelemetryCapabilityLines.Clear();
        if (_telemetryCapabilityLoadFailed)
        {
            TelemetryCapabilityLines.Add(T("Capability.TelemetryDetectionFailed"));
            return;
        }

        foreach (var capability in _telemetryProviderCapabilities)
        {
            var metrics = capability.SupportedMetrics.Count == 0
                ? T("Status.Unknown")
                : string.Join(", ", capability.SupportedMetrics.Select(metric =>
                    T($"Enum.TelemetryMetricKind.{metric}")));
            var overhead = T($"Enum.TelemetryOverheadClass.{capability.EstimatedOverhead}");
            if (capability.IsSupported)
            {
                TelemetryCapabilityLines.Add(F(
                    "Capability.TelemetryProviderSupportedFormat",
                    capability.ProviderId,
                    metrics,
                    capability.MinimumSamplingInterval.TotalSeconds,
                    overhead));
                continue;
            }

            var reason = string.IsNullOrWhiteSpace(capability.UnavailableReasonCode)
                ? T("Telemetry.ProviderUnsupported")
                : T(capability.UnavailableReasonCode);
            if (capability.UnavailableReasonCode is
                TelemetryReasonCodes.CapabilityTimeout or
                TelemetryReasonCodes.CapabilityFailed or
                TelemetryReasonCodes.TotalBudgetExceeded)
            {
                TelemetryCapabilityLines.Add(F(
                    "Capability.TelemetryProviderUnavailableFormat",
                    capability.ProviderId,
                    reason));
                continue;
            }

            TelemetryCapabilityLines.Add(F(
                "Capability.TelemetryProviderUnsupportedFormat",
                capability.ProviderId,
                reason,
                capability.MinimumSamplingInterval.TotalSeconds,
                overhead));
        }
    }

    private async Task CaptureTelemetryObservationAsync()
    {
        var result = await _telemetryObservationService.CaptureAsync(
            _lastRunResult?.MatchResult?.Profile);
        TelemetryObservationLines.Clear();
        if (result.Status == ForegroundTelemetryObservationStatus.ForegroundUnavailable)
        {
            SetTelemetryTrends(Array.Empty<TelemetryTrendSummary>());
            SetTelemetryObservationSummary("Status.TelemetryForegroundUnavailable");
            return;
        }

        if (result.Status == ForegroundTelemetryObservationStatus.ProcessUnavailable ||
            result.Target is null ||
            result.Observation is null)
        {
            SetTelemetryTrends(Array.Empty<TelemetryTrendSummary>());
            SetTelemetryObservationSummary("Status.TelemetryProcessUnavailable");
            return;
        }

        foreach (var sample in result.Observation.ProviderSamples)
        {
            var metrics = sample.Status == TelemetryCollectionStatus.Collected
                ? sample.Metrics
                : Array.Empty<TelemetryMetricValue>();
            if (metrics.Count == 0)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetryProviderStatusFormat",
                    sample.ProviderId,
                    T($"Enum.TelemetryCollectionStatus.{sample.Status}"),
                    T(sample.ReasonCode)));
                continue;
            }

            TelemetryObservationLines.Add(F(
                "Status.TelemetryProviderCollectedFormat",
                sample.ProviderId,
                metrics.Count,
                sample.CollectionDuration.TotalMilliseconds));

            foreach (var metric in metrics)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetryMetricFormat",
                    T($"Enum.TelemetryMetricKind.{metric.Kind}"),
                    sample.ProviderId,
                    metric.Value,
                    metric.Unit));
            }
        }

        var latestSuccessfulRun = RuntimeHistoryItems
            .Select(item => item.Entry)
            .Where(entry =>
                entry.Success &&
                entry.OccurredAt < result.Observation.StartedAt &&
                string.Equals(
                    entry.ProcessName,
                    result.Target.ProcessName,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.OccurredAt)
            .FirstOrDefault();
        var targetExperience = ResolveTargetExperiencePreference(result.Target);
        var analysis = _telemetryAnalysisEvaluator.Evaluate(
            result.Observation,
            _telemetryHistoryEntries,
            latestSuccessfulRun,
            targetExperience,
            result.ResourceArbitrationCandidates);
        SetTelemetryTrends(analysis.Trends);

        TelemetryObservationLines.Add(analysis.ResourcePressure.HasData
            ? F(
                "Status.TelemetryResourcePressureFormat",
                T($"Enum.ResourcePressureLevel.{analysis.ResourcePressure.Level}"),
                FormatTelemetryPercent(analysis.ResourcePressure.SystemCpuUsagePercent),
                FormatTelemetryPercent(analysis.ResourcePressure.SystemGpuUsagePercent))
            : T("Status.TelemetryResourcePressureUnavailable"));

        TelemetryObservationLines.Add(F(
            "Status.TelemetryResourceContentionFormat",
            T($"Enum.ResourceContentionLevel.{analysis.ResourceContention.Level}"),
            FormatTelemetryPercent(analysis.ResourceContention.ForegroundCpuUsagePercent),
            FormatTelemetryPercent(analysis.ResourceContention.SystemCpuUsagePercent),
            FormatTelemetryPercent(analysis.ResourceContention.ForegroundGpuUsagePercent),
            FormatTelemetryPercent(analysis.ResourceContention.SystemGpuUsagePercent)));

        TelemetryObservationLines.Add(F(
            "Status.TelemetryResourceAdvisoryFormat",
            T($"Enum.ResourceAdvisoryKind.{analysis.ResourceAdvisory.Kind}")));

        TelemetryObservationLines.Add(F(
            "Status.TelemetryResourceArbitrationFormat",
            T($"Enum.ResourceArbitrationState.{analysis.ResourceArbitration.State}"),
            analysis.ResourceArbitration.CandidateCount,
            analysis.ResourceArbitration.MeasuredCandidateCount,
            analysis.ResourceArbitration.ProtectedCandidateCount));

        if (analysis.TargetExperience.IsConfigured)
        {
            TelemetryObservationLines.Add(F(
                "Status.TelemetryTargetExperienceFormat",
                T($"Enum.TargetExperienceState.{analysis.TargetExperience.State}"),
                T(analysis.TargetExperience.ReasonKey)));
            foreach (var constraint in analysis.TargetExperience.Constraints)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetryTargetConstraintFormat",
                    T($"Enum.TelemetryMetricKind.{constraint.MetricKind}"),
                    T($"Enum.TargetExperienceConstraintStatus.{constraint.Status}"),
                    FormatTelemetryValue(constraint.ActualValue),
                    FormatTelemetryValue(constraint.TargetValue)));
            }

            TelemetryObservationLines.Add(F(
                "Status.TelemetryTargetControlFormat",
                T($"Enum.TargetExperienceControlState.{analysis.TargetExperienceControl.State}"),
                analysis.TargetExperienceControl.EvidenceSampleCount,
                analysis.TargetExperienceControl.StableSampleCount,
                T(analysis.TargetExperienceControl.ReasonKey)));

            if (targetExperience?.ControlMode == TargetExperienceControlMode.Automatic &&
                analysis.TargetExperienceControl.Recommendation?.CanApplyAutomatically == true)
            {
                try
                {
                    var adaptiveResult = await _schedulerOrchestrator.AdaptTargetExperienceAsync(
                        analysis.TargetExperienceControl);
                    if (adaptiveResult is { Applied: true, Plan: { } plan })
                    {
                        TelemetryObservationLines.Add(F(
                            "Status.TelemetryTargetExperienceAdaptiveFormat",
                            plan.CurrentProcessorMaxPercent,
                            plan.TargetProcessorMaxPercent,
                            T(plan.ReasonKey)));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Adaptive target experience execution failed: {ex.Message}");
                }
            }
        }

        var comparison = analysis.Comparison;
        if (!comparison.HasPreviousObservation)
        {
            TelemetryObservationLines.Add(T("Status.TelemetryComparisonNoPrevious"));
        }
        else if (!comparison.HasComparableMetrics)
        {
            TelemetryObservationLines.Add(T("Status.TelemetryComparisonNoSharedMetrics"));
        }
        else
        {
            const int comparisonDisplayLimit = 8;
            foreach (var delta in comparison.Deltas.Take(comparisonDisplayLimit))
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetryComparisonMetricFormat",
                    T($"Enum.TelemetryMetricKind.{delta.Kind}"),
                    delta.ProviderId,
                    delta.Delta,
                    delta.Unit));
            }

            if (comparison.Deltas.Count > comparisonDisplayLimit)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetryComparisonMoreFormat",
                    comparisonDisplayLimit,
                    comparison.Deltas.Count));
            }
        }

        var schedulingComparison = analysis.SchedulingComparison;
        if (schedulingComparison.HasEligibleRun)
        {
            var profileName = string.IsNullOrWhiteSpace(schedulingComparison.ProfileName)
                ? T("Status.Unknown")
                : schedulingComparison.ProfileName;
            if (!schedulingComparison.HasBeforeObservation ||
                !schedulingComparison.HasAfterObservation)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetrySchedulingComparisonInsufficient",
                    profileName));
            }
            else if (!schedulingComparison.HasComparableMetrics)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetrySchedulingComparisonNoSharedMetrics",
                    profileName));
            }
            else
            {
                const int schedulingComparisonDisplayLimit = 4;
                foreach (var delta in schedulingComparison.Deltas.Take(schedulingComparisonDisplayLimit))
                {
                    TelemetryObservationLines.Add(F(
                        "Status.TelemetrySchedulingComparisonMetricFormat",
                        T($"Enum.TelemetryMetricKind.{delta.Kind}"),
                        delta.ProviderId,
                        delta.Delta,
                        delta.Unit));
                }

                if (schedulingComparison.Deltas.Count > schedulingComparisonDisplayLimit)
                {
                    TelemetryObservationLines.Add(F(
                        "Status.TelemetrySchedulingComparisonMoreFormat",
                        schedulingComparisonDisplayLimit,
                        schedulingComparison.Deltas.Count));
                }
            }
        }

        const int trendDisplayLimit = 4;
        var trends = analysis.Trends;
        foreach (var trend in trends.Take(trendDisplayLimit))
        {
            TelemetryObservationLines.Add(F(
                "Status.TelemetryTrendMetricFormat",
                trend.SampleCount,
                T($"Enum.TelemetryMetricKind.{trend.Kind}"),
                trend.ProviderId,
                trend.AverageValue,
                trend.MinimumValue,
                trend.MaximumValue,
                T($"Enum.TelemetryTrendDirection.{trend.Direction}"),
                trend.Unit));
        }

        if (trends.Count > trendDisplayLimit)
        {
            TelemetryObservationLines.Add(F(
                "Status.TelemetryTrendMoreFormat",
                trendDisplayLimit,
                trends.Count));
        }

        SetTelemetryObservationSummary(
            result.Observation.HasMetrics
                ? "Status.TelemetryObservationCollected"
                : "Status.TelemetryObservationNoMetrics",
            result.Target.ProcessName,
            result.Observation.TotalDuration.TotalMilliseconds);
        if (result.HistoryRecorded is { } historyRecorded)
        {
            TelemetryObservationLines.Add(T(historyRecorded
                ? "Status.TelemetryHistorySaved"
                : "Status.TelemetryHistorySaveFailed"));
            if (historyRecorded)
            {
                await LoadTelemetryHistoryAsync(CancellationToken.None);
            }
        }
    }

    private string FormatTelemetryPercent(double? value) =>
        value is double percentage
            ? percentage.ToString("F0")
            : T("Status.Unknown");

    private string FormatTelemetryValue(double? value) =>
        value is double measured
            ? measured.ToString("F1")
            : T("Status.Unknown");

    private TargetExperiencePreference? ResolveTargetExperiencePreference(FocusedAppContext target)
    {
        var lastMatch = _lastRunResult?.MatchResult;
        if (_lastRunResult?.ActiveApp is not null &&
            lastMatch is not null &&
            _lastRunResult.ActiveApp.ProcessId == target.ProcessId &&
            string.Equals(
                _lastRunResult.ActiveApp.ProcessName,
                target.ProcessName,
                StringComparison.OrdinalIgnoreCase))
        {
            return lastMatch.Profile.TargetExperience;
        }

        return null;
    }

    private void SetTelemetryTrends(IEnumerable<TelemetryTrendSummary> trends)
    {
        _lastTelemetryTrends = trends.ToArray();
        RebuildTelemetryTrendItems();
    }

    private void RebuildTelemetryTrendItems()
    {
        TelemetryTrendItems.Clear();
        foreach (var trend in _lastTelemetryTrends.Take(4))
        {
            var scale = new[] { trend.MinimumValue, trend.MaximumValue, trend.LatestValue }
                .Where(double.IsFinite)
                .Select(Math.Abs)
                .DefaultIfEmpty(0)
                .Max();
            var ratio = scale <= 0
                ? 0
                : Math.Clamp(Math.Abs(trend.LatestValue) / scale * 100, 0, 100);

            TelemetryTrendItems.Add(new TelemetryTrendItemViewModel
            {
                MetricName = T($"Enum.TelemetryMetricKind.{trend.Kind}"),
                ProviderName = trend.ProviderId,
                Summary = F(
                    "Status.TelemetryTrendChartValueFormat",
                    trend.AverageValue,
                    trend.MinimumValue,
                    trend.MaximumValue,
                    trend.Unit),
                Direction = T($"Enum.TelemetryTrendDirection.{trend.Direction}"),
                DirectionColor = trend.Direction switch
                {
                    TelemetryTrendDirection.Rising => "#FFD97706",
                    TelemetryTrendDirection.Falling => "#FF2E9AAE",
                    TelemetryTrendDirection.Stable => "#FF35A66F",
                    _ => "#FF7B8794"
                },
                LatestRatio = ratio
            });
        }

        OnPropertyChanged(nameof(HasTelemetryTrendItems));
    }

    private void SetTelemetryObservationSummary(string key, params object[] args)
    {
        _telemetryObservationSummaryKey = key;
        _telemetryObservationSummaryArgs = args;
        ApplyTelemetryObservationSummary();
    }

    private void ApplyTelemetryObservationSummary() =>
        TelemetryObservationSummary = FormatByKey(
            _telemetryObservationSummaryKey,
            _telemetryObservationSummaryArgs);

    private async Task LoadTelemetryHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            _telemetryHistoryEntries = await _telemetryObservationService.LoadRecentAsync(
                TelemetryHistoryLoadLimit,
                cancellationToken);
            _telemetryHistoryLoadFailed = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Telemetry observation history could not be loaded.", exception);
            _telemetryHistoryEntries = Array.Empty<TelemetryObservationHistoryEntry>();
            _telemetryHistoryLoadFailed = true;
        }

        RebuildTelemetryHistoryLines();
    }

    private void RebuildTelemetryHistoryLines()
    {
        TelemetryHistoryLines.Clear();
        if (_telemetryHistoryLoadFailed)
        {
            TelemetryHistoryLines.Add(T("Status.TelemetryHistoryLoadFailed"));
            return;
        }

        if (_telemetryHistoryEntries.Count == 0)
        {
            TelemetryHistoryLines.Add(T("Status.TelemetryHistoryEmpty"));
            return;
        }

        foreach (var entry in _telemetryHistoryEntries.Take(TelemetryHistoryDisplayLimit))
        {
            var metricCount = entry.Observation.ProviderSamples
                .Where(sample => sample.Status == TelemetryCollectionStatus.Collected)
                .Sum(sample => sample.Metrics.Count);
            var analysis = _telemetryAnalysisEvaluator.Evaluate(
                entry.Observation,
                Array.Empty<TelemetryObservationHistoryEntry>());
            var pressure = entry.ResourcePressure ?? analysis.ResourcePressure;
            var contention = entry.ResourceContention ?? analysis.ResourceContention;
            var advisory = entry.ResourceAdvisory ?? analysis.ResourceAdvisory;
            TelemetryHistoryLines.Add(F(
                "Status.TelemetryHistoryItemFormat",
                entry.Observation.StartedAt.LocalDateTime,
                entry.Observation.Target.ProcessName,
                entry.Observation.ProviderSamples.Count,
                metricCount,
                T($"Enum.ResourcePressureLevel.{pressure.Level}"),
                T($"Enum.ResourceContentionLevel.{contention.Level}"),
                T($"Enum.ResourceAdvisoryKind.{advisory.Kind}")));
        }

        if (_telemetryHistoryEntries.Count > TelemetryHistoryDisplayLimit)
        {
            TelemetryHistoryLines.Add(F(
                "Status.TelemetryHistoryMoreFormat",
                TelemetryHistoryDisplayLimit,
                _telemetryHistoryEntries.Count));
        }
    }

    private async Task ExportDiagnosticPackageAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Diagnostic Package (*.sxtdiag.zip)|*.sxtdiag.zip|ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*",
            FileName = $"suixindiao-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.sxtdiag.zip",
            Title = T("Dialog.ExportDiagnosticPackageTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _diagnosticPackageService.ExportAsync(dialog.FileName);
            _logger.Info($"Redacted diagnostic package exported to {dialog.FileName}.");
            SetDiagnosticPackageStatus("Status.DiagnosticPackageExported", dialog.FileName);
            AddStatusCenterEvent(
                "Section.Diagnostics",
                "Status.DiagnosticPackageExported",
                [dialog.FileName],
                "Success");
        }
        catch (Exception exception)
        {
            _logger.Error("Diagnostic package export failed.", exception);
            SetDiagnosticPackageStatus("Status.DiagnosticPackageExportFailed");
            AddStatusCenterEvent(
                "Section.Diagnostics",
                "Status.DiagnosticPackageExportFailed",
                [],
                "Error");
        }
    }

    private void SetDiagnosticPackageStatus(string key, params object[] args)
    {
        _diagnosticPackageStatusKey = key;
        _diagnosticPackageStatusArgs = args;
        ApplyDiagnosticPackageStatus();
    }

    private void ApplyDiagnosticPackageStatus() =>
        DiagnosticPackageStatus = FormatByKey(_diagnosticPackageStatusKey, _diagnosticPackageStatusArgs);

    private async Task LoadRuntimeHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var history = await _runtimeStateStore.LoadRecentRunsAsync(
                RuntimeHistoryLimit,
                cancellationToken);
            RebuildRuntimeHistoryItems(history, selectedRunId: null, selectedStorageId: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Runtime history could not be loaded. Diagnostics will continue without history.", exception);
            RuntimeHistoryItems.Clear();
            SelectedRuntimeHistory = null;
            ActionAuditItems.Clear();
            ActionAuditSummary = T("Status.ActionAuditLoadFailed");
            RefreshRuntimeHistoryDetail(null);
        }
    }

    private void AddRuntimeHistoryResult(SchedulerRunResult result)
    {
        var entry = new SchedulerRunHistoryEntry
        {
            RunId = result.RunId,
            TransactionId = result.TransactionId,
            OccurredAt = result.OccurredAt,
            ProcessName = result.ActiveApp?.ProcessName ?? string.Empty,
            WindowTitle = result.ActiveApp?.WindowTitle ?? string.Empty,
            ProfileId = result.MatchResult?.Profile.Id ?? string.Empty,
            ProfileName = result.MatchResult?.Profile.Name ?? string.Empty,
            PowerPlanGuid = result.ActivePowerPlan?.SchemeGuid,
            PowerPlanName = result.ActivePowerPlan?.Name ?? string.Empty,
            Priority = result.PriorityChange?.AppliedPriority?.ToString() ?? string.Empty,
            Success = result.Success,
            IsRetryDeferred = result.IsRetryDeferred,
            ConsecutiveFailureCount = result.ConsecutiveFailureCount,
            RetryAfter = result.RetryAfter,
            FailureRecoveryCompleted = result.FailureRecoveryCompleted,
            Summary = result.Summary,
            Actions = result.Actions
        };

        var duplicate = RuntimeHistoryItems.FirstOrDefault(item => item.Entry.RunId == result.RunId);
        if (duplicate is not null)
        {
            RuntimeHistoryItems.Remove(duplicate);
        }

        var item = CreateRuntimeHistoryItem(entry);
        RuntimeHistoryItems.Insert(0, item);
        while (RuntimeHistoryItems.Count > RuntimeHistoryLimit)
        {
            RuntimeHistoryItems.RemoveAt(RuntimeHistoryItems.Count - 1);
        }

        SelectedRuntimeHistory = item;
    }

    private void RelocalizeRuntimeHistory()
    {
        var entries = RuntimeHistoryItems.Select(item => item.Entry).ToArray();
        var selectedRunId = SelectedRuntimeHistory?.Entry.RunId;
        var selectedStorageId = SelectedRuntimeHistory?.Entry.StorageId;
        RebuildRuntimeHistoryItems(entries, selectedRunId, selectedStorageId);
    }

    private void RebuildRuntimeHistoryItems(
        IReadOnlyList<SchedulerRunHistoryEntry> entries,
        Guid? selectedRunId,
        long? selectedStorageId)
    {
        SelectedRuntimeHistory = null;
        RuntimeHistoryItems.Clear();
        foreach (var entry in entries)
        {
            RuntimeHistoryItems.Add(CreateRuntimeHistoryItem(entry));
        }

        SelectedRuntimeHistory = RuntimeHistoryItems.FirstOrDefault(item =>
                                     selectedRunId is not null && item.Entry.RunId == selectedRunId) ??
                                 RuntimeHistoryItems.FirstOrDefault(item =>
                                     selectedRunId is null &&
                                     selectedStorageId is > 0 &&
                                     item.Entry.StorageId == selectedStorageId) ??
                                 RuntimeHistoryItems.FirstOrDefault();

        if (SelectedRuntimeHistory is null)
        {
            ActionAuditItems.Clear();
            ActionAuditSummary = T("Status.ActionAuditEmpty");
            RefreshRuntimeHistoryDetail(null);
        }
    }

    private SchedulerRunHistoryItemViewModel CreateRuntimeHistoryItem(SchedulerRunHistoryEntry entry)
    {
        var applicationName = !string.IsNullOrWhiteSpace(entry.WindowTitle)
            ? entry.WindowTitle
            : !string.IsNullOrWhiteSpace(entry.ProcessName)
                ? entry.ProcessName
                : T("Status.Unknown");
        var profileName = string.IsNullOrWhiteSpace(entry.ProfileName)
            ? T("Status.NoProfileMatched")
            : entry.ProfileName;
        var unsupportedCount = SchedulerResultStatusEvaluator.CountUnsupportedActions(entry.Actions);
        var status = entry.Success
            ? unsupportedCount > 0
                ? T("Status.RuntimeHistoryPartial")
                : T("Status.RuntimeHistorySuccess")
            : T("Status.RuntimeHistoryFailed");
        return new SchedulerRunHistoryItemViewModel
        {
            Entry = entry,
            DisplayName = F(
                "Status.RuntimeHistoryDisplayFormat",
                entry.OccurredAt.LocalDateTime,
                applicationName,
                profileName,
                status,
                entry.Actions.Count)
        };
    }

    private void RefreshActionAudit(IReadOnlyList<SchedulerActionResult> actions)
    {
        ActionAuditItems.Clear();
        foreach (var action in actions)
        {
            var kind = T($"Enum.SchedulerActionKind.{action.Plan.Kind}");
            var status = T($"Enum.SchedulerActionStatus.{action.Status}");
            var hasStructuredReason = action.ReasonCode != SchedulerActionReasonCode.None;
            var originalValue = string.IsNullOrWhiteSpace(action.Plan.OriginalValue)
                ? T("Status.ActionValueUnavailable")
                : action.Plan.OriginalValue;
            var targetValue = string.IsNullOrWhiteSpace(action.Plan.TargetValue)
                ? T("Status.ActionValueUnavailable")
                : action.Plan.TargetValue;
            ActionAuditItems.Add(new SchedulerActionItemViewModel
            {
                CompletedAt = action.CompletedAt,
                Kind = kind,
                Status = status,
                Target = string.IsNullOrWhiteSpace(action.Plan.Target)
                    ? T("Status.ActionTargetUnavailable")
                    : action.Plan.Target,
                ValueChange = F("Status.ActionValueChangeFormat", originalValue, targetValue),
                Message = hasStructuredReason
                    ? T($"SchedulerActionReason.{action.ReasonCode}")
                    : F("Status.ActionOutcomeFormat", kind, status),
                Suggestion = hasStructuredReason
                    ? T($"SchedulerActionSuggestion.{action.ReasonCode}")
                    : string.Empty,
                TechnicalMessage = hasStructuredReason
                    ? F("Status.ActionTechnicalDetailFormat", action.ReasonCode, action.Message)
                    : action.Message,
                Recovery = action.Plan.RequiresRollback
                    ? T("Status.ActionRecoveryRecorded")
                    : T("Status.ActionRecoveryNotRequired")
            });
        }

        ActionAuditSummary = ActionAuditItems.Count == 0
            ? T("Status.ActionAuditEmpty")
            : F("Status.ActionAuditCountFormat", ActionAuditItems.Count);
    }

    private void RefreshRuntimeHistoryDetail(SchedulerRunHistoryEntry? entry)
    {
        if (entry is null)
        {
            SelectedRuntimeHistoryDetail = T("Status.RuntimeHistoryNoSelection");
            return;
        }

        if (entry.Success)
        {
            var unsupportedCount = SchedulerResultStatusEvaluator.CountUnsupportedActions(entry.Actions);
            SelectedRuntimeHistoryDetail = unsupportedCount > 0
                ? F("Status.RuntimeHistoryPartialDetailFormat", unsupportedCount)
                : T("Status.RuntimeHistorySuccessDetail");
            return;
        }

        var retry = entry.RetryAfter is not { } retryAfter
            ? T("Status.RuntimeHistoryRetryNotScheduled")
            : entry.IsRetryDeferred
                ? F("Status.RuntimeHistoryRetryDeferredAtFormat", retryAfter.LocalDateTime)
                : F("Status.RuntimeHistoryRetryAtFormat", retryAfter.LocalDateTime);
        var recovery = entry.FailureRecoveryCompleted switch
        {
            true => T("Status.RuntimeHistoryRecoveryCompleted"),
            false => T("Status.RuntimeHistoryRecoveryPending"),
            null => T("Status.RuntimeHistoryRecoveryUnknown")
        };
        SelectedRuntimeHistoryDetail = F(
            "Status.RuntimeHistoryFailureDetailFormat",
            entry.ConsecutiveFailureCount,
            retry,
            recovery);
    }
}
