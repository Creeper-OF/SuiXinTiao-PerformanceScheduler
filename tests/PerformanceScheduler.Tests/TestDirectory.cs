namespace PerformanceScheduler.Tests;

internal static class TestDirectory
{
    public static void DeleteWithRetry(string path, int maximumAttempts = 5)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }
}
