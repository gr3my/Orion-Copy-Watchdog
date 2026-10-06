using System.Text;
using System.Text.Json;
using OrionCopy.Watchdog.Logging;

namespace OrionCopy.Watchdog.Reporting;

public sealed record ServiceStatusReport(
    string Name,
    string State,
    int Restarts,
    DateTime? LastCheck,
    DateTime? LastRecovery);

public sealed record WatchdogStatusReport(
    string Watchdog,
    DateTime Timestamp,
    List<ServiceStatusReport> Services,
    List<string> FailedServices);

/// <summary>
/// Abstraction for optional Orion Copy Server reporting. The watchdog works
/// perfectly without it; enable it only if your backend implements the endpoint.
/// </summary>
public interface IStatusReporter
{
    Task ReportAsync(WatchdogStatusReport report, CancellationToken ct);
}

/// <summary>
/// Expected server-side contract (implement this on your backend):
/// <code>
/// POST {api_url}/api/watchdog/status
/// Authorization: Bearer {api_key}
/// Content-Type: application/json
///
/// {
///   "watchdog": "hostname-or-id",
///   "timestamp": "2026-10-06T12:00:00Z",
///   "services": [
///     { "name": "Orion Backend", "state": "Healthy", "restarts": 1,
///       "last_check": "...", "last_recovery": "..." }
///   ],
///   "failed_services": ["Arena #2"]
/// }
/// </code>
/// The server should answer 2xx on acceptance. Any 4xx/5xx or network error is
/// logged (without the key) and retried on the next interval.
/// </summary>
public sealed class HttpStatusReporter : IStatusReporter
{
    private static readonly HttpClient Client = new();
    private readonly string _apiUrl;
    private readonly string? _apiKey;
    private readonly TimeSpan _timeout;
    private readonly IWatchdogLogger _log;

    public HttpStatusReporter(string apiUrl, string? apiKey, TimeSpan timeout, IWatchdogLogger log)
    {
        _apiUrl = apiUrl.TrimEnd('/');
        _apiKey = apiKey;
        _timeout = timeout;
        _log = log;
    }

    public async Task ReportAsync(WatchdogStatusReport report, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/api/watchdog/status")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            using var cts = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var response = await Client.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _log.Warn("watchdog", "status report rejected",
                    ("status", (int)response.StatusCode), ("server", SecretRedaction.SafeUrl(_apiUrl)));
        }
        catch (Exception ex)
        {
            _log.Warn("watchdog", "status report failed",
                ("error", ex.Message), ("server", SecretRedaction.SafeUrl(_apiUrl)));
        }
    }
}

public sealed class NoOpStatusReporter : IStatusReporter
{
    public static readonly NoOpStatusReporter Instance = new();
    public Task ReportAsync(WatchdogStatusReport report, CancellationToken ct) => Task.CompletedTask;
}
