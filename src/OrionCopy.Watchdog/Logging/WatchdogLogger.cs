using System.Text.RegularExpressions;

namespace OrionCopy.Watchdog.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public interface IWatchdogLogger
{
    void Debug(string service, string message, params (string Key, object? Value)[] fields);
    void Info(string service, string message, params (string Key, object? Value)[] fields);
    void Warn(string service, string message, params (string Key, object? Value)[] fields);
    void Error(string service, string message, params (string Key, object? Value)[] fields);
}

/// <summary>
/// Structured console + rotating-file logger. Every message passes through
/// secret redaction before output: values that look like keys/tokens/passwords
/// or Authorization headers are masked, and URLs are stripped of credentials.
/// </summary>
public sealed class WatchdogLogger : IWatchdogLogger, IDisposable
{
    private readonly LogLevel _minLevel;
    private StreamWriter? _file;
    private readonly string? _filePath;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _lock = new();
    private bool _disposed;

    public WatchdogLogger(string level = "info", string? logFile = null,
        long maxBytes = 10 * 1024 * 1024, int maxFiles = 5)
    {
        _minLevel = Parse(level);
        _maxBytes = maxBytes;
        _maxFiles = Math.Max(1, maxFiles);

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            _filePath = logFile;
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(logFile));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                _file = new StreamWriter(new FileStream(
                    logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
            }
            catch
            {
                _file = null; // file logging is best-effort; console still works
            }
        }
    }

    public static LogLevel Parse(string? level) =>
        level?.Trim().ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            _ => LogLevel.Info,
        };

    public void Debug(string service, string message, params (string Key, object? Value)[] fields) =>
        Write(LogLevel.Debug, service, message, fields);

    public void Info(string service, string message, params (string Key, object? Value)[] fields) =>
        Write(LogLevel.Info, service, message, fields);

    public void Warn(string service, string message, params (string Key, object? Value)[] fields) =>
        Write(LogLevel.Warn, service, message, fields);

    public void Error(string service, string message, params (string Key, object? Value)[] fields) =>
        Write(LogLevel.Error, service, message, fields);

    private void Write(LogLevel level, string service, string message,
        (string Key, object? Value)[] fields)
    {
        if (level < _minLevel)
            return;

        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {level.ToString().ToUpperInvariant(),-5} " +
                   $"{service} {message}{FormatFields(fields)}";
        line = SecretRedaction.Sanitize(line);

        lock (_lock)
        {
            if (_disposed)
                return;
            Console.WriteLine(line);
            if (_file is not null)
            {
                try
                {
                    RotateIfNeeded();
                    _file.WriteLine(line);
                }
                catch { /* logging must never crash the watchdog */ }
            }
        }
    }

    private static string FormatFields((string Key, object? Value)[] fields)
    {
        if (fields.Length == 0)
            return "";
        return " " + string.Join(" ", fields.Select(f => $"{f.Key}={f.Value}"));
    }

    private void RotateIfNeeded()
    {
        if (_filePath is null || _file is null)
            return;
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists || info.Length < _maxBytes)
                return;

            _file.Flush();
            _file.Dispose();

            var oldest = $"{_filePath}.{_maxFiles}";
            if (File.Exists(oldest))
                File.Delete(oldest);
            for (var i = _maxFiles - 1; i >= 1; i--)
            {
                var src = $"{_filePath}.{i}";
                if (File.Exists(src))
                    File.Move(src, $"{_filePath}.{i + 1}", overwrite: true);
            }
            File.Move(_filePath, $"{_filePath}.1", overwrite: true);

            // Reopen the active file: without this, every later write would hit
            // the disposed writer and file logging would silently die after the
            // first rotation.
            _file = new StreamWriter(new FileStream(
                _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
        }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _file?.Dispose(); } catch { }
        }
    }
}

/// <summary>Redacts secrets from log lines: Authorization headers, key/token/password
/// assignments, Bearer tokens, and userinfo/query credentials in URLs.</summary>
public static class SecretRedaction
{
    private static readonly Regex AuthHeader = new(
        @"(?i)(authorization\s*[:=]\s*)([^\s""]+)", RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(api[_-]?key|apikey|token|secret|password|passwd|pwd|client[_-]?secret|access[_-]?key)\b\s*[:=]\s*([^\s,;}""]+)",
        RegexOptions.Compiled);
    private static readonly Regex Bearer = new(
        @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/=]+", RegexOptions.Compiled);
    private static readonly Regex UrlCredentials = new(
        @"(?i)(https?://)[^/\s:@]+(:[^/\s:@]+)?@", RegexOptions.Compiled);
    private static readonly Regex QuerySecret = new(
        @"(?i)([?&](?:key|token|secret|password|api[_-]?key)=)[^&\s]*", RegexOptions.Compiled);

    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? "";
        // Bearer tokens first: an "Authorization: Bearer <token>" line must not
        // leave the token behind when the header name is masked afterwards.
        var s = Bearer.Replace(input, "Bearer [REDACTED]");
        s = AuthHeader.Replace(s, "$1[REDACTED]");
        s = SecretAssignment.Replace(s, "$1=[REDACTED]");
        s = UrlCredentials.Replace(s, "$1[REDACTED]@");
        s = QuerySecret.Replace(s, "$1[REDACTED]");
        return s;
    }

    /// <summary>Render a URL safe for logs: no userinfo, no query string.</summary>
    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(unset)";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.GetComponents(
                UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path,
                UriFormat.Unescaped);
        return "[REDACTED]";
    }
}

/// <summary>No-op logger for tests.</summary>
public sealed class NullLogger : IWatchdogLogger
{
    public static readonly NullLogger Instance = new();
    public void Debug(string service, string message, params (string Key, object? Value)[] fields) { }
    public void Info(string service, string message, params (string Key, object? Value)[] fields) { }
    public void Warn(string service, string message, params (string Key, object? Value)[] fields) { }
    public void Error(string service, string message, params (string Key, object? Value)[] fields) { }
}
