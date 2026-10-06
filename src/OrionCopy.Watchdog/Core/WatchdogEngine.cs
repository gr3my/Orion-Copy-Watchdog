using System.Text.Json;
using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Health;
using OrionCopy.Watchdog.Logging;
using OrionCopy.Watchdog.Notifications;
using OrionCopy.Watchdog.Reporting;
using OrionCopy.Watchdog.State;

namespace OrionCopy.Watchdog.Core;

public sealed record ControlCommand(string Id, string Action, string? Service, DateTime RequestedAt);
public sealed record ControlResult(string Id, bool Ok, string Message, DateTime CompletedAt);

/// <summary>
/// Owns all service monitors: concurrent loops, dependency gating, control-file
/// commands from the CLI, config reload, and optional status reporting.
/// One broken service never takes down the engine.
/// </summary>
public sealed class WatchdogEngine
{
    private readonly IWatchdogLogger _log;
    private WebhookNotifier _notifications;
    private IStatusReporter _reporter = NoOpStatusReporter.Instance;
    private TimeSpan _reportInterval = TimeSpan.FromSeconds(60);

    private WatchdogConfig _config;
    private readonly string _configPath;
    private StateStore _store = null!;

    private sealed class MonitorEntry
    {
        public required ServiceMonitor Monitor;
        public required CancellationTokenSource Cts;
        public Task? Loop;
    }

    private readonly Dictionary<string, MonitorEntry> _monitors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationToken _shutdownToken;

    public WatchdogEngine(WatchdogConfig config, string configPath, IWatchdogLogger log)
    {
        _config = config;
        _configPath = configPath;
        _log = log;
        _notifications = BuildNotifier(config);
        RebuildInfra();
    }

    private WebhookNotifier BuildNotifier(WatchdogConfig config) => new(
        config.Notifications.Webhook.Enabled,
        config.Notifications.Webhook.Url,
        Duration.TryParse(config.Notifications.Webhook.Timeout, out var t) ? t : TimeSpan.FromSeconds(10),
        _log);

    private void RebuildInfra()
    {
        var stateDir = string.IsNullOrWhiteSpace(_config.Watchdog.StateDirectory)
            ? "./state" : _config.Watchdog.StateDirectory;
        // Keep the existing store when the directory did not change: monitors hold
        // a reference to it, and swapping it under them would split reads/writes
        // across two objects (last writer wins per save, silently dropping state).
        var resolved = Path.GetFullPath(stateDir);
        var current = _store is null ? "" : Path.GetFullPath(
            Path.GetDirectoryName(_store.FilePath) ?? "./state");
        if (_store is null || !string.Equals(current, resolved, StringComparison.OrdinalIgnoreCase))
            _store = new StateStore(stateDir);

        _notifications = BuildNotifier(_config);

        if (_config.Reporting.Enabled && !string.IsNullOrWhiteSpace(_config.Reporting.ApiUrl))
        {
            _reporter = new HttpStatusReporter(
                _config.Reporting.ApiUrl!,
                _config.Reporting.ApiKey,
                Duration.TryParse(_config.Reporting.Timeout, out var t) ? t : TimeSpan.FromSeconds(10),
                _log);
            _reportInterval = Duration.TryParse(_config.Reporting.Interval, out var ri)
                ? ri : TimeSpan.FromSeconds(60);
        }
        else
        {
            _reporter = NoOpStatusReporter.Instance;
        }
    }

    public StateStore Store => _store;
    public string StateDirectory => Path.GetDirectoryName(_store.FilePath) ?? "./state";

    public HealthState? GetServiceState(string name)
    {
        lock (_lock)
        {
            if (_monitors.TryGetValue(name, out var entry))
                return entry.Monitor.CurrentState;
        }
        var snapshot = _store.Snapshot();
        if (snapshot.TryGetValue(name, out var record) &&
            Enum.TryParse<HealthState>(record.State, out var state))
            return state;
        return null;
    }

    public ServiceMonitor? GetMonitor(string name)
    {
        lock (_lock)
        {
            return _monitors.TryGetValue(name, out var entry) ? entry.Monitor : null;
        }
    }

    public List<string> ServiceNames()
    {
        lock (_lock)
        {
            return _monitors.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _shutdownToken = ct;
        ReconcileMonitors(_config);

        _log.Info("watchdog", "Orion Copy Watchdog started",
            ("services", _monitors.Count), ("config", _configPath));
        await _notifications.SendAsync(Notifications.WatchdogEvent.WatchdogStarted,
            "watchdog", "STARTING",
            $"Watchdog started, monitoring {_monitors.Count} service(s).", ct).ConfigureAwait(false);

        var tasks = new List<Task>();
        lock (_lock)
        {
            // Loops for monitors added by a reload are started inside
            // ReconcileMonitors, so this snapshot always covers everything.
            tasks.AddRange(_monitors.Values.Select(e => e.Loop).Where(t => t is not null)!);
        }

        tasks.Add(ControlLoopAsync(ct));
        tasks.Add(ReportingLoopAsync(ct));

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            // Stop anything still running and release their loop tokens.
            List<MonitorEntry> entries;
            lock (_lock)
            {
                entries = _monitors.Values.ToList();
                _monitors.Clear();
            }
            foreach (var entry in entries)
            {
                try { entry.Cts.Cancel(); } catch { }
            }
            try { await Task.WhenAll(entries.Select(e => e.Loop).Where(t => t is not null)!).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            foreach (var entry in entries)
                entry.Cts.Dispose();
        }
        _log.Info("watchdog", "Orion Copy Watchdog stopped");
    }

    private async Task RunMonitorGuardedAsync(ServiceMonitor monitor, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await monitor.RunAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error(monitor.Name, $"monitor crashed, restarting loop in 5s: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void ReconcileMonitors(WatchdogConfig config)
    {
        lock (_lock)
        {
            var wanted = config.Services
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var name in _monitors.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                // Cancel the loop token: a removed service must actually stop being
                // watched (and recovered), not linger until the daemon restarts.
                if (_monitors.TryGetValue(name, out var removed))
                {
                    try { removed.Cts.Cancel(); } catch { }
                    removed.Cts.Dispose();
                    _monitors.Remove(name);
                }
                _log.Info("watchdog", $"stopped monitoring removed service '{name}'");
            }

            foreach (var (name, svc) in wanted)
            {
                if (_monitors.TryGetValue(name, out var existing))
                {
                    existing.Monitor.UpdateConfig(svc, config.Watchdog);
                }
                else
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                    var monitor = new ServiceMonitor(
                        svc, config.Watchdog, _store, _log, _notifications, GetServiceState);
                    // Start the loop immediately: a service added by `reload`
                    // must be watched now, not after the next daemon restart.
                    var entry = new MonitorEntry { Monitor = monitor, Cts = cts };
                    entry.Loop = RunMonitorGuardedAsync(monitor, cts.Token);
                    _monitors[name] = entry;
                    _log.Info("watchdog", $"monitoring service '{name}'",
                        ("lifecycle", svc.Lifecycle), ("checks", svc.Checks.Count),
                        ("restart", svc.Restart.Type));
                }
            }
        }
        _store.Prune(_monitors.Keys);
    }

    // ── Control channel: the CLI drops request files here; the daemon answers. ──

    private string ControlDir => Path.Combine(StateDirectory, "control");
    private string ResultsDir => Path.Combine(ControlDir, "results");

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ResultsDir);
        // Clean stale requests from a previous run so they are not replayed.
        foreach (var stale in Directory.GetFiles(ControlDir, "*.json"))
        {
            try { File.Delete(stale); } catch { }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var file in Directory.GetFiles(ControlDir, "*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                        var command = JsonSerializer.Deserialize<ControlCommand>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (command is not null)
                            await HandleControlAsync(command).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.Warn("watchdog", $"ignoring malformed control file: {ex.Message}");
                    }
                    finally
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.Warn("watchdog", $"control loop error: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private Task HandleControlAsync(ControlCommand command)
    {
        ControlResult result = command.Action.ToLowerInvariant() switch
        {
            "pause" => PauseService(command.Service),
            "resume" => ResumeService(command.Service),
            "reset" => ResetService(command.Service),
            "restart" => RestartService(command.Service),
            "reload" => ReloadConfig(),
            _ => new ControlResult(command.Id, false, $"unknown action '{command.Action}'", DateTime.UtcNow),
        };
        result = result with { Id = command.Id, CompletedAt = DateTime.UtcNow };

        try
        {
            File.WriteAllText(Path.Combine(ResultsDir, command.Id + ".json"),
                JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            _log.Warn("watchdog", $"could not write control result: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private ControlResult PauseService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return new ControlResult("", false, "pause requires a service name", DateTime.UtcNow);
        if (GetMonitor(service) is null)
            return new ControlResult("", false, $"unknown service '{service}'", DateTime.UtcNow);
        _store.Update(service, r =>
        {
            r.Paused = true;
            r.State = HealthState.Disabled.ToString();
            r.LastMessage = "paused by operator";
            return r;
        });
        _log.Info(service, "paused by operator");
        return new ControlResult("", true, $"Service '{service}' paused.", DateTime.UtcNow);
    }

    private ControlResult ResumeService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return new ControlResult("", false, "resume requires a service name", DateTime.UtcNow);
        if (GetMonitor(service) is null)
            return new ControlResult("", false, $"unknown service '{service}'", DateTime.UtcNow);
        _store.Update(service, r =>
        {
            r.Paused = false;
            r.State = HealthState.Starting.ToString();
            r.StartedAt = DateTime.UtcNow;
            r.ConsecutiveFailures = 0;
            r.LastMessage = "resumed by operator";
            return r;
        });
        var monitor = GetMonitor(service);
        monitor?.ResumeFromPause();
        _log.Info(service, "resumed by operator");
        return new ControlResult("", true, $"Service '{service}' resumed.", DateTime.UtcNow);
    }

    private ControlResult ResetService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return new ControlResult("", false, "reset requires a service name", DateTime.UtcNow);
        var monitor = GetMonitor(service);
        if (monitor is null)
            return new ControlResult("", false, $"unknown service '{service}'", DateTime.UtcNow);
        monitor.OperatorReset();
        return new ControlResult("", true, $"Service '{service}' reset (counters cleared, back under watch).", DateTime.UtcNow);
    }

    private ControlResult RestartService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
            return new ControlResult("", false, "restart requires a service name", DateTime.UtcNow);
        var monitor = GetMonitor(service);
        if (monitor is null)
            return new ControlResult("", false, $"unknown service '{service}'", DateTime.UtcNow);
        monitor.RequestRestart();
        _log.Info(service, "manual restart requested by operator");
        return new ControlResult("", true, $"Restart of '{service}' requested.", DateTime.UtcNow);
    }

    private ControlResult ReloadConfig()
    {
        try
        {
            var loaded = ConfigLoader.Load(_configPath);
            var findings = ConfigValidator.Validate(loaded.Config, loaded.MissingEnv);
            var errors = findings.Where(f => f.IsError).ToList();
            if (errors.Count > 0)
                return new ControlResult("", false,
                    $"reload rejected: {errors.Count} config error(s), first: {errors[0].Message}",
                    DateTime.UtcNow);

            _config = loaded.Config;
            RebuildInfra();
            ReconcileMonitors(_config);
            _log.Info("watchdog", "configuration reloaded",
                ("services", _config.Services.Count));
            foreach (var warning in loaded.Warnings)
                _log.Warn("watchdog", $"config warning after reload: {warning}");
            return new ControlResult("", true,
                $"Configuration reloaded ({_config.Services.Count} service(s)).", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new ControlResult("", false, $"reload failed: {ex.Message}", DateTime.UtcNow);
        }
    }

    private async Task ReportingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_reportInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (_reporter is NoOpStatusReporter)
                continue;

            try
            {
                var snapshot = _store.Snapshot();
                var services = snapshot.Select(kv => new ServiceStatusReport(
                    kv.Key,
                    kv.Value.State,
                    kv.Value.TotalRestarts,
                    kv.Value.LastCheck,
                    kv.Value.LastRecovery)).ToList();
                var report = new WatchdogStatusReport(
                    Environment.MachineName,
                    DateTime.UtcNow,
                    services,
                    snapshot.Where(kv => kv.Value.State == HealthState.Failed.ToString())
                        .Select(kv => kv.Key).ToList());
                await _reporter.ReportAsync(report, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.Warn("watchdog", $"reporting loop error: {ex.Message}");
            }
        }
    }
}
