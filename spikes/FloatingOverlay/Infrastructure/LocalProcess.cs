using System.Diagnostics;
using System.IO;

namespace LLMLimitsWidget.FloatingOverlay;

internal static class LocalProcess
{
    public static async Task<string> CaptureAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The provider process did not start.");
        }

        WidgetLogger.Debug(
            "ProviderProcess",
            "process_started",
            ("executable", Path.GetFileName(startInfo.FileName)),
            ("timeoutSeconds", timeout.TotalSeconds),
            ("processId", process.Id));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                WidgetLogger.Warning(
                    "ProviderProcess",
                    "process_exited_with_error",
                    ("executable", Path.GetFileName(startInfo.FileName)),
                    ("exitCode", process.ExitCode),
                    ("stderrLength", error.Length));
                throw new InvalidOperationException(
                    $"The provider process exited with code {process.ExitCode}.");
            }

            WidgetLogger.Debug(
                "ProviderProcess",
                "process_completed",
                ("executable", Path.GetFileName(startInfo.FileName)),
                ("exitCode", process.ExitCode),
                ("stdoutLength", output.Length),
                ("stderrLength", error.Length));
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            WidgetLogger.Warning(
                "ProviderProcess",
                "process_timed_out",
                ("executable", Path.GetFileName(startInfo.FileName)),
                ("timeoutSeconds", timeout.TotalSeconds));
            throw new TimeoutException(
                $"The provider process exceeded the {timeout.TotalSeconds:0}s timeout.");
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
}
