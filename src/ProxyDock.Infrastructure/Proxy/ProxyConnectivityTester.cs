using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Core.Validation;

namespace ProxyDock.Infrastructure.Proxy;

/// <summary>
/// Асинхронно проверяет TCP-доступность прокси и прохождение HTTPS-запроса через явно сконфигурированный HttpClient,
/// не используя и не изменяя системный прокси Windows.
/// </summary>
public sealed class ProxyConnectivityTester : IProxyConnectivityTester
{
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<ProxyConnectivityTester> _logger;

    public ProxyConnectivityTester(
        ICredentialStore credentialStore,
        ILogger<ProxyConnectivityTester> logger)
    {
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task<ProxyTestResult> TestAsync(ProxyProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = ProxyProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            return new ProxyTestResult
            {
                Status = ProxyCheckStatus.Invalid,
                Message = validation.Errors[0].Message,
                SuggestedAction = "Исправьте параметры прокси и повторите проверку.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(profile.CheckTimeoutSeconds, 1, 120));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        long? tcpLatencyMs = null;
        var hostForTcp = profile.Host.Trim().Trim('[', ']');

        // Этап 1: TCP-подключение к узлу прокси
        try
        {
            var swTcp = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(hostForTcp, profile.Port, timeoutCts.Token).ConfigureAwait(false);
            swTcp.Stop();
            tcpLatencyMs = swTcp.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProxyTestResult
            {
                Status = ProxyCheckStatus.Unreachable,
                TcpSucceeded = false,
                Message = $"Превышен таймаут TCP-подключения ({profile.CheckTimeoutSeconds} сек) к {profile.Host}:{profile.Port}.",
                SuggestedAction = "Проверьте адрес, порт, запущен ли прокси-сервер и не блокируется ли соединение фаерволом.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TCP-проверка прокси {ProxyName} ({Host}:{Port}) завершилась ошибкой.", profile.Name, profile.Host, profile.Port);
            return new ProxyTestResult
            {
                Status = ProxyCheckStatus.Unreachable,
                TcpSucceeded = false,
                Message = $"Не удалось установить TCP-соединение с {profile.Host}:{profile.Port}: {ex.Message}",
                SuggestedAction = "Убедитесь, что прокси запущен и слушает указанный порт.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }

        // Этап 2: Явный HTTP/HTTPS-запрос через настроенный WebProxy (без системных настроек Windows)
        try
        {
            ProxyCredentials? credentials = null;
            if (profile.HasCredentials || !string.IsNullOrWhiteSpace(profile.Username))
            {
                credentials = await _credentialStore.LoadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                if (credentials is null && !string.IsNullOrWhiteSpace(profile.Username))
                {
                    credentials = new ProxyCredentials(profile.Username, string.Empty);
                }
            }

            var proxyUri = new Uri(ProxyUriBuilder.BuildDisplayUri(profile));
            var webProxy = new WebProxy(proxyUri)
            {
                BypassProxyOnLocal = false,
                UseDefaultCredentials = false,
                Credentials = credentials is not null
                    ? new NetworkCredential(credentials.Username, credentials.Password)
                    : null
            };

            using var handler = new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = webProxy,
                DefaultProxyCredentials = webProxy.Credentials,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectTimeout = timeout,
                AllowAutoRedirect = true
            };

            using var client = new HttpClient(handler)
            {
                Timeout = timeout
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyDock/1.0");

            var swHttps = Stopwatch.StartNew();
            using var response = await client.GetAsync(profile.DiagnosticEndpointUrl.Trim(), HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            swHttps.Stop();

            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                return new ProxyTestResult
                {
                    Status = ProxyCheckStatus.AuthRequired,
                    TcpSucceeded = true,
                    HttpsSucceeded = false,
                    TcpLatencyMs = tcpLatencyMs,
                    HttpStatusCode = (int)response.StatusCode,
                    Message = "Прокси принял TCP-подключение, но отклонил запрос с кодом 407 (требуется авторизация).",
                    SuggestedAction = "Проверьте логин и пароль в настройках профиля прокси.",
                    CheckedAtUtc = DateTimeOffset.UtcNow
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProxyTestResult
                {
                    Status = ProxyCheckStatus.TcpOnly,
                    TcpSucceeded = true,
                    HttpsSucceeded = false,
                    TcpLatencyMs = tcpLatencyMs,
                    HttpsLatencyMs = swHttps.ElapsedMilliseconds,
                    HttpStatusCode = (int)response.StatusCode,
                    Message = $"TCP доступен ({tcpLatencyMs} мс), но диагностический запрос вернул HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    SuggestedAction = "Проверьте доступность диагностического URL и правила доступа на стороне прокси.",
                    CheckedAtUtc = DateTimeOffset.UtcNow
                };
            }

            var externalIp = TryExtractIpAddress(body);
            var ipNote = string.IsNullOrWhiteSpace(externalIp) ? string.Empty : $" Внешний IP: {externalIp}.";
            return new ProxyTestResult
            {
                Status = ProxyCheckStatus.Reachable,
                TcpSucceeded = true,
                HttpsSucceeded = true,
                TcpLatencyMs = tcpLatencyMs,
                HttpsLatencyMs = swHttps.ElapsedMilliseconds,
                ExternalIp = externalIp,
                HttpStatusCode = (int)response.StatusCode,
                Message = $"HTTPS через прокси успешен ({swHttps.ElapsedMilliseconds} мс, TCP {tcpLatencyMs} мс).{ipNote} Это подтверждает доступность диагностического endpoint, но не гарантирует доступ ко всем внешним API.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProxyTestResult
            {
                Status = ProxyCheckStatus.TcpOnly,
                TcpSucceeded = true,
                HttpsSucceeded = false,
                TcpLatencyMs = tcpLatencyMs,
                Message = $"TCP-соединение установлено ({tcpLatencyMs} мс), но HTTPS-запрос через прокси превысил таймаут ({profile.CheckTimeoutSeconds} сек).",
                SuggestedAction = "Возможно, прокси не поддерживает HTTPS CONNECT или блокирует выход во внешнюю сеть.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired
                || ex.Message.Contains("407", StringComparison.Ordinal))
            {
                return new ProxyTestResult
                {
                    Status = ProxyCheckStatus.AuthRequired,
                    TcpSucceeded = true,
                    HttpsSucceeded = false,
                    TcpLatencyMs = tcpLatencyMs,
                    HttpStatusCode = 407,
                    Message = $"TCP доступен ({tcpLatencyMs} мс), но прокси запросил авторизацию (HTTP 407): {ex.Message}",
                    SuggestedAction = "Укажите корректные логин и пароль в профиле прокси.",
                    CheckedAtUtc = DateTimeOffset.UtcNow
                };
            }

            _logger.LogWarning(ex, "HTTPS-проверка прокси {ProxyName} завершилась ошибкой.", profile.Name);
            return new ProxyTestResult
            {
                Status = ProxyCheckStatus.TcpOnly,
                TcpSucceeded = true,
                HttpsSucceeded = false,
                TcpLatencyMs = tcpLatencyMs,
                Message = $"TCP доступен ({tcpLatencyMs} мс), но HTTPS-туннель через прокси не удался: {ex.Message}",
                SuggestedAction = "Убедитесь, что это HTTP/HTTPS-прокси (с поддержкой CONNECT), а не SOCKS-прокси или обычный веб-сервер.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Неожиданная ошибка при проверке прокси {ProxyName}.", profile.Name);
            return new ProxyTestResult
            {
                Status = ProxyCheckStatus.TcpOnly,
                TcpSucceeded = true,
                HttpsSucceeded = false,
                TcpLatencyMs = tcpLatencyMs,
                Message = $"TCP доступен ({tcpLatencyMs} мс), но проверка HTTPS завершилась ошибкой: {ex.Message}",
                SuggestedAction = "Проверьте диагностический endpoint и журнал работы ProxyDock.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private static string? TryExtractIpAddress(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        var trimmed = responseBody.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("ip", out var ipProp) && ipProp.ValueKind == JsonValueKind.String)
                {
                    return ipProp.GetString()?.Trim();
                }

                if (doc.RootElement.TryGetProperty("origin", out var originProp) && originProp.ValueKind == JsonValueKind.String)
                {
                    return originProp.GetString()?.Trim();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            return ip.ToString();
        }

        return null;
    }
}
