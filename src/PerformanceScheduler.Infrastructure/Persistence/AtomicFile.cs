using System.Text.Json;
using System.Text;

namespace PerformanceScheduler.Infrastructure.Persistence;

public static class AtomicFile
{
    public static void WriteAllText(string destinationPath, string contents)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The destination path does not have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = CreateTemporaryPath(directory, fullDestinationPath);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    public static Task WriteJsonAsync<T>(
        string destinationPath,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            destinationPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, value, options, token),
            cancellationToken);

    public static async Task WriteAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default)
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The destination path does not have a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = CreateTemporaryPath(directory, fullDestinationPath);

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writeAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    private static string CreateTemporaryPath(string directory, string destinationPath) =>
        Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

    private static void DeleteTemporaryFile(string temporaryPath)
    {
        if (!File.Exists(temporaryPath))
        {
            return;
        }

        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale temporary file is safer than masking the original write failure.
        }
    }
}
