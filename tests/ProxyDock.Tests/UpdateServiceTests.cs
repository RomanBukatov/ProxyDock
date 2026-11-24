using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDock.App;
using ProxyDock.App.Localization;
using ProxyDock.App.Services;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Infrastructure.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace ProxyDock.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.1", UpdateChannel.Stable, true)]
    [InlineData("v0.1.0", "0.2.0", UpdateChannel.Stable, true)]
    [InlineData("0.1.0", "1.0.0", UpdateChannel.Stable, true)]
    [InlineData("0.1.0", "0.1.0", UpdateChannel.Stable, false)]
    [InlineData("0.2.0", "0.1.9", UpdateChannel.Stable, false)]
    [InlineData("0.1.0", "0.2.0-beta.1", UpdateChannel.Stable, false)]
    [InlineData("0.1.0", "0.1.1-rc.1", UpdateChannel.Stable, false)]
    [InlineData("0.1.0", "invalid", UpdateChannel.Stable, false)]
    public void SemanticVersionComparer_EnforcesSemVerNoDowngradeAndNoPrereleaseOnStable(
        string current,
        string candidate,
        UpdateChannel channel,
        bool expected)
    {
        Assert.Equal(expected, SemanticVersionComparer.IsEligibleUpdate(current, candidate, channel));
    }

    [Fact]
    public async Task VelopackUpdateService_CheckAndDownload_HandlesUpToDateAvailablePrereleaseAndDowngrade()
    {
        using var scope = new TempTestScope();
        var settingsService = scope.Services.GetRequiredService<ISettingsService>();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var credStore = scope.Services.GetRequiredService<ICredentialStore>();

        // 1. UpToDate (0.1.0 == 0.1.0)
        var upToDateSource = new MockUpdateSource(new VelopackAssetFeed
        {
            Assets = [new VelopackAsset { PackageId = "ProxyDockApp", Version = Velopack.SemanticVersion.Parse("0.1.0"), Type = VelopackAssetType.Full, FileName = "ProxyDockApp-0.1.0-full.nupkg", Size = 1024 }]
        });
        var svcUpToDate = CreateService(settingsService, proxyService, credStore, _ => upToDateSource, "0.1.0", UpdateInstallMode.VelopackManaged);
        var res1 = await svcUpToDate.CheckForUpdatesAsync(isManualCheck: true);
        Assert.Equal(UpdateCheckStatus.UpToDate, res1.Status);
        Assert.Null(res1.AvailableRelease);

        // 2. Downgrade blocked (0.2.0 -> 0.1.0)
        var svcDowngrade = CreateService(settingsService, proxyService, credStore, _ => upToDateSource, "0.2.0", UpdateInstallMode.VelopackManaged);
        var res2 = await svcDowngrade.CheckForUpdatesAsync(isManualCheck: true);
        Assert.Equal(UpdateCheckStatus.UpToDate, res2.Status);

        // 3. Prerelease excluded on Stable (0.1.0 -> 0.2.0-beta.1)
        var preSource = new MockUpdateSource(new VelopackAssetFeed
        {
            Assets = [new VelopackAsset { PackageId = "ProxyDockApp", Version = Velopack.SemanticVersion.Parse("0.2.0-beta.1"), Type = VelopackAssetType.Full, FileName = "ProxyDockApp-0.2.0-beta.1-full.nupkg", Size = 2048 }]
        });
        var svcPre = CreateService(settingsService, proxyService, credStore, _ => preSource, "0.1.0", UpdateInstallMode.VelopackManaged);
        var res3 = await svcPre.CheckForUpdatesAsync(isManualCheck: true);
        Assert.Equal(UpdateCheckStatus.UpToDate, res3.Status);

        // 4. Update available (0.1.0 -> 0.1.1)
        var newerSource = new MockUpdateSource(new VelopackAssetFeed
        {
            Assets =
            [
                new VelopackAsset
                {
                    PackageId = "ProxyDockApp",
                    Version = Velopack.SemanticVersion.Parse("0.1.1"),
                    Type = VelopackAssetType.Full,
                    FileName = "ProxyDockApp-0.1.1-full.nupkg",
                    Size = 42_000_000,
                    NotesMarkdown = "Bug fixes and improvements"
                }
            ]
        });
        var svcNewer = CreateService(settingsService, proxyService, credStore, _ => newerSource, "0.1.0", UpdateInstallMode.VelopackManaged);
        var res4 = await svcNewer.CheckForUpdatesAsync(isManualCheck: true);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, res4.Status);
        Assert.NotNull(res4.AvailableRelease);
        Assert.Equal("0.1.1", res4.AvailableRelease!.Version);
        Assert.Equal("Bug fixes and improvements", res4.AvailableRelease.ReleaseNotesMarkdown);
        Assert.Equal(42_000_000, res4.AvailableRelease.DownloadSizeBytes);
        Assert.True(res4.AvailableRelease.CanSelfUpdate);
        Assert.Equal("https://github.com/RomanBukatov/ProxyDock/releases/tag/v0.1.1", res4.AvailableRelease.ReleasePageUrl);
    }

    [Fact]
    public async Task VelopackUpdateService_HandlesNetworkErrorsCancellationAndIntegrityErrorsGracefully()
    {
        using var scope = new TempTestScope();
        var settingsService = scope.Services.GetRequiredService<ISettingsService>();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var credStore = scope.Services.GetRequiredService<ICredentialStore>();

        // 1. Network error (offline / 403 rate limit)
        var failingSource = new MockUpdateSource(feedException: new HttpRequestException("Rate limit", null, HttpStatusCode.Forbidden));
        var svcFail = CreateService(settingsService, proxyService, credStore, _ => failingSource, "0.1.0", UpdateInstallMode.VelopackManaged);
        var failRes = await svcFail.CheckForUpdatesAsync(isManualCheck: false);
        Assert.Equal(UpdateCheckStatus.Failed, failRes.Status);
        Assert.Contains("Rate limit", failRes.Message, StringComparison.OrdinalIgnoreCase);

        // 2. Cancellation
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelRes = await svcFail.CheckForUpdatesAsync(isManualCheck: true, cts.Token);
        Assert.Equal(UpdateCheckStatus.Cancelled, cancelRes.Status);

        // 3. Corrupt / incomplete package on DownloadUpdateAsync throws clear error without crashing
        var corruptSource = new MockUpdateSource(
            new VelopackAssetFeed
            {
                Assets = [new VelopackAsset { PackageId = "ProxyDockApp", Version = Velopack.SemanticVersion.Parse("0.1.1"), Type = VelopackAssetType.Full, FileName = "ProxyDockApp-0.1.1-full.nupkg", Size = 1024 }]
            },
            downloadException: new InvalidDataException("Corrupt nupkg archive"));
        var svcCorrupt = CreateService(settingsService, proxyService, credStore, _ => corruptSource, "0.1.0", UpdateInstallMode.VelopackManaged);
        var checkRes = await svcCorrupt.CheckForUpdatesAsync(isManualCheck: true);
        Assert.NotNull(checkRes.AvailableRelease);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svcCorrupt.DownloadUpdateAsync(checkRes.AvailableRelease!));
        Assert.Contains("Corrupt nupkg archive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VelopackUpdateService_StandaloneMode_DetectsUpdateFromGitHubJson_WithCanSelfUpdateFalse()
    {
        using var scope = new TempTestScope();
        var settingsService = scope.Services.GetRequiredService<ISettingsService>();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var credStore = scope.Services.GetRequiredService<ICredentialStore>();

        const string json = """
        [
          {
            "tag_name": "v0.3.0-alpha.1",
            "draft": false,
            "prerelease": true,
            "html_url": "https://github.com/RomanBukatov/ProxyDock/releases/tag/v0.3.0-alpha.1",
            "body": "Preview",
            "assets": [{ "name": "ProxyDockApp-win-Setup.exe", "size": 50000000 }]
          },
          {
            "tag_name": "v0.1.2",
            "draft": false,
            "prerelease": false,
            "html_url": "https://github.com/RomanBukatov/ProxyDock/releases/tag/v0.1.2",
            "body": "Stable release 0.1.2",
            "assets": [{ "name": "ProxyDockApp-win-Setup.exe", "size": 45000000 }]
          }
        ]
        """;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(serverCts.Token);
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    _ = await stream.ReadAsync(buf, serverCts.Token);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    var hdr = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), serverCts.Token);
                    await stream.WriteAsync(bytes, serverCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }, serverCts.Token);

        try
        {
            var svc = new VelopackUpdateService(
                settingsService,
                proxyService,
                credStore,
                new UpdateRuntimeOptions(),
                NullLogger<VelopackUpdateService>.Instance,
                currentVersionOverride: "0.1.0",
                installModeOverride: UpdateInstallMode.Standalone,
                releasesApiUrlOverride: $"http://127.0.0.1:{port}/releases",
                allowCustomHostsForTesting: true);

            var res = await svc.CheckForUpdatesAsync(isManualCheck: true);
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, res.Status);
            Assert.False(res.CanSelfUpdate);
            Assert.NotNull(res.AvailableRelease);
            Assert.False(res.AvailableRelease!.CanSelfUpdate);
            Assert.Equal("0.1.2", res.AvailableRelease.Version); // 0.3.0-alpha.1 исключён
            Assert.Equal("Stable release 0.1.2", res.AvailableRelease.ReleaseNotesMarkdown);
            Assert.Equal(45000000, res.AvailableRelease.DownloadSizeBytes);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.DownloadUpdateAsync(res.AvailableRelease));
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
        }
    }

    [Fact]
    public async Task ProxyAwareFileDownloader_RoutesSelectedProxyWithBasicAuth_AndRejectsUntrustedProductionUrls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawProxyAuth = false;
        var leakedAuthToRequest = false;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(serverCts.Token);
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    var read = await stream.ReadAsync(buf, serverCts.Token);
                    var req = Encoding.ASCII.GetString(buf, 0, read);
                    if (req.Contains("Proxy-Authorization: Basic", StringComparison.OrdinalIgnoreCase))
                    {
                        sawProxyAuth = true;
                        if (req.Contains("\nAuthorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            leakedAuthToRequest = true;
                        }

                        const string body = "{\"ok\":true}";
                        var resp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), serverCts.Token);
                    }
                    else
                    {
                        const string authResp = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(authResp), serverCts.Token);
                    }
                }
                catch
                {
                    break;
                }
            }
        }, serverCts.Token);

        try
        {
            var proxy = new ProxyProfile { Host = "127.0.0.1", Port = port, Username = "u1" };
            var creds = new ProxyCredentials("u1", "secret99");
            using var downloader = new ProxyAwareFileDownloader(
                UpdateNetworkMode.SelectedProxy,
                proxy,
                creds,
                "0.1.0",
                allowCustomHostsForTesting: true);

            var payload = await downloader.DownloadString("http://example.test/releases");
            Assert.Equal("{\"ok\":true}", payload);
            Assert.True(sawProxyAuth);
            Assert.False(leakedAuthToRequest);

            // В production-режиме (allowCustomHostsForTesting: false) HTTP и сторонние хосты запрещены
            using var prodDownloader = new ProxyAwareFileDownloader(UpdateNetworkMode.DirectNoProxy);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                prodDownloader.DownloadString("http://github.com/RomanBukatov/ProxyDock"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                prodDownloader.DownloadString("https://evil.example.com/payload.exe"));

            Assert.Equal(
                "https://github.com/RomanBukatov/ProxyDock/releases",
                ProxyAwareFileDownloader.ValidateTrustedReleaseUrl("https://evil.example.com/phish"));
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
        }
    }

    [Fact]
    public async Task UpdateSettingsAndUserData_ArePreservedAcrossSaves_AndStartupCheckDoesNotBlockUi()
    {
        using var scope = new TempTestScope();
        var settingsService = scope.Services.GetRequiredService<ISettingsService>();
        var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
        var credStore = scope.Services.GetRequiredService<ICredentialStore>();

        var savedProxy = await proxyService.SaveAsync(
            new ProxyProfile { Name = "KeepProxy", Host = "127.0.0.1", Port = 8080, Username = "alice", IsDefault = true },
            newPassword: "KeepSecretPassword");
        var savedApp = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "KeepApp",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = @"C:\Apps\Keep.exe"
        });

        var settings = await settingsService.GetAsync();
        Assert.True(settings.AutoCheckUpdates);
        Assert.Equal(UpdateChannel.Stable, settings.UpdateChannel);
        Assert.Equal(UpdateNetworkMode.SystemDefault, settings.UpdateNetworkMode);

        settings.UpdateNetworkMode = UpdateNetworkMode.DirectNoProxy;
        settings.AutoCheckUpdates = false;
        await settingsService.SaveAsync(settings);

        var reloaded = await settingsService.GetAsync();
        Assert.False(reloaded.AutoCheckUpdates);
        Assert.Equal(UpdateNetworkMode.DirectNoProxy, reloaded.UpdateNetworkMode);
        Assert.Single(await proxyService.GetAllAsync());
        Assert.Single(await appService.GetAllAsync());
        var loadedCreds = await credStore.LoadAsync(savedProxy.Id);
        Assert.Equal("KeepSecretPassword", loadedCreds?.Password);

        // Проверяем парсинг --test-update-source и неблокирующий фоновый запуск
        var testFeedDir = Path.Combine(scope.RootPath, "Feed");
        Assert.Equal(Path.GetFullPath(testFeedDir), Program.TryParseTestUpdateSourceArgument(["--test-update-source", testFeedDir]));
        Assert.Equal(Path.GetFullPath(testFeedDir), Program.TryParseTestUpdateSourceArgument([$"--test-update-source={testFeedDir}"]));

        var notifications = new AppNotificationService();
        var loc = new LocalizationService();
        var mockSource = new MockUpdateSource(new VelopackAssetFeed
        {
            Assets = [new VelopackAsset { PackageId = "ProxyDockApp", Version = Velopack.SemanticVersion.Parse("0.1.5"), Type = VelopackAssetType.Full, FileName = "ProxyDockApp-0.1.5-full.nupkg", Size = 1000 }]
        });
        var mockUpdateSvc = CreateService(settingsService, proxyService, credStore, _ => mockSource, "0.1.0", UpdateInstallMode.VelopackManaged);

        // Включаем AutoCheckUpdates обратно для теста RunStartupUpdateCheckAsync
        reloaded.AutoCheckUpdates = true;
        await settingsService.SaveAsync(reloaded);

        AvaloniaTestRuntime.RunOnUiThread(() =>
        {
            var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();
            var historyRepo = scope.Services.GetRequiredService<ILaunchHistoryRepository>();
            var diagnostics = scope.Services.GetRequiredService<IConfigDiagnosticsStore>();
            var iconCache = new IconCacheService(scope.Services.GetRequiredService<IAppIconExtractor>(), discovery);
            var dialogStub = new StubUpdateDialogService();

            var appsVm = new ApplicationsViewModel(loc, appService, proxyService, discovery, iconCache, dialogStub, new AppLaunchCoordinator(new StubLauncher(), appService, discovery, dialogStub, notifications, loc), notifications);
            var proxiesVm = new ProxyProfilesViewModel(loc, proxyService, scope.Services.GetRequiredService<IProxyConnectivityTester>(), dialogStub, notifications);
            var historyVm = new LaunchHistoryViewModel(loc, historyRepo, dialogStub, notifications);
            var settingsVm = new SettingsViewModel(loc, settingsService, proxyService, scope.Services.GetRequiredService<IStartupManager>(), scope.Services.GetRequiredService<IPathProvider>(), scope.Services.GetRequiredService<IConfigExportService>(), mockUpdateSvc, new ThemeService(), scope.Services.GetRequiredService<ProxyDock.Infrastructure.Logging.FileLoggerProvider>(), dialogStub, notifications);

            var mainVm = new MainWindowViewModel(loc, diagnostics, settingsService, mockUpdateSvc, dialogStub, notifications, appsVm, proxiesVm, historyVm, settingsVm);
            mainVm.RunStartupUpdateCheckAsync().GetAwaiter().GetResult();

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (mainVm.Toasts.Count == 0 && DateTime.UtcNow < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(15);
            }

            Assert.Single(mainVm.Toasts);
            Assert.Contains("0.1.5", mainVm.Toasts[0].Title, StringComparison.Ordinal);
            Assert.True(mainVm.Toasts[0].ShowCustomAction);

            mainVm.Toasts[0].ExecuteCustomActionCommand.Execute(null);
            Assert.Equal("0.1.5", dialogStub.LastUpdateRelease?.Version);
        });
    }

    private static VelopackUpdateService CreateService(
        ISettingsService settingsService,
        IProxyProfileService proxyService,
        ICredentialStore credStore,
        Func<ProxyAwareFileDownloader, IUpdateSource> sourceFactory,
        string currentVersion,
        UpdateInstallMode mode,
        bool suppressed = false)
    {
        var packagesDir = Path.Combine(Path.GetTempPath(), "ProxyDock.Tests.Vpk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packagesDir);
        return new VelopackUpdateService(
            settingsService,
            proxyService,
            credStore,
            new UpdateRuntimeOptions { IsAutoCheckSuppressed = suppressed },
            NullLogger<VelopackUpdateService>.Instance,
            sourceFactory: sourceFactory,
            currentVersionOverride: currentVersion,
            installModeOverride: mode,
            allowCustomHostsForTesting: true,
            locatorFactory: () => new TestVelopackLocator("ProxyDockApp", currentVersion, packagesDir, null));
    }

    private sealed class MockUpdateSource(
        VelopackAssetFeed? feed = null,
        Exception? feedException = null,
        Exception? downloadException = null) : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger,
            string? appId,
            string channel,
            Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null)
        {
            if (feedException is not null)
            {
                throw feedException;
            }

            return Task.FromResult(feed ?? new VelopackAssetFeed());
        }

        public Task DownloadReleaseEntry(
            IVelopackLogger logger,
            VelopackAsset releaseEntry,
            string localFile,
            Action<int> progress,
            CancellationToken cancelToken)
        {
            if (downloadException is not null)
            {
                throw downloadException;
            }

            progress(100);
            return Task.CompletedTask;
        }
    }

    private sealed class StubUpdateDialogService : IDialogService
    {
        public UpdateReleaseInfo? LastUpdateRelease { get; private set; }
        public Task<ApplicationProfile?> ShowApplicationEditorAsync(ApplicationProfile? initialProfile) => Task.FromResult<ApplicationProfile?>(null);
        public Task<ProxyProfile?> ShowProxyEditorAsync(ProxyProfile? initialProfile) => Task.FromResult<ProxyProfile?>(null);
        public Task ShowApplicationDiagnosticsAsync(ApplicationProfile profile, Func<Task>? onEditRequested = null) => Task.CompletedTask;
        public Task ShowUpdateDialogAsync(UpdateReleaseInfo release)
        {
            LastUpdateRelease = release;
            return Task.CompletedTask;
        }
        public Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "Продолжить", string? cancelText = "Отмена", bool isDanger = false) => Task.FromResult(false);
        public Task<string?> PickExecutableFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<string?> PickExportFileAsync(string defaultFileName) => Task.FromResult<string?>(null);
        public Task<string?> PickImportFileAsync() => Task.FromResult<string?>(null);
    }

    private sealed class StubLauncher : IApplicationLauncher
    {
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LaunchResult { Status = LaunchStatus.Started });
    }
}
