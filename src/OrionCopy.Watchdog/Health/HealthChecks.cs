using System.Diagnostics;
using System.Net.Sockets;
using OrionCopy.Watchdog.Config;

namespace OrionCopy.Watchdog.Health;

public sealed class HttpHealthCheck : IHealthCheck
{
    public string Kind => "http";

    // Shared client: per-request timeouts are enforced with a linked CTS, so one
    // slow endpoint never blocks other services' checks.
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    public async Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return CheckResult.Fail(Kind, $"invalid url '{config.Url}'", sw.Elapsed);

        var timeout = Duration.TryParse(config.Timeout, out var t) ? t : TimeSpan.FromSeconds(5);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var method = string.Equals(config.Method, "POST", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Post : HttpMethod.Get;
            using var request = new HttpRequestMessage(method, uri);
            foreach (var (key, value) in config.Headers)
            {
                try { request.Headers.TryAddWithoutValidation(key, value); } catch { /* ignore bad header */ }
            }

            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            if ((int)response.StatusCode != config.ExpectedStatus)
                return CheckResult.Fail(Kind,
                    $"GET {RedactUrl(uri)} returned {(int)response.StatusCode} (expected {config.ExpectedStatus})",
                    sw.Elapsed);

            if (!string.IsNullOrEmpty(config.Contains))
            {
                var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                if (!body.Contains(config.Contains, StringComparison.OrdinalIgnoreCase))
                    return CheckResult.Fail(Kind,
                        $"GET {RedactUrl(uri)} returned {config.ExpectedStatus} but body did not contain '{config.Contains}'",
                        sw.Elapsed);
            }

            return CheckResult.Pass(Kind,
                $"GET {RedactUrl(uri)} returned {config.ExpectedStatus}", sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail(Kind, $"GET {config.Url} timed out after {timeout.TotalSeconds:F0}s", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return CheckResult.Fail(Kind, $"GET {config.Url} failed: {Trim(ex.Message)}", sw.Elapsed);
        }
    }

    private static string RedactUrl(Uri uri) => uri.GetComponents(
        UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path,
        UriFormat.Unescaped);

    private static string Trim(string message) =>
        message.Length > 160 ? message[..160] + "..." : message;
}

public sealed class TcpHealthCheck : IHealthCheck
{
    public string Kind => "tcp";

    public async Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var timeout = Duration.TryParse(config.Timeout, out var t) ? t : TimeSpan.FromSeconds(5);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(config.Host ?? "127.0.0.1", config.Port, linked.Token)
                .ConfigureAwait(false);
            return CheckResult.Pass(Kind, $"tcp {config.Host}:{config.Port} accepted connection", sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail(Kind, $"tcp {config.Host}:{config.Port} timed out after {timeout.TotalSeconds:F0}s", sw.Elapsed);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 160 ? ex.Message[..160] + "..." : ex.Message;
            return CheckResult.Fail(Kind, $"tcp {config.Host}:{config.Port} refused: {msg}", sw.Elapsed);
        }
    }
}

public sealed class ProcessHealthCheck : IHealthCheck
{
    public string Kind => "process";

    public Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var name = (config.ProcessName ?? "").Trim();
        // Allow "foo.exe" in config; GetProcessesByName wants "foo".
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        try
        {
            var found = Process.GetProcessesByName(name);
            try
            {
                if (found.Length > 0)
                    return Task.FromResult(CheckResult.Pass(Kind,
                        $"process '{config.ProcessName}' is running (pid {found[0].Id})", sw.Elapsed));
                return Task.FromResult(CheckResult.Fail(Kind,
                    $"process '{config.ProcessName}' is not running", sw.Elapsed));
            }
            finally
            {
                foreach (var p in found) p.Dispose();
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(CheckResult.Fail(Kind,
                $"process lookup '{config.ProcessName}' failed: {ex.Message}", sw.Elapsed));
        }
    }
}

public sealed class WindowsServiceHealthCheck : IHealthCheck
{
    public string Kind => "windows_service";

    public Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.Fail(Kind,
                $"windows_service check for '{config.ServiceName}' is not supported on this platform", sw.Elapsed));

        try
        {
            using var controller = new System.ServiceProcess.ServiceController(config.ServiceName!);
            var status = controller.Status;
            return Task.FromResult(status == System.ServiceProcess.ServiceControllerStatus.Running
                ? CheckResult.Pass(Kind, $"service '{config.ServiceName}' is running", sw.Elapsed)
                : CheckResult.Fail(Kind, $"service '{config.ServiceName}' status is {status}", sw.Elapsed));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CheckResult.Fail(Kind,
                $"service '{config.ServiceName}' query failed: {ex.Message}", sw.Elapsed));
        }
    }
}

public sealed class SystemdHealthCheck : IHealthCheck
{
    public string Kind => "systemd";

    public async Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await ShellRunner.RunAsync(
            "systemctl", $"is-active {Quote(config.ServiceName ?? "")}",
            TimeSpan.FromSeconds(10), workingDirectory: null, ct).ConfigureAwait(false);

        if (!result.Executed)
            return CheckResult.Fail(Kind, "systemctl is not available on this machine", sw.Elapsed);

        return result.ExitCode == 0
            ? CheckResult.Pass(Kind, $"systemd unit '{config.ServiceName}' is active", sw.Elapsed)
            : CheckResult.Fail(Kind,
                $"systemd unit '{config.ServiceName}' is {(result.Output.Trim().Length > 0 ? result.Output.Trim() : "not active")}",
                sw.Elapsed);
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}

public sealed class CommandHealthCheck : IHealthCheck
{
    public string Kind => "command";

    public async Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var timeout = Duration.TryParse(config.Timeout, out var t) ? t : TimeSpan.FromSeconds(10);

        var (file, prefix) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c ")
            : ("/bin/sh", "-c ");

        var result = await ShellRunner.RunAsync(
            file, prefix + config.Command,
            timeout, config.WorkingDirectory, ct).ConfigureAwait(false);

        if (!result.Executed)
            return CheckResult.Fail(Kind, "command check could not start a shell", sw.Elapsed);

        return result.ExitCode == 0
            ? CheckResult.Pass(Kind, "health command exited 0 (healthy)", sw.Elapsed)
            : CheckResult.Fail(Kind,
                $"health command exited {result.ExitCode} (unhealthy)" +
                (result.Output.Trim().Length > 0 ? $": {Snippet(result.Output)}" : ""),
                sw.Elapsed);
    }

    private static string Snippet(string output)
    {
        var oneLine = output.ReplaceLineEndings(" ").Trim();
        return oneLine.Length > 160 ? oneLine[..160] + "..." : oneLine;
    }
}
