namespace LLMLimitsWidget.FloatingOverlay;

/// <summary>
/// Presentation-only state for one live countdown. It never fetches data: the
/// domain supplies ResetAt, while this object determines the visible string
/// and when that string can next change.
/// </summary>
internal sealed class CountdownViewModel
{
    public DateTimeOffset? ResetAt { get; private set; }

    public string Text { get; private set; } = "—";

    public CountdownUrgency Urgency { get; private set; } = CountdownUrgency.Unknown;

    /// <returns>True only if rendering needs to change.</returns>
    public bool Update(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        var nextText = CountdownFormatter.Format(resetAt, now);
        var nextUrgency = CountdownFormatter.GetUrgency(resetAt, now);
        var changed = !string.Equals(Text, nextText, StringComparison.Ordinal)
            || Urgency != nextUrgency;
        ResetAt = resetAt;
        Text = nextText;
        Urgency = nextUrgency;
        return changed;
    }

    public DateTimeOffset? GetNextVisualChangeAt(DateTimeOffset now) =>
        CountdownFormatter.GetNextVisualChangeAt(ResetAt, now);
}
