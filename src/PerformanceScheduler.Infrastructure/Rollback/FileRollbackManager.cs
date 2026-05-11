using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Rollback;

public sealed class FileRollbackManager : IRollbackManager
{
    private readonly string _stateFilePath;
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IPriorityManager _priorityManager;
    private readonly IAppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileRollbackManager(
        string stateFilePath,
        IPowerPlanManager powerPlanManager,
        IPriorityManager priorityManager,
        IAppLogger logger)
    {
        _stateFilePath = stateFilePath;
        _powerPlanManager = powerPlanManager;
        _priorityManager = priorityManager;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
    }

    public async Task CaptureAsync(SchedulerState state, CancellationToken cancellationToken = default)
    {
        await SaveStateAsync(state, cancellationToken);
    }

    public async Task<SchedulerState?> GetLastKnownStateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            return await JsonSerializer.DeserializeAsync<SchedulerState>(stream, _jsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warn($"Rollback state could not be read and will be ignored: {exception.Message}");
            return null;
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetLastKnownStateAsync(cancellationToken);
        if (state is null)
        {
            _logger.Info("Rollback skipped because no previous state was captured.");
            return;
        }

        var remainingState = state;

        if (state.PreviousPowerPlan is not null)
        {
            try
            {
                var restored = await _powerPlanManager.SetActivePlanAsync(state.PreviousPowerPlan.SchemeGuid, cancellationToken);
                if (restored)
                {
                    remainingState = remainingState with { PreviousPowerPlan = null };
                }
                else
                {
                    _logger.Warn($"Failed to restore power plan {state.PreviousPowerPlan.Name}: powercfg did not accept the change.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Warn($"Failed to restore power plan {state.PreviousPowerPlan.Name}: {exception.Message}");
            }
        }

        if (state.PreviousAdvancedPowerSettings is not null)
        {
            try
            {
                var restored = await _powerPlanManager.RestoreAdvancedSettingsAsync(state.PreviousAdvancedPowerSettings, cancellationToken);
                if (restored)
                {
                    remainingState = remainingState with { PreviousAdvancedPowerSettings = null };
                }
                else
                {
                    _logger.Warn("Failed to restore advanced power settings: powercfg did not accept the change.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Warn($"Failed to restore advanced power settings: {exception.Message}");
            }
        }

        var remainingPriorities = new Dictionary<int, PriorityLevel>(state.OriginalPriorities);
        foreach (var (processId, priority) in state.OriginalPriorities)
        {
            try
            {
                var restored = await _priorityManager.SetPriorityAsync(processId, priority, cancellationToken);
                if (restored)
                {
                    remainingPriorities.Remove(processId);
                }
                else
                {
                    _logger.Warn($"Failed to restore priority for PID {processId}: priority change was not permitted.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Warn($"Failed to restore priority for PID {processId}: {exception.Message}");
            }
        }

        remainingState = remainingState with { OriginalPriorities = remainingPriorities };
        if (IsEmpty(remainingState))
        {
            DeleteStateFile();
            return;
        }

        await SaveStateAsync(remainingState, cancellationToken);
        _logger.Warn("Rollback completed with unresolved item(s); remaining state was preserved.");
    }

    private async Task SaveStateAsync(SchedulerState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
        var temporaryPath = $"{_stateFilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _stateFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteStateFile()
    {
        if (File.Exists(_stateFilePath))
        {
            File.Delete(_stateFilePath);
        }
    }

    private static bool IsEmpty(SchedulerState state) =>
        state.PreviousPowerPlan is null &&
        state.PreviousAdvancedPowerSettings is null &&
        state.OriginalPriorities.Count == 0;
}
