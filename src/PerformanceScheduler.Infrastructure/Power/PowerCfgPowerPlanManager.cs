using System.Diagnostics;
using System.Text.RegularExpressions;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Power;

public sealed partial class PowerCfgPowerPlanManager : IPowerPlanManager
{
    private const string ProcessorSubgroupGuid = "54533251-82be-4824-96c1-47b60b740d00";
    private const string ProcessorMaxStateSettingGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";

    public async Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunPowerCfgAsync("/list", cancellationToken);
        if (result.ExitCode != 0)
        {
            return Array.Empty<PowerPlanInfo>();
        }

        var plans = new List<PowerPlanInfo>();
        foreach (var line in result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PowerPlanRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var guid = Guid.Parse(match.Groups["guid"].Value);
            var name = match.Groups["name"].Value.Trim();
            var isActive = line.Contains('*');
            plans.Add(new PowerPlanInfo(guid, name, isActive));
        }

        return plans;
    }

    public async Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default)
    {
        var plans = await GetAvailablePlansAsync(cancellationToken);
        return plans.FirstOrDefault(plan => plan.IsActive);
    }

    public async Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default)
    {
        var result = await RunPowerCfgAsync($"/setactive {schemeGuid:D}", cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
        Guid schemeGuid,
        PowerSourceMode powerSourceMode,
        CancellationToken cancellationToken = default)
    {
        var result = await RunPowerCfgAsync(
            $"/query {schemeGuid:D} {ProcessorSubgroupGuid} {ProcessorMaxStateSettingGuid}",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var processorMaxState = ParsePowerSettingValue(result.StandardOutput, powerSourceMode);
        return new PowerPlanAdvancedState
        {
            SchemeGuid = schemeGuid,
            PowerSourceMode = powerSourceMode,
            ProcessorMaxStatePercent = processorMaxState
        };
    }

    public async Task<bool> ApplyAdvancedSettingsAsync(
        Guid schemeGuid,
        PowerSourceMode powerSourceMode,
        PowerPlanAdvancedPreference preference,
        CancellationToken cancellationToken = default)
    {
        if (preference.ProcessorMaxStatePercent is not { } processorMaxState)
        {
            return true;
        }

        return await SetProcessorMaxStateAsync(schemeGuid, powerSourceMode, processorMaxState, cancellationToken);
    }

    public Task<bool> RestoreAdvancedSettingsAsync(
        PowerPlanAdvancedState state,
        CancellationToken cancellationToken = default) =>
        state.ProcessorMaxStatePercent is { } processorMaxState
            ? SetProcessorMaxStateAsync(state.SchemeGuid, state.PowerSourceMode, processorMaxState, cancellationToken)
            : Task.FromResult(true);

    private async Task<bool> SetProcessorMaxStateAsync(
        Guid schemeGuid,
        PowerSourceMode powerSourceMode,
        int value,
        CancellationToken cancellationToken)
    {
        var normalizedValue = Math.Clamp(value, 1, 100);
        var argumentsPrefix = powerSourceMode == PowerSourceMode.Battery
            ? "/setdcvalueindex"
            : "/setacvalueindex";
        var result = await RunPowerCfgAsync(
            $"{argumentsPrefix} {schemeGuid:D} {ProcessorSubgroupGuid} {ProcessorMaxStateSettingGuid} {normalizedValue}",
            cancellationToken);

        return result.ExitCode == 0;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunPowerCfgAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    [GeneratedRegex(@"(?<guid>[0-9a-fA-F\-]{36}).*?\((?<name>.+?)\)")]
    private static partial Regex PowerPlanRegex();

    private static int? ParsePowerSettingValue(string output, PowerSourceMode powerSourceMode)
    {
        var matches = PowerSettingValueRegex().Matches(output);
        if (matches.Count == 0)
        {
            return null;
        }

        var index = powerSourceMode == PowerSourceMode.Battery ? matches.Count - 1 : Math.Max(0, matches.Count - 2);
        var rawValue = matches[index].Groups["value"].Value;
        return Convert.ToInt32(rawValue, 16);
    }

    [GeneratedRegex(@"0x(?<value>[0-9a-fA-F]+)")]
    private static partial Regex PowerSettingValueRegex();
}
