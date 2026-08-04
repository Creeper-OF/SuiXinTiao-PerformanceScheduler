using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.Services.Diagnostics;

public sealed partial class DiagnosticDataSanitizer
{
    private readonly string[] _privateValues;

    public DiagnosticDataSanitizer()
    {
        _privateValues =
        [
            Environment.UserName,
            Environment.MachineName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppContext.BaseDirectory
        ];
    }

    public IReadOnlyList<DiagnosticRunSnapshot> SanitizeRuns(
        IReadOnlyList<SchedulerRunHistoryEntry> runs) =>
        runs.Select(SanitizeRun).ToArray();

    public IReadOnlyList<DiagnosticLogSnapshot> SanitizeLogs(
        IReadOnlyList<LogEntry> entries,
        IReadOnlyList<SchedulerRunHistoryEntry>? runs = null)
    {
        var profileTokens = (runs ?? [])
            .Where(run => !string.IsNullOrWhiteSpace(run.ProfileId) || !string.IsNullOrWhiteSpace(run.ProfileName))
            .Select(run => new
            {
                run.ProfileId,
                run.ProfileName,
                Token = CreateToken("profile", run.ProfileId, run.ProfileName)
            })
            .ToArray();

        string SanitizeLogText(string? value)
        {
            var sanitized = SanitizeText(value);
            foreach (var profile in profileTokens)
            {
                sanitized = ReplaceIfPresent(sanitized, profile.ProfileId, profile.Token);
                sanitized = ReplaceIfPresent(sanitized, profile.ProfileName, profile.Token);
            }

            return sanitized;
        }

        return
        entries.Select(entry => new DiagnosticLogSnapshot
        {
            Timestamp = entry.Timestamp,
            Level = entry.Level,
            Audience = entry.Audience,
            EventCode = entry.EventCode,
            Message = SanitizeLogText(entry.Message),
            Arguments = entry.Arguments?
                .Select(argument => SanitizeLogText(argument?.ToString() ?? string.Empty))
                .ToArray() ?? []
        }).ToArray();
    }

    public string SanitizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value;
        foreach (var privateValue in _privateValues.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            sanitized = sanitized.Replace(privateValue, "<private>", StringComparison.OrdinalIgnoreCase);
        }

        return AbsoluteWindowsPathRegex().Replace(sanitized, "<path>");
    }

    private DiagnosticRunSnapshot SanitizeRun(SchedulerRunHistoryEntry run)
    {
        var profileToken = CreateToken("profile", run.ProfileId, run.ProfileName);
        string SanitizeRunText(string? value)
        {
            var sanitized = SanitizeText(value);
            sanitized = ReplaceIfPresent(sanitized, run.ProfileId, profileToken);
            sanitized = ReplaceIfPresent(sanitized, run.ProfileName, profileToken);
            sanitized = ReplaceIfPresent(sanitized, run.WindowTitle, "<window-title>");
            return sanitized;
        }

        var summary = SanitizeRunText(run.Summary);

        return new DiagnosticRunSnapshot
        {
            RunId = run.RunId,
            TransactionId = run.TransactionId,
            OccurredAt = run.OccurredAt,
            ProcessName = SanitizeText(run.ProcessName),
            ProfileId = string.IsNullOrWhiteSpace(run.ProfileId) ? string.Empty : profileToken,
            ProfileName = string.IsNullOrWhiteSpace(run.ProfileName) ? string.Empty : profileToken,
            PowerPlanName = SanitizeText(run.PowerPlanName),
            Priority = run.Priority,
            Success = run.Success,
            IsRetryDeferred = run.IsRetryDeferred,
            ConsecutiveFailureCount = run.ConsecutiveFailureCount,
            RetryAfter = run.RetryAfter,
            FailureRecoveryCompleted = run.FailureRecoveryCompleted,
            Summary = summary,
            Actions = run.Actions.Select(action => new DiagnosticActionSnapshot
            {
                ActionId = action.Plan.ActionId,
                Kind = action.Plan.Kind,
                Status = action.Status,
                ReasonCode = action.ReasonCode,
                Target = SanitizeRunText(action.Plan.Target),
                OriginalValue = SanitizeRunText(action.Plan.OriginalValue),
                TargetValue = SanitizeRunText(action.Plan.TargetValue),
                Message = SanitizeRunText(action.Message),
                RequiresRollback = action.Plan.RequiresRollback,
                CompletedAt = action.CompletedAt
            }).ToArray()
        };
    }

    private static string CreateToken(string category, params string[] values)
    {
        var source = string.Join("|", values.Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..10];
        return $"<{category}:{hash}>";
    }

    private static string ReplaceIfPresent(string source, string? value, string replacement) =>
        string.IsNullOrWhiteSpace(value)
            ? source
            : source.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)(?<![\w])(?:[a-z]:\\|\\\\)[^\r\n\t\""<>|]+")]
    private static partial Regex AbsoluteWindowsPathRegex();
}
