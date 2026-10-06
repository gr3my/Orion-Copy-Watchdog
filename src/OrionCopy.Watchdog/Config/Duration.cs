using System.Text.RegularExpressions;

namespace OrionCopy.Watchdog.Config;

/// <summary>
/// Parses Go-style durations: "500ms", "10s", "5m", "2h", "1d".
/// A bare number means seconds. Throws <see cref="FormatException"/> on invalid input.
/// </summary>
public static class Duration
{
    private static readonly Regex Pattern = new(
        @"^\s*(\d+(?:\.\d+)?)\s*(ms|s|m|h|d)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TimeSpan Parse(string? value, string fieldName = "duration")
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"{fieldName}: empty duration (examples: 500ms, 10s, 5m, 2h, 1d).");

        var m = Pattern.Match(value);
        if (!m.Success)
            throw new FormatException(
                $"{fieldName}: invalid duration '{value}' (examples: 500ms, 10s, 5m, 2h, 1d).");

        var amount = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Value.ToLowerInvariant();

        try
        {
            return unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => TimeSpan.FromSeconds(amount), // "s" or bare number
            };
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException)
        {
            throw new FormatException($"{fieldName}: duration '{value}' is out of range.");
        }
    }

    public static bool TryParse(string? value, out TimeSpan result)
    {
        try
        {
            result = Parse(value);
            return true;
        }
        catch
        {
            result = TimeSpan.Zero;
            return false;
        }
    }
}
