using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Health;

namespace OrionCopy.Watchdog.Tests;

public sealed class MonitorTests
{
    [Fact]
    public async Task FailedCheckIncrementsCounter_ButStaysBelowUnhealthyUnderThreshold()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var global = TestFactory.FastGlobal(failureThreshold: 5);
        var svc = TestFactory.HttpService();
        var strategy = new FakeRestartStrategy();
        var monitor = TestFactory.BuildMonitor(svc, global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => false) }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(
            () => new State.StateStore(dir).Get("svc").ConsecutiveFailures >= 2,
            "failure counter to reach 2");

        var record = new State.StateStore(dir).Get("svc");
        Assert.True(record.ConsecutiveFailures is >= 2 and < 5);
        Assert.NotEqual(HealthState.Unhealthy, TestFactory.StateOf(dir, "svc"));
        Assert.Equal(0, strategy.StartCalls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task SuccessResetsFailureCounter()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var healthy = false; // fail twice, then recover on our own
        var global = TestFactory.FastGlobal(failureThreshold: 5);
        var monitor = TestFactory.BuildMonitor(TestFactory.HttpService(), global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => healthy) },
            new FakeRestartStrategy());
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(
            () => new State.StateStore(dir).Get("svc").ConsecutiveFailures >= 2,
            "failure counter to reach 2");
        healthy = true;
        await TestFactory.WaitUntilAsync(
            () => new State.StateStore(dir).Get("svc").ConsecutiveFailures == 0
               && TestFactory.StateOf(dir, "svc") == HealthState.Healthy,
            "counter to reset and state to return to Healthy");

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task ServiceBecomesUnhealthyOnlyAfterThreshold()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // High restart limit + failing strategy start so recovery keeps failing fast.
        var global = TestFactory.FastGlobal(failureThreshold: 3, maxRestarts: 100,
            recoveryTimeout: "1s");
        var strategy = new FakeRestartStrategy { StartResult = false };
        var monitor = TestFactory.BuildMonitor(TestFactory.HttpService(), global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => false) }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(
            () => TestFactory.StateOf(dir, "svc") == HealthState.Unhealthy
               || TestFactory.StateOf(dir, "svc") == HealthState.Recovering,
            "service to be declared unhealthy");
        var record = new State.StateStore(dir).Get("svc");
        Assert.True(record.ConsecutiveFailures >= 3);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task RestartStartsAfterConfirmedFailure()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var global = TestFactory.FastGlobal(failureThreshold: 2);
        var strategy = new FakeRestartStrategy { StartResult = false };
        var monitor = TestFactory.BuildMonitor(TestFactory.HttpService(), global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => false) }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(() => strategy.StartCalls >= 1,
            "restart to start after confirmed failure");

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task RestartCooldownPreventsRapidRestarts()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var global = TestFactory.FastGlobal(failureThreshold: 1, cooldown: "30s",
            recoveryTimeout: "1s");
        var strategy = new FakeRestartStrategy { StartResult = false };
        var monitor = TestFactory.BuildMonitor(TestFactory.HttpService(), global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => false) }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(() => strategy.StartCalls >= 1, "first restart");
        await Task.Delay(TimeSpan.FromSeconds(3)); // recovery fails (~1s), cooldown must hold
        Assert.Equal(1, strategy.StartCalls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task MaximumRestartLimitTripsFailedState()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var global = TestFactory.FastGlobal(failureThreshold: 1, maxRestarts: 2,
            recoveryTimeout: "1s");
        var strategy = new FakeRestartStrategy { StartResult = false };
        var monitor = TestFactory.BuildMonitor(TestFactory.HttpService(), global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => false) }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(
            () => TestFactory.StateOf(dir, "svc") == HealthState.Failed,
            "service to reach FAILED after exceeding restart limit", timeoutMs: 20000);
        Assert.Equal(2, strategy.StartCalls);

        // FAILED sticks: no further restarts without operator reset.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(2, strategy.StartCalls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task VerifiedRecoveryResetsBackoffLadder()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var healthy = false;
        var global = TestFactory.FastGlobal(failureThreshold: 1, recoveryTimeout: "15s", backoff: true);
        var strategy = new FakeRestartStrategy
        {
            OnStartAsync = () => { healthy = true; return Task.CompletedTask; },
        };
        var monitor = TestFactory.BuildMonitor(TestFactory.HttpService(), global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => healthy) }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(
            () => TestFactory.StateOf(dir, "svc") == HealthState.Healthy
               && new State.StateStore(dir).Get("svc").LastRecovery != null,
            "service to recover and verify healthy", timeoutMs: 20000);

        var record = new State.StateStore(dir).Get("svc");
        Assert.Equal(0, record.ConsecutiveRestarts);
        Assert.Equal(1, record.TotalRestarts);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task UnhealthyDependencyBlocksRestart()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var global = TestFactory.FastGlobal(failureThreshold: 1, recoveryTimeout: "1s");
        var svc = TestFactory.HttpService();
        svc.DependsOn.Add("backend");
        var strategy = new FakeRestartStrategy();
        var monitor = TestFactory.BuildMonitor(svc, global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => false) }, strategy,
            getState: name => name == "backend" ? HealthState.Unhealthy : HealthState.Healthy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(
            () => TestFactory.StateOf(dir, "svc") == HealthState.Unhealthy,
            "service to be unhealthy");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(0, strategy.StartCalls); // held: backend is down, never restarted

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task DynamicInstanceExpectedStopDoesNotRestart()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var global = TestFactory.FastGlobal(failureThreshold: 1);
        var svc = TestFactory.HttpService("arena-4");
        svc.Lifecycle = "dynamic";
        svc.Checks = new List<CheckConfig>
        {
            new() { Type = "process", ProcessName = "ArenaServer" },
            new() { Type = "http", Url = "http://127.0.0.1:7777/health", Timeout = "1s" },
        };
        var strategy = new FakeRestartStrategy();
        var monitor = TestFactory.BuildMonitor(svc, global, dir,
            new()
            {
                ["process"] = new ScriptedCheck("process", () => false), // match ended
                ["http"] = new ScriptedCheck("http", () => true),
            }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(
            () => TestFactory.StateOf(dir, "arena-4") == HealthState.Stopped,
            "dynamic instance to reach STOPPED");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, strategy.StartCalls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task DynamicInstanceWedgedWhileAliveStillRestarts()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var global = TestFactory.FastGlobal(failureThreshold: 1, recoveryTimeout: "1s");
        var svc = TestFactory.HttpService("arena-5");
        svc.Lifecycle = "dynamic";
        svc.Checks = new List<CheckConfig>
        {
            new() { Type = "process", ProcessName = "ArenaServer" },
            new() { Type = "http", Url = "http://127.0.0.1:7777/health", Timeout = "1s" },
        };
        var strategy = new FakeRestartStrategy { StartResult = false };
        var monitor = TestFactory.BuildMonitor(svc, global, dir,
            new()
            {
                ["process"] = new ScriptedCheck("process", () => true), // alive...
                ["http"] = new ScriptedCheck("http", () => false),      // ...but wedged
            }, strategy);
        var run = monitor.RunAsync(cts.Token);

        await TestFactory.WaitUntilAsync(() => strategy.StartCalls >= 1,
            "wedged dynamic instance to restart");

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task ConcurrentRestartAttemptsNeverOverlap()
    {
        var dir = TestFactory.TempDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var global = TestFactory.FastGlobal(failureThreshold: 1000, recoveryTimeout: "15s");
        var strategy = new FakeRestartStrategy { StartDelay = TimeSpan.FromMilliseconds(500) };
        var monitor = TestFactory.BuildMonitor(TestFactory.HttpService(), global, dir,
            new() { ["http"] = new ScriptedCheck("http", () => true) }, strategy);
        var run = monitor.RunAsync(cts.Token);

        monitor.RequestRestart();
        await TestFactory.WaitUntilAsync(() => strategy.StartCalls >= 1, "first restart to begin");
        monitor.RequestRestart(); // operator double-request during active recovery
        await TestFactory.WaitUntilAsync(() => strategy.StartCalls >= 2, "second restart to run after");

        Assert.Equal(1, strategy.MaxConcurrency);

        cts.Cancel();
        await run;
    }
}
