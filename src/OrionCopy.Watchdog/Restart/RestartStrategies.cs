using System.Diagnostics;
using OrionCopy.Watchdog.Health;

namespace OrionCopy.Watchdog.Restart;

/// <summary>Restarts an executable: graceful close first, kill only if necessary, then launch.</summary>
public sealed class ProcessRestartStrategy : IRestartStrategy
{
    public string Kind => "process";

    public async Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct)
    {
        var processes = FindProcesses(ctx.Service);
        if (processes.Count == 0)
            return RestartResult.Ok("no matching process was running; nothing to stop");

        ctx.Log.Info(ctx.Service.Name, $"stopping {processes.Count} process(es) gracefully", ("timeout", ctx.GracefulTimeout.ToString()));

        // Ask nicely first (lets game servers flush state / deregister).
        foreach (var p in processes)
        {
            try { p.CloseMainWindow(); } catch { /* headless processes have no window */ }
        }

        var deadline = DateTime.UtcNow + ctx.GracefulTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Refresh(processes);
            if (processes.Count == 0)
                return RestartResult.Ok("process(es) stopped gracefully");
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        Refresh(processes);
        if (processes.Count == 0)
            return RestartResult.Ok("process(es) stopped gracefully");

        // Still alive past the grace period: force-terminate. Kill first, always,
        // so a wedged process cannot hold its port while the replacement starts.
        foreach (var p in processes)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* raced exit */ }
        }
        foreach (var p in processes) p.Dispose();

        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        var remaining = FindProcesses(ctx.Service);
        foreach (var p in remaining) p.Dispose();
        return remaining.Count == 0
            ? RestartResult.Ok("process(es) force-terminated after graceful timeout")
            : RestartResult.Fail($"{remaining.Count} process(es) are still alive after kill");
    }

    public Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct)
    {
        var exe = ctx.Service.Restart.Executable ?? "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = ctx.Service.Restart.Arguments ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var workdir = ctx.Service.Restart.WorkingDirectory;
            if (!string.IsNullOrWhiteSpace(workdir) && Directory.Exists(workdir))
                psi.WorkingDirectory = workdir;

            var process = Process.Start(psi);
            if (process is null)
                return Task.FromResult(RestartResult.Fail($"failed to launch '{exe}' (no process handle)"));
            using (process)
            {
                if (process.HasExited && process.ExitCode != 0)
                    return Task.FromResult(RestartResult.Fail($"'{exe}' exited immediately with code {process.ExitCode}"));
                return Task.FromResult(RestartResult.Ok($"launched '{exe}' (pid {process.Id})"));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(RestartResult.Fail($"failed to launch '{exe}': {ex.Message}"));
        }
    }

    /// <summary>Locate candidate processes: explicit process check names first,
    /// then the executable file name. Shared with health evaluation for dynamic lifecycle.</summary>
    public static List<Process> FindProcesses(Config.ServiceConfig service)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in service.Checks)
        {
            if (string.Equals(check.Type, "process", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(check.ProcessName))
                names.Add(StripExe(check.ProcessName!));
        }
        if (!string.IsNullOrWhiteSpace(service.Restart.Executable))
        {
            try { names.Add(StripExe(Path.GetFileNameWithoutExtension(service.Restart.Executable!))); }
            catch { /* non-path executable like "myserver" */ names.Add(service.Restart.Executable!); }
        }

        var found = new List<Process>();
        foreach (var name in names)
        {
            try { found.AddRange(Process.GetProcessesByName(name)); }
            catch { /* insufficient rights for some entries; keep the rest */ }
        }
        return found;
    }

    private static void Refresh(List<Process> processes)
    {
        for (var i = processes.Count - 1; i >= 0; i--)
        {
            try
            {
                processes[i].Refresh();
                if (processes[i].HasExited)
                {
                    processes[i].Dispose();
                    processes.RemoveAt(i);
                }
            }
            catch
            {
                try { processes[i].Dispose(); } catch { }
                processes.RemoveAt(i);
            }
        }
    }

    private static string StripExe(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

public sealed class WindowsServiceRestartStrategy : IRestartStrategy
{
    public string Kind => "windows_service";

    public async Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return RestartResult.Fail("windows_service restart is only supported on Windows");
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ctx.Service.Restart.ServiceName!);
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                return RestartResult.Ok($"service '{sc.ServiceName}' is already stopped");
            sc.Stop();
            await WaitForAsync(sc,
                System.ServiceProcess.ServiceControllerStatus.Stopped, ctx.GracefulTimeout).ConfigureAwait(false);
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped
                ? RestartResult.Ok($"service '{sc.ServiceName}' stopped")
                : RestartResult.Fail($"service '{sc.ServiceName}' did not stop within {ctx.GracefulTimeout.TotalSeconds:F0}s (status {sc.Status})");
        }
        catch (Exception ex)
        {
            return RestartResult.Fail($"failed to stop service '{ctx.Service.Restart.ServiceName}': {ex.Message}");
        }
    }

    public async Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return RestartResult.Fail("windows_service restart is only supported on Windows");
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ctx.Service.Restart.ServiceName!);
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                return RestartResult.Ok($"service '{sc.ServiceName}' is already running");
            sc.Start();
            await WaitForAsync(sc,
                System.ServiceProcess.ServiceControllerStatus.Running, ctx.GracefulTimeout).ConfigureAwait(false);
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running
                ? RestartResult.Ok($"service '{sc.ServiceName}' started")
                : RestartResult.Fail($"service '{sc.ServiceName}' did not start within {ctx.GracefulTimeout.TotalSeconds:F0}s (status {sc.Status})");
        }
        catch (Exception ex)
        {
            return RestartResult.Fail($"failed to start service '{ctx.Service.Restart.ServiceName}': {ex.Message}");
        }
    }

    private static async Task WaitForAsync(
        System.ServiceProcess.ServiceController sc,
        System.ServiceProcess.ServiceControllerStatus want,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                sc.Refresh();
                if (sc.Status == want)
                    return;
            }
            catch { return; }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }
}

public sealed class SystemdRestartStrategy : IRestartStrategy
{
    public string Kind => "systemd";

    public async Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct)
    {
        var unit = ctx.Service.Restart.ServiceName ?? "";
        var result = await ShellRunner.RunAsync("systemctl", $"stop {Quote(unit)}",
            ctx.GracefulTimeout + TimeSpan.FromSeconds(10), null, ct).ConfigureAwait(false);
        if (!result.Executed)
            return RestartResult.Fail("systemctl is not available on this machine");
        return result.ExitCode == 0
            ? RestartResult.Ok($"systemd unit '{unit}' stopped")
            : RestartResult.Fail($"systemctl stop {unit} failed: {Snippet(result.Output)}");
    }

    public async Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct)
    {
        var unit = ctx.Service.Restart.ServiceName ?? "";
        var result = await ShellRunner.RunAsync("systemctl", $"start {Quote(unit)}",
            ctx.GracefulTimeout + TimeSpan.FromSeconds(10), null, ct).ConfigureAwait(false);
        if (!result.Executed)
            return RestartResult.Fail("systemctl is not available on this machine");
        return result.ExitCode == 0
            ? RestartResult.Ok($"systemd unit '{unit}' started")
            : RestartResult.Fail($"systemctl start {unit} failed: {Snippet(result.Output)}");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string Snippet(string output)
    {
        var oneLine = output.ReplaceLineEndings(" ").Trim();
        return oneLine.Length switch
        {
            0 => "no output",
            > 200 => oneLine[..200] + "...",
            _ => oneLine,
        };
    }
}

public sealed class CommandRestartStrategy : IRestartStrategy
{
    public string Kind => "command";

    public Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct)
    {
        var stop = ctx.Service.Restart.StopCommand;
        if (string.IsNullOrWhiteSpace(stop))
            return Task.FromResult(RestartResult.Ok("no stop_command configured; skipping graceful stop"));
        return RunCommandAsync(ctx, stop, "stop", ct);
    }

    public Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct)
    {
        var start = !string.IsNullOrWhiteSpace(ctx.Service.Restart.StartCommand)
            ? ctx.Service.Restart.StartCommand
            : ctx.Service.Restart.Command;
        if (string.IsNullOrWhiteSpace(start))
            return Task.FromResult(RestartResult.Fail("command restart requires 'command' or 'start_command'"));
        return RunCommandAsync(ctx, start, "start", ct);
    }

    public async Task<RestartResult> RestartAsync(RestartContext ctx, CancellationToken ct)
    {
        // A single restart command replaces stop+start when no split commands exist.
        if (string.IsNullOrWhiteSpace(ctx.Service.Restart.StopCommand) &&
            string.IsNullOrWhiteSpace(ctx.Service.Restart.StartCommand) &&
            !string.IsNullOrWhiteSpace(ctx.Service.Restart.Command))
            return await RunCommandAsync(ctx, ctx.Service.Restart.Command, "restart", ct).ConfigureAwait(false);

        // Same stop-then-start sequence as the default interface implementation,
        // inlined to avoid re-dispatching to this override.
        var stop = await StopAsync(ctx, ct).ConfigureAwait(false);
        if (!stop.Success)
            return RestartResult.Fail($"graceful stop failed, aborting restart: {stop.Detail}");
        return await StartAsync(ctx, ct).ConfigureAwait(false);
    }

    private static async Task<RestartResult> RunCommandAsync(
        RestartContext ctx, string command, string phase, CancellationToken ct)
    {
        var (file, prefix) = OperatingSystem.IsWindows() ? ("cmd.exe", "/c ") : ("/bin/sh", "-c ");
        var timeout = ctx.GracefulTimeout + TimeSpan.FromSeconds(30);
        var result = await ShellRunner.RunAsync(file, prefix + command,
            timeout, ctx.Service.Restart.WorkingDirectory, ct).ConfigureAwait(false);

        if (!result.Executed)
            return RestartResult.Fail($"restart {phase} command could not start a shell");
        return result.ExitCode == 0
            ? RestartResult.Ok($"restart {phase} command succeeded")
            : RestartResult.Fail($"restart {phase} command exited {result.ExitCode}");
    }
}

public sealed class HttpRestartStrategy : IRestartStrategy
{
    public string Kind => "http";

    private static readonly HttpClient Client = new();

    public Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct) =>
        Task.FromResult(RestartResult.Ok("http restart has no graceful stop; proceeding to management call"));

    public async Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct)
    {
        var url = ctx.Service.Restart.Url ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return RestartResult.Fail($"http restart url '{url}' is invalid");

        try
        {
            var method = string.Equals(ctx.Service.Restart.Method, "GET", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Get : HttpMethod.Post;
            using var request = new HttpRequestMessage(method, uri);
            var authEnv = ctx.Service.Restart.AuthorizationEnv;
            if (!string.IsNullOrWhiteSpace(authEnv))
            {
                var secret = Environment.GetEnvironmentVariable(authEnv!);
                if (string.IsNullOrEmpty(secret))
                    return RestartResult.Fail($"http restart: environment variable '{authEnv}' is not set");
                // The secret value is used here but never logged.
                request.Headers.TryAddWithoutValidation("Authorization", secret);
            }

            using var cts = new CancellationTokenSource(ctx.GracefulTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var response = await Client.SendAsync(request, linked.Token).ConfigureAwait(false);
            return (int)response.StatusCode == ctx.Service.Restart.ExpectedStatus
                ? RestartResult.Ok($"management endpoint returned {ctx.Service.Restart.ExpectedStatus}")
                : RestartResult.Fail($"management endpoint returned {(int)response.StatusCode} (expected {ctx.Service.Restart.ExpectedStatus})");
        }
        catch (Exception ex)
        {
            return RestartResult.Fail($"management endpoint call failed: {ex.Message}");
        }
    }
}

/// <summary>Monitoring-only services: recovery is a logged no-op.</summary>
public sealed class NoneRestartStrategy : IRestartStrategy
{
    public string Kind => "none";

    public Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct) =>
        Task.FromResult(RestartResult.Ok("restart type is 'none' (monitoring only)"));

    public Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct) =>
        Task.FromResult(RestartResult.Fail(
            "restart type is 'none': automatic recovery is disabled for this service; operator action required"));
}

public static class RestartStrategyFactory
{
    private static readonly Dictionary<string, IRestartStrategy> Strategies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["process"] = new ProcessRestartStrategy(),
            ["windows_service"] = new WindowsServiceRestartStrategy(),
            ["systemd"] = new SystemdRestartStrategy(),
            ["command"] = new CommandRestartStrategy(),
            ["http"] = new HttpRestartStrategy(),
            ["none"] = new NoneRestartStrategy(),
        };

    public static IRestartStrategy Get(string type) =>
        Strategies.TryGetValue(type ?? "none", out var strategy) ? strategy : Strategies["none"];
}
