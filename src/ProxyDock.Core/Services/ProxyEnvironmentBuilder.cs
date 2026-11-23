using ProxyDock.Core.Models;

namespace ProxyDock.Core.Services;

public sealed record EnvironmentDelta(
    Dictionary<string, string> Set,
    List<string> Remove,
    IReadOnlyList<string> SensitiveValues);

/// <summary>
/// Строит изолированный набор переменных окружения только для запускаемого дочернего процесса.
/// </summary>
public static class ProxyEnvironmentBuilder
{
    public static readonly string[] ProxyVariableNames =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy"
    ];

    public static readonly string[] NoProxyVariableNames =
    [
        "NO_PROXY",
        "no_proxy"
    ];

    public static EnvironmentDelta Build(
        ProxyProfile? proxy,
        ProxyCredentials? credentials,
        string? noProxyOverride,
        IEnumerable<EnvironmentVariableEntry>? customVariables,
        bool isDirectNoProxy)
    {
        var set = new Dictionary<string, string>(StringComparer.Ordinal);
        var remove = new List<string>();
        var sensitive = new List<string>();

        if (isDirectNoProxy || proxy is null)
        {
            remove.AddRange(ProxyVariableNames);
            if (!string.IsNullOrWhiteSpace(noProxyOverride))
            {
                var trimmedNoProxy = noProxyOverride.Trim();
                set["NO_PROXY"] = trimmedNoProxy;
                set["no_proxy"] = trimmedNoProxy;
            }
        }
        else
        {
            var runtimeUri = ProxyUriBuilder.BuildRuntimeUri(proxy, credentials);
            foreach (var varName in ProxyVariableNames)
            {
                set[varName] = runtimeUri;
            }

            if (credentials is not null && !string.IsNullOrEmpty(credentials.Password))
            {
                sensitive.Add(credentials.Password);
                sensitive.Add(runtimeUri);
            }

            var effectiveNoProxy = !string.IsNullOrWhiteSpace(noProxyOverride)
                ? noProxyOverride.Trim()
                : proxy.NoProxy?.Trim();

            if (!string.IsNullOrWhiteSpace(effectiveNoProxy))
            {
                set["NO_PROXY"] = effectiveNoProxy;
                set["no_proxy"] = effectiveNoProxy;
            }
        }

        if (customVariables is not null)
        {
            foreach (var entry in customVariables)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                var key = entry.Key.Trim();
                if (key.Contains('=') || key.Contains('\0'))
                {
                    continue;
                }

                var value = entry.Value ?? string.Empty;
                set[key] = value;

                // Если пользователь явно переопределил прокси-переменную вручную, удаляем её из списка на удаление.
                remove.RemoveAll(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase));

                if ((entry.IsSecret || SecretRedactor.IsSensitiveKeyName(key)) && !string.IsNullOrEmpty(value))
                {
                    sensitive.Add(value);
                }
            }
        }

        return new EnvironmentDelta(set, remove, sensitive);
    }
}
