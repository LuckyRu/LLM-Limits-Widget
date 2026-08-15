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
            return null;
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return nowUtc.Add(DelayUntilFloorDecrements(remaining.TotalSeconds, TimeSpan.FromSeconds(1)));
        }

        if (remaining < TimeSpan.FromDays(1))
        {
            return nowUtc.Add(DelayUntilFloorDecrements(remaining.TotalMinutes, TimeSpan.FromMinutes(1)));
        }

        if (remaining < TimeSpan.FromDays(7))
        {
            return nowUtc.Add(DelayUntilFloorDecrements(remaining.TotalHours, TimeSpan.FromHours(1)));
        }

        return nowUtc.Add(DelayUntilFloorDecrements(remaining.TotalDays, TimeSpan.FromDays(1)));
    }

    private static TimeSpan DelayUntilFloorDecrements(double unitsRemaining, TimeSpan unit)
    {
        var fraction = unitsRemaining - Math.Floor(unitsRemaining);
        return fraction <= double.Epsilon
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling(unit.Ticks * fraction)));
    }
}
