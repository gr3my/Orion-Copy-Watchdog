using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Core;
using OrionCopy.Watchdog.Health;
using OrionCopy.Watchdog.Logging;

namespace OrionCopy.Watchdog.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData(1, 5000)]
    [InlineData(2, 10000)]
    [InlineData(3, 20000)]
    [InlineData(4, 40000)]
    [InlineData(10, 120000)] // capped at max
    public void ExponentialBackoffSequenceIsCapped(int attempt, int expectedMs)
    {
        var delay = BackoffPolicy.ComputeDelay(
            attempt, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(2));
        Assert.Equal(expectedMs, (int)delay.TotalMilliseconds);
    }

    [Fact]
    public void RestartWindowSlidesAfterExpiry()
    {
        var now = DateTime.UtcNow;
        var slid = RestartWindowTracker.SlideWindow(
            now, now - TimeSpan.FromHours(2), TimeSpan.FromMinutes(10), 5,
            out var newStart, out var newCount);
        Assert.True(slid);
        Assert.Equal(0, newCount);
        Assert.Equal(now, newStart, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RestartWindowHoldsInsideWindow()
    {
        var now = DateTime.UtcNow;
        var start = now - TimeSpan.FromMinutes(2);
        var slid = RestartWindowTracker.SlideWindow(
            now, start, TimeSpan.FromMinutes(10), 3,
            out var newStart, out var newCount);
        Assert.False(slid);
        Assert.Equal(3, newCount);
        Assert.Equal(start, newStart);
    }

    [Fact]
    public void DependenciesGateRecovery()
    {
        var svc = new ServiceConfig { Name = "arena", DependsOn = new() { "backend" } };
        Assert.False(DependencyGraph.DependenciesHealthy(
            svc, _ => HealthState.Healthy == HealthState.Unhealthy ? HealthState.Healthy : HealthState.Unhealthy,
            out var blocker));
        Assert.Contains("backend", blocker);

        Assert.True(DependencyGraph.DependenciesHealthy(
            svc, _ => HealthState.Healthy, out blocker));
        Assert.Null(blocker);
    }

    [Fact]
    public void TopologicalOrderPutsDependenciesFirst()
    {
        var services = new[]
        {
            new ServiceConfig { Name = "arena", DependsOn = new() { "allocator" } },
            new ServiceConfig { Name = "backend" },
            new ServiceConfig { Name = "allocator", DependsOn = new() { "backend" } },
        };
        var order = DependencyGraph.TopologicalOrder(services);
        Assert.True(order.IndexOf("backend") < order.IndexOf("allocator"));
        Assert.True(order.IndexOf("allocator") < order.IndexOf("arena"));
    }

    [Fact]
    public void ServiceOverridesWinOverGlobalPolicy()
    {
        var global = new GlobalConfig { FailureThreshold = 3, CheckInterval = "10s" };
        var svc = new ServiceConfig { Name = "x", FailureThreshold = 7, CheckInterval = "25s" };
        var policy = ResolvedPolicy.From(svc, global);
        Assert.Equal(7, policy.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(25), policy.CheckInterval);
    }

    [Fact]
    public void GlobalPolicyAppliesWhenNoOverride()
    {
        var global = new GlobalConfig { FailureThreshold = 3 };
        var policy = ResolvedPolicy.From(new ServiceConfig { Name = "x" }, global);
        Assert.Equal(3, policy.FailureThreshold);
    }
}

public sealed class ConfigTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("10s", 10000)]
    [InlineData("5m", 300000)]
    [InlineData("2h", 7200000)]
    [InlineData("1d", 86400000)]
    [InlineData("30", 30000)] // bare number = seconds
    public void DurationParsesKnownFormats(string input, int expectedMs)
    {
        Assert.Equal(expectedMs, (int)Duration.Parse(input).TotalMilliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("10x")]
    [InlineData("m5")]
    public void DurationRejectsGarbage(string input)
    {
        Assert.Throws<FormatException>(() => Duration.Parse(input));
    }

    [Fact]
    public void DurationRejectsOutOfRange()
    {
        Assert.Throws<FormatException>(() => Duration.Parse("999999999999999d"));
    }

    [Fact]
    public void EnvSubstitutionReplacesAndCollectsMissing()
    {
        Environment.SetEnvironmentVariable("ORION_WD_TEST_PRESENT", "hello");
        Environment.SetEnvironmentVariable("ORION_WD_TEST_ABSENT", null);

        var result = EnvSubstitution.ApplyEnvironment(
            "a: ${ORION_WD_TEST_PRESENT} b: ${ORION_WD_TEST_ABSENT:-fallback} c: ${ORION_WD_TEST_ABSENT}");
        Assert.Contains("a: hello", result.Text);
        Assert.Contains("b: fallback", result.Text);
        Assert.Contains("${ORION_WD_TEST_ABSENT}", result.Text);
        Assert.Contains("ORION_WD_TEST_ABSENT", result.MissingVariables);
    }

    [Fact]
    public void EnvSubstitutionIgnoresFullLineComments()
    {
        Environment.SetEnvironmentVariable("ORION_WD_TEST_COMMENT_VAR", null);
        var result = EnvSubstitution.ApplyEnvironment(
            "# document ${ORION_WD_TEST_COMMENT_VAR} here\nkey: value");
        Assert.Contains("${ORION_WD_TEST_COMMENT_VAR}", result.Text);
        Assert.DoesNotContain("ORION_WD_TEST_COMMENT_VAR", result.MissingVariables);
    }

    [Fact]
    public void UnknownKeysProduceDidYouMeanWarnings()
    {
        var warnings = ConfigSchemaWarnings.FindUnknownKeys("""
            watchdog:
              check_interval: 10s
            services:
              - name: x
                lifecyle: persistent
                checks:
                  - type: http
                    uri: http://127.0.0.1:1/
                restart:
                  type: none
            """);
        Assert.Contains(warnings, w => w.Contains("lifecyle") && w.Contains("lifecycle"));
        Assert.Contains(warnings, w => w.Contains("uri") && w.Contains("url"));
    }

    [Fact]
    public void KnownKeysProduceNoWarnings()
    {
        var warnings = ConfigSchemaWarnings.FindUnknownKeys("""
            watchdog:
              check_interval: 10s
              restart_policy:
                max_restarts: 5
                window: 10m
              backoff:
                enabled: true
                initial: 5s
                multiplier: 2
                max: 2m
            services:
              - name: x
                lifecycle: persistent
                health_mode: all
                depends_on: []
                checks:
                  - type: http
                    url: http://127.0.0.1:1/
                    headers:
                      X-Custom: yes
                restart:
                  type: process
                  executable: server.exe
            notifications:
              webhook:
                enabled: false
            reporting:
              enabled: false
            """);
        Assert.Empty(warnings);
    }

    [Fact]
    public void RotatedLogFileKeepsReceivingWrites()
    {
        var dir = TestFactory.TempDir();
        var path = Path.Combine(dir, "watchdog.log");
        const string marker = "POSTROTATIONMARKER";
        using (var log = new Logging.WatchdogLogger("debug", path, maxBytes: 300, maxFiles: 2))
        {
            for (var i = 0; i < 30; i++)
                log.Info("svc", $"filler line {i} with padding text to exceed the rotation size");
            log.Info("svc", marker);
        } // disposed: no open handles while asserting on the files
        Assert.True(File.Exists(path + ".1"), "rotation should have archived the first file");
        Assert.Contains(marker, File.ReadAllText(path));
    }

    private static WatchdogConfig MinimalValid() => new()
    {
        Watchdog = new GlobalConfig(),
        Services = new()
        {
            new ServiceConfig
            {
                Name = "backend",
                Checks = new() { new CheckConfig { Type = "http", Url = "http://127.0.0.1:8080/health" } },
                Restart = new RestartConfig { Type = "systemd", ServiceName = "orion-copy-server" },
            },
        },
    };

    [Fact]
    public void ValidConfigProducesNoErrors()
    {
        var findings = ConfigValidator.Validate(MinimalValid());
        Assert.Empty(findings.Where(f => f.IsError));
    }

    [Fact]
    public void DuplicateServiceNamesAreRejected()
    {
        var config = MinimalValid();
        config.Services.Add(new ServiceConfig
        {
            Name = "BACKEND", // case-insensitive duplicate
            Checks = new() { new CheckConfig { Type = "tcp", Host = "127.0.0.1", Port = 90 } },
        });
        Assert.Contains(ConfigValidator.Validate(config),
            f => f.IsError && f.Message.Contains("Duplicate service name"));
    }

    [Fact]
    public void DependencyCyclesAreRejected()
    {
        var config = new WatchdogConfig
        {
            Services = new()
            {
                new ServiceConfig
                {
                    Name = "a",
                    DependsOn = new() { "b" },
                    Checks = new() { new CheckConfig { Type = "tcp", Host = "127.0.0.1", Port = 1 } },
                },
                new ServiceConfig
                {
                    Name = "b",
                    DependsOn = new() { "a" },
                    Checks = new() { new CheckConfig { Type = "tcp", Host = "127.0.0.1", Port = 2 } },
                },
            },
        };
        Assert.Contains(ConfigValidator.Validate(config),
            f => f.IsError && f.Message.Contains("cycle"));
    }

    [Fact]
    public void UnknownDependencyIsRejected()
    {
        var config = MinimalValid();
        config.Services[0].DependsOn.Add("ghost");
        Assert.Contains(ConfigValidator.Validate(config),
            f => f.IsError && f.Message.Contains("ghost"));
    }

    [Fact]
    public void InvalidPortsUrlsAndDurationsAreRejected()
    {
        var config = new WatchdogConfig
        {
            Watchdog = new GlobalConfig { CheckInterval = "never" },
            Services = new()
            {
                new ServiceConfig
                {
                    Name = "bad",
                    Checks = new()
                    {
                        new CheckConfig { Type = "tcp", Host = "127.0.0.1", Port = 99999 },
                        new CheckConfig { Type = "http", Url = "not a url" },
                        new CheckConfig { Type = "bogus" },
                    },
                    Restart = new RestartConfig { Type = "process" }, // missing executable
                },
            },
        };
        var errors = ConfigValidator.Validate(config).Where(f => f.IsError).ToList();
        Assert.True(errors.Count >= 5);
    }

    [Fact]
    public void LoaderAppliesEnvSubstitutionAndReportsMissing()
    {
        var dir = TestFactory.TempDir();
        var path = Path.Combine(dir, "watchdog.yml");
        Environment.SetEnvironmentVariable("ORION_WD_TEST_KEY", "s3cret-value");
        Environment.SetEnvironmentVariable("ORION_WD_TEST_MISSING_XYZ", null);
        File.WriteAllText(path, """
            watchdog:
              check_interval: 10s
            reporting:
              enabled: true
              api_url: http://127.0.0.1:8080
              api_key: ${ORION_WD_TEST_KEY}
            services:
              - name: backend
                checks:
                  - type: http
                    url: http://127.0.0.1:${ORION_WD_TEST_MISSING_XYZ}/health
                restart:
                  type: none
            """);

        var loaded = ConfigLoader.Load(path);
        Assert.Equal("s3cret-value", loaded.Config.Reporting.ApiKey);
        Assert.Contains("ORION_WD_TEST_MISSING_XYZ", loaded.MissingEnv);

        var errors = ConfigValidator.Validate(loaded.Config, loaded.MissingEnv);
        Assert.Contains(errors, f => f.IsError && f.Message.Contains("ORION_WD_TEST_MISSING_XYZ"));
    }

    [Fact]
    public void LoaderAcceptsJsonToo()
    {
        var dir = TestFactory.TempDir();
        var path = Path.Combine(dir, "watchdog.json");
        File.WriteAllText(path, """
            { "services": [
              { "name": "x",
                "checks": [ { "type": "tcp", "host": "127.0.0.1", "port": 80 } ] }
            ] }
            """);
        var loaded = ConfigLoader.Load(path);
        Assert.Single(loaded.Config.Services);
    }
}

public sealed class StateTests
{
    [Fact]
    public void StateSurvivesReload()
    {
        var dir = TestFactory.TempDir();
        var store = new State.StateStore(dir);
        var before = DateTime.UtcNow;
        store.Update("backend", r =>
        {
            r.State = "Unhealthy";
            r.ConsecutiveFailures = 3;
            r.RestartsInWindow = 2;
            r.TotalRestarts = 7;
            return r;
        });

        var reloaded = new State.StateStore(dir).Get("backend");
        Assert.Equal("Unhealthy", reloaded.State);
        Assert.Equal(3, reloaded.ConsecutiveFailures);
        Assert.Equal(2, reloaded.RestartsInWindow);
        Assert.Equal(7, reloaded.TotalRestarts);
        Assert.True(DateTime.UtcNow - before < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void CorruptStateFileDoesNotCrashStartup()
    {
        var dir = TestFactory.TempDir();
        File.WriteAllText(Path.Combine(dir, "services.json"), "{ not valid json !!!");
        var record = new State.StateStore(dir).Get("backend");
        Assert.Equal("Starting", record.State);
    }
}

public sealed class LoggingTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc123", "abc123")]
    [InlineData("api_key=s3cret", "s3cret")]
    [InlineData("token: hunter2", "hunter2")]
    [InlineData("password=letmein", "letmein")]
    [InlineData("https://user:pass@example.com/hook?token=abc", "pass")]
    public void SecretsAreRedacted(string input, string secret)
    {
        var sanitized = Logging.SecretRedaction.Sanitize(input);
        Assert.DoesNotContain(secret, sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void OrdinaryMessagesPassThrough()
    {
        var line = "backend health-check passed attempt=1/3 status=200";
        Assert.Equal(line, Logging.SecretRedaction.Sanitize(line));
    }

    [Fact]
    public void WebhookUrlsAreLoggedWithoutCredentials()
    {
        Assert.Equal("https://example.com/hook",
            Logging.SecretRedaction.SafeUrl("https://user:pass@example.com/hook?token=abc"));
    }
}
