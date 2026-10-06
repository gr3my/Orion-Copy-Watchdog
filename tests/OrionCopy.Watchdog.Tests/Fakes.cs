using System.Diagnostics;
using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Core;
using OrionCopy.Watchdog.Health;
using OrionCopy.Watchdog.Logging;
using OrionCopy.Watchdog.Notifications;
using OrionCopy.Watchdog.Restart;
using OrionCopy.Watchdog.State;

namespace OrionCopy.Watchdog.Tests;

// ── Fakes: no real processes are ever touched by unit tests. ──

public sealed class ScriptedCheck : IHealthCheck
{
    public string Kind { get; }
    private readonly Func<bool> _healthy;
    private readonly string _detail;

    public ScriptedCheck(string kind, Func<bool> healthy, string? detail = null)
    {
        Kind = kind;
        _healthy = healthy;
        _detail = detail ?? kind;
    }

    public Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct) =>
        Task.FromResult(_healthy()
            ? CheckResult.Pass(Kind, $"{_detail} ok", TimeSpan.FromMilliseconds(1))
            : CheckResult.Fail(Kind, $"{_detail} FAILED", TimeSpan.FromMilliseconds(1)));
}

public sealed class FakeRestartStrategy : IRestartStrategy
{
    public string Kind => "fake";
    public int StopCalls;
    public int StartCalls;
    public int CurrentConcurrency;
    public int MaxConcurrency;
    public bool StopResult = true;
    public bool StartResult = true;
    public TimeSpan StartDelay = TimeSpan.Zero;
    public Func<Task>? OnStartAsync;

    public Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref StopCalls);
        return Task.FromResult(StopResult
            ? RestartResult.Ok("fake stop ok")
            : RestartResult.Fail("fake stop FAILED"));
    }

    public async Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref StartCalls);
        var current = Interlocked.Increment(ref CurrentConcurrency);
        try
        {
            int observed, prev;
            do
            {
                prev = MaxConcurrency;
                observed = Math.Max(prev, current);
            } while (prev != Interlocked.CompareExchange(ref MaxConcurrency, observed, prev));

            if (OnStartAsync is not null)
                await OnStartAsync().ConfigureAwait(false);
            if (StartDelay > TimeSpan.Zero)
                await Task.Delay(StartDelay, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref CurrentConcurrency);
        }
        return StartResult
            ? RestartResult.Ok("fake start ok")
            : RestartResult.Fail("fake start FAILED");
    }
}

public static class TestFactory
{
    public static string TempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "orion-wd-tests",
            $"{name}-{Guid.NewGuid():N}")).FullName;

    public static GlobalConfig FastGlobal(
        int failureThreshold = 1,
        int maxRestarts = 5,
        string cooldown = "0s",
        string recoveryTimeout = "1s",
        bool backoff = false) => new()
        {
            CheckInterval = "20ms",
            FailureThreshold = failureThreshold,
            RecoveryTimeout = recoveryTimeout,
            RestartCooldown = cooldown,
            StartupGrace = "0s",
            StateDirectory = "unused",
            RestartPolicy = new RestartPolicyConfig { MaxRestarts = maxRestarts, Window = "1h" },
            Backoff = new BackoffConfig { Enabled = backoff, Initial = "50ms", Multiplier = 2.0, Max = "500ms" },
        };

    public static ServiceConfig HttpService(string name = "svc", int? threshold = null) => new()
    {
        Name = name,
        FailureThreshold = threshold,
        Checks = new List<CheckConfig>
        {
            new() { Type = "http", Url = "http://127.0.0.1:9/health", Timeout = "1s" },
        },
        Restart = new RestartConfig { Type = "process", Executable = "fake.exe", GracefulTimeout = "1s" },
    };

    public static ServiceMonitor BuildMonitor(
        ServiceConfig service,
        GlobalConfig global,
        string stateDir,
        Dictionary<string, IHealthCheck>? checks = null,
        IRestartStrategy? strategy = null,
        Func<string, HealthState?>? getState = null)
    {
        var store = new StateStore(stateDir);
        return new ServiceMonitor(
            service, global, store,
            NullLogger.Instance,
            new WebhookNotifier(false, null, TimeSpan.FromSeconds(1), NullLogger.Instance),
            getState ?? (_ => HealthState.Healthy),
            checkFactory: type => checks != null && checks.TryGetValue(type, out var c) ? c : null,
            strategyFactory: _ => strategy ?? new FakeRestartStrategy());
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    public static HealthState StateOf(string stateDir, string service)
    {
        var record = new StateStore(stateDir).Get(service);
        return Enum.TryParse<HealthState>(record.State, out var s) ? s : HealthState.Starting;
    }
}
