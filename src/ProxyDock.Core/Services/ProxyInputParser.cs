using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ProxyDock.Core.Services;

public sealed record ParsedProxyInput(
    string Host,
    int Port,
    string? Username = null,
    string? Password = null);

/// <summary>
/// Разбирает строку прокси в форматах IP:PORT, [IPv6]:PORT, HOST:PORT:USER:PASS,
/// USER:PASS@HOST:PORT и HTTP/HTTPS URL (http://user:pass@host:port).
/// </summary>
public static class ProxyInputParser
{
    public static bool TryParse(string? rawInput, out ParsedProxyInput? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return false;
        }

        var trimmed = rawInput.Trim();

        // Голый IPv6 без скобок и порта (например, ::1 или 2001:db8::1) — это просто хост, а не host:port
        if (!trimmed.StartsWith('[')
            && !trimmed.Contains("://", StringComparison.Ordinal)
            && !trimmed.Contains('@')
            && IPAddress.TryParse(trimmed, out var bareIp)
            && bareIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return false;
        }

        // 1. Формат URL со схемой http:// или https://
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseUri(trimmed, requireExplicitPort: false, out parsed);
        }

        // Отклоняем неподдерживаемые схемы (socks5:// и т.п.), чтобы не заполнить мусор
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        // 2. Формат user:pass@host:port (без схемы)
        if (trimmed.Contains('@'))
        {
            return TryParseUri("http://" + trimmed, requireExplicitPort: true, out parsed);
        }

        // 3. Формат [IPv6]:port или [IPv6]:port:user:pass
        if (trimmed.StartsWith('['))
        {
            var closeBracket = trimmed.IndexOf(']');
            if (closeBracket <= 1 || closeBracket + 2 > trimmed.Length || trimmed[closeBracket + 1] != ':')
            {
                return false;
            }

            var v6Host = trimmed[1..closeBracket];
            if (!IPAddress.TryParse(v6Host, out var v6Addr) || v6Addr.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            var rest = trimmed[(closeBracket + 2)..].TrimEnd('/');
            return TryParsePortAndOptionalAuth(v6Host, rest, out parsed);
        }

        // 4. Формат host:port или host:port:user:pass
        var firstColon = trimmed.IndexOf(':');
        if (firstColon <= 0 || firstColon + 1 >= trimmed.Length)
        {
            return false;
        }

        var hostPart = trimmed[..firstColon].Trim();
        var tail = trimmed[(firstColon + 1)..].TrimEnd('/');
        if (!IsPlausibleHost(hostPart))
        {
            return false;
        }

        return TryParsePortAndOptionalAuth(hostPart, tail, out parsed);
    }

    private static bool TryParseUri(string uriString, bool requireExplicitPort, out ParsedProxyInput? parsed)
    {
        parsed = null;
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (requireExplicitPort && uri.IsDefaultPort && !uriString.Contains($":{uri.Port}", StringComparison.Ordinal))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host) || uri.Port is < 1 or > 65535)
        {
            return false;
        }

        string? username = null;
        string? password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var colonIdx = uri.UserInfo.IndexOf(':');
            if (colonIdx >= 0)
            {
                username = Uri.UnescapeDataString(uri.UserInfo[..colonIdx]);
                password = Uri.UnescapeDataString(uri.UserInfo[(colonIdx + 1)..]);
            }
            else
            {
                username = Uri.UnescapeDataString(uri.UserInfo);
            }
        }

        parsed = new ParsedProxyInput(host, uri.Port, username, password);
        return true;
    }

    private static bool TryParsePortAndOptionalAuth(string host, string rest, out ParsedProxyInput? parsed)
    {
        parsed = null;
        var parts = rest.Split(':', 3);
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        if (parts.Length == 1)
        {
            parsed = new ParsedProxyInput(host, port);
            return true;
        }

        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            parsed = new ParsedProxyInput(host, port, parts[1], parts[2]);
            return true;
        }

        return false;
    }

    private static bool IsPlausibleHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Contains('/'))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }
}
