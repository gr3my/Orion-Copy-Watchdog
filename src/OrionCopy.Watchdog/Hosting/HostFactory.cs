using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrionCopy.Watchdog.Config;
using OrionCopy.Watchdog.Core;
using OrionCopy.Watchdog.Logging;

namespace OrionCopy.Watchdog.Hosting;

/// <summary>Generic-Host wrapper so the watchdog runs as a console app,
/// a Windows Service, or a systemd unit without code changes.</summary>
public sealed class WatchdogHostedService : BackgroundService
{
    private readonly WatchdogEngine _engine;

    public WatchdogHostedService(WatchdogEngine engine) => _engine = engine;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _engine.RunAsync(stoppingToken);
}

public static class HostFactory
{
    public static async Task<int> RunDaemonAsync(WatchdogConfig config, string configPath, WatchdogLogger log)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(log);
                services.AddSingleton(config);
                services.AddSingleton(new DaemonOptions(configPath));
                services.AddSingleton<WatchdogEngine>(sp =>
                    new WatchdogEngine(
                        sp.GetRequiredService<WatchdogConfig>(),
                        sp.GetRequiredService<DaemonOptions>().ConfigPath,
                        sp.GetRequiredService<WatchdogLogger>()));
                services.AddHostedService<WatchdogHostedService>();
            });

        try
        {
            if (OperatingSystem.IsWindows())
                builder.UseWindowsService(options => options.ServiceName = "OrionCopyWatchdog");
            else
                builder.UseSystemd();
        }
        catch (Exception ex)
        {
            log.Warn("watchdog", $"service integration not applied, running as console: {ex.Message}");
        }

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        log.Dispose();
        return 0;
    }

    public sealed record DaemonOptions(string ConfigPath);
}
