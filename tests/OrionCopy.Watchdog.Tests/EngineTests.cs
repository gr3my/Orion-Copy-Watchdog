using System.Text.Json;
using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Core;
using OrionCopy.Watchdog.Logging;

namespace OrionCopy.Watchdog.Tests;

/// <summary>
/// Engine-level regression tests: reload semantics, removed-service shutdown,
/// monitor-only services, and control-channel validation. Uses real `command`
/// checks (`exit 0` / `exit 1` work in both cmd and sh) but never real services.
/// </summary>
public sealed class EngineTests
{
    private static string WriteConfig(string dir, string body)
    {
        var path = Path.Combine(dir, "watchdog.yml");
        File.WriteAllText(path, body);
        return path;
    }

    private static string ConfigBody(string stateDir, string services) => $"""
        watchdog:
          check_interval: 1s
          failure_threshold: 1
          recovery_timeout: 20s
          restart_cooldown: 0s
          startup_grace: 0s
          state_directory: '{stateDir}'
          log_level: error
          restart_policy:
            max_restarts: 2
            window: 1h
          backoff:
            enabled: false
        services:
        {services}
        """;

    private const string FlakyService = """
          - name: flaky
            checks:
              - type: command
                command: "exit 1"
                timeout: 2s
            restart:
              type: none
        """;

    private const string SteadyService = """
          - name: steady
            checks:
              - type: command
                command: "exit 0"
                timeout: 2s
            restart:
              type: none
        """;

    private static async Task<ControlResult> SendControlAsync(string stateDir, string action, string? service)
    {
        var controlDir = Path.Combine(stateDir, "control");
        var resultsDir = Path.Combine(controlDir, "results");
        Directory.CreateDirectory(resultsDir);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(controlDir, id + ".json"),
            JsonSerializer.Serialize(new ControlCommand(id, action, service, DateTime.UtcNow)));
        // Monotonic clock on purpose: wall-clock deadlines fire spuriously
        // across sleep/wake cycles.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var resultFile = Path.Combine(resultsDir, id + ".json");
            if (File.Exists(resultFile))
            {
                var result = JsonSerializer.Deserialize<ControlResult>(
                    await File.ReadAllTextAsync(resultFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                try { File.Delete(resultFile); } catch { }
                Assert.NotNull(result);
                return result;
            }
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for control result '{action}'.");
    }

    [Fact]
    public async Task MonitorOnlyServiceNeverBurnsRestartBudget()
    {
        var dir = TestFactory.TempDir();
        var stateDir = Path.Combine(dir, "state");
        var path = WriteConfig(dir, ConfigBody(stateDir, FlakyService));
        var loaded = ConfigLoader.Load(path);
        var engine = new WatchdogEngine(loaded.Config, path, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = engine.RunAsync(cts.Token);
        try
        {
            await TestFactory.WaitUntilAsync(
                () => engine.GetServiceState("flaky") == Health.HealthState.Unhealthy,
                "flaky to become unhealthy");
            await Task.Delay(TimeSpan.FromSeconds(1500));

            var record = engine.Store.Get("flaky");
            Assert.Equal(0, record.TotalRestarts);
            Assert.Equal(0, record.RestartsInWindow);
            // Must NOT escalate to FAILED: there is no restart budget to exhaust.
            Assert.NotEqual(Health.HealthState.Failed.ToString(), record.State);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task ReloadKeepsState_RemovedServicesStop_UnknownControlRejected()
    {
        var dir = TestFactory.TempDir();
        var stateDir = Path.Combine(dir, "state");
        var path = WriteConfig(dir, ConfigBody(stateDir, FlakyService));
        var loaded = ConfigLoader.Load(path);
        var engine = new WatchdogEngine(loaded.Config, path, NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var run = engine.RunAsync(cts.Token);
        try
        {
            await TestFactory.WaitUntilAsync(
                () => engine.GetServiceState("flaky") == Health.HealthState.Unhealthy,
                "flaky to become unhealthy");
        var failuresBefore = engine.Store.Get("flaky").ConsecutiveFailures;
        Assert.True(failuresBefore > 0);

        // Add a service via reload: store must survive (same object, same file).
        WriteConfig(dir, ConfigBody(stateDir, FlakyService + "\n" + SteadyService));
        var reload = await SendControlAsync(stateDir, "reload", null);
        Assert.True(reload.Ok, reload.Message);
        await TestFactory.WaitUntilAsync(
            () => engine.GetServiceState("steady") == Health.HealthState.Healthy,
            "steady to become healthy after reload");
        Assert.True(engine.Store.Get("flaky").ConsecutiveFailures > 0);

        // Pause must stick after a reload (guards against split-brain stores).
        var pause = await SendControlAsync(stateDir, "pause", "flaky");
        Assert.True(pause.Ok, pause.Message);
        await Task.Delay(TimeSpan.FromSeconds(1500));
        var paused = new State.StateStore(stateDir).Get("flaky");
        Assert.True(paused.Paused);
        Assert.Equal(Health.HealthState.Disabled.ToString(), paused.State);

        // Unknown services are rejected, not silently recorded.
        var ghost = await SendControlAsync(stateDir, "pause", "ghost");
        Assert.False(ghost.Ok);
        var resume = await SendControlAsync(stateDir, "resume", "ghost");
        Assert.False(resume.Ok);

        // Removing a service from config stops its loop entirely. The engine prunes
        // the record; a still-running orphaned loop would recreate it on its next
        // tick, so its continued absence proves the loop really stopped.
        WriteConfig(dir, ConfigBody(stateDir, SteadyService));
        var reload2 = await SendControlAsync(stateDir, "reload", null);
        Assert.True(reload2.Ok, reload2.Message);
        Assert.Null(engine.GetMonitor("flaky"));
        await Task.Delay(TimeSpan.FromSeconds(2500));
        Assert.False(new State.StateStore(stateDir).Snapshot().ContainsKey("flaky"));
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }
}
