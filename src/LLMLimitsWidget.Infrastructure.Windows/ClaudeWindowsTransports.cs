using System.Collections.Immutable;
using LLMLimitsWidget.Application;
using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Provider.Claude;

namespace LLMLimitsWidget.Infrastructure.Windows;

public sealed class ClaudeDirectCliTransport : IProviderAttemptTransport
{
    private static readonly ImmutableArray<string> UsageArguments =
    [
        "-p", "/usage",
        "--output-format", "json",
        "--tools", string.Empty,
        "--no-session-persistence",
        "--setting-sources", "user",
        "--permission-mode", "plan"
    ];

    private readonly string _executablePath;
    private readonly IHiddenProcessRunner _processRunner;
    private readonly TimeProvider _clock;

    public ClaudeDirectCliTransport(
        string executablePath,
        IHiddenProcessRunner processRunner,
        TimeProvider? clock = null)
    {
        _executablePath = executablePath;
        _processRunner = processRunner;
        _clock = clock ?? TimeProvider.System;
    }

    public ProviderId Provider => ProviderId.Claude;

    public async Task<AttemptOutcome> AcquireAsync(
        AttemptContext context,
        CancellationToken cancellationToken)
    {
        var capturedAtUtc = _clock.GetUtcNow();
        var timeout = context.DeadlineUtc - capturedAtUtc;
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(1);
        }

        HiddenProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new HiddenProcessRequest(_executablePath, UsageArguments, timeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (HiddenProcessException exception)
        {
            return new AttemptFailed(MapProcessError(exception, capturedAtUtc));
        }

        if (result.ExitCode != 0)
        {
            return new AttemptFailed(new ClaudeDirectError(
                ErrorCode.ProcessExited,
                ErrorCategory.Transient,
                RetryDisposition.Backoff,
                UserAction.OpenDiagnostics,
                $"Claude CLI exited with code {result.ExitCode}.",
                capturedAtUtc));
        }

        var parsed = ClaudeUsageParser.Parse(
            result.StandardOutput,
            capturedAtUtc,
            _clock.GetUtcNow(),
            context.Generation,
            context.Sequence,
            context.Effect);
        return parsed.IsSuccess
            ? new AttemptSucceeded(parsed.Observation!)
            : new AttemptFailed(parsed.Error!);
    }

    private static ClaudeDirectError MapProcessError(
        HiddenProcessException exception,
        DateTimeOffset occurredAtUtc) =>
        new(
            exception.Kind switch
            {
                ProcessFailureKind.ExecutableNotFound => ErrorCode.ExecutableNotFound,
                ProcessFailureKind.TimedOut => ErrorCode.RequestTimeout,
                ProcessFailureKind.OutputUnavailable => ErrorCode.IoUnavailable,
                _ => ErrorCode.ProcessStartFailed
            },
            exception.Kind == ProcessFailureKind.ExecutableNotFound
                ? ErrorCategory.Configuration
                : ErrorCategory.Transient,
            exception.Kind == ProcessFailureKind.ExecutableNotFound
                ? RetryDisposition.WaitForUserAction
                : RetryDisposition.Backoff,
            exception.Kind == ProcessFailureKind.ExecutableNotFound
                ? UserAction.RepairConfiguration
                : UserAction.OpenDiagnostics,
            exception.Message,
            occurredAtUtc);
}
