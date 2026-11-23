using System.Text;
using System.Text.RegularExpressions;

namespace ProxyDock.Core.Services;

/// <summary>
/// Фильтрует пароли, токены и секретные значения перед записью в логи и историю запусков.
/// </summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"(https?://)([^:\s/@]+):([^@\s/]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex(@"(--?(?:password|passwd|pwd|token|api[-_]?key|secret|auth|bearer)\s*[= ]\s*)(""[^""]*""|'[^']*'|[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretFlagRegex();

    [GeneratedRegex(@"(Bearer\s+)([A-Za-z0-9\-._~+/]+=*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    private static readonly string[] SensitiveKeyMarkers =
    [
        "PASSWORD",
        "PASSWD",
        "SECRET",
        "TOKEN",
        "API_KEY",
        "APIKEY",
        "AUTH",
        "CREDENTIAL",
        "PRIVATE_KEY"
    ];

    public static bool IsSensitiveKeyName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var upper = key.Trim().ToUpperInvariant();
        foreach (var marker in SensitiveKeyMarkers)
        {
            if (upper.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string Redact(string? text, IEnumerable<string>? additionalSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = UriUserInfoRegex().Replace(text, "$1***:***@");
        result = SecretFlagRegex().Replace(result, "$1***");
        result = BearerTokenRegex().Replace(result, "$1***");

        if (additionalSecrets is not null)
        {
            foreach (var secret in additionalSecrets)
            {
                if (!string.IsNullOrEmpty(secret))
                {
                    result = result.Replace(secret, "***", StringComparison.Ordinal);
                }
            }
        }

        return result;
    }

    public static string FormatRedactedArguments(IEnumerable<string> arguments, IEnumerable<string>? additionalSecrets = null)
    {
        var sb = new StringBuilder();
        foreach (var arg in arguments)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            var safe = Redact(arg, additionalSecrets);
            if (safe.Contains(' ', StringComparison.Ordinal) || safe.Contains('\t', StringComparison.Ordinal))
            {
                sb.Append('"').Append(safe.Replace("\"", "\\\"")).Append('"');
            }
            else
            {
                sb.Append(safe);
            }
        }

        return sb.ToString();
    }
}
