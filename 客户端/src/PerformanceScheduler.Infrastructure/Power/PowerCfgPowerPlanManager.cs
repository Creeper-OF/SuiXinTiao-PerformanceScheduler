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

        return ParsePowerPlans(result.StandardOutput);
    }

    internal static IReadOnlyList<PowerPlanInfo> ParsePowerPlans(string output)
    {
        var plans = new List<PowerPlanInfo>();
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
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
        if (result.ExitCode != 0)
        {
            return false;
        }

        // powercfg can exit successfully before the active scheme is
        // observable. Do not report success until a fresh read confirms it.
        var activePlan = await GetActivePlanAsync(cancellationToken);
        return activePlan?.SchemeGuid == schemeGuid;
    }

    public async Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
        Guid schemeGuid,
        PowerSourceMode powerSourceMode,
        CancellationToken cancellationToken = default)
    {
        if (!IsConcretePowerSource(powerSourceMode))
        {
            return null;
        }

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

        if (!IsConcretePowerSource(powerSourceMode))
        {
            return false;
        }

        return await SetProcessorMaxStateAsync(schemeGuid, powerSourceMode, processorMaxState, cancellationToken);
    }

    public Task<bool> RestoreAdvancedSettingsAsync(
        PowerPlanAdvancedState state,
        CancellationToken cancellationToken = default) =>
        state.ProcessorMaxStatePercent is not { } processorMaxState
            ? Task.FromResult(true)
            : IsConcretePowerSource(state.PowerSourceMode)
            ? SetProcessorMaxStateAsync(state.SchemeGuid, state.PowerSourceMode, processorMaxState, cancellationToken)
            : Task.FromResult(false);

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

        if (result.ExitCode != 0)
        {
            return false;
        }

        var verifiedState = await GetAdvancedSettingsAsync(
            schemeGuid,
            powerSourceMode,
            cancellationToken);
        return verifiedState?.ProcessorMaxStatePercent == normalizedValue;
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
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("powercfg process could not be started.");
            }

            // Keep draining both streams even when the caller cancels so the
            // cleanup path can terminate the child without leaving pipe tasks.
            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await outputTask, await errorTask);
        }
        catch
        {
            TryStopOwnedProcess(process);
            await DrainProcessOutputAsync(outputTask);
            await DrainProcessOutputAsync(errorTask);
            throw;
        }
    }

    private static async Task DrainProcessOutputAsync(Task<string>? outputTask)
    {
        if (outputTask is null)
        {
            return;
        }

        try
        {
            await outputTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Preserve the original provider or cancellation failure.
        }
    }

    private static void TryStopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
            // Preserve the original provider or cancellation failure.
        }
    }

    [GeneratedRegex(@"(?<guid>[0-9a-fA-F\-]{36}).*?\((?<name>.*)\)\s*\*?\s*$")]
    private static partial Regex PowerPlanRegex();

    internal static int? ParsePowerSettingValue(string output, PowerSourceMode powerSourceMode)
    {
        if (!IsConcretePowerSource(powerSourceMode))
        {
            return null;
        }

        var matches = PowerSettingValueRegex().Matches(output);
        if (matches.Count == 0)
        {
            return null;
        }

        var index = powerSourceMode == PowerSourceMode.Battery ? matches.Count - 1 : Math.Max(0, matches.Count - 2);
        var rawValue = matches[index].Groups["value"].Value;
        return Convert.ToInt32(rawValue, 16);
    }

    private static bool IsConcretePowerSource(PowerSourceMode powerSourceMode) =>
        powerSourceMode is PowerSourceMode.Ac or PowerSourceMode.Battery;

    [GeneratedRegex(@"0x(?<value>[0-9a-fA-F]+)")]
    private static partial Regex PowerSettingValueRegex();
}
