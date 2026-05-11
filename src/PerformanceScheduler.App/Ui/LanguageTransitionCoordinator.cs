using System.Windows.Controls;

namespace PerformanceScheduler.App.Ui;

public sealed record LanguageTextTiming(TimeSpan Delay, TimeSpan Duration, int Direction);

public sealed class LanguageTransitionPlan
{
    public LanguageTransitionPlan(
        int sequence,
        IReadOnlyList<TextBlock> textBlocks,
        IReadOnlyDictionary<TextBlock, LanguageTextTiming> outTimings,
        IReadOnlyDictionary<TextBlock, LanguageTextTiming> inTimings,
        TimeSpan totalDuration,
        TimeSpan outBudget,
        TimeSpan inBudget)
    {
        Sequence = sequence;
        TextBlocks = textBlocks;
        OutTimings = outTimings;
        InTimings = inTimings;
        TotalDuration = totalDuration;
        OutBudget = outBudget;
        InBudget = inBudget;
    }

    public int Sequence { get; }

    public IReadOnlyList<TextBlock> TextBlocks { get; }

    public IReadOnlyDictionary<TextBlock, LanguageTextTiming> OutTimings { get; }

    public IReadOnlyDictionary<TextBlock, LanguageTextTiming> InTimings { get; }

    public TimeSpan TotalDuration { get; }

    public TimeSpan OutBudget { get; }

    public TimeSpan InBudget { get; }
}

public static class LanguageTransitionCoordinator
{
    private static readonly object Gate = new();
    private static readonly Random Random = new();
    private static DateTimeOffset _activeUntil = DateTimeOffset.MinValue;
    private static IReadOnlyDictionary<TextBlock, LanguageTextTiming> _activeInTimings =
        new Dictionary<TextBlock, LanguageTextTiming>();
    private static int _sequence;

    public static bool IsEnabled { get; set; } = true;

    public static LanguageTransitionPlan CreatePlan(IReadOnlyList<TextBlock> textBlocks, double durationSeconds)
    {
        var normalizedSeconds = NormalizeDuration(durationSeconds);
        var totalDuration = TimeSpan.FromSeconds(normalizedSeconds);
        var visibleTextBlocks = textBlocks
            .Where(textBlock => textBlock.IsVisible && !string.IsNullOrEmpty(textBlock.Text))
            .Distinct()
            .ToArray();

        if (visibleTextBlocks.Length == 0)
        {
            return new LanguageTransitionPlan(
                NextSequence(),
                visibleTextBlocks,
                new Dictionary<TextBlock, LanguageTextTiming>(),
                new Dictionary<TextBlock, LanguageTextTiming>(),
                totalDuration,
                TimeSpan.Zero,
                TimeSpan.Zero);
        }

        var outBudget = TimeSpan.FromMilliseconds(totalDuration.TotalMilliseconds * 0.46);
        var inBudget = totalDuration - outBudget;

        var directions = visibleTextBlocks.ToDictionary(
            textBlock => textBlock,
            _ => Random.Next(2) == 0 ? -1 : 1);

        return new LanguageTransitionPlan(
            NextSequence(),
            visibleTextBlocks,
            CreateTimings(visibleTextBlocks, outBudget, prefersShorterMotion: true, directions),
            CreateTimings(visibleTextBlocks, inBudget, prefersShorterMotion: false, directions),
            totalDuration,
            outBudget,
            inBudget);
    }

    public static int BeginInTransition(LanguageTransitionPlan plan)
    {
        if (!IsEnabled)
        {
            return _sequence;
        }

        lock (Gate)
        {
            _sequence = plan.Sequence;
            _activeInTimings = plan.InTimings;
            _activeUntil = DateTimeOffset.UtcNow.Add(plan.InBudget);
            return _sequence;
        }
    }

    public static bool TryCreateTiming(
        TextBlock textBlock,
        out int sequence,
        out TimeSpan delay,
        out TimeSpan duration,
        out int direction)
    {
        lock (Gate)
        {
            sequence = _sequence;
            if (!IsEnabled ||
                sequence == 0 ||
                DateTimeOffset.UtcNow >= _activeUntil ||
                !_activeInTimings.TryGetValue(textBlock, out var timing))
            {
                delay = TimeSpan.Zero;
                duration = TimeSpan.Zero;
                direction = -1;
                return false;
            }

            delay = timing.Delay;
            duration = timing.Duration;
            direction = timing.Direction;
            return true;
        }
    }

    private static IReadOnlyDictionary<TextBlock, LanguageTextTiming> CreateTimings(
        IReadOnlyList<TextBlock> textBlocks,
        TimeSpan budget,
        bool prefersShorterMotion,
        IReadOnlyDictionary<TextBlock, int> directions)
    {
        var count = textBlocks.Count;
        var budgetMilliseconds = Math.Max(120, budget.TotalMilliseconds);
        var shuffled = textBlocks.OrderBy(_ => Random.Next()).ToArray();
        var durationMilliseconds = CalculateItemDurationMilliseconds(budgetMilliseconds, count, prefersShorterMotion);
        var lastStartMilliseconds = Math.Max(0, budgetMilliseconds - durationMilliseconds);
        var stepMilliseconds = count <= 1 ? 0 : lastStartMilliseconds / (count - 1);
        var timings = new Dictionary<TextBlock, LanguageTextTiming>(count);

        for (var index = 0; index < shuffled.Length; index++)
        {
            var jitterRange = Math.Min(stepMilliseconds * 0.45, 36);
            var jitter = jitterRange <= 0 ? 0 : (Random.NextDouble() - 0.5) * jitterRange;
            var delayMilliseconds = Math.Clamp(index * stepMilliseconds + jitter, 0, lastStartMilliseconds);
            var textBlock = shuffled[index];
            timings[textBlock] = new LanguageTextTiming(
                TimeSpan.FromMilliseconds(delayMilliseconds),
                TimeSpan.FromMilliseconds(durationMilliseconds),
                directions.TryGetValue(textBlock, out var direction) ? direction : -1);
        }

        return timings;
    }

    private static int NextSequence()
    {
        lock (Gate)
        {
            return _sequence + 1;
        }
    }

    private static double CalculateItemDurationMilliseconds(double budgetMilliseconds, int count, bool prefersShorterMotion)
    {
        var safeCount = Math.Max(1, count);
        var orderStep = budgetMilliseconds / safeCount;
        var densityDuration = budgetMilliseconds / Math.Max(1, safeCount / 30d);
        var preferred = orderStep + densityDuration;
        var maxDuration = budgetMilliseconds * (prefersShorterMotion ? 0.42 : 0.5);
        return Math.Clamp(preferred, 120, Math.Min(900, Math.Max(120, maxDuration)));
    }

    private static double NormalizeDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return 0.9;
        }

        return Math.Clamp(seconds, 0.2, 10);
    }
}
