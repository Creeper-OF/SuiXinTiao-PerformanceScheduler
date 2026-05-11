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
        await using var stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken);
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

        if (state.PreviousPowerPlan is not null)
        {
            try
            {
                await _powerPlanManager.SetActivePlanAsync(state.PreviousPowerPlan.SchemeGuid, cancellationToken);
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
                await _powerPlanManager.RestoreAdvancedSettingsAsync(state.PreviousAdvancedPowerSettings, cancellationToken);
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

        foreach (var (processId, priority) in state.OriginalPriorities)
        {
            try
            {
                await _priorityManager.SetPriorityAsync(processId, priority, cancellationToken);
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

        File.Delete(_stateFilePath);
    }
}
