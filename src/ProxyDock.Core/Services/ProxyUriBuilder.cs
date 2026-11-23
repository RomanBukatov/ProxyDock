using System.Net;
using System.Net.Sockets;
using ProxyDock.Core.Models;

namespace ProxyDock.Core.Services;

/// <summary>
/// Формирует безопасные для отображения и runtime HTTP-адреса прокси.
/// </summary>
public static class ProxyUriBuilder
{
    public static string FormatHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            return trimmed;
        }

        if (IPAddress.TryParse(trimmed, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{trimmed}]";
        }

        return trimmed;
    }

    public static string BuildDisplayUri(ProxyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var host = FormatHost(profile.Host);
        return $"http://{host}:{profile.Port}";
    }

    public static string BuildMaskedUri(ProxyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var host = FormatHost(profile.Host);
        if (profile.HasCredentials || !string.IsNullOrWhiteSpace(profile.Username))
        {
            return $"http://***@{host}:{profile.Port}";
        }

        return $"http://{host}:{profile.Port}";
    }

    public static string BuildRuntimeUri(ProxyProfile profile, ProxyCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var host = FormatHost(profile.Host);

        if (credentials is not null && !string.IsNullOrEmpty(credentials.Username))
        {
            var user = Uri.EscapeDataString(credentials.Username);
            var pass = Uri.EscapeDataString(credentials.Password);
            return $"http://{user}:{pass}@{host}:{profile.Port}";
        }

        return $"http://{host}:{profile.Port}";
    }
}
