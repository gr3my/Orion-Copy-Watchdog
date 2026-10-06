using System.Diagnostics;

namespace OrionCopy.Watchdog.Health;

/// <summary>Runs a local process with a timeout. Never throws for target failures;
/// <see cref="Executed"/> is false only when the process could not be started.</summary>
public sealed record ShellRunResult(bool Executed, int ExitCode, string Output);

public static class ShellRunner
{
    public static async Task<ShellRunResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        string? workingDirectory,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch
        {
            return new ShellRunResult(false, -1, "");
        }

        if (process is null)
            return new ShellRunResult(false, -1, "");

        using (process)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                var partial = SafeRead(process);
                return new ShellRunResult(true, 124, partial + " [timed out]");
            }

            return new ShellRunResult(true, process.ExitCode, SafeRead(process));
        }
    }

    private static string SafeRead(Process process)
    {
        try
        {
            // Process already exited, so these complete synchronously.
            return (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
        }
        catch
        {
            return "";
        }
    }
}
