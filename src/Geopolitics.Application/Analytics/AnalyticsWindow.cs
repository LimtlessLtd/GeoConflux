namespace Geopolitics.Application.Analytics;

/// <summary>
/// One of the fixed periods analytics are reported over, together with the resolution its timeseries
/// is bucketed at.
/// <para>
/// A closed set rather than an arbitrary <see cref="TimeSpan"/>, because every window here is also a
/// cache key, a chart axis, and a label a reader compares against another reader's. Allowing any
/// duration would make two people looking at "recent activity" be looking at different questions
/// without either of them knowing.
/// </para>
/// <para>
/// The bucket size is part of the window rather than a separate parameter so a timeseries always has
/// a comparable number of points — between 24 and 90 — whichever window is chosen. A single fixed
/// bucket would give the 24-hour view one point and the 90-day view two thousand.
/// </para>
/// </summary>
public sealed record AnalyticsWindow
{
    private AnalyticsWindow(string token, string label, TimeSpan duration, TimeSpan bucketSize)
    {
        Token = token;
        Label = label;
        Duration = duration;
        BucketSize = bucketSize;
    }

    public static AnalyticsWindow Last24Hours { get; } =
        new("24h", "Last 24 hours", TimeSpan.FromHours(24), TimeSpan.FromHours(1));

    public static AnalyticsWindow Last7Days { get; } =
        new("7d", "Last 7 days", TimeSpan.FromDays(7), TimeSpan.FromHours(6));

    public static AnalyticsWindow Last30Days { get; } =
        new("30d", "Last 30 days", TimeSpan.FromDays(30), TimeSpan.FromDays(1));

    public static AnalyticsWindow Last90Days { get; } =
        new("90d", "Last 90 days", TimeSpan.FromDays(90), TimeSpan.FromDays(1));

    /// <summary>Every supported window, shortest first. This is the list the dashboard offers.</summary>
    public static IReadOnlyList<AnalyticsWindow> All { get; } =
        [Last24Hours, Last7Days, Last30Days, Last90Days];

    /// <summary>Wire and query-string form, for example <c>7d</c>.</summary>
    public string Token { get; }

    /// <summary>Human-readable form, for chart titles and tab labels.</summary>
    public string Label { get; }

    public TimeSpan Duration { get; }

    public TimeSpan BucketSize { get; }

    /// <summary>How many buckets a full window contains, which is the length of its timeseries.</summary>
    public int BucketCount => (int)Math.Round(Duration / BucketSize);

    public static bool TryParse(string? token, out AnalyticsWindow window)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Token, token?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                window = candidate;
                return true;
            }
        }

        window = Last24Hours;
        return false;
    }

    /// <summary>
    /// Resolves a caller-supplied token, falling back to 24 hours. An unrecognised token is not an
    /// error at this layer: the API rejects it explicitly rather than letting a typo silently widen
    /// the period someone is reading.
    /// </summary>
    public static AnalyticsWindow Parse(string? token) => TryParse(token, out var window) ? window : Last24Hours;

    public override string ToString() => Token;
}
