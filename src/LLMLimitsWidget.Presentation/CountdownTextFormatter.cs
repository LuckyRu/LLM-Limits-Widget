namespace LLMLimitsWidget.Presentation;

public static class CountdownTextFormatter
{
    public static string Format(DateTimeOffset? resetAtUtc, DateTimeOffset nowUtc)
    {
        if (resetAtUtc is null)
        {
            return string.Empty;
        }

        var remaining = resetAtUtc.Value - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} sec";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            return $"in {(int)remaining.TotalMinutes} min";
        }

        if (remaining < TimeSpan.FromDays(1))
        {
            return $"in {(int)remaining.TotalHours} hr {remaining.Minutes} min";
        }

        if (remaining < TimeSpan.FromDays(7))
        {
            return $"in {remaining.Days} d {remaining.Hours} hr";
        }

        return $"in {remaining.Days / 7} wk {remaining.Days % 7} d";
    }

    public static DateTimeOffset? GetNextVisualChangeAt(
        DateTimeOffset? resetAtUtc,
        DateTimeOffset nowUtc)
    {
        if (resetAtUtc is null)
        {
            return null;
        }

        var remaining = resetAtUtc.Value - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return nowUtc.AddSeconds(1);
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return nowUtc.AddSeconds(1);
        }

        if (remaining < TimeSpan.FromDays(1))
        {
            var withinMinute = new TimeSpan(0, 0, 0, remaining.Seconds, remaining.Milliseconds);
            return nowUtc.Add(TimeSpan.FromMinutes(1) - withinMinute);
        }

        if (remaining < TimeSpan.FromDays(7))
        {
            return nowUtc.Add(TimeSpan.FromHours(1) -
                new TimeSpan(0, remaining.Hours, remaining.Minutes, remaining.Seconds, remaining.Milliseconds));
        }

        return nowUtc.Add(TimeSpan.FromDays(1) -
            new TimeSpan(0, remaining.Hours, remaining.Minutes, remaining.Seconds, remaining.Milliseconds));
    }
}
