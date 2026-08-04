using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.Services.Diagnostics;

public sealed class DiagnosticPackageService
{
    private static readonly string[] PackageFiles =
    [
        "README.txt",
        "manifest.json",
        "device.json",
        "capabilities.json",
        "runs.json",
        "logs.json",
        "safety-settings.json",
        "collection-errors.json"
    ];

    private readonly IRuntimeStateStore _runtimeStateStore;
    private readonly IDeviceFingerprintProvider _deviceFingerprintProvider;
    private readonly ICapabilityDetector _capabilityDetector;
    private readonly IAppLogger _logger;
    private readonly JsonAppSettingsStore _settingsStore;
    private readonly DiagnosticDataSanitizer _sanitizer;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public DiagnosticPackageService(
        IRuntimeStateStore runtimeStateStore,
        IDeviceFingerprintProvider deviceFingerprintProvider,
        ICapabilityDetector capabilityDetector,
        IAppLogger logger,
        JsonAppSettingsStore settingsStore,
        DiagnosticDataSanitizer? sanitizer = null)
    {
        _runtimeStateStore = runtimeStateStore;
        _deviceFingerprintProvider = deviceFingerprintProvider;
        _capabilityDetector = capabilityDetector;
        _logger = logger;
        _settingsStore = settingsStore;
        _sanitizer = sanitizer ?? new DiagnosticDataSanitizer();
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var parentDirectory = Path.GetDirectoryName(fullDestinationPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        var temporaryPath = $"{fullDestinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var collectionErrors = new List<DiagnosticPackageCollectionError>();
            var device = await TryCollectAsync(
                "device",
                () => _deviceFingerprintProvider.GetCurrentAsync(cancellationToken),
                new DeviceFingerprint(),
                collectionErrors,
                cancellationToken);
            var capabilities = await TryCollectAsync(
                "capabilities",
                () => _capabilityDetector.DetectAsync(cancellationToken),
                new CapabilitySnapshot(),
                collectionErrors,
                cancellationToken);
            var runs = await TryCollectAsync<IReadOnlyList<SchedulerRunHistoryEntry>>(
                "runtime-history",
                () => _runtimeStateStore.LoadRecentRunsAsync(50, cancellationToken),
                [],
                collectionErrors,
                cancellationToken);
            var logs = TryCollectLogs(collectionErrors);

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteTextEntryAsync(archive, "README.txt", BuildReadme(), cancellationToken);
                await WriteJsonEntryAsync(archive, "manifest.json", CreateManifest(), cancellationToken);
                await WriteJsonEntryAsync(archive, "device.json", device, cancellationToken);
                await WriteJsonEntryAsync(archive, "capabilities.json", capabilities, cancellationToken);
                await WriteJsonEntryAsync(
                    archive,
                    "runs.json",
                    _sanitizer.SanitizeRuns(runs),
                    cancellationToken);
                await WriteJsonEntryAsync(
                    archive,
                    "logs.json",
                    _sanitizer.SanitizeLogs(logs, runs),
                    cancellationToken);
                await WriteJsonEntryAsync(
                    archive,
                    "safety-settings.json",
                    CreateSafetySettingsSnapshot(_settingsStore.Current),
                    cancellationToken);
                await WriteJsonEntryAsync(
                    archive,
                    "collection-errors.json",
                    collectionErrors,
                    cancellationToken);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private DiagnosticPackageManifest CreateManifest() =>
        new()
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            OperatingSystem = RuntimeInformation.OSDescription,
            DotNetRuntime = RuntimeInformation.FrameworkDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            IncludedFiles = PackageFiles
        };

    private async Task<T> TryCollectAsync<T>(
        string component,
        Func<Task<T>> collectAsync,
        T fallback,
        ICollection<DiagnosticPackageCollectionError> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            return await collectAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            errors.Add(CreateCollectionError(component, exception));
            return fallback;
        }
    }

    private IReadOnlyList<LogEntry> TryCollectLogs(
        ICollection<DiagnosticPackageCollectionError> errors)
    {
        try
        {
            return _logger.GetRecentEntries(500);
        }
        catch (Exception exception)
        {
            errors.Add(CreateCollectionError("logs", exception));
            return [];
        }
    }

    private DiagnosticPackageCollectionError CreateCollectionError(string component, Exception exception) =>
        new()
        {
            Component = component,
            ErrorType = exception.GetType().Name,
            Message = _sanitizer.SanitizeText(exception.Message)
        };

    private async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, value, _jsonOptions, cancellationToken);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string entryName,
        string value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
    }

    private static DiagnosticSafetySettingsSnapshot CreateSafetySettingsSnapshot(AppSettings settings) =>
        new()
        {
            PauseSchedulingAfterUnsafeExit = settings.PauseSchedulingAfterUnsafeExit,
            AutoRollbackOnUnsafeExit = settings.AutoRollbackOnUnsafeExit,
            DisableProfileAfterRepeatedUnsafeRuns = settings.DisableProfileAfterRepeatedUnsafeRuns,
            UnsafeRunThreshold = settings.UnsafeRunThreshold,
            MonitoringIntervalSeconds = settings.MonitoringIntervalSeconds,
            AutoStartMonitoringOnLaunch = settings.AutoStartMonitoringOnLaunch,
            LaunchAtStartup = settings.LaunchAtStartup
        };

    private static string BuildReadme() =>
        """
        随芯调诊断包 / SuiXinTiao Diagnostic Package

        此压缩包用于排查能力检测、调度和回滚问题。
        This archive is intended for capability, scheduling, and rollback diagnostics.

        默认隐私处理：不包含原始数据库、Profile 文件、背景图片、窗口标题或完整本机路径；
        Profile 标识使用不可逆短哈希替代，日志中的用户名、机器名和绝对路径会被脱敏。

        Default privacy handling: raw databases, Profile files, background images, window titles,
        and full local paths are excluded. Profile identifiers are replaced with irreversible short
        hashes, while user names, machine names, and absolute paths are redacted from log text.
        """;
}
