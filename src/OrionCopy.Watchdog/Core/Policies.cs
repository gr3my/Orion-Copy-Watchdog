namespace OrionCopy.Watchdog.Core;

/// <summary>Exponential backoff for restart delays: initial, initial*multiplier, ... capped at max.</summary>
public static class BackoffPolicy
{
    public static TimeSpan ComputeDelay(int attempt, TimeSpan initial, double multiplier, TimeSpan max)
    {
        if (attempt < 1)
            attempt = 1;
        var delay = initial.TotalMilliseconds * Math.Pow(multiplier, attempt - 1);
        if (double.IsNaN(delay) || double.IsInfinity(delay) || delay < 0)
            delay = max.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(delay, max.TotalMilliseconds));
    }
}

/// <summary>Sliding-window restart counter: the crash-loop circuit breaker
/// Sliding-window restart counter: the crash-loop circuit breaker
/// (max restarts inside a restart window, then retire for a human).</summary>
public sealed class RestartWindowTracker
{
    public static bool SlideWindow(DateTime now, DateTime windowStart, TimeSpan window,
        int restartsInWindow, out DateTime newWindowStart, out int newCount)
    {
        if (now - windowStart > window)
        {
            newWindowStart = now;
            newCount = 0;
            return true;
        }
        newWindowStart = windowStart;
        newCount = restartsInWindow;
        return false;
    }
}

/// <summary>Dependency helpers: topological start order + health gating.</summary>
public static class DependencyGraph
{
    /// <summary>Order services so dependencies come first (for startup sequencing).</summary>
    public static List<string> TopologicalOrder(Config.ServiceConfig[] services)
    {
        var byName = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Visit(string name)
        {
            if (!visited.Add(name))
                return;
            if (byName.TryGetValue(name, out var svc))
                foreach (var dep in svc.DependsOn)
                    if (byName.ContainsKey(dep))
                        Visit(dep);
            order.Add(name);
        }

        foreach (var svc in services)
            Visit(svc.Name);
        return order;
    }

    /// <summary>
    /// A service may only restart when every dependency is Healthy. If the backend
    /// is down, restarting the arena server against it is pointless and risks a
    /// restart storm - hold instead (the "never act on a guess" principle).
    /// </summary>
    public static bool DependenciesHealthy(
        Config.ServiceConfig service,
        Func<string, Health.HealthState?> getState,
        out string? blocker)
    {
        foreach (var dep in service.DependsOn)
        {
            var state = getState(dep);
            if (state != Health.HealthState.Healthy)
            {
                blocker = $"'{dep}' is {(state?.ToString().ToUpperInvariant() ?? "UNKNOWN")}";
                return false;
            }
        }
        blocker = null;
        return true;
    }
}
