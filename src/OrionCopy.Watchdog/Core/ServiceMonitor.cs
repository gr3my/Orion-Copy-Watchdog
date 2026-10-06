using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Health;
using OrionCopy.Watchdog.Logging;
using OrionCopy.Watchdog.Notifications;
using OrionCopy.Watchdog.Restart;
using OrionCopy.Watchdog.State;

namespace OrionCopy.Watchdog.Core;

/// <summary>All tunable timings for one service, resolved from service overrides ?? global defaults.</summary>
public sealed record ResolvedPolicy(
    TimeSpan CheckInterval,
    int FailureThreshold,
    TimeSpan RecoveryTimeout,
    TimeSpan RestartCooldown,
    TimeSpan StartupGrace,
    int MaxRestarts,
    TimeSpan RestartWindow,
    bool BackoffEnabled,
    TimeSpan BackoffInitial,
    double BackoffMultiplier,
    TimeSpan BackoffMax,
    bool AutoRetryFailed)
{
    public static ResolvedPolicy From(ServiceConfig service, GlobalConfig global) => new(
        CheckInterval: Duration.TryParse(service.CheckInterval ?? global.CheckInterval, out var ci) ? ci : TimeSpan.FromSeconds(10),
        FailureThreshold: service.FailureThreshold ?? global.FailureThreshold,
        RecoveryTimeout: Duration.TryParse(service.RecoveryTimeout ?? global.RecoveryTimeout, out var rt) ? rt : TimeSpan.FromSeconds(60),
        RestartCooldown: Duration.TryParse(global.RestartCooldown, out var rc) ? rc : TimeSpan.FromSeconds(30),
        StartupGrace: Duration.TryParse(service.StartupGrace ?? global.StartupGrace, out var sg) ? sg : TimeSpan.FromSeconds(60),
        MaxRestarts: global.RestartPolicy.MaxRestarts,
        RestartWindow: Duration.TryParse(global.RestartPolicy.Window, out var w) ? w : TimeSpan.FromMinutes(10),
        BackoffEnabled: global.Backoff.Enabled,
        BackoffInitial: Duration.TryParse(global.Backoff.Initial, out var bi) ? bi : TimeSpan.FromSeconds(5),
        BackoffMultiplier: global.Backoff.Multiplier,
        BackoffMax: Duration.TryParse(global.Backoff.Max, out var bm) ? bm : TimeSpan.FromMinutes(2),
        AutoRetryFailed: service.AutoRetryFailed ?? global.AutoRetryFailed);
}

/// <summary>
/// Per-service monitor: CHECK → CONFIRM (threshold) → RECOVER (graceful restart)
/// → VERIFY → LOG. Isolated state, per-service lock, never throws out of the loop.
/// </summary>
public sealed class ServiceMonitor
{
    private readonly IWatchdogLogger _log;
    private readonly StateStore _store;
    private readonly WebhookNotifier _notifications;
    private readonly Func<string, HealthState?> _getState;
    private readonly Func<string, IHealthCheck?> _checkFactory;
    private readonly Func<string, IRestartStrategy> _strategyFactory;
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);

    private ServiceConfig _config;
    private ResolvedPolicy _policy;
    private bool _downNotified;
    private string _holdLoggedFor = "";
    private volatile bool _restartRequested;

    public ServiceMonitor(
        ServiceConfig config,
        GlobalConfig global,
        StateStore store,
        IWatchdogLogger log,
        WebhookNotifier notifications,
        Func<string, HealthState?> getState,
        Func<string, IHealthCheck?>? checkFactory = null,
        Func<string, IRestartStrategy>? strategyFactory = null)
    {
        _config = config;
        _policy = ResolvedPolicy.From(config, global);
        _store = store;
        _log = log;
        _notifications = notifications;
        _getState = getState;
        _checkFactory = checkFactory ?? HealthCheckFactory.Get;
        _strategyFactory = strategyFactory ?? RestartStrategyFactory.Get;
    }

    public string Name => _config.Name;

    public ServiceConfig ConfigSnapshot() => _config;

    /// <summary>Called by the engine when the operator resumes a paused service.</summary>
    public void ResumeFromPause()
    {
        _downNotified = false;
        _holdLoggedFor = "";
    }

    /// <summary>Called by the engine for the `reset` control command.</summary>
    public void OperatorReset() => ResetCounters("operator reset via CLI");

    public void UpdateConfig(ServiceConfig config, GlobalConfig global)
    {
        _config = config;
        _policy = ResolvedPolicy.From(config, global);
    }

    public HealthState CurrentState
    {
        get
        {
            var record = _store.Get(Name);
            return Enum.TryParse<HealthState>(record.State, out var s) ? s : HealthState.Starting;
        }
    }

    public void RequestRestart() => _restartRequested = true;

    public async Task RunAsync(CancellationToken ct)
    {
        // Stagger startup so 50 services don't all probe in the same millisecond.
        try { await Task.Delay(Random.Shared.Next(0, 2000), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One broken service must never crash the watchdog loop.
                _log.Error(Name, $"monitor tick failed: {ex.Message}");
            }

            try { await Task.Delay(_policy.CheckInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var record = _store.Get(Name);

        if (!_config.Enabled)
        {
            SetState("Disabled", "service is disabled in config", record);
            return;
        }

        if (record.Paused)
        {
            SetState("Disabled", "paused by operator", record);
            return;
        }

        if (TryParseState(record.State) == HealthState.Failed)
        {
            // FAILED = manual intervention required. Auto-clear only when the
            // operator reset it, the whole window slid past with auto-retry on,
            // or a manual restart was requested.
            if (_restartRequested)
            {
                _restartRequested = false;
                ResetCounters("operator requested restart");
            }
            else if (_policy.AutoRetryFailed &&
                     DateTime.UtcNow - record.WindowStart > _policy.RestartWindow)
            {
                ResetCounters("restart window elapsed with auto-retry enabled");
                _log.Info(Name, "leaving FAILED state: restart window elapsed and auto-retry is enabled");
            }
            return;
        }

        if (_restartRequested)
        {
            _restartRequested = false;
            if (IsRecoveryDisabled())
            {
                _log.Warn(Name, "manual restart requested but automatic recovery is disabled " +
                    "(restart type 'none' or max_restarts 0); no action taken");
                _store.Update(Name, r =>
                {
                    r.LastMessage = "manual restart ignored: automatic recovery is disabled for this service";
                    return r;
                });
                return;
            }
            await RecoverAsync("operator requested restart", ct).ConfigureAwait(false);
            return;
        }

        var results = await RunChecksAsync(ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var passed = Combine(results);
        var processGone = IsProcessGone(results);

        _store.Update(Name, r =>
        {
            r.LastCheck = now;
            if (passed) { r.LastSuccess = now; }
            else { r.LastFailure = now; }
            return r;
        });

        // ── Dynamic lifecycle: an allocator-spawned match that ended is expected. ──
        if (_config.IsDynamic && processGone)
        {
            if (!_config.WantsRestartOnExit)
            {
                if (TryParseState(record.State) != HealthState.Stopped)
                    _log.Info(Name, "dynamic instance stopped (expected shutdown, no restart)");
                _store.Update(Name, r =>
                {
                    r.State = HealthState.Stopped.ToString();
                    r.ConsecutiveFailures = 0;
                    r.HealthySince = null;
                    r.LastMessage = "dynamic instance stopped (expected)";
                    return r;
                });
                _downNotified = false;
                return;
            }
            // restart_on_exit: fall through and treat the vanished process as a failure.
        }
        else if (TryParseState(record.State) == HealthState.Stopped && passed)
        {
            // A dynamic instance reappeared (allocator spun up a new match reusing the name).
            _log.Info(Name, "dynamic instance is back under watch");
            _store.Update(Name, r =>
            {
                r.State = HealthState.Starting.ToString();
                r.StartedAt = now;
                r.HealthySince = null;
                r.ConsecutiveFailures = 0;
                r.LastMessage = "instance reappeared";
                return r;
            });
            return;
        }

        if (passed)
        {
            OnSuccess(record, results);
            return;
        }

        await OnFailureAsync(record, results, ct).ConfigureAwait(false);
    }

    private void OnSuccess(ServiceStateRecord record, List<CheckResult> results)
    {
        var previous = TryParseState(record.State);
        _store.Update(Name, r =>
        {
            r.ConsecutiveFailures = 0;
            if (previous != HealthState.Healthy)
            {
                r.State = HealthState.Healthy.ToString();
                r.HealthySince = DateTime.UtcNow;
                r.LastMessage = "all checks passed";
            }
            return r;
        });

        if (previous is HealthState.Unhealthy or HealthState.Recovering or HealthState.Backoff)
            _log.Info(Name, "service recovered on its own during backoff/recovery window");

        if (previous != HealthState.Healthy)
            _log.Info(Name, "health-check passed",
                ("detail", results.FirstOrDefault(r => r.Healthy)?.Detail ?? "healthy"));

        _downNotified = false;
        _holdLoggedFor = "";
    }

    private async Task OnFailureAsync(ServiceStateRecord record, List<CheckResult> results, CancellationToken ct)
    {
        var failures = record.ConsecutiveFailures + 1;
        _store.Update(Name, r =>
        {
            r.ConsecutiveFailures = failures;
            r.HealthySince = null;
            if (failures < _policy.FailureThreshold && TryParseState(r.State) == HealthState.Healthy)
                r.State = HealthState.Degraded.ToString();
            r.LastMessage = FirstFailure(results);
            return r;
        });

        if (failures < _policy.FailureThreshold)
        {
            _log.Warn(Name, "health-check failed",
                ("attempt", $"{failures}/{_policy.FailureThreshold}"),
                ("detail", FirstFailure(results)));
            return;
        }

        // Threshold reached: confirm, then act. During start-up grace a fresh
        // process gets time to boot before any verdict is acted on.
        var startedAt = record.StartedAt ?? DateTime.UtcNow;
        if (record.LastSuccess is null && DateTime.UtcNow - startedAt < _policy.StartupGrace)
        {
            _store.Update(Name, r =>
            {
                r.State = HealthState.Starting.ToString();
                r.LastMessage = "starting (no successful check yet, within grace period)";
                return r;
            });
            return;
        }

        var wasAlready = TryParseState(_store.Get(Name).State) == HealthState.Unhealthy;
        _store.Update(Name, r =>
        {
            if (TryParseState(r.State) is HealthState.Starting or HealthState.Healthy or HealthState.Degraded)
                r.State = HealthState.Unhealthy.ToString();
            r.LastMessage = $"confirmed unhealthy: {FirstFailure(results)}";
            return r;
        });

        if (!wasAlready)
        {
            _log.Error(Name, "service declared unhealthy",
                ("detail", FirstFailure(results)));
            if (!_downNotified)
            {
                _downNotified = true;
                await _notifications.SendAsync(WatchdogEvent.ServiceDown, Name, "UNHEALTHY",
                    $"Service '{Name}' failed {_policy.FailureThreshold} consecutive checks: {FirstFailure(results)}",
                    ct).ConfigureAwait(false);
            }
        }

        // ── Recovery gates ──
        // Monitor-only services (restart type 'none', or max_restarts 0) never
        // enter recovery: they stay UNHEALTHY without burning restart budget or
        // emitting "restart initiated" for a restart that cannot happen.
        if (IsRecoveryDisabled())
        {
            const string key = "manual";
            _store.Update(Name, r =>
            {
                r.LastMessage = "unhealthy; automatic recovery is disabled (restart type 'none'), operator action required";
                return r;
            });
            if (_holdLoggedFor != key)
            {
                _holdLoggedFor = key;
                _log.Warn(Name, "holding: automatic recovery is disabled for this service");
            }
            return;
        }

        if (!DependencyGraph.DependenciesHealthy(_config, _getState, out var blocker))
        {
            var key = "dep:" + blocker;
            _store.Update(Name, r => { r.LastMessage = $"holding: dependency {blocker} is not healthy"; return r; });
            if (_holdLoggedFor != key)
            {
                _holdLoggedFor = key;
                _log.Warn(Name, "holding recovery: dependency is not healthy", ("dependency", blocker ?? ""));
            }
            return;
        }

        var fresh = _store.Get(Name);
        var now = DateTime.UtcNow;

        // Slide the restart window before counting.
        RestartWindowTracker.SlideWindow(now, fresh.WindowStart, _policy.RestartWindow,
            fresh.RestartsInWindow, out var windowStart, out var inWindow);
        _store.Update(Name, r =>
        {
            if (r.WindowStart != windowStart)
            {
                r.WindowStart = windowStart;
                r.RestartsInWindow = inWindow;
            }
            return r;
        });

        if (inWindow >= _policy.MaxRestarts)
        {
            _store.Update(Name, r =>
            {
                r.State = HealthState.Failed.ToString();
                r.LastMessage = $"{_policy.MaxRestarts} restarts inside {_policy.RestartWindow.TotalMinutes:F0}m: manual intervention required";
                return r;
            });
            _log.Error(Name, "restart limit reached: manual intervention required",
                ("restarts", inWindow), ("window", _policy.RestartWindow.ToString()));
            await _notifications.SendAsync(WatchdogEvent.RestartLimitReached, Name, "FAILED",
                $"Service '{Name}' hit the restart limit ({inWindow} in {_policy.RestartWindow.TotalMinutes:F0}m).", ct)
                .ConfigureAwait(false);
            return;
        }

        if (fresh.LastRestart is { } last && now - last < _policy.RestartCooldown)
        {
            var key = "cooldown";
            if (_holdLoggedFor != key)
            {
                _holdLoggedFor = key;
                _log.Info(Name, "cooling down after recent restart",
                    ("wait", (last + _policy.RestartCooldown - now).ToString()));
            }
            return;
        }

        // Exponential backoff: wait longer after every consecutive restart.
        if (_policy.BackoffEnabled && fresh.ConsecutiveRestarts > 0)
        {
            var delay = BackoffPolicy.ComputeDelay(fresh.ConsecutiveRestarts + 1,
                _policy.BackoffInitial, _policy.BackoffMultiplier, _policy.BackoffMax);
            var resumeAt = (fresh.LastRestart ?? now) + delay;
            if (now < resumeAt)
            {
                _store.Update(Name, r =>
                {
                    r.State = HealthState.Backoff.ToString();
                    r.BackoffUntil = resumeAt;
                    r.LastMessage = $"backing off before restart #{fresh.ConsecutiveRestarts + 1} (wait {resumeAt - now:hh\\:mm\\:ss})";
                    return r;
                });
                var key = "backoff";
                if (_holdLoggedFor != key)
                {
                    _holdLoggedFor = key;
                    _log.Warn(Name, "backing off before restart",
                        ("wait", (resumeAt - now).ToString()), ("next_restart", $"#{fresh.ConsecutiveRestarts + 1}"));
                }
                return;
            }
        }

        _holdLoggedFor = "";
        await RecoverAsync($"confirmed unhealthy ({FirstFailure(results)})", ct).ConfigureAwait(false);
    }

    /// <summary>True when the operator configured monitoring without recovery.</summary>
    private bool IsRecoveryDisabled() =>
        string.Equals(_config.Restart.Type, "none", StringComparison.OrdinalIgnoreCase) ||
        _policy.MaxRestarts == 0;

    /// <summary>Graceful recovery: stop → wait → force only if needed → start → verify.</summary>
    private async Task RecoverAsync(string reason, CancellationToken ct)
    {
        if (!await _recoveryLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _log.Debug(Name, "recovery already in progress; skipping duplicate attempt");
            return;
        }

        try
        {
            var strategy = _strategyFactory(_config.Restart.Type);
            var graceful = Duration.TryParse(_config.Restart.GracefulTimeout, out var g)
                ? g : TimeSpan.FromSeconds(15);

            _store.Update(Name, r =>
            {
                r.State = HealthState.Recovering.ToString();
                r.LastMessage = $"restart initiated: {reason}";
                // Count the restart up-front so the loop guard holds even if we crash mid-recovery.
                var now = DateTime.UtcNow;
                RestartWindowTracker.SlideWindow(now, r.WindowStart, _policy.RestartWindow,
                    r.RestartsInWindow, out var ws, out var count);
                r.WindowStart = ws;
                r.RestartsInWindow = count + 1;
                r.TotalRestarts++;
                r.ConsecutiveRestarts++;
                r.LastRestart = now;
                r.BackoffUntil = null;
                return r;
            });

            var attempt = _store.Get(Name).ConsecutiveRestarts;
            _log.Info(Name, "restart initiated",
                ("reason", reason), ("attempt", $"{attempt}/{_policy.MaxRestarts}"),
                ("strategy", strategy.Kind));
            await _notifications.SendAsync(WatchdogEvent.RestartStarted, Name, "RECOVERING",
                $"Restarting '{Name}' ({reason}).", ct).ConfigureAwait(false);

            using var overallCts = new CancellationTokenSource(_policy.RecoveryTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, overallCts.Token);

            var ctx = new RestartContext(_config, graceful, _log);
            RestartResult startResult;
            try
            {
                var stop = await strategy.StopAsync(ctx, linked.Token).ConfigureAwait(false);
                _log.Info(Name, $"stop phase: {stop.Detail}");
                if (!stop.Success && strategy.Kind != "none")
                {
                    throw new InvalidOperationException($"stop phase failed: {stop.Detail}");
                }
                startResult = await strategy.StartAsync(ctx, linked.Token).ConfigureAwait(false);
                _log.Info(Name, $"start phase: {startResult.Detail}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await FailRecoveryAsync($"recovery timed out after {_policy.RecoveryTimeout.TotalSeconds:F0}s", ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                await FailRecoveryAsync($"recovery error: {ex.Message}", ct).ConfigureAwait(false);
                return;
            }

            if (!startResult.Success)
            {
                await FailRecoveryAsync($"start phase failed: {startResult.Detail}", ct).ConfigureAwait(false);
                return;
            }

            // Verify: poll until healthy or the recovery timeout expires.
            var verified = await VerifyRecoveryAsync(linked.Token).ConfigureAwait(false);
            if (verified)
            {
                _store.Update(Name, r =>
                {
                    r.State = HealthState.Healthy.ToString();
                    r.HealthySince = DateTime.UtcNow;
                    r.ConsecutiveFailures = 0;
                    r.ConsecutiveRestarts = 0; // a verified recovery resets the backoff ladder
                    r.LastRecovery = DateTime.UtcNow;
                    r.LastSuccess = DateTime.UtcNow;
                    r.LastMessage = "recovered and verified healthy";
                    return r;
                });
                _log.Info(Name, "service recovered and verified healthy");
                _downNotified = false;
                await _notifications.SendAsync(WatchdogEvent.ServiceRecovered, Name, "HEALTHY",
                    $"Service '{Name}' restarted successfully and passed verification.", ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await FailRecoveryAsync(
                    $"service still unhealthy {_policy.RecoveryTimeout.TotalSeconds:F0}s after restart", ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    private async Task<bool> VerifyRecoveryAsync(CancellationToken ct)
    {
        // Give the process a moment to bind ports before the first probe.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }

        while (!ct.IsCancellationRequested)
        {
            List<CheckResult> results;
            try
            {
                results = await RunChecksAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            if (Combine(results))
                return true;
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task FailRecoveryAsync(string detail, CancellationToken ct)
    {
        _store.Update(Name, r =>
        {
            r.State = HealthState.Unhealthy.ToString();
            r.LastMessage = $"failed recovery: {detail}";
            return r;
        });
        _log.Error(Name, "failed recovery", ("detail", detail));
        await _notifications.SendAsync(WatchdogEvent.RestartFailed, Name, "UNHEALTHY",
            $"Recovery of '{Name}' failed: {detail}", ct).ConfigureAwait(false);
    }

    private async Task<List<CheckResult>> RunChecksAsync(CancellationToken ct)
    {
        var tasks = _config.Checks.Select(async check =>
        {
            var probe = _checkFactory(check.Type);
            if (probe is null)
                return CheckResult.Fail(check.Type, $"unknown check type '{check.Type}'", TimeSpan.Zero);
            try
            {
                return await probe.CheckAsync(check, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return CheckResult.Fail(check.Type, $"check threw: {ex.Message}", TimeSpan.Zero);
            }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    private bool Combine(List<CheckResult> results)
    {
        if (results.Count == 0)
            return false;
        return string.Equals(_config.HealthMode, "any", StringComparison.OrdinalIgnoreCase)
            ? results.Any(r => r.Healthy)
            : results.All(r => r.Healthy);
    }

    /// <summary>
    /// A process being alive does not mean the server is healthy — but when every
    /// process probe fails, the process is gone, which matters for dynamic lifecycle.
    /// </summary>
    private bool IsProcessGone(List<CheckResult> results)
    {
        var processResults = results.Where(r => r.CheckType == "process").ToList();
        return processResults.Count > 0 && processResults.All(r => !r.Healthy);
    }

    private static string FirstFailure(List<CheckResult> results) =>
        results.FirstOrDefault(r => !r.Healthy)?.Detail ?? "unknown failure";

    private void SetState(string state, string message, ServiceStateRecord record)
    {
        if (record.State != state)
            _store.Update(Name, r =>
            {
                r.State = state;
                r.LastMessage = message;
                if (state != HealthState.Healthy.ToString())
                    r.HealthySince = null;
                return r;
            });
    }

    private static HealthState TryParseState(string? state) =>
        Enum.TryParse<HealthState>(state, out var s) ? s : HealthState.Starting;

    private void ResetCounters(string reason)
    {
        _store.Update(Name, r =>
        {
            r.State = HealthState.Starting.ToString();
            r.StartedAt = DateTime.UtcNow;
            r.ConsecutiveFailures = 0;
            r.ConsecutiveRestarts = 0;
            r.RestartsInWindow = 0;
            r.WindowStart = DateTime.UtcNow;
            r.BackoffUntil = null;
            r.Paused = false;
            r.LastMessage = $"reset by operator: {reason}";
            return r;
        });
        _downNotified = false;
        _holdLoggedFor = "";
        _log.Info(Name, $"counters reset: {reason}");
    }

    /// <summary>One-shot evaluation for the `check` CLI command (no state changes to counters).</summary>
    public async Task<List<CheckResult>> CheckOnceAsync(CancellationToken ct) =>
        await RunChecksAsync(ct).ConfigureAwait(false);

    public bool EvaluateOnce(List<CheckResult> results) => Combine(results);
}
