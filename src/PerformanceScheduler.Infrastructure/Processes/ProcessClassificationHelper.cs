using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Processes;

internal static class ProcessClassificationHelper
{
    public static ProcessClassification Classify(string processName, string? executablePath, string windowTitle)
    {
        var probe = $"{processName} {Path.GetFileName(executablePath ?? string.Empty)} {windowTitle}".ToLowerInvariant();

        if (ContainsAny(probe, "steam", "epicgameslauncher", "battle.net", "ubisoftconnect", "launcher"))
        {
            return ProcessClassification.Launcher;
        }

        if (ContainsAny(probe, "chrome", "msedge", "firefox", "browser"))
        {
            return ProcessClassification.Browser;
        }

        if (ContainsAny(probe, "teams", "discord", "wechat", "slack", "qq"))
        {
            return ProcessClassification.Communication;
        }

        if (ContainsAny(probe, "vlc", "spotify", "music", "player"))
        {
            return ProcessClassification.Media;
        }

        if (ContainsAny(probe, "game", "unreal", "unity", "elden", "valorant", "cs2", "dota"))
        {
            return ProcessClassification.Game;
        }

        if (ContainsAny(probe, "code", "devenv", "excel", "word", "powerpnt"))
        {
            return ProcessClassification.Productivity;
        }

        return ProcessClassification.Unknown;
    }

    private static bool ContainsAny(string source, params string[] patterns) =>
        patterns.Any(pattern => source.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}
