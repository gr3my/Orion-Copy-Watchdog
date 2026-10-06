namespace OrionCopy.Watchdog.Config;

public sealed record ConfigFinding(bool IsError, string Message);

/// <summary>
/// Strong validation for watchdog.yml. Run via `orion-watchdog validate-config`.
/// Distinguishes errors (watchdog refuses to start) from warnings (suspicious but allowed).
/// </summary>
public static class ConfigValidator
{
    private static readonly HashSet<string> CheckTypes = new(StringComparer.OrdinalIgnoreCase)
        { "http", "tcp", "process", "windows_service", "systemd", "command" };

    private static readonly HashSet<string> RestartTypes = new(StringComparer.OrdinalIgnoreCase)
        { "none", "process", "windows_service", "systemd", "command", "http" };

    public static List<ConfigFinding> Validate(WatchdogConfig config, List<string>? missingEnv = null)
    {
        var findings = new List<ConfigFinding>();
        void Error(string m) => findings.Add(new ConfigFinding(true, m));
        void Warn(string m) => findings.Add(new ConfigFinding(false, m));

        if (missingEnv is not null)
            foreach (var name in missingEnv)
                Error($"Environment variable '{name}' is referenced in the config (as ${{{name}}}) but is not set.");

        var g = config.Watchdog;
        CheckDuration(g.CheckInterval, "watchdog.check_interval", Error, min: TimeSpan.FromSeconds(1));
        if (g.FailureThreshold < 1)
            Error("watchdog.failure_threshold must be >= 1.");
        CheckDuration(g.RecoveryTimeout, "watchdog.recovery_timeout", Error, min: TimeSpan.FromSeconds(5));
        CheckDuration(g.RestartCooldown, "watchdog.restart_cooldown", Error, min: TimeSpan.Zero);
        CheckDuration(g.StartupGrace, "watchdog.startup_grace", Error, min: TimeSpan.Zero);
        if (string.IsNullOrWhiteSpace(g.StateDirectory))
            Error("watchdog.state_directory must not be empty.");
        if (!IsValidLogLevel(g.LogLevel))
            Error($"watchdog.log_level '{g.LogLevel}' is invalid (expected: debug, info, warn, error).");
        if (g.RestartPolicy.MaxRestarts < 0)
            Error("watchdog.restart_policy.max_restarts must be >= 0 (0 = monitor only, never restart).");
        CheckDuration(g.RestartPolicy.Window, "watchdog.restart_policy.window", Error, min: TimeSpan.FromSeconds(30));
        CheckDuration(g.Backoff.Initial, "watchdog.backoff.initial", Error, min: TimeSpan.Zero);
        if (g.Backoff.Multiplier < 1.0)
            Error("watchdog.backoff.multiplier must be >= 1.");
        CheckDuration(g.Backoff.Max, "watchdog.backoff.max", Error, min: TimeSpan.Zero);

        if (config.Services.Count == 0)
            Warn("No services are configured; the watchdog will start but monitor nothing.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in config.Services)
        {
            var label = string.IsNullOrWhiteSpace(svc.Name) ? "<unnamed>" : $"'{svc.Name}'";

            if (string.IsNullOrWhiteSpace(svc.Name))
            {
                Error("A service is missing its required 'name'.");
                continue;
            }
            if (!names.Add(svc.Name))
                Error($"Duplicate service name {label}; names must be unique (case-insensitive).");

            if (svc.Lifecycle is not ("persistent" or "dynamic"))
                Error($"Service {label}: lifecycle '{svc.Lifecycle}' is invalid (expected 'persistent' or 'dynamic').");
            if (svc.HealthMode is not ("all" or "any"))
                Error($"Service {label}: health_mode '{svc.HealthMode}' is invalid (expected 'all' or 'any').");
            if (svc.FailureThreshold is { } ft && ft < 1)
                Error($"Service {label}: failure_threshold must be >= 1.");
            if (svc.CheckInterval is { } ci)
                CheckDuration(ci, $"service {label}.check_interval", Error, min: TimeSpan.FromSeconds(1));
            if (svc.RecoveryTimeout is { } rt)
                CheckDuration(rt, $"service {label}.recovery_timeout", Error, min: TimeSpan.FromSeconds(5));
            if (svc.StartupGrace is { } sg)
                CheckDuration(sg, $"service {label}.startup_grace", Error, min: TimeSpan.Zero);

            foreach (var dep in svc.DependsOn)
            {
                if (string.Equals(dep, svc.Name, StringComparison.OrdinalIgnoreCase))
                    Error($"Service {label}: cannot depend on itself.");
            }

            if (svc.Checks.Count == 0)
            {
                Error($"Service {label}: define at least one entry under 'checks'.");
                continue;
            }

            foreach (var check in svc.Checks)
                ValidateCheck(check, label, Error, Warn);

            ValidateRestart(svc.Restart, label, Error, Warn);
        }

        // Unknown dependency targets.
        foreach (var svc in config.Services)
            foreach (var dep in svc.DependsOn)
                if (!names.Contains(dep))
                    Error($"Service '{svc.Name}': depends_on '{dep}' does not match any configured service.");

        // Dependency cycles.
        foreach (var cycle in FindCycles(config.Services))
            Error($"Dependency cycle detected: {string.Join(" -> ", cycle.Select(n => $"'{n}'"))}.");

        // Notifications / reporting sanity.
        if (config.Notifications.Webhook.Enabled && string.IsNullOrWhiteSpace(config.Notifications.Webhook.Url))
            Error("notifications.webhook is enabled but no 'url' is set.");
        if (config.Notifications.Webhook.Enabled)
            CheckDuration(config.Notifications.Webhook.Timeout, "notifications.webhook.timeout", Error, min: TimeSpan.FromSeconds(1));
        if (config.Reporting.Enabled)
        {
            if (string.IsNullOrWhiteSpace(config.Reporting.ApiUrl))
                Error("reporting is enabled but no 'api_url' is set.");
            else if (!IsHttpUrl(config.Reporting.ApiUrl))
                Error($"reporting.api_url '{config.Reporting.ApiUrl}' is not a valid http(s) URL.");
            if (string.IsNullOrWhiteSpace(config.Reporting.ApiKey))
                Warn("reporting is enabled but no 'api_key' is set; the server may reject reports.");
            CheckDuration(config.Reporting.Interval, "reporting.interval", Error, min: TimeSpan.FromSeconds(10));
        }

        return findings;
    }

    private static void ValidateCheck(CheckConfig check, string label,
        Action<string> Error, Action<string> Warn)
    {
        if (!CheckTypes.Contains(check.Type))
        {
            Error($"Service {label}: unknown check type '{check.Type}' " +
                  "(expected: http, tcp, process, windows_service, systemd, command).");
            return;
        }

        if (!Duration.TryParse(check.Timeout, out var timeout) || timeout <= TimeSpan.Zero)
            Error($"Service {label}: check '{check.Type}' has invalid timeout '{check.Timeout}'.");
        else if (timeout > TimeSpan.FromMinutes(2))
            Warn($"Service {label}: check '{check.Type}' timeout '{check.Timeout}' is very long; checks run every interval.");

        switch (check.Type)
        {
            case "http":
                if (string.IsNullOrWhiteSpace(check.Url))
                    Error($"Service {label}: http check requires 'url'.");
                else if (!IsHttpUrl(check.Url))
                    Error($"Service {label}: http check url '{check.Url}' is not a valid http(s) URL.");
                if (check.ExpectedStatus is < 100 or > 599)
                    Error($"Service {label}: http check expected_status {check.ExpectedStatus} is not a valid HTTP status.");
                break;
            case "tcp":
                if (string.IsNullOrWhiteSpace(check.Host))
                    Error($"Service {label}: tcp check requires 'host'.");
                if (check.Port is < 1 or > 65535)
                    Error($"Service {label}: tcp check port {check.Port} is out of range (1-65535).");
                break;
            case "process":
                if (string.IsNullOrWhiteSpace(check.ProcessName))
                    Error($"Service {label}: process check requires 'process_name'.");
                break;
            case "windows_service":
            case "systemd":
                if (string.IsNullOrWhiteSpace(check.ServiceName))
                    Error($"Service {label}: {check.Type} check requires 'service_name'.");
                break;
            case "command":
                if (string.IsNullOrWhiteSpace(check.Command))
                    Error($"Service {label}: command check requires 'command'.");
                else
                    Warn($"Service {label}: command checks execute local commands; ensure the command is trusted.");
                break;
        }
    }

    private static void ValidateRestart(RestartConfig restart, string label,
        Action<string> Error, Action<string> Warn)
    {
        if (!RestartTypes.Contains(restart.Type))
        {
            Error($"Service {label}: unknown restart type '{restart.Type}' " +
                  "(expected: none, process, windows_service, systemd, command, http).");
            return;
        }

        if (!Duration.TryParse(restart.GracefulTimeout, out var grace) || grace < TimeSpan.Zero)
            Error($"Service {label}: restart graceful_timeout '{restart.GracefulTimeout}' is invalid.");

        switch (restart.Type)
        {
            case "process":
                if (string.IsNullOrWhiteSpace(restart.Executable))
                    Error($"Service {label}: process restart requires 'executable'.");
                else if (Path.IsPathRooted(restart.Executable) && !File.Exists(restart.Executable))
                    Warn($"Service {label}: executable '{restart.Executable}' does not exist on this machine.");
                if (!string.IsNullOrWhiteSpace(restart.WorkingDirectory) &&
                    Path.IsPathRooted(restart.WorkingDirectory) && !Directory.Exists(restart.WorkingDirectory))
                    Warn($"Service {label}: working_directory '{restart.WorkingDirectory}' does not exist on this machine.");
                break;
            case "windows_service":
            case "systemd":
                if (string.IsNullOrWhiteSpace(restart.ServiceName))
                    Error($"Service {label}: {restart.Type} restart requires 'service_name'.");
                break;
            case "command":
                if (string.IsNullOrWhiteSpace(restart.Command) &&
                    string.IsNullOrWhiteSpace(restart.StartCommand))
                    Error($"Service {label}: command restart requires 'command' or 'start_command'.");
                else
                    Warn($"Service {label}: command restarts execute local commands; ensure they are trusted.");
                break;
            case "http":
                if (string.IsNullOrWhiteSpace(restart.Url))
                    Error($"Service {label}: http restart requires 'url'.");
                else if (!IsHttpUrl(restart.Url))
                    Error($"Service {label}: http restart url '{restart.Url}' is not a valid http(s) URL.");
                break;
        }
    }

    private static void CheckDuration(string? value, string field,
        Action<string> Error, TimeSpan min)
    {
        if (!Duration.TryParse(value, out var parsed))
            Error($"{field} has invalid duration '{value}' (examples: 500ms, 10s, 5m, 2h, 1d).");
        else if (parsed < min)
            Error($"{field} of '{value}' is below the minimum of {min}.");
    }

    private static bool IsValidLogLevel(string? level) =>
        level?.Trim().ToLowerInvariant() is "debug" or "info" or "warn" or "error";

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Depth-first cycle detection over depends_on edges. Returns each cycle found.</summary>
    public static List<List<string>> FindCycles(List<ServiceConfig> services)
    {
        var byName = services
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var cycles = new List<List<string>>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unvisited 1=in-stack 2=done
        var stack = new Stack<string>();

        void Visit(string name)
        {
            state[name] = 1;
            stack.Push(name);
            if (byName.TryGetValue(name, out var svc))
            {
                foreach (var dep in svc.DependsOn)
                {
                    if (!byName.ContainsKey(dep))
                        continue;
                    if (!state.TryGetValue(dep, out var s))
                        Visit(dep);
                    else if (s == 1)
                        cycles.Add(stack.Reverse().SkipWhile(n =>
                            !string.Equals(n, dep, StringComparison.OrdinalIgnoreCase))
                            .Concat(new[] { dep }).ToList());
                }
            }
            stack.Pop();
            state[name] = 2;
        }

        foreach (var name in byName.Keys)
            if (!state.ContainsKey(name))
                Visit(name);

        return cycles;
    }
}
