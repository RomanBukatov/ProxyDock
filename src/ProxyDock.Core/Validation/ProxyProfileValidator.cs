using System.Net;
using System.Text.RegularExpressions;
using ProxyDock.Core.Models;

namespace ProxyDock.Core.Validation;

public static partial class ProxyProfileValidator
{
    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostnameRegex();

    public static ValidationResult Validate(ProxyProfile profile, string? newPassword = null, bool clearPassword = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            result.AddError(nameof(profile.Name), "Укажите название профиля прокси.");
        }

        var host = profile.Host?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            result.AddError(nameof(profile.Host), "Укажите адрес (Host) прокси-сервера.");
        }
        else if (host.Contains("://", StringComparison.Ordinal))
        {
            result.AddError(nameof(profile.Host), "Укажите только имя хоста или IP без протокола http://.");
        }
        else if (host.Contains('@', StringComparison.Ordinal) || host.Any(char.IsWhiteSpace))
        {
            result.AddError(nameof(profile.Host), "Host не должен содержать пробелы или учётные данные.");
        }
        else if (!IsValidHost(host))
        {
            result.AddError(nameof(profile.Host), "Некорректный формат хоста или IP-адреса.");
        }

        if (profile.Port is < 1 or > 65535)
        {
            result.AddError(nameof(profile.Port), "Порт должен быть в диапазоне от 1 до 65535.");
        }

        if (profile.CheckTimeoutSeconds is < 1 or > 120)
        {
            result.AddError(nameof(profile.CheckTimeoutSeconds), "Таймаут проверки должен быть от 1 до 120 секунд.");
        }

        if (string.IsNullOrWhiteSpace(profile.DiagnosticEndpointUrl)
            || !Uri.TryCreate(profile.DiagnosticEndpointUrl.Trim(), UriKind.Absolute, out var diagUri)
            || (diagUri.Scheme != Uri.UriSchemeHttp && diagUri.Scheme != Uri.UriSchemeHttps))
        {
            result.AddError(nameof(profile.DiagnosticEndpointUrl), "Укажите корректный HTTP/HTTPS URL для проверки внешнего IP.");
        }

        var hasPasswordPending = !string.IsNullOrEmpty(newPassword) || (profile.HasCredentials && !clearPassword);
        if (hasPasswordPending && string.IsNullOrWhiteSpace(profile.Username))
        {
            result.AddError(nameof(profile.Username), "При использовании пароля необходимо указать имя пользователя (Username).");
        }

        return result;
    }

    private static bool IsValidHost(string host)
    {
        var unbracketed = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (IPAddress.TryParse(unbracketed, out _))
        {
            return true;
        }

        return HostnameRegex().IsMatch(host);
    }
}
