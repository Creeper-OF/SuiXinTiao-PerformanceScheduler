using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Persistence;

/// <summary>
/// Captures and restores the raw files owned by a JSON-backed configuration store.
/// Keeping the snapshot at the file level also restores profile history and renamed
/// files, not only the currently deserialized objects.
/// </summary>
public static class FileStoreSnapshot
{
    public static void EnsureSafeDirectoryPath(string directoryPath) =>
        EnsureDirectoryStorePath(Path.GetFullPath(directoryPath));

    public static void EnsureSafeFilePath(string filePath) =>
        EnsureFileStorePath(Path.GetFullPath(filePath));

    internal static IReadOnlyList<string> EnumerateTopLevelJsonFiles(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directoryPath);
        EnsureDirectoryStorePath(root);
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(path);
            files.Add(path);
        }

        return files
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void EnsureDirectoryStorePath(string path)
    {
        EnsurePathHasNoReparsePoint(path);
        if (File.Exists(path))
        {
            throw new InvalidDataException($"The configuration store path is a file, not a directory: '{path}'.");
        }
    }

    public static async Task<ConfigurationStoreSnapshot> CaptureDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directoryPath);
        EnsureDirectoryStorePath(root);
        if (!Directory.Exists(root))
        {
            return new ConfigurationStoreSnapshot();
        }

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateStoreFiles(root, cancellationToken)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-check immediately before opening each file; the store may be
            // changed after enumeration but before its bytes are captured.
            EnsureFileStorePath(path);
            var relativePath = Path.GetRelativePath(root, path);
            files[relativePath] = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return new ConfigurationStoreSnapshot { Files = files };
    }

    public static async Task RestoreDirectoryAsync(
        string directoryPath,
        ConfigurationStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directoryPath);
        EnsureDirectoryStorePath(root);
        Directory.CreateDirectory(root);

        if (snapshot.Files is null)
        {
            throw new InvalidDataException("The configuration snapshot does not contain a file collection.");
        }

        // Validate every destination before deleting anything from the live store.
        // A malformed snapshot must not turn rollback into data loss.
        var entries = snapshot.Files
            .Select(entry =>
            {
                if (entry.Value is null)
                {
                    throw new InvalidDataException(
                        $"The configuration snapshot contains null contents for '{entry.Key}'.");
                }

                return (Path: GetSafeChildPath(root, entry.Key), Contents: entry.Value);
            })
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Cancellation is checked before destructive work. Once deletion starts,
        // finish restoring the captured tree so a cancelled rollback cannot leave
        // the store partially empty.
        var existingFiles = EnumerateStoreFiles(root, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var path in existingFiles)
        {
            // The directory was enumerated and validated earlier. Re-check
            // immediately before deletion so a replaced reparse point cannot
            // redirect destructive work outside the store.
            EnsureFileStorePath(path);
            File.Delete(path);
        }

        foreach (var entry in entries)
        {
            var parent = Path.GetDirectoryName(entry.Path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await AtomicFile.WriteBytesAsync(entry.Path, entry.Contents, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static async Task<ConfigurationStoreSnapshot> CaptureFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        EnsureFileStorePath(fullPath);
        if (!File.Exists(fullPath))
        {
            return new ConfigurationStoreSnapshot();
        }

        // Re-check immediately before opening the user-writable file. The
        // initial validation and the read are separate filesystem operations.
        EnsureFileStorePath(fullPath);
        return new ConfigurationStoreSnapshot
        {
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false)
            }
        };
    }

    public static async Task RestoreFileAsync(
        string filePath,
        ConfigurationStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        EnsureFileStorePath(fullPath);
        if (snapshot.Files is null)
        {
            throw new InvalidDataException("The configuration snapshot does not contain a file collection.");
        }

        if (!snapshot.Files.TryGetValue(string.Empty, out var contents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
            {
                // Do not delete a file that was replaced by a reparse point
                // after the initial path validation.
                EnsureFileStorePath(fullPath);
                File.Delete(fullPath);
            }

            return;
        }

        if (contents is null)
        {
            throw new InvalidDataException("The configuration snapshot contains null file contents.");
        }

        await AtomicFile.WriteBytesAsync(fullPath, contents, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> EnumerateStoreFiles(
        string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = directories.Pop();
            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureNotReparsePoint(file);
                files.Add(file);
            }

            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureNotReparsePoint(directory);
                directories.Push(directory);
            }
        }

        return files;
    }

    private static void EnsureFileStorePath(string path)
    {
        EnsurePathHasNoReparsePoint(path);
        if (Directory.Exists(path))
        {
            throw new InvalidDataException($"The configuration store path is a directory, not a file: '{path}'.");
        }
    }

    private static void EnsurePathHasNoReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                EnsureNotReparsePoint(current);
            }
            catch (FileNotFoundException)
            {
                // The path may not exist yet; continue checking its parents.
            }
            catch (DirectoryNotFoundException)
            {
                // The path may not exist yet; continue checking its parents.
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The configuration store contains a reparse point: '{path}'.");
        }
    }

    private static string GetSafeChildPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("The configuration snapshot contains an invalid relative path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The configuration snapshot escapes its store directory.");
        }

        return fullPath;
    }
}
