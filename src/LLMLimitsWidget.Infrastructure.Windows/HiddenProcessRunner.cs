using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;

namespace LLMLimitsWidget.Infrastructure.Windows;

public enum ProcessFailureKind
{
    ExecutableNotFound,
    StartFailed,
    TimedOut,
    ExitCode,
    OutputUnavailable
}

public sealed class HiddenProcessException(
    ProcessFailureKind kind,
    string message,
    Exception? inner = null,
    int? exitCode = null)
    : Exception(message, inner)
{
    public ProcessFailureKind Kind { get; } = kind;
    public int? ExitCode { get; } = exitCode;
}

public sealed record HiddenProcessRequest(
    string FileName,
    ImmutableArray<string> Arguments,
    TimeSpan Timeout,
    ImmutableDictionary<string, string>? Environment = null);

public sealed record HiddenProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IHiddenProcessRunner
{
    Task<HiddenProcessResult> RunAsync(
        HiddenProcessRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs provider CLIs without creating a visible console window. The process is
/// always terminated on timeout/cancellation, including its child tree.
/// </summary>
public sealed class WindowsHiddenProcessRunner : IHiddenProcessRunner
{
    public async Task<HiddenProcessResult> RunAsync(
        HiddenProcessRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout > TimeSpan.Zero && request.Timeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(request.Timeout);
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request)
        };

        try
        {
            if (!process.Start())
            {
                throw new HiddenProcessException(
                    ProcessFailureKind.StartFailed,
                    $"Provider process '{request.FileName}' did not start.");
            }
        }
        catch (FileNotFoundException exception)
        {
            throw new HiddenProcessException(
                ProcessFailureKind.ExecutableNotFound,
                $"Provider executable '{request.FileName}' was not found.",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw new HiddenProcessException(
                ProcessFailureKind.StartFailed,
                $"Provider process '{request.FileName}' could not start.",
                exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new HiddenProcessResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HiddenProcessException(
                ProcessFailureKind.TimedOut,
                $"Provider process '{request.FileName}' exceeded {request.Timeout.TotalSeconds:0.#} seconds.");
        }
        catch (IOException exception)
        {
            throw new HiddenProcessException(
                ProcessFailureKind.OutputUnavailable,
                $"Provider process '{request.FileName}' output became unavailable.",
                exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(HiddenProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var entry in request.Environment)
            {
                info.Environment[entry.Key] = entry.Value;
            }
        }

        return info;
    }
}
