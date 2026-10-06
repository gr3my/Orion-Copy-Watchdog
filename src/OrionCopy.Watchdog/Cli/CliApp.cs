using System.Reflection;
using System.Text.Json;
using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Core;
using OrionCopy.Watchdog.Health;
using OrionCopy.Watchdog.Hosting;
using OrionCopy.Watchdog.Logging;
using OrionCopy.Watchdog.State;

namespace OrionCopy.Watchdog.Cli;

public static class CliApp
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static async Task<int> RunAsync(string[] args)
    {
        var (configPath, rest) = ExtractConfigFlag(args);
        var command = rest.Length > 0 ? rest[0].ToLowerInvariant() : "help";
        var commandArgs = rest.Skip(1).ToArray();

        switch (command)
        {
            case "start":
                return await StartAsync(configPath).ConfigureAwait(false);
            case "status":
                return Status(configPath, commandArgs.FirstOrDefault());
            case "list":
                return Status(configPath, null);
            case "check":
                return await CheckAsync(configPath, commandArgs.FirstOrDefault()).ConfigureAwait(false);
            case "restart":
                return await SendControlAsync(configPath, "restart", RequiredName(commandArgs, "restart")).ConfigureAwait(false);
            case "pause":
                return await SendControlAsync(configPath, "pause", RequiredName(commandArgs, "pause")).ConfigureAwait(false);
            case "resume":
                return await SendControlAsync(configPath, "resume", RequiredName(commandArgs, "resume")).ConfigureAwait(false);
            case "reset":
                return await SendControlAsync(configPath, "reset", RequiredName(commandArgs, "reset")).ConfigureAwait(false);
            case "reload":
                return await SendControlAsync(configPath, "reload", null).ConfigureAwait(false);
            case "validate-config":
                return ValidateConfig(configPath);
            case "version":
                Console.WriteLine($"orion-watchdog {Version}");
                return 0;
            case "help":
            case "--help":
            case "-h":
                PrintHelp();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Run 'orion-watchdog help'.");
                return 2;
        }
    }

    private static string? RequiredName(string[] args, string command)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine($"Usage: orion-watchdog {command} <service-name>");
            return null;
        }
        return args[0];
    }

    private static (string? ConfigPath, string[] Args) ExtractConfigFlag(string[] args)
    {
        string? config = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length)
            {
                config = args[++i];
            }
            else if (args[i].StartsWith("--config=", StringComparison.Ordinal))
            {
                config = args[i]["--config=".Length..];
            }
            else
            {
                rest.Add(args[i]);
            }
        }
        return (config, rest.ToArray());
    }

    // ── start ──

    private static async Task<int> StartAsync(string? configPath)
    {
        WatchdogConfig config;
        string resolved;
        try
        {
            var loaded = ConfigLoader.Load(configPath);
            config = loaded.Config;
            resolved = loaded.Path;
            var findings = ConfigValidator.Validate(config, loaded.MissingEnv);
            foreach (var warning in findings.Where(f => !f.IsError))
                Console.WriteLine($"WARN: {warning.Message}");
            foreach (var warning in loaded.Warnings)
                Console.WriteLine($"WARN: {warning}");
            var errors = findings.Where(f => f.IsError).ToList();
            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"Refusing to start: {errors.Count} config error(s):");
                foreach (var error in errors)
                    Console.Error.WriteLine($"  - {error.Message}");
                return 2;
            }
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"Config error: {ex.Message}");
            return 2;
        }

        using var log = new WatchdogLogger(config.Watchdog.LogLevel, config.Watchdog.LogFile);
        return await HostFactory.RunDaemonAsync(config, resolved, log).ConfigureAwait(false);
    }

    // ── validate-config ──

    private static int ValidateConfig(string? configPath)
    {
        try
        {
            var loaded = ConfigLoader.Load(configPath);
            var findings = ConfigValidator.Validate(loaded.Config, loaded.MissingEnv);
            var errors = findings.Where(f => f.IsError).ToList();
            var warnings = findings.Where(f => !f.IsError).Select(f => f.Message)
                .Concat(loaded.Warnings).ToList();

            Console.WriteLine($"Config: {loaded.Path}");
            Console.WriteLine($"Services: {loaded.Config.Services.Count}");
            foreach (var warning in warnings)
                Console.WriteLine($"  WARN: {warning}");
            foreach (var error in errors)
                Console.WriteLine($"  ERROR: {error.Message}");

            if (errors.Count > 0)
            {
                Console.WriteLine($"INVALID: {errors.Count} error(s), {warnings.Count} warning(s).");
                return 1;
            }
            Console.WriteLine(warnings.Count > 0
                ? $"VALID with {warnings.Count} warning(s)."
                : "VALID: configuration is OK.");
            return 0;
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"INVALID: {ex.Message}");
            return 1;
        }
    }

    // ── status / list ──

    private static int Status(string? configPath, string? nameFilter)
    {
        WatchdogConfig config;
        try
        {
            config = ConfigLoader.Load(configPath).Config;
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"Config error: {ex.Message}");
            return 2;
        }

        var stateDir = string.IsNullOrWhiteSpace(config.Watchdog.StateDirectory)
            ? "./state" : config.Watchdog.StateDirectory;
        var stateFile = Path.Combine(stateDir, "services.json");
        if (!File.Exists(stateFile))
        {
            Console.WriteLine("The watchdog does not appear to be running (no state file yet).");
            Console.WriteLine($"Expected state at: {Path.GetFullPath(stateFile)}");
            return 1;
        }

        Dictionary<string, ServiceStateRecord> snapshot;
        try
        {
            snapshot = new StateStore(stateDir).Snapshot();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read state: {ex.Message}");
            return 1;
        }

        var names = config.Services.Select(s => s.Name).ToList();
        if (nameFilter is not null)
        {
            names = names.Where(n => string.Equals(n, nameFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (names.Count == 0)
            {
                Console.Error.WriteLine($"Unknown service '{nameFilter}'. Known services:");
                foreach (var known in config.Services.Select(s => s.Name))
                    Console.Error.WriteLine($"  - {known}");
                return 1;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Orion Copy Watchdog");
        Console.WriteLine();
        Console.WriteLine($"{"NAME",-28} {"STATE",-12} {"UPTIME",-12} {"RESTARTS",-9} LAST MESSAGE");
        Console.WriteLine(new string('-', 100));

        DateTime? lastRecovery = null;
        string lastRecoveredService = "";
        foreach (var name in names)
        {
            snapshot.TryGetValue(name, out var record);
            var state = record?.State.ToUpperInvariant() ?? "UNKNOWN";
            var uptime = record?.HealthySince is { } since ? FormatAge(DateTime.UtcNow - since) : "-";
            var restarts = record?.TotalRestarts.ToString() ?? "0";
            var message = record?.LastMessage ?? "no data yet";
            if (message.Length > 45)
                message = message[..42] + "...";
            Console.WriteLine($"{Truncate(name, 28),-28} {state,-12} {uptime,-12} {restarts,-9} {message}");

            if (record?.LastRecovery is { } rec && (lastRecovery is null || rec > lastRecovery))
            {
                lastRecovery = rec;
                lastRecoveredService = name;
            }
        }

        Console.WriteLine();
        if (lastRecovery is not null)
            Console.WriteLine($"Last recovery: {lastRecoveredService} restarted successfully {FormatAge(DateTime.UtcNow - lastRecovery.Value)} ago");
        else
            Console.WriteLine("Last recovery: none recorded");

        var failed = names.Where(n => snapshot.TryGetValue(n, out var r) && r.State == "Failed").ToList();
        if (failed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"ATTENTION: {failed.Count} service(s) need manual intervention: {string.Join(", ", failed)}");
            Console.WriteLine("Run 'orion-watchdog reset <name>' after fixing the underlying problem.");
        }
        return 0;
    }

    // ── check (one-shot, no daemon needed) ──

    private static async Task<int> CheckAsync(string? configPath, string? nameFilter)
    {
        WatchdogConfig config;
        try
        {
            var loaded = ConfigLoader.Load(configPath);
            config = loaded.Config;
            var findings = ConfigValidator.Validate(config, loaded.MissingEnv);
            var errors = findings.Where(f => f.IsError).ToList();
            if (errors.Count > 0)
            {
                Console.Error.WriteLine("Config has errors; fix them first (run validate-config).");
                return 2;
            }
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"Config error: {ex.Message}");
            return 2;
        }

        var services = config.Services.AsEnumerable();
        if (nameFilter is not null)
        {
            services = services.Where(s => string.Equals(s.Name, nameFilter, StringComparison.OrdinalIgnoreCase));
            if (!services.Any())
            {
                Console.Error.WriteLine($"Unknown service '{nameFilter}'.");
                return 2;
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var overallHealthy = true;
        foreach (var svc in services)
        {
            using var log = new WatchdogLogger("error", null);
            var monitor = new ServiceMonitor(svc, config.Watchdog,
                new StateStore(Path.Combine(Path.GetTempPath(), "orion-watchdog-check")),
                log, new Notifications.WebhookNotifier(false, null, TimeSpan.FromSeconds(5), log),
                _ => null);
            List<Health.CheckResult> results;
            try
            {
                results = await monitor.CheckOnceAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{svc.Name}: ERROR {ex.Message}");
                overallHealthy = false;
                continue;
            }

            var healthy = monitor.EvaluateOnce(results);
            overallHealthy &= healthy;
            Console.WriteLine($"{svc.Name}: {(healthy ? "HEALTHY" : "UNHEALTHY")} " +
                              $"(mode {svc.HealthMode})");
            foreach (var result in results)
                Console.WriteLine($"  [{(result.Healthy ? "pass" : "FAIL")}] {result.CheckType}: {result.Detail}");
        }

        // Best-effort cleanup of the throwaway check state.
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "orion-watchdog-check"), recursive: true); }
        catch { }

        return overallHealthy ? 0 : 1;
    }

    // ── daemon control commands ──

    private static async Task<int> SendControlAsync(string? configPath, string action, string? service)
    {
        if (service is null && action != "reload")
            return 2; // usage already printed

        string stateDir;
        try
        {
            var config = ConfigLoader.Load(configPath).Config;
            stateDir = string.IsNullOrWhiteSpace(config.Watchdog.StateDirectory)
                ? "./state" : config.Watchdog.StateDirectory;
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"Config error: {ex.Message}");
            return 2;
        }

        var controlDir = Path.Combine(stateDir, "control");
        var resultsDir = Path.Combine(controlDir, "results");
        var id = Guid.NewGuid().ToString("N");
        var command = new ControlCommand(id, action, service, DateTime.UtcNow);

        try
        {
            Directory.CreateDirectory(resultsDir);
            await File.WriteAllTextAsync(Path.Combine(controlDir, id + ".json"),
                JsonSerializer.Serialize(command)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not send command: {ex.Message}");
            return 1;
        }

        // Wait for the daemon to pick it up and answer. Monotonic clock on
        // purpose: wall-clock deadlines fire spuriously across sleep/wake.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(12))
        {
            var resultFile = Path.Combine(resultsDir, id + ".json");
            if (File.Exists(resultFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(resultFile).ConfigureAwait(false);
                    var result = JsonSerializer.Deserialize<ControlResult>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    try { File.Delete(resultFile); } catch { }
                    if (result is null)
                    {
                        Console.Error.WriteLine("Daemon returned an unreadable response.");
                        return 1;
                    }
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not read daemon response: {ex.Message}");
                    return 1;
                }
            }
            await Task.Delay(250).ConfigureAwait(false);
        }

        Console.Error.WriteLine(
            "The watchdog daemon did not answer. Is it running? " +
            "(Start it with 'orion-watchdog start'. 'check' works without the daemon.)");
        try { File.Delete(Path.Combine(controlDir, id + ".json")); } catch { }
        return 1;
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{(int)age.TotalDays}d {age.Hours:D2}h" :
        age.TotalHours >= 1 ? $"{(int)age.TotalHours}h {age.Minutes:D2}m" :
        age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m {age.Seconds:D2}s" :
        $"{(int)age.TotalSeconds}s";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 3)] + "...";

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            orion-watchdog {Version} — configurable health monitor and automatic recovery
            for Orion Copy infrastructure.

            Usage: orion-watchdog [--config <path>] <command> [args]

            Daemon:
              start                 Start monitoring (runs as console, Windows Service,
                                    or systemd unit depending on how it was launched)

            Inspection (read local state, daemon must have run at least once):
              status [name]         Show service states, uptime, restarts
              list                  Same as status (all services)
              check [name]          Run health checks once right now (no daemon needed)

            Control (sent to the running daemon):
              restart <name>        Recover a service immediately (graceful restart + verify)
              pause <name>          Stop monitoring a service (state DISABLED)
              resume <name>         Resume monitoring a paused service
              reset <name>          Clear failure/restart counters (e.g. after fixing a FAILED service)
              reload                Reload watchdog.yml without interrupting healthy services

            Config:
              validate-config       Validate watchdog.yml and explain exactly what is wrong

            Other:
              version               Print version
              help                  Show this help

            Quick start:
              1. Copy watchdog.example.yml to watchdog.yml and edit your servers.
              2. orion-watchdog validate-config
              3. orion-watchdog start
            """);
    }
}
