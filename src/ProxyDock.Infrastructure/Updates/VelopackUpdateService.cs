using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ProxyDock.Infrastructure.Updates;

/// <summary>
/// Изолированный сервис проверки, загрузки и установки обновлений ProxyDock на базе Velopack и GitHub Releases.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    public const string DefaultReleasesApiUrl = "https://api.github.com/repos/RomanBukatov/ProxyDock/releases?per_page=10";

    private readonly ISettingsService _settingsService;
    private readonly IProxyProfileService _proxyService;
    private readonly ICredentialStore _credentialStore;
    private readonly UpdateRuntimeOptions _runtimeOptions;
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly Func<ProxyAwareFileDownloader, IUpdateSource>? _sourceFactory;
    private readonly Func<IVelopackLocator?>? _locatorFactory;
    private readonly string? _releasesApiUrlOverride;
    private readonly bool _allowCustomHostsForTesting;
    private readonly TimeSpan _checkTimeout;
    private readonly UpdateInstallMode? _installModeOverride;

    public VelopackUpdateService(
        ISettingsService settingsService,
        IProxyProfileService proxyService,
        ICredentialStore credentialStore,
        UpdateRuntimeOptions runtimeOptions,
        ILogger<VelopackUpdateService> logger,
        Func<ProxyAwareFileDownloader, IUpdateSource>? sourceFactory = null,
        string? currentVersionOverride = null,
        UpdateInstallMode? installModeOverride = null,
        string? releasesApiUrlOverride = null,
        bool allowCustomHostsForTesting = false,
        TimeSpan? checkTimeout = null,
        Func<IVelopackLocator?>? locatorFactory = null)
    {
        _settingsService = settingsService;
        _proxyService = proxyService;
        _credentialStore = credentialStore;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
        _sourceFactory = sourceFactory;
        _locatorFactory = locatorFactory;
        _releasesApiUrlOverride = releasesApiUrlOverride;
        _allowCustomHostsForTesting = allowCustomHostsForTesting;
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(20);
        _installModeOverride = installModeOverride;

        CurrentVersion = string.IsNullOrWhiteSpace(currentVersionOverride)
            ? AppVersionProvider.GetCurrentVersion()
            : currentVersionOverride;

        InstallMode = _installModeOverride ?? DetectInstallMode();
    }

    public string CurrentVersion { get; }
    public UpdateInstallMode InstallMode { get; }
    public bool IsAutoCheckSuppressed => _runtimeOptions.IsAutoCheckSuppressed;
    public string RepositoryUrl => ProxyAwareFileDownloader.TrustedRepositoryUrl;
    public string ReleasesPageUrl => ProxyAwareFileDownloader.TrustedReleasesUrl;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool isManualCheck = false, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var checkedAt = DateTimeOffset.UtcNow;

        using var timeoutCts = new CancellationTokenSource(_checkTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        ProxyAwareFileDownloader? downloader = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            downloader = await CreateDownloaderAsync(settings, cancellationToken).ConfigureAwait(false);

            UpdateCheckResult result;
            if (!string.IsNullOrWhiteSpace(_runtimeOptions.TestUpdateSourcePath) || _sourceFactory is not null || InstallMode == UpdateInstallMode.VelopackManaged)
            {
                result = await CheckViaVelopackAsync(settings, downloader, checkedAt, linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                result = await CheckStandaloneViaGitHubApiAsync(settings, downloader, checkedAt, linkedCts.Token).ConfigureAwait(false);
            }

            await PersistCheckStateAsync(settings, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Cancelled,
                CheckedAtUtc = checkedAt,
                CurrentVersion = CurrentVersion,
                CanSelfUpdate = InstallMode == UpdateInstallMode.VelopackManaged,
                Message = "Проверка обновлений отменена."
            };
            await PersistCheckStateAsync(settings, cancelled, CancellationToken.None).ConfigureAwait(false);
            return cancelled;
        }
        catch (OperationCanceledException)
        {
            var timedOut = new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Failed,
                CheckedAtUtc = checkedAt,
                CurrentVersion = CurrentVersion,
                CanSelfUpdate = InstallMode == UpdateInstallMode.VelopackManaged,
                Message = "Превышено время ожидания ответа от сервера обновлений.",
                ErrorDetails = "Timeout"
            };
            await PersistCheckStateAsync(settings, timedOut, CancellationToken.None).ConfigureAwait(false);
            return timedOut;
        }
        catch (Exception ex)
        {
            var friendly = TranslateNetworkError(ex);
            _logger.LogWarning("Ошибка проверки обновлений (manual={Manual}, mode={Mode}): {Message}", isManualCheck, settings.UpdateNetworkMode, friendly);
            var failed = new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Failed,
                CheckedAtUtc = checkedAt,
                CurrentVersion = CurrentVersion,
                CanSelfUpdate = InstallMode == UpdateInstallMode.VelopackManaged,
                Message = friendly,
                ErrorDetails = SecretRedactor.Redact(ex.Message, [])
            };
            await PersistCheckStateAsync(settings, failed, CancellationToken.None).ConfigureAwait(false);
            return failed;
        }
        finally
        {
            if (downloader is not null)
            {
                // Не освобождаем здесь только если downloader сохранён внутри InternalPayload для последующей загрузки
            }
        }
    }

    public async Task DownloadUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.CanSelfUpdate || release.InternalPayload is not VelopackUpdatePayload payload)
        {
            throw new InvalidOperationException("Автоматическое обновление недоступно для standalone-сборки. Скачайте установщик или portable-версию на GitHub Releases.");
        }

        try
        {
            progress?.Report(new UpdateDownloadProgress(0, $"Загрузка ProxyDock {release.Version}..."));
            await payload.Manager.DownloadUpdatesAsync(
                payload.UpdateInfo,
                pct => progress?.Report(new UpdateDownloadProgress(Math.Clamp(pct, 0, 100), $"Загрузка обновления: {pct}%")),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateDownloadProgress(100, "Пакет проверен и готов к установке."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка загрузки или проверки целостности пакета ProxyDock {Version}.", release.Version);
            throw new InvalidOperationException(
                $"Не удалось загрузить или проверить пакет обновления: {TranslateNetworkError(ex)}",
                ex);
        }
    }

    public void ApplyUpdateAndRestart(UpdateReleaseInfo release, Action requestAppShutdown)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(requestAppShutdown);

        if (!release.CanSelfUpdate || release.InternalPayload is not VelopackUpdatePayload payload)
        {
            throw new InvalidOperationException("Применение обновления поддерживается только в установленной/portable Velopack-сборке.");
        }

        _logger.LogInformation("Запуск установки обновления ProxyDock до версии {Version} и перезапуск.", release.Version);
        payload.Manager.WaitExitThenApplyUpdates(
            payload.UpdateInfo.TargetFullRelease,
            silent: false,
            restart: true,
            restartArgs: _runtimeOptions.RestartArguments.Length > 0 ? _runtimeOptions.RestartArguments : null);

        requestAppShutdown();
    }

    private async Task<UpdateCheckResult> CheckViaVelopackAsync(
        AppSettings settings,
        ProxyAwareFileDownloader downloader,
        DateTimeOffset checkedAt,
        CancellationToken ct)
    {
        var source = CreateUpdateSource(downloader);
        var locator = _locatorFactory?.Invoke();
        var mgr = new UpdateManager(source, locator: locator);

        var updateInfo = await mgr.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        if (updateInfo is null || updateInfo.IsDowngrade)
        {
            downloader.Dispose();
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpToDate,
                CheckedAtUtc = checkedAt,
                CurrentVersion = CurrentVersion,
                CanSelfUpdate = InstallMode == UpdateInstallMode.VelopackManaged,
                Message = $"Установлена актуальная версия ({CurrentVersion})."
            };
        }

        var candidateVersion = updateInfo.TargetFullRelease.Version.ToString();
        if (!SemanticVersionComparer.IsEligibleUpdate(CurrentVersion, candidateVersion, settings.UpdateChannel))
        {
            downloader.Dispose();
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpToDate,
                CheckedAtUtc = checkedAt,
                CurrentVersion = CurrentVersion,
                CanSelfUpdate = InstallMode == UpdateInstallMode.VelopackManaged,
                Message = $"Установлена актуальная версия ({CurrentVersion})."
            };
        }

        var canSelfUpdate = InstallMode == UpdateInstallMode.VelopackManaged;
        var releaseUrl = $"{ReleasesPageUrl}/tag/v{candidateVersion}";
        var releaseInfo = new UpdateReleaseInfo
        {
            Version = candidateVersion,
            CurrentVersion = CurrentVersion,
            ReleaseNotesMarkdown = updateInfo.TargetFullRelease.NotesMarkdown ?? string.Empty,
            DownloadSizeBytes = updateInfo.TargetFullRelease.Size > 0 ? updateInfo.TargetFullRelease.Size : null,
            ReleasePageUrl = ProxyAwareFileDownloader.ValidateTrustedReleaseUrl(releaseUrl),
            CanSelfUpdate = canSelfUpdate,
            IsPrerelease = SemanticVersionComparer.TryParse(candidateVersion, out var parsed) && parsed.IsPrerelease,
            Channel = settings.UpdateChannel,
            InternalPayload = canSelfUpdate ? new VelopackUpdatePayload(mgr, updateInfo, downloader) : null
        };

        if (!canSelfUpdate)
        {
            downloader.Dispose();
        }

        return new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            CheckedAtUtc = checkedAt,
            CurrentVersion = CurrentVersion,
            AvailableRelease = releaseInfo,
            CanSelfUpdate = canSelfUpdate,
            Message = $"Доступна новая версия: {candidateVersion}."
        };
    }

    private async Task<UpdateCheckResult> CheckStandaloneViaGitHubApiAsync(
        AppSettings settings,
        ProxyAwareFileDownloader downloader,
        DateTimeOffset checkedAt,
        CancellationToken ct)
    {
        try
        {
            var apiUrl = _releasesApiUrlOverride ?? DefaultReleasesApiUrl;
            string json;
            try
            {
                json = await downloader.DownloadString(apiUrl).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CheckedAtUtc = checkedAt,
                    CurrentVersion = CurrentVersion,
                    CanSelfUpdate = false,
                    Message = "Опубликованных релизов пока нет."
                };
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CheckedAtUtc = checkedAt,
                    CurrentVersion = CurrentVersion,
                    CanSelfUpdate = false,
                    Message = $"Установлена актуальная версия ({CurrentVersion})."
                };
            }

            UpdateReleaseInfo? bestRelease = null;
            ProxyDock.Core.Services.SemanticVersion bestVer = default;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var isPrerelease = item.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True;
                if (settings.UpdateChannel == UpdateChannel.Stable && isPrerelease)
                {
                    continue;
                }

                var tag = item.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                if (!SemanticVersionComparer.IsEligibleUpdate(CurrentVersion, tag, settings.UpdateChannel)
                    || !SemanticVersionComparer.TryParse(tag, out var parsedVer))
                {
                    continue;
                }

                if (bestRelease is not null && SemanticVersionComparer.Compare(parsedVer, bestVer) <= 0)
                {
                    continue;
                }

                bestVer = parsedVer;
                var body = item.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;
                var htmlUrl = item.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
                long? preferredSize = null;

                if (item.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                        var size = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var s) ? s : 0;
                        if (size > 0 && (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)
                                         || name.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase)
                                         || name.EndsWith("-standalone.exe", StringComparison.OrdinalIgnoreCase)))
                        {
                            preferredSize = size;
                            break;
                        }
                    }
                }

                bestRelease = new UpdateReleaseInfo
                {
                    Version = parsedVer.ToString(),
                    CurrentVersion = CurrentVersion,
                    ReleaseNotesMarkdown = body,
                    DownloadSizeBytes = preferredSize,
                    ReleasePageUrl = ProxyAwareFileDownloader.ValidateTrustedReleaseUrl(htmlUrl),
                    CanSelfUpdate = false,
                    IsPrerelease = isPrerelease || parsedVer.IsPrerelease,
                    Channel = settings.UpdateChannel
                };
            }

            if (bestRelease is null)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CheckedAtUtc = checkedAt,
                    CurrentVersion = CurrentVersion,
                    CanSelfUpdate = false,
                    Message = $"Установлена актуальная версия ({CurrentVersion})."
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CheckedAtUtc = checkedAt,
                CurrentVersion = CurrentVersion,
                AvailableRelease = bestRelease,
                CanSelfUpdate = false,
                Message = $"Доступна новая версия: {bestRelease.Version} (для standalone-сборки скачайте установщик или portable-версию с GitHub)."
            };
        }
        finally
        {
            downloader.Dispose();
        }
    }

    private IUpdateSource CreateUpdateSource(ProxyAwareFileDownloader downloader)
    {
        if (_sourceFactory is not null)
        {
            return _sourceFactory(downloader);
        }

        if (!string.IsNullOrWhiteSpace(_runtimeOptions.TestUpdateSourcePath))
        {
            return new SimpleFileSource(new DirectoryInfo(_runtimeOptions.TestUpdateSourcePath));
        }

        return new GithubSource(RepositoryUrl, accessToken: null, prerelease: false, downloader: downloader);
    }

    private async Task<ProxyAwareFileDownloader> CreateDownloaderAsync(AppSettings settings, CancellationToken ct)
    {
        ProxyProfile? proxy = null;
        ProxyCredentials? creds = null;

        if (settings.UpdateNetworkMode == UpdateNetworkMode.SelectedProxy)
        {
            var proxyId = settings.UpdateProxyProfileId ?? settings.DefaultProxyId;
            if (proxyId is Guid pid)
            {
                proxy = await _proxyService.GetByIdAsync(pid, ct).ConfigureAwait(false);
            }

            proxy ??= await _proxyService.GetDefaultAsync(ct).ConfigureAwait(false);
            if (proxy is null)
            {
                throw new InvalidOperationException("Не выбран HTTP-прокси для обновлений.");
            }

            if (proxy.HasCredentials || !string.IsNullOrWhiteSpace(proxy.Username))
            {
                creds = await _credentialStore.LoadAsync(proxy.Id, ct).ConfigureAwait(false);
            }
        }

        return new ProxyAwareFileDownloader(
            settings.UpdateNetworkMode,
            proxy,
            creds,
            CurrentVersion,
            _checkTimeout,
            _allowCustomHostsForTesting);
    }

    private async Task PersistCheckStateAsync(AppSettings settings, UpdateCheckResult result, CancellationToken ct)
    {
        try
        {
            settings.LastUpdateCheckAtUtc = result.CheckedAtUtc;
            settings.LastUpdateCheckStatus = result.Status;
            settings.LastUpdateCheckSummary = result.Message;
            settings.LastAvailableUpdateVersion = result.AvailableRelease?.Version;
            await _settingsService.SaveAsync(settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось сохранить состояние последней проверки обновлений.");
        }
    }

    private static UpdateInstallMode DetectInstallMode()
    {
        try
        {
            var mgr = new UpdateManager(new SimpleFileSource(new DirectoryInfo(AppContext.BaseDirectory)));
            return mgr.IsInstalled ? UpdateInstallMode.VelopackManaged : UpdateInstallMode.Standalone;
        }
        catch
        {
            return UpdateInstallMode.Standalone;
        }
    }

    private static string TranslateNetworkError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                return httpEx.Message;
            }

            if (httpEx.StatusCode is not null)
            {
                return $"Сервер обновлений вернул HTTP {(int)httpEx.StatusCode}.";
            }

            return "Нет подключения к сети или недоступен GitHub.";
        }

        return ex.Message;
    }

    private sealed record VelopackUpdatePayload(
        UpdateManager Manager,
        UpdateInfo UpdateInfo,
        ProxyAwareFileDownloader Downloader);
}
