using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OrionCopy.Watchdog.Config;

public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
    public ConfigException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Loads watchdog.yml (or .json), applies ${ENV} substitution, deserializes,
/// applies defaults, and resolves the config file location.
/// </summary>
public static class ConfigLoader
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string DefaultFileName => "watchdog.yml";

    /// <summary>Resolve which config file to use.</summary>
    public static string ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var fromEnv = Environment.GetEnvironmentVariable("ORION_WATCHDOG_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        var local = Path.GetFullPath(DefaultFileName);
        if (File.Exists(local))
            return local;

        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var candidate = Path.Combine(programData, "OrionCopyWatchdog", DefaultFileName);
            if (File.Exists(candidate))
                return candidate;
        }
        else
        {
            foreach (var candidate in new[]
            {
                "/etc/orion-copy-watchdog/watchdog.yml",
                "/opt/orion-copy-watchdog/watchdog.yml",
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return local; // does not exist -> caller reports a helpful error
    }

    public sealed record LoadedConfig(
        WatchdogConfig Config,
        string Path,
        List<string> MissingEnv,
        List<string> Warnings);

    public static LoadedConfig Load(string? explicitPath = null)
    {
        var path = ResolvePath(explicitPath);
        if (!File.Exists(path))
            throw new ConfigException(
                $"Config file not found: {path}\n" +
                $"Copy watchdog.example.yml to watchdog.yml and edit it for your servers, " +
                $"or set ORION_WATCHDOG_CONFIG to its location.");

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new ConfigException($"Cannot read config file {path}: {ex.Message}", ex);
        }

        var substituted = EnvSubstitution.ApplyEnvironment(raw);

        try
        {
            WatchdogConfig config;
            List<string> schemaWarnings = new();
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                config = System.Text.Json.JsonSerializer.Deserialize<WatchdogConfig>(
                    substituted.Text,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) ?? new WatchdogConfig();
            }
            else
            {
                config = YamlDeserializer.Deserialize<WatchdogConfig>(substituted.Text) ?? new WatchdogConfig();
                schemaWarnings = ConfigSchemaWarnings.FindUnknownKeys(substituted.Text);
            }

            Normalize(config);
            return new LoadedConfig(config, path, substituted.MissingVariables, schemaWarnings);
        }
        catch (ConfigException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConfigException($"Failed to parse config file {path}: {ex.Message}", ex);
        }
    }

    private static void Normalize(WatchdogConfig config)
    {
        config.Watchdog ??= new GlobalConfig();
        config.Services ??= new List<ServiceConfig>();
        config.Notifications ??= new NotificationsConfig();
        config.Reporting ??= new ReportingConfig();

        foreach (var svc in config.Services)
        {
            svc.DependsOn ??= new List<string>();
            svc.Checks ??= new List<CheckConfig>();
            svc.Restart ??= new RestartConfig();
            svc.Lifecycle = (svc.Lifecycle ?? "persistent").Trim().ToLowerInvariant();
            svc.HealthMode = (svc.HealthMode ?? "all").Trim().ToLowerInvariant();
            foreach (var check in svc.Checks)
            {
                check.Type = (check.Type ?? "").Trim().ToLowerInvariant();
                check.Headers ??= new Dictionary<string, string>();
            }
            svc.Restart.Type = (svc.Restart.Type ?? "none").Trim().ToLowerInvariant();
        }
    }
}
