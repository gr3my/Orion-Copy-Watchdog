namespace OrionCopy.Watchdog.Health;

/// <summary>Clear, operator-friendly service states (never just online/offline).</summary>
public enum HealthState
{
    Starting,
    Healthy,
    Degraded,
    Unhealthy,
    Recovering,
    Backoff,
    Stopped,
    Disabled,
    Failed
}

public static class HealthStateExtensions
{
    public static string ToDisplay(this HealthState state) => state switch
    {
        HealthState.Starting => "STARTING",
        HealthState.Healthy => "HEALTHY",
        HealthState.Degraded => "DEGRADED",
        HealthState.Unhealthy => "UNHEALTHY",
        HealthState.Recovering => "RECOVERING",
        HealthState.Backoff => "BACKOFF",
        HealthState.Stopped => "STOPPED",
        HealthState.Disabled => "DISABLED",
        HealthState.Failed => "FAILED",
        _ => state.ToString().ToUpperInvariant(),
    };
}
