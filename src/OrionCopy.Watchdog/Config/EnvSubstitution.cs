using System.Text.RegularExpressions;

namespace OrionCopy.Watchdog.Config;

/// <summary>
/// Simple, safe environment-variable substitution for config files.
/// Supports ${VAR} and ${VAR:-default}. No expression evaluation.
/// Unset variables without a default are left in place and reported by validation.
/// </summary>
public static class EnvSubstitution
{
    private static readonly Regex Token = new(
        @"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-(.*?))?\}",
        RegexOptions.Compiled);

    public sealed record Result(string Text, List<string> MissingVariables);

    public static Result Apply(string text, Func<string, string?> lookup)
    {
        var missing = new List<string>();
        // Substitute line-by-line so ${...} inside full-line YAML comments
        // (documentation, examples) is left alone and never reported missing.
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('#'))
                continue;
            lines[i] = Token.Replace(lines[i], m =>
            {
                var name = m.Groups[1].Value;
                var hasDefault = m.Groups[2].Success;
                var fallback = hasDefault ? m.Groups[2].Value : null;

                var value = lookup(name);
                if (value is null)
                {
                    if (hasDefault)
                        return fallback!;
                    if (!missing.Contains(name))
                        missing.Add(name);
                    return m.Value; // leave placeholder so validation can flag it
                }
                return value;
            });
        }
        return new Result(string.Join("\n", lines), missing);
    }

    public static Result ApplyEnvironment(string text) =>
        Apply(text, Environment.GetEnvironmentVariable);
}
