using YamlDotNet.RepresentationModel;

namespace OrionCopy.Watchdog.Config;

/// <summary>
/// Catches config typos that would otherwise be silently ignored
/// (e.g. `heath_url` instead of `url`, or `lifecyle` instead of `lifecycle`).
/// Reports warnings — never errors — with a "did you mean" suggestion.
/// </summary>
public static class ConfigSchemaWarnings
{
    private static readonly HashSet<string> RootKeys = new() { "watchdog", "services", "notifications", "reporting" };

    private static readonly HashSet<string> WatchdogKeys = new()
    {
        "check_interval", "failure_threshold", "recovery_timeout", "restart_cooldown",
        "startup_grace", "state_directory", "log_level", "log_file",
        "restart_policy", "backoff", "auto_retry_failed",
    };

    private static readonly HashSet<string> RestartPolicyKeys = new() { "max_restarts", "window" };
    private static readonly HashSet<string> BackoffKeys = new() { "enabled", "initial", "multiplier", "max" };

    private static readonly HashSet<string> ServiceKeys = new()
    {
        "name", "enabled", "lifecycle", "health_mode", "failure_threshold",
        "check_interval", "recovery_timeout", "startup_grace", "auto_retry_failed",
        "restart_on_exit", "depends_on", "checks", "restart",
    };

    private static readonly HashSet<string> CheckKeys = new()
    {
        "type", "url", "method", "expected_status", "contains", "host", "port",
        "process_name", "service_name", "command", "working_directory", "timeout", "headers",
    };

    private static readonly HashSet<string> RestartKeys = new()
    {
        "type", "service_name", "executable", "arguments", "working_directory",
        "command", "stop_command", "start_command", "url", "method", "expected_status",
        "authorization_env", "graceful_timeout",
    };

    private static readonly HashSet<string> NotificationsKeys = new() { "webhook" };
    private static readonly HashSet<string> WebhookKeys = new() { "enabled", "url", "timeout" };

    private static readonly HashSet<string> ReportingKeys = new()
        { "enabled", "api_url", "api_key", "interval", "timeout" };

    public static List<string> FindUnknownKeys(string yamlText)
    {
        var warnings = new List<string>();
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new System.IO.StringReader(yamlText));
            if (stream.Documents.Count == 0)
                return warnings;
            root = stream.Documents[0].RootNode as YamlMappingNode ?? new YamlMappingNode();
        }
        catch
        {
            return warnings; // syntax errors are reported by the loader itself
        }

        CheckMapping(root, RootKeys, "", warnings);

        if (ChildMapping(root, "watchdog") is { } watchdog)
        {
            CheckMapping(watchdog, WatchdogKeys, "watchdog", warnings);
            if (ChildMapping(watchdog, "restart_policy") is { } policy)
                CheckMapping(policy, RestartPolicyKeys, "watchdog.restart_policy", warnings);
            if (ChildMapping(watchdog, "backoff") is { } backoff)
                CheckMapping(backoff, BackoffKeys, "watchdog.backoff", warnings);
        }

        if (root.Children.TryGetValue(new YamlScalarNode("services"), out var servicesNode) &&
            servicesNode is YamlSequenceNode services)
        {
            for (var i = 0; i < services.Children.Count; i++)
            {
                if (services.Children[i] is not YamlMappingNode svc)
                    continue;
                var name = ScalarChild(svc, "name") ?? $"#{i}";
                var path = $"services['{name}']";
                CheckMapping(svc, ServiceKeys, path, warnings);

                if (svc.Children.TryGetValue(new YamlScalarNode("checks"), out var checksNode) &&
                    checksNode is YamlSequenceNode checks)
                {
                    for (var j = 0; j < checks.Children.Count; j++)
                        if (checks.Children[j] is YamlMappingNode check)
                            CheckMapping(check, CheckKeys, $"{path}.checks[{j}]", warnings);
                        // 'headers' contents are user-defined and intentionally skipped.
                }

                if (ChildMapping(svc, "restart") is { } restart)
                    CheckMapping(restart, RestartKeys, $"{path}.restart", warnings);
            }
        }

        if (ChildMapping(root, "notifications") is { } notifications)
        {
            CheckMapping(notifications, NotificationsKeys, "notifications", warnings);
            if (ChildMapping(notifications, "webhook") is { } webhook)
                CheckMapping(webhook, WebhookKeys, "notifications.webhook", warnings);
        }

        if (ChildMapping(root, "reporting") is { } reporting)
            CheckMapping(reporting, ReportingKeys, "reporting", warnings);

        return warnings;
    }

    private static void CheckMapping(YamlMappingNode node, HashSet<string> known,
        string path, List<string> warnings)
    {
        foreach (var key in node.Children.Keys.OfType<YamlScalarNode>())
        {
            var name = key.Value ?? "";
            if (!known.Contains(name))
            {
                var hint = Suggest(name, known);
                warnings.Add(string.IsNullOrEmpty(path)
                    ? $"unknown top-level key '{name}'{(hint is null ? "" : $" (did you mean '{hint}'?)")}."
                    : $"unknown key '{path}.{name}'{(hint is null ? "" : $" (did you mean '{hint}'?)")}.");
            }
        }
    }

    private static YamlMappingNode? ChildMapping(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) &&
        child is YamlMappingNode mapping ? mapping : null;

    private static string? ScalarChild(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) &&
        child is YamlScalarNode scalar ? scalar.Value : null;

    private static string? Suggest(string unknown, HashSet<string> known)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            var distance = Levenshtein(unknown, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return bestDistance <= 2 && bestDistance < Math.Max(unknown.Length, 3) ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
