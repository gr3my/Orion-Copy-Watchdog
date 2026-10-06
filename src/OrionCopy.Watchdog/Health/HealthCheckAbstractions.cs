using OrionCopy.Watchdog.Config;

namespace OrionCopy.Watchdog.Health;

public sealed record CheckResult(
    bool Healthy,
    string CheckType,
    string Detail,
    TimeSpan Elapsed)
{
    public static CheckResult Pass(string type, string detail, TimeSpan elapsed) =>
        new(true, type, detail, elapsed);

    public static CheckResult Fail(string type, string detail, TimeSpan elapsed) =>
        new(false, type, detail, elapsed);
}

/// <summary>A single health probe. Implementations must be safe to run concurrently,
/// must honor cancellation, and must never throw on an unhealthy target
/// (return a failed result instead). Unexpected exceptions are caught by the caller.</summary>
public interface IHealthCheck
{
    string Kind { get; }
    Task<CheckResult> CheckAsync(CheckConfig config, CancellationToken ct);
}
