using Microsoft.Win32;
using PerformanceScheduler.App.Services.Telemetry;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int RuntimeHistoryLimit = 50;
    private const int TelemetryHistoryLimit = 5;
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

    private async Task CaptureTelemetryObservationAsync()
    {
        var result = await _telemetryObservationService.CaptureAsync();
        TelemetryObservationLines.Clear();
        if (result.Status == ForegroundTelemetryObservationStatus.ForegroundUnavailable)
        {
            SetTelemetryObservationSummary("Status.TelemetryForegroundUnavailable");
            return;
        }

        if (result.Status == ForegroundTelemetryObservationStatus.ProcessUnavailable ||
            result.Target is null ||
            result.Observation is null)
        {
            SetTelemetryObservationSummary("Status.TelemetryProcessUnavailable");
            return;
        }

        foreach (var sample in result.Observation.ProviderSamples)
        {
            if (sample.Metrics.Count == 0)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetryProviderStatusFormat",
                    sample.ProviderId,
                    T($"Enum.TelemetryCollectionStatus.{sample.Status}"),
                    T(sample.ReasonCode)));
                continue;
            }

            foreach (var metric in sample.Metrics)
            {
                TelemetryObservationLines.Add(F(
                    "Status.TelemetryMetricFormat",
                    T($"Enum.TelemetryMetricKind.{metric.Kind}"),
                    metric.Value,
                    metric.Unit));
            }
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
                TelemetryHistoryLimit,
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

        foreach (var entry in _telemetryHistoryEntries)
        {
            var metricCount = entry.Observation.ProviderSamples.Sum(sample => sample.Metrics.Count);
            TelemetryHistoryLines.Add(F(
                "Status.TelemetryHistoryItemFormat",
                entry.Observation.StartedAt.LocalDateTime,
                entry.Observation.Target.ProcessName,
                entry.Observation.ProviderSamples.Count,
                metricCount));
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
        var status = entry.Success
            ? T("Status.RuntimeHistorySuccess")
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
            SelectedRuntimeHistoryDetail = T("Status.RuntimeHistorySuccessDetail");
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
