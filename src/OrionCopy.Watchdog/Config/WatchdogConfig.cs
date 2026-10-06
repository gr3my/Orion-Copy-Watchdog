namespace OrionCopy.Watchdog.Config;

/// <summary>
/// Root of watchdog.yml. All deployment-specific values live here or in the
/// environment — never in source code.
/// </summary>
public sealed class WatchdogConfig
{
    public GlobalConfig Watchdog { get; set; } = new();
    public List<ServiceConfig> Services { get; set; } = new();
    public NotificationsConfig Notifications { get; set; } = new();
    public ReportingConfig Reporting { get; set; } = new();
}

public sealed class GlobalConfig
{
    public string CheckInterval { get; set; } = "10s";
    public int FailureThreshold { get; set; } = 3;
    public string RecoveryTimeout { get; set; } = "60s";
    public string RestartCooldown { get; set; } = "30s";
    public string StartupGrace { get; set; } = "60s";
    public string StateDirectory { get; set; } = "./state";
    public string LogLevel { get; set; } = "info";
    public string? LogFile { get; set; }
    public RestartPolicyConfig RestartPolicy { get; set; } = new();
    public BackoffConfig Backoff { get; set; } = new();
    public bool AutoRetryFailed { get; set; } = false;
}

public sealed class RestartPolicyConfig
{
    public int MaxRestarts { get; set; } = 5;
    public string Window { get; set; } = "10m";
}

public sealed class BackoffConfig
{
    public bool Enabled { get; set; } = true;
    public string Initial { get; set; } = "5s";
    public double Multiplier { get; set; } = 2.0;
    public string Max { get; set; } = "2m";
}

public sealed class ServiceConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>"persistent" (unexpected stop triggers restart) or "dynamic" (a stopped
    /// process is expected, e.g. an allocator-spawned match that ended).</summary>
    public string Lifecycle { get; set; } = "persistent";

    /// <summary>"all" (every check must pass) or "any" (one passing check is enough).</summary>
    public string HealthMode { get; set; } = "all";

    public int? FailureThreshold { get; set; }
    public string? CheckInterval { get; set; }
    public string? RecoveryTimeout { get; set; }
    public string? StartupGrace { get; set; }
    public bool? AutoRetryFailed { get; set; }

    /// <summary>
    /// For dynamic services: restart when the process vanishes unexpectedly.
    /// Default is true for persistent services, false for dynamic ones.
    /// (A dynamic service whose process is alive but app-unhealthy still recovers.)
    /// </summary>
    public bool? RestartOnExit { get; set; }

    public List<string> DependsOn { get; set; } = new();
    public List<CheckConfig> Checks { get; set; } = new();
    public RestartConfig Restart { get; set; } = new();

    public bool IsDynamic =>
        string.Equals(Lifecycle, "dynamic", StringComparison.OrdinalIgnoreCase);

    public bool WantsRestartOnExit =>
        RestartOnExit ?? !IsDynamic;
}

public sealed class CheckConfig
{
    /// <summary>http | tcp | process | windows_service | systemd | command</summary>
    public string Type { get; set; } = "";
    public string? Url { get; set; }
    public string? Method { get; set; }
    public int ExpectedStatus { get; set; } = 200;
    public string? Contains { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? ProcessName { get; set; }
    public string? ServiceName { get; set; }
    public string? Command { get; set; }
    public string? WorkingDirectory { get; set; }
    public string Timeout { get; set; } = "5s";
    public Dictionary<string, string> Headers { get; set; } = new();
}

public sealed class RestartConfig
{
    /// <summary>none | process | windows_service | systemd | command | http</summary>
    public string Type { get; set; } = "none";
    public string? ServiceName { get; set; }
    public string? Executable { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Command { get; set; }
    public string? StopCommand { get; set; }
    public string? StartCommand { get; set; }
    public string? Url { get; set; }
    public string? Method { get; set; }
    public int ExpectedStatus { get; set; } = 200;

    /// <summary>Name of an environment variable holding the Authorization header
    /// value for http restarts. The secret is never logged.</summary>
    public string? AuthorizationEnv { get; set; }
    public string GracefulTimeout { get; set; } = "15s";
}

public sealed class NotificationsConfig
{
    public WebhookConfig Webhook { get; set; } = new();
}

public sealed class WebhookConfig
{
    public bool Enabled { get; set; } = false;
    public string? Url { get; set; }
    public string Timeout { get; set; } = "10s";
}

public sealed class ReportingConfig
{
    /// <summary>Optional Orion Copy Server status reporting. Off by default;
    /// the watchdog works perfectly without it.</summary>
    public bool Enabled { get; set; } = false;
    public string? ApiUrl { get; set; }
    public string? ApiKey { get; set; }
    public string Interval { get; set; } = "60s";
    public string Timeout { get; set; } = "10s";
}
