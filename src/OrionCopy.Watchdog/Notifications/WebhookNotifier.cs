using System.Text;
using System.Text.Json;
using OrionCopy.Watchdog.Logging;

namespace OrionCopy.Watchdog.Notifications;

/// <summary>Watchdog lifecycle events that can trigger notifications.</summary>
public enum WatchdogEvent
{
    ServiceDown,
    RestartStarted,
    ServiceRecovered,
    RestartFailed,
    RestartLimitReached,
    WatchdogStarted,
}

public sealed record WatchdogNotification(
    string Event,
    string Service,
    string State,
    DateTime Timestamp,
    string Message);

/// <summary>
/// Generic webhook notifier. Posts plain JSON to any receiver — no
/// Discord/Slack-specific logic in the core. The URL is never logged with
/// credentials (see <see cref="SecretRedaction"/>).
/// </summary>
public sealed class WebhookNotifier
{
    private static readonly HttpClient Client = new();
    private readonly bool _enabled;
    private readonly string _url;
    private readonly TimeSpan _timeout;
    private readonly IWatchdogLogger _log;

    public WebhookNotifier(bool enabled, string? url, TimeSpan timeout, IWatchdogLogger log)
    {
        _enabled = enabled;
        _url = url ?? "";
        _timeout = timeout;
        _log = log;
    }

    public bool Enabled => _enabled && !string.IsNullOrWhiteSpace(_url);

    public async Task SendAsync(WatchdogEvent @event, string service, string state,
        string message, CancellationToken ct = default)
    {
        if (!Enabled)
            return;

        var payload = new WatchdogNotification(
            @event.ToString().ToLowerInvariant() switch
            {
                "servicedown" => "service_down",
                "restartstarted" => "restart_started",
                "servicerecovered" => "service_recovered",
                "restartfailed" => "restart_failed",
                "restartlimitreached" => "restart_limit_reached",
                "watchdogstarted" => "watchdog_started",
                _ => @event.ToString(),
            },
            service, state, DateTime.UtcNow, message);

        try
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var response = await Client.PostAsync(_url, content, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _log.Warn("watchdog", "webhook notification failed",
                    ("event", payload.Event), ("status", (int)response.StatusCode),
                    ("url", SecretRedaction.SafeUrl(_url)));
        }
        catch (Exception ex)
        {
            _log.Warn("watchdog", "webhook notification failed",
                ("event", payload.Event), ("error", ex.Message),
                ("url", SecretRedaction.SafeUrl(_url)));
        }
    }
}
