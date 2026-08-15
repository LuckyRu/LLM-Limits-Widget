using System.Collections.Immutable;
using System.Text.Json;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Provider.Codex;

public sealed record CodexParseResult(
    ProviderObservationEnvelope? Observation,
    DomainError? Error)
{
    public bool IsSuccess => Observation is not null && Error is null;
}

/// <summary>
/// Pure Codex app-server protocol parser. It knows JSON and Codex protocol
/// shapes, but has no process, file, clock, logging or WPF dependency.
/// </summary>
public static class CodexRateLimitsParser
{
    public static CodexParseResult Parse(
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
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                var code = message?.Contains("login", StringComparison.OrdinalIgnoreCase) == true
                    ? ErrorCode.LoginRequired
                    : ErrorCode.ProcessExited;
                return Failure(code, ErrorCategory.Transient, RetryDisposition.Backoff, message);
            }

            if (!root.TryGetProperty("result", out var result)
                || !TryGetCodexBucket(result, out var bucket))
            {
                return Failure(
                    ErrorCode.CapabilityMissing,
                    ErrorCategory.Compatibility,
                    RetryDisposition.WaitForVersionChange,
                    "Codex rate-limit bucket is unavailable.");
            }

            var windows = ImmutableDictionary.CreateBuilder<LimitPeriod, LimitWindowCandidate>();
            var parseError = AddWindow(
                bucket,
                "primary",
                capturedAtUtc,
                generation,
                sequence,
                effectId,
                windows);
            if (parseError is not null)
            {
                return new CodexParseResult(null, parseError);
            }

            parseError = AddWindow(
                bucket,
                "secondary",
                capturedAtUtc,
                generation,
                sequence,
                effectId,
                windows);
            if (parseError is not null)
            {
                return new CodexParseResult(null, parseError);
            }

            if (!windows.ContainsKey(LimitPeriod.SevenDays))
            {
                return Failure(
                    ErrorCode.NoSupportedWindows,
                    ErrorCategory.InvalidPayload,
                    RetryDisposition.WaitForVersionChange,
                    "Codex returned no supported seven-day window.");
            }

            return new CodexParseResult(
                new ProviderObservationEnvelope(
                    ProviderId.Codex,
                    TransportId.CodexAppServer,
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
            return Failure(
                ErrorCode.MalformedPayload,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForVersionChange,
                "Codex response is not valid JSON.");
        }
    }

    private static DomainError? AddWindow(
        JsonElement bucket,
        string propertyName,
        DateTimeOffset capturedAtUtc,
        long generation,
        long sequence,
        EffectId effectId,
        ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Builder windows)
    {
        if (!bucket.TryGetProperty(propertyName, out var window)
            || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("windowDurationMins", out var duration)
            || duration.ValueKind != JsonValueKind.Number
            || !duration.TryGetInt32(out var durationMinutes)
            || !window.TryGetProperty("usedPercent", out var usedPercent)
            || usedPercent.ValueKind != JsonValueKind.Number
            || !usedPercent.TryGetDecimal(out var used))
        {
            return Error(
                ErrorCode.SchemaMismatch,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForVersionChange,
                $"Codex window '{propertyName}' has an unsupported shape.");
        }

        var period = durationMinutes switch
        {
            300 => LimitPeriod.FiveHours,
            10080 => LimitPeriod.SevenDays,
            _ => (LimitPeriod?)null
        };
        if (period is null)
        {
            return null;
        }

        if (!window.TryGetProperty("resetsAt", out var reset)
            || reset.ValueKind != JsonValueKind.Number
            || !reset.TryGetInt64(out var epoch))
        {
            return Error(
                ErrorCode.InvalidResetTime,
                ErrorCategory.InvalidPayload,
                RetryDisposition.WaitForVersionChange,
                $"Codex window '{propertyName}' has no valid reset timestamp.");
        }

        var remaining = RemainingPercent.Create(
            100m - used,
            ProviderId.Codex,
            TransportId.CodexAppServer,
            capturedAtUtc);
        if (!remaining.IsSuccess)
        {
            return remaining.Error;
        }

        var cursor = new ObservationCursor(generation, sequence, capturedAtUtc, null);
        windows[period.Value] = new LimitWindowCandidate(
            period.Value,
            remaining.Value!,
            DateTimeOffset.FromUnixTimeSeconds(epoch),
            cursor,
            new DataProvenance(TransportId.CodexAppServer, capturedAtUtc, null));
        return null;
    }

    private static bool TryGetCodexBucket(JsonElement result, out JsonElement bucket)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId)
            && byId.ValueKind == JsonValueKind.Object
            && byId.TryGetProperty("codex", out bucket))
        {
            return true;
        }

        if (result.TryGetProperty("rateLimits", out var rateLimits)
            && rateLimits.ValueKind == JsonValueKind.Object
            && (!rateLimits.TryGetProperty("limitId", out var limitId)
                || string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase)))
        {
            bucket = rateLimits;
            return true;
        }

        bucket = default;
        return false;
    }

    private static CodexParseResult Failure(
        ErrorCode code,
        ErrorCategory category,
        RetryDisposition retry,
        string? detail) =>
        new(
            null,
            Error(code, category, retry, detail));

    private static DomainError Error(
        ErrorCode code,
        ErrorCategory category,
        RetryDisposition retry,
        string? detail) =>
        new CodexAcquisitionError(
            code,
            category,
            retry,
            code == ErrorCode.LoginRequired ? UserAction.SignIn : UserAction.OpenDiagnostics,
            detail ?? code.ToString(),
            DateTimeOffset.UnixEpoch);
}
