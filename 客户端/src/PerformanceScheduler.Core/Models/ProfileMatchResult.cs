namespace PerformanceScheduler.Core.Models;

public sealed record ProfileMatchResult
{
    public ProfileMatchResult(
        PerformanceProfile profile,
        int score,
        string reason)
        : this(profile, score, reason, Array.Empty<ProfileMatchReason>())
    {
    }

    public ProfileMatchResult(
        PerformanceProfile profile,
        int score,
        string reason,
        IReadOnlyList<ProfileMatchReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(reasons);

        Profile = profile;
        Score = score;
        Reason = reason;
        Reasons = reasons.ToArray();
    }

    public PerformanceProfile Profile { get; }

    public int Score { get; }

    public string Reason { get; }

    public IReadOnlyList<ProfileMatchReason> Reasons { get; }
}
