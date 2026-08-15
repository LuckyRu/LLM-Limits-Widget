namespace LLMLimitsWidget.FloatingOverlay;

internal static class CountdownFormatter
{
    public static string Format(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } target)
        {
            return "—";
        }

        var remaining = target - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        var totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        if (totalMinutes < 60)
        {
            return $"in {totalMinutes}m";
        }

        var hours = totalMinutes / 60;
        if (hours < 24)
        {
            var minutes = totalMinutes % 60;
            return minutes == 0
                ? $"in {hours}h"
                : $"in {hours}h {minutes}m";
        }

        var totalHours = Math.Max(1, (int)Math.Ceiling(remaining.TotalHours));
        var days = totalHours / 24;
        var remainingHours = totalHours % 24;
        return remainingHours == 0
            ? $"in {days}d"
            : $"in {days}d {remainingHours}h";
    }

    public static CountdownUrgency GetUrgency(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } target)
        {
            return CountdownUrgency.Unknown;
        }

        var remaining = target - now;
        return remaining <= TimeSpan.FromMinutes(10)
            ? CountdownUrgency.Critical
            : remaining <= TimeSpan.FromHours(1)
                ? CountdownUrgency.Near
                : CountdownUrgency.Normal;
    }

    /// <summary>
    /// Returns the earliest moment at which the formatted text or its urgency
    /// can visibly change. The caller can sleep until this instant instead of
    /// redrawing once a second.
    /// </summary>
    public static DateTimeOffset? GetNextVisualChangeAt(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } target || target <= now)
        {
            return null;
        }

        var remaining = target - now;
        var unit = remaining >= TimeSpan.FromHours(24)
            ? TimeSpan.FromHours(1)
            : TimeSpan.FromMinutes(1);
        var next = GetNextBoundary(target, now, unit);

        foreach (var threshold in new[] { TimeSpan.FromHours(1), TimeSpan.FromMinutes(10) })
        {
            var transition = target - threshold;
            if (transition > now && transition < next)
            {
                next = transition;
            }
        }

        return next;
    }

    private static DateTimeOffset GetNextBoundary(DateTimeOffset target, DateTimeOffset now, TimeSpan unit)
    {
        var remaining = target - now;
        var completedUnits = (long)Math.Floor(remaining.Ticks / (double)unit.Ticks);
        var next = target - TimeSpan.FromTicks(completedUnits * unit.Ticks);
        return next > now ? next : next + unit;
    }
}

internal enum CountdownUrgency
{
    Unknown,
    Normal,
    Near,
    Critical
}
