using System.Globalization;
using System.Text;

namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed class PresentMonCsvParser
{
    private static readonly string[] FrameTimeColumnCandidates =
    [
        "MsBetweenPresents",
        "MsBetweenAppStart",
        "FrameTime",
        "CPUFrameTime",
        "MsBetweenDisplayChange"
    ];

    public async Task<PresentMonFrameStatistics?> ParseAsync(
        string csvPath,
        int expectedProcessId,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            csvPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return null;
        }

        var headers = ParseCsvLine(headerLine);
        var frameTimeIndex = FindColumn(headers, FrameTimeColumnCandidates);
        if (frameTimeIndex < 0)
        {
            throw new InvalidDataException("PresentMon CSV does not contain a supported frame-time column.");
        }

        var processIdIndex = FindColumn(headers, ["ProcessID"]);
        var frameTimes = new List<double>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            if (frameTimeIndex >= fields.Count ||
                !double.TryParse(fields[frameTimeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTime) ||
                !double.IsFinite(frameTime) ||
                frameTime <= 0 ||
                frameTime > 10_000)
            {
                continue;
            }

            if (processIdIndex >= 0 &&
                (processIdIndex >= fields.Count ||
                 !int.TryParse(fields[processIdIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
                 processId != expectedProcessId))
            {
                continue;
            }

            frameTimes.Add(frameTime);
        }

        if (frameTimes.Count == 0)
        {
            return null;
        }

        frameTimes.Sort();
        var averageFrameTime = frameTimes.Average();
        return new PresentMonFrameStatistics
        {
            SampleCount = frameTimes.Count,
            AverageFramesPerSecond = 1000d / averageFrameTime,
            AverageFrameTimeMilliseconds = averageFrameTime,
            FrameTimeP95Milliseconds = Percentile(frameTimes, 0.95),
            FrameTimeP99Milliseconds = Percentile(frameTimes, 0.99)
        };
    }

    private static int FindColumn(IReadOnlyList<string> headers, IReadOnlyList<string> candidates)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (candidates.Contains(headers[index].Trim('\uFEFF'), StringComparer.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        var position = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * (position - lower));
    }

    internal static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("PresentMon CSV contains an unterminated quoted field.");
        }

        fields.Add(current.ToString());
        return fields;
    }
}
