using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Provider.Claude;

namespace LLMLimitsWidget.Infrastructure.Windows;

/// <summary>
/// Reads the statusLine snapshot only. It does not publish into AppStore; the
/// caller dispatches the resulting observation as ObservationReceivedCommand.
/// </summary>
public sealed class ClaudeStatusLineFileReader
{
    private readonly string _snapshotPath;
    private readonly TimeProvider _clock;

    public ClaudeStatusLineFileReader(string snapshotPath, TimeProvider? clock = null)
    {
        _snapshotPath = snapshotPath;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ClaudeParseResult> ReadAsync(
        long generation,
        long sequence,
        EffectId effectId,
        CancellationToken cancellationToken)
    {
        var receivedAtUtc = _clock.GetUtcNow();
        try
        {
            var metadata = new FileInfo(_snapshotPath);
            if (!metadata.Exists)
            {
                return Failure(
                    ErrorCode.StatusLineNotConfigured,
                    ErrorCategory.Configuration,
                    RetryDisposition.WaitForSignal,
                    UserAction.RepairConfiguration,
                    "Claude statusLine snapshot does not exist.",
                    receivedAtUtc);
            }

            var json = await File.ReadAllTextAsync(_snapshotPath, cancellationToken).ConfigureAwait(false);
            var revision = $"{metadata.LastWriteTimeUtc.Ticks}:{metadata.Length}";
            return ClaudeStatusLineParser.Parse(
                json,
                metadata.LastWriteTimeUtc,
                receivedAtUtc,
                generation,
                sequence,
                effectId,
                revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException exception)
        {
            return Failure(
                ErrorCode.IoUnavailable,
                ErrorCategory.Transient,
                RetryDisposition.WaitForSignal,
                UserAction.OpenDiagnostics,
                $"Claude statusLine snapshot could not be read: {exception.Message}",
                receivedAtUtc);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                ErrorCode.PermissionDenied,
                ErrorCategory.Configuration,
                RetryDisposition.WaitForUserAction,
                UserAction.RepairConfiguration,
                $"Claude statusLine snapshot is not accessible: {exception.Message}",
                receivedAtUtc);
        }
    }

    private static ClaudeParseResult Failure(
        ErrorCode code,
        ErrorCategory category,
        RetryDisposition retry,
        UserAction action,
        string diagnostic,
        DateTimeOffset occurredAtUtc) =>
        new(
            null,
            new ClaudeStatusLineError(
                code,
                category,
                retry,
                action,
                diagnostic,
                occurredAtUtc));
}
