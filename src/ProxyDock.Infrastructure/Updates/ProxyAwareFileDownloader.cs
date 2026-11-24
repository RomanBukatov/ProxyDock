using System.Net;
using System.Net.Http.Headers;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using Velopack.Sources;

namespace ProxyDock.Infrastructure.Updates;

/// <summary>
/// Реализация Velopack IFileDownloader на базе SocketsHttpHandler:
/// поддерживает стандартное подключение, прямое без системного прокси и работу через выбранный HTTP-прокси ProxyDock,
/// не изменяет системный прокси Windows, проверяет TLS и ограничивает доверенные хосты GitHub.
/// </summary>
public sealed class ProxyAwareFileDownloader : IFileDownloader, IDisposable
{
    public const string TrustedRepositoryUrl = "https://github.com/RomanBukatov/ProxyDock";
    public const string TrustedReleasesUrl = "https://github.com/RomanBukatov/ProxyDock/releases";

    private static readonly HashSet<string> TrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "codeload.github.com"
    };

    private readonly HttpClient _httpClient;
    private readonly bool _allowCustomHostsForTesting;
    private readonly TimeSpan _metadataTimeout;

    public ProxyAwareFileDownloader(
        UpdateNetworkMode networkMode,
        ProxyProfile? proxy = null,
        ProxyCredentials? credentials = null,
        string? appVersion = null,
        TimeSpan? metadataTimeout = null,
        bool allowCustomHostsForTesting = false)
    {
        _allowCustomHostsForTesting = allowCustomHostsForTesting;
        _metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(20);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        switch (networkMode)
        {
            case UpdateNetworkMode.DirectNoProxy:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;

            case UpdateNetworkMode.SelectedProxy:
                if (proxy is null)
                {
                    throw new InvalidOperationException("Выбран режим обновления через HTTP-прокси, но профиль прокси не найден.");
                }

                var proxyUri = new Uri($"http://{ProxyUriBuilder.FormatHost(proxy.Host)}:{proxy.Port}");
                var webProxy = new WebProxy(proxyUri)
                {
                    BypassProxyOnLocal = false
                };

                if (credentials is not null && !string.IsNullOrWhiteSpace(credentials.Username))
                {
                    webProxy.Credentials = new NetworkCredential(credentials.Username, credentials.Password);
                }

                handler.UseProxy = true;
                handler.Proxy = webProxy;
                break;

            default:
                handler.UseProxy = true;
                break;
        }

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var version = string.IsNullOrWhiteSpace(appVersion) ? "0.1.0" : appVersion;
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProxyDock", version));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        var uri = ValidateUrl(url);
        using var cts = new CancellationTokenSource(_metadataTimeout);
        using var req = CreateRequest(uri, headers);
        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        EnsureSuccessOrRateLimitMessage(resp);
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        var uri = ValidateUrl(url);
        using var cts = new CancellationTokenSource(_metadataTimeout);
        using var req = CreateRequest(uri, headers);
        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        EnsureSuccessOrRateLimitMessage(resp);
        return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
    }

    public async Task DownloadFile(
        string url,
        string targetFile,
        Action<int> progress,
        IDictionary<string, string>? headers = null,
        double timeout = 30,
        CancellationToken cancelToken = default)
    {
        var uri = ValidateUrl(url);
        var effectiveTimeout = TimeSpan.FromMinutes(Math.Clamp(timeout, 1, 60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        linkedCts.CancelAfter(effectiveTimeout);

        var targetDir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrWhiteSpace(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var tempPath = targetFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var req = CreateRequest(uri, headers);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            EnsureSuccessOrRateLimitMessage(resp);

            var totalBytes = resp.Content.Headers.ContentLength;
            await using (var src = await resp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false))
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                var lastPct = -1;

                while (true)
                {
                    var read = await src.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await dst.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token).ConfigureAwait(false);
                    totalRead += read;

                    if (totalBytes is > 0)
                    {
                        var pct = (int)Math.Clamp(totalRead * 100L / totalBytes.Value, 0, 100);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress?.Invoke(pct);
                        }
                    }
                }
            }

            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }

            File.Move(tempPath, targetFile);
            progress?.Invoke(100);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Игнорируем ошибку очистки временного файла
            }

            throw;
        }
    }

    public static string ValidateTrustedReleaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/RomanBukatov/ProxyDock", StringComparison.OrdinalIgnoreCase))
        {
            return TrustedReleasesUrl;
        }

        return uri.ToString();
    }

    private Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Некорректный URL источника обновлений.");
        }

        if (_allowCustomHostsForTesting)
        {
            return uri;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Небезопасная схема URL обновления ({uri.Scheme}). Разрешён только HTTPS.");
        }

        if (!TrustedHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException($"Недоверенный хост обновлений: {uri.Host}.");
        }

        return uri;
    }

    private static HttpRequestMessage CreateRequest(Uri uri, IDictionary<string, string>? headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                if (string.Equals(key, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                req.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return req;
    }

    private static void EnsureSuccessOrRateLimitMessage(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden
            && resp.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
            && vals.Contains("0"))
        {
            throw new HttpRequestException(
                "Превышен лимит анонимных запросов GitHub API (rate limit). Повторите проверку позже.",
                null,
                HttpStatusCode.Forbidden);
        }

        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _httpClient.Dispose();
}
