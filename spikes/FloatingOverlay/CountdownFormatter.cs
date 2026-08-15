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
}

internal enum CountdownUrgency
{
    Unknown,
    Normal,
    Near,
    Critical
}
