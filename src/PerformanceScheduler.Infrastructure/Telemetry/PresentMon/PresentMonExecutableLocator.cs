namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed class PresentMonExecutableLocator : IPresentMonExecutableLocator
{
    public const string EnvironmentVariableName = "SUIXINTIAO_PRESENTMON_PATH";

    private readonly string _applicationDirectory;
    private readonly Func<string, string?> _readEnvironmentVariable;
    private readonly Func<string?> _configuredPathProvider;

    public PresentMonExecutableLocator(
        string applicationDirectory,
        Func<string, string?>? readEnvironmentVariable = null,
        Func<string?>? configuredPathProvider = null)
    {
        _applicationDirectory = Path.GetFullPath(applicationDirectory);
        _readEnvironmentVariable = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _configuredPathProvider = configuredPathProvider ?? (() => null);
    }

    public string? FindExecutable()
    {
        if (TryResolveExecutable(_configuredPathProvider()) is { } explicitlyConfiguredExecutable)
        {
            return explicitlyConfiguredExecutable;
        }

        var configuredPath = _readEnvironmentVariable(EnvironmentVariableName);
        if (TryResolveExecutable(configuredPath) is { } configuredExecutable)
        {
            return configuredExecutable;
        }

        var toolsDirectory = Path.Combine(_applicationDirectory, "tools", "PresentMon");
        if (!Directory.Exists(toolsDirectory))
        {
            return null;
        }

        var exactCandidates = new[]
        {
            Path.Combine(toolsDirectory, "PresentMon.exe"),
            Path.Combine(toolsDirectory, "PresentMonConsole.exe")
        };
        foreach (var candidate in exactCandidates)
        {
            if (TryResolveExecutable(candidate) is { } executable)
            {
                return executable;
            }
        }

        try
        {
            return Directory
                .EnumerateFiles(toolsDirectory, "PresentMon-*-x64.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(TryResolveExecutable)
                .FirstOrDefault(path => path is not null);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryResolveExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            return IsSupportedConsoleExecutableName(Path.GetFileName(fullPath)) &&
                   File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsSupportedConsoleExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return IsSupportedConsoleExecutableName(Path.GetFileName(path));
    }

    public static bool IsSupportedConsoleExecutableName(string? fileName)
    {
        if (string.Equals(fileName, "PresentMon.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "PresentMonConsole.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName is not null &&
               fileName.StartsWith("PresentMon-", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith("-x64.exe", StringComparison.OrdinalIgnoreCase);
    }
}
