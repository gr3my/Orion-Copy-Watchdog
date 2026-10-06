using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Logging;

namespace OrionCopy.Watchdog.Restart;

/// <summary>Outcome of one stop/start/restart step. Never throws for target failures.</summary>
public sealed record RestartResult(bool Success, string Detail)
{
    public static RestartResult Ok(string detail) => new(true, detail);
    public static RestartResult Fail(string detail) => new(false, detail);
}

/// <summary>Context handed to every restart strategy.</summary>
public sealed record RestartContext(
    ServiceConfig Service,
    TimeSpan GracefulTimeout,
    IWatchdogLogger Log);

/// <summary>
/// Graceful-first recovery primitive. <see cref="StopAsync"/> should attempt a
/// graceful stop and force-terminate only after the graceful timeout expires.
/// Strategies must be safe to call concurrently for different services; the
/// caller serializes calls per service.
/// </summary>
public interface IRestartStrategy
{
    string Kind { get; }
    Task<RestartResult> StopAsync(RestartContext ctx, CancellationToken ct);
    Task<RestartResult> StartAsync(RestartContext ctx, CancellationToken ct);

    virtual async Task<RestartResult> RestartAsync(RestartContext ctx, CancellationToken ct)
    {
        var stop = await StopAsync(ctx, ct).ConfigureAwait(false);
        if (!stop.Success)
            return RestartResult.Fail($"graceful stop failed, aborting restart: {stop.Detail}");
        return await StartAsync(ctx, ct).ConfigureAwait(false);
    }
}
