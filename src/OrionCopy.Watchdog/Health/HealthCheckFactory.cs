namespace OrionCopy.Watchdog.Health;

public static class HealthCheckFactory
{
    private static readonly Dictionary<string, IHealthCheck> Checks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["http"] = new HttpHealthCheck(),
            ["tcp"] = new TcpHealthCheck(),
            ["process"] = new ProcessHealthCheck(),
            ["windows_service"] = new WindowsServiceHealthCheck(),
            ["systemd"] = new SystemdHealthCheck(),
            ["command"] = new CommandHealthCheck(),
        };

    public static IHealthCheck? Get(string type) =>
        Checks.TryGetValue(type ?? "", out var check) ? check : null;

    public static IReadOnlyCollection<string> Supported => Checks.Keys;
}
