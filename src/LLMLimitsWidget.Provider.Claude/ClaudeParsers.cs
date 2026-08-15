using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Provider.Claude;

public sealed record ClaudeParseResult(
    ProviderObservationEnvelope? Observation,
    DomainError? Error)
{
    public bool IsSuccess => Observation is not null && Error is null;
}

public static class ClaudeStatusLineParser
{
    public static ClaudeParseResult Parse(
        string json,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset receivedAtUtc,
        long generation,
        long sequence,
        EffectId effectId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("rate_limits", out var rateLimits)
                || rateLimits.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    TransportId.ClaudeStatusLine,
                    ErrorCode.NoSupportedWindows,
                    ErrorCategory.InvalidPayload,
                    RetryDisposition.WaitForSignal,
                    "Claude statusLine has no rate_limits object.",
                    capturedAtUtc);
            }

            var windows = ImmutableDictionary.CreateBuilder<LimitPeriod, LimitWindowCandidate>();
            var error = AddWindow(
                rateLimits,
                "five_hour",
                LimitPeriod.FiveHours,
                capturedAtUtc,
                generation,
                sequence,
                effectId,
                windows);
            if (error is not null)
            {
                return new ClaudeParseResult(null, error);
            }

            error = AddWindow(
                rateLimits,
                "seven_day",
                LimitPeriod.SevenDays,
                capturedAtUtc,
                generation,
                sequence,
                effectId,
                windows);
            if (error is not null)
            {
                return new ClaudeParseResult(null, error);
            }

            return BuildObservation(
                TransportId.ClaudeStatusLine,
                windows.ToImmutable(),
                capturedAtUtc,
                receivedAtUtc,
                generation,
                sequence,
                effectId,
                ObservationCompleteness.Partial);
        }
        catch (JsonException)
        {
            return Failure(
                TransportId.ClaudeStatusLine,
                ErrorCode.MalformedPayload,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForSignal,
                "Claude statusLine is not valid JSON.",
                capturedAtUtc);
        }
    }

    private static DomainError? AddWindow(
        JsonElement rateLimits,
        string propertyName,
        LimitPeriod period,
        DateTimeOffset capturedAtUtc,
        long generation,
        long sequence,
        EffectId effectId,
        ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Builder windows)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window)
            || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used)
            || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDecimal(out var usedValue))
        {
            return new ClaudeStatusLineError(
                ErrorCode.SchemaMismatch,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForSignal,
                UserAction.OpenDiagnostics,
                $"Claude statusLine window '{propertyName}' has an unsupported shape.",
                capturedAtUtc);
        }

        if (!window.TryGetProperty("resets_at", out var reset)
            || reset.ValueKind != JsonValueKind.Number
            || !reset.TryGetInt64(out var epoch))
        {
            return new ClaudeStatusLineError(
                ErrorCode.InvalidResetTime,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForSignal,
                UserAction.OpenDiagnostics,
                $"Claude statusLine window '{propertyName}' has no reset timestamp.",
                capturedAtUtc);
        }

        var remaining = RemainingPercent.Create(
            100m - usedValue,
            ProviderId.Claude,
            TransportId.ClaudeStatusLine,
            capturedAtUtc);
        if (!remaining.IsSuccess)
        {
            return remaining.Error;
        }

        var cursor = new ObservationCursor(generation, sequence, capturedAtUtc, null);
        windows[period] = new LimitWindowCandidate(
            period,
            remaining.Value!,
            DateTimeOffset.FromUnixTimeSeconds(epoch),
            cursor,
            new DataProvenance(TransportId.ClaudeStatusLine, capturedAtUtc, null));
        return null;
    }

    private static ClaudeParseResult BuildObservation(
        TransportId transport,
        ImmutableDictionary<LimitPeriod, LimitWindowCandidate> windows,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset receivedAtUtc,
        long generation,
        long sequence,
        EffectId effectId,
        ObservationCompleteness completeness) =>
        new(
            new ProviderObservationEnvelope(
                ProviderId.Claude,
                transport,
                generation,
                sequence,
                null,
                capturedAtUtc,
                receivedAtUtc,
                completeness,
                windows,
                effectId),
            null);

    internal static ClaudeParseResult Failure(
        TransportId transport,
        ErrorCode code,
        ErrorCategory category,
        RetryDisposition retry,
        string detail,
        DateTimeOffset occurredAtUtc) =>
        new(
            null,
            new ClaudeStatusLineError(
                code,
                category,
                retry,
                UserAction.OpenDiagnostics,
                detail,
                occurredAtUtc));
}

public static partial class ClaudeUsageParser
{
    public static ClaudeParseResult Parse(
        string json,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset receivedAtUtc,
        long generation,
        long sequence,
        EffectId effectId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("is_error", out var isError)
                && isError.ValueKind == JsonValueKind.True)
            {
                return DirectFailure(
                    TransportId.ClaudeDirectCli,
                    ErrorCode.LoginRequired,
                    ErrorCategory.Authentication,
                    RetryDisposition.WaitForUserAction,
                    "Claude /usage returned an error.",
                    capturedAtUtc);
            }

            if (!root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.String)
            {
                return DirectFailure(
                    TransportId.ClaudeDirectCli,
                    ErrorCode.SchemaMismatch,
                    ErrorCategory.InvalidPayload,
                    RetryDisposition.WaitForVersionChange,
                    "Claude /usage returned no readable result.",
                    capturedAtUtc);
            }

            var text = result.GetString() ?? string.Empty;
            var windows = ImmutableDictionary.CreateBuilder<LimitPeriod, LimitWindowCandidate>();
            var error = AddWindow(text, "session", LimitPeriod.FiveHours, capturedAtUtc, generation, sequence, windows);
            if (error is not null)
            {
                return new ClaudeParseResult(null, error);
            }

            error = AddWindow(text, "week", LimitPeriod.SevenDays, capturedAtUtc, generation, sequence, windows);
            if (error is not null)
            {
                return new ClaudeParseResult(null, error);
            }

            if (windows.Count == 0)
            {
                return DirectFailure(
                    TransportId.ClaudeDirectCli,
                    ErrorCode.NoSupportedWindows,
                    ErrorCategory.InvalidPayload,
                    RetryDisposition.WaitForVersionChange,
                    "Claude /usage returned no supported limit windows.",
                    capturedAtUtc);
            }

            return new ClaudeParseResult(
                new ProviderObservationEnvelope(
                    ProviderId.Claude,
                    TransportId.ClaudeDirectCli,
                    generation,
                    sequence,
                    null,
                    capturedAtUtc,
                    receivedAtUtc,
                    ObservationCompleteness.Complete,
                    windows.ToImmutable(),
                    effectId),
                null);
        }
        catch (JsonException)
        {
            return DirectFailure(
                TransportId.ClaudeDirectCli,
                ErrorCode.MalformedPayload,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForVersionChange,
                "Claude /usage response is not valid JSON.",
                capturedAtUtc);
        }
    }

    private static ClaudeParseResult DirectFailure(
        TransportId transport,
        ErrorCode code,
        ErrorCategory category,
        RetryDisposition retry,
        string detail,
        DateTimeOffset occurredAtUtc) =>
        new(
            null,
            new ClaudeDirectError(
                code,
                category,
                retry,
                UserAction.OpenDiagnostics,
                detail,
                occurredAtUtc));

    private static DomainError? AddWindow(
        string text,
        string windowName,
        LimitPeriod period,
        DateTimeOffset capturedAtUtc,
        long generation,
        long sequence,
        ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Builder windows)
    {
        var match = Regex.Match(
            text,
            $"Current\\s+{windowName}(?:\\s+\\(all models\\))?\\s*:\\s*(?<used>[0-9]+(?:\\.[0-9]+)?)%\\s+used\\s*[·•]\\s*resets\\s+(?<reset>[^\\r\\n(]+)\\s*\\((?<zone>[^)]+)\\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success
            || !decimal.TryParse(match.Groups["used"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var used))
        {
            return null;
        }

        if (!TryParseResetAt(
                match.Groups["reset"].Value.Trim(),
                match.Groups["zone"].Value.Trim(),
                capturedAtUtc,
                out var resetAt))
        {
            return new ClaudeDirectError(
                ErrorCode.InvalidResetTime,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForVersionChange,
                UserAction.OpenDiagnostics,
                $"Claude /usage window '{windowName}' has an invalid reset.",
                capturedAtUtc);
        }

        var remaining = RemainingPercent.Create(
            100m - used,
            ProviderId.Claude,
            TransportId.ClaudeDirectCli,
            capturedAtUtc);
        if (!remaining.IsSuccess)
        {
            return remaining.Error;
        }

        var cursor = new ObservationCursor(generation, sequence, capturedAtUtc, null);
        windows[period] = new LimitWindowCandidate(
            period,
            remaining.Value!,
            resetAt,
            cursor,
            new DataProvenance(TransportId.ClaudeDirectCli, capturedAtUtc, null));
        return null;
    }

    private static bool TryParseResetAt(
        string value,
        string zoneName,
        DateTimeOffset observedAtUtc,
        out DateTimeOffset resetAt)
    {
        var commaIndex = value.IndexOf(',');
        var withYear = commaIndex >= 0
            ? value.Insert(commaIndex, $" {observedAtUtc.Year}")
            : $"{value} {observedAtUtc.Year}";
        if (!DateTime.TryParse(
                withYear,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var localTime))
        {
            resetAt = default;
            return false;
        }

        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        var zone = FindTimeZone(zoneName);
        resetAt = new DateTimeOffset(localTime, zone.GetUtcOffset(localTime));
        if (resetAt < observedAtUtc.AddHours(-1))
        {
            var nextYear = localTime.AddYears(1);
            resetAt = new DateTimeOffset(nextYear, zone.GetUtcOffset(nextYear));
        }

        return true;
    }

    private static TimeZoneInfo FindTimeZone(string zoneName)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
