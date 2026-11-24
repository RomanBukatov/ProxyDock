using System.Globalization;
using System.Xml.Linq;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App.Localization;
using ProxyDock.App.Services;
using ProxyDock.App.ViewModels;
using ProxyDock.App.Views;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Infrastructure.Launching;
using Xunit;

namespace ProxyDock.Tests;

public sealed class UiFixesAndProcessDetectionTests
{
    [Fact]
    public void AppCardMenuBuilder_PrePopulatesItemsBeforePresenterAttachment_AndExecutesCommands()
    {
        var loc = new LocalizationService();
        var proxy1 = new ProxyProfile { Id = Guid.NewGuid(), Name = "USA Proxy", Host = "127.0.0.1", Port = 8888, IsDefault = true };
        var app = new ApplicationProfile
        {
            Id = Guid.NewGuid(),
            Name = "Codex Desktop",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = @"C:\Apps\Codex\Codex.exe",
            ProxyMode = ProxyAssignmentMode.InheritDefault
        };

        var launchedDirect = false;
        ProxyAssignmentMode? assignedMode = null;
        Guid? assignedProxyId = null;
        var edited = false;
        var duplicated = false;
        var deleted = false;
        var diagnosticsOpened = false;
        var openedFolder = false;

        var card = new AppCardViewModel(
            app,
            icon: null,
            assignedProxyLabel: "USA Proxy",
            proxyStatus: ProxyCheckStatus.Reachable,
            targetFound: true,
            resolvedPath: app.ExecutableOrCommand,
            stackInfo: null,
            loc: loc,
            availableProxies: [proxy1],
            defaultProxy: proxy1,
            launchAction: (_, direct) => { launchedDirect = direct; return Task.CompletedTask; },
            assignProxyAction: (_, mode, pid) => { assignedMode = mode; assignedProxyId = pid; return Task.CompletedTask; },
            editAction: _ => { edited = true; return Task.CompletedTask; },
            duplicateAction: _ => { duplicated = true; return Task.CompletedTask; },
            deleteAction: _ => { deleted = true; return Task.CompletedTask; },
            diagnosticsAction: _ => { diagnosticsOpened = true; return Task.CompletedTask; },
            openFolderAction: _ => { openedFolder = true; });

        AvaloniaTestRuntime.RunOnUiThread(() =>
        {
            var flyout = AppCardMenuBuilder.Build(card);

            // Критическая проверка регрессии Avalonia 12: при привязке MenuFlyoutPresenter к flyout.Items
            // элементы должны быть уже добавлены, иначе ItemCount будет 0 (пустое меню).
            var presenter = new MenuFlyoutPresenter { ItemsSource = flyout.Items };
            Assert.Equal(9, flyout.Items.Count);
            Assert.Equal(9, presenter.ItemCount);

            var items = flyout.Items.OfType<MenuItem>().ToList();
            Assert.Equal(7, items.Count);
            Assert.Equal(loc["MenuLaunchDirect"], items[0].Header);
            Assert.Equal(loc["MenuChangeProxy"], items[1].Header);
            Assert.Equal(loc["MenuEdit"], items[2].Header);
            Assert.Equal(loc["MenuOpenFileLocation"], items[3].Header);
            Assert.Equal(loc["MenuDuplicate"], items[4].Header);
            Assert.Equal(loc["MenuDiagnostics"], items[5].Header);
            Assert.Equal(loc["MenuDelete"], items[6].Header);

            // Проверяем подменю выбора прокси
            var proxySubItems = items[1].Items.OfType<MenuItem>().ToList();
            Assert.Equal(3, proxySubItems.Count);
            Assert.True(proxySubItems[0].IsChecked); // InheritDefault
            Assert.False(proxySubItems[1].IsChecked); // USA Proxy
            Assert.False(proxySubItems[2].IsChecked); // DirectNoProxy

            // Реальное выполнение команд меню
            Assert.True(items[0].Command!.CanExecute(null));
            items[0].Command!.Execute(null);
            Assert.True(launchedDirect);

            proxySubItems[1].Command!.Execute(null);
            Assert.Equal(ProxyAssignmentMode.Specific, assignedMode);
            Assert.Equal(proxy1.Id, assignedProxyId);

            items[2].Command!.Execute(null);
            Assert.True(edited);

            items[3].Command!.Execute(null);
            Assert.True(openedFolder);

            items[4].Command!.Execute(null);
            Assert.True(duplicated);

            items[5].Command!.Execute(null);
            Assert.True(diagnosticsOpened);

            items[6].Command!.Execute(null);
            Assert.True(deleted);
        });
    }

    [Fact]
    public async Task DeleteProfile_RemovesOnlyProxyDockProfile_LeavingExecutableOnDiskIntact()
    {
        using var scope = new TempTestScope();
        var appService = scope.Services.GetRequiredService<IApplicationProfileService>();

        var fakeExe = Path.Combine(scope.RootPath, "InstalledApp.exe");
        await File.WriteAllBytesAsync(fakeExe, [0x4D, 0x5A]);

        var saved = await appService.SaveAsync(new ApplicationProfile
        {
            Name = "Installed App",
            LaunchKind = LaunchKind.Desktop,
            ExecutableOrCommand = fakeExe
        });

        await appService.DeleteAsync(saved.Id);

        var remaining = await appService.GetAllAsync();
        Assert.Empty(remaining);
        Assert.True(File.Exists(fakeExe));
    }

    [Fact]
    public void MissingTarget_DisablesLaunchCommands_WhileKeepingEditCommandEnabled()
    {
        var loc = new LocalizationService();
        var card = new AppCardViewModel(
            new ApplicationProfile { Name = "Missing App", LaunchKind = LaunchKind.Desktop, ExecutableOrCommand = @"C:\Missing\app.exe" },
            icon: null,
            assignedProxyLabel: "USA Proxy",
            proxyStatus: ProxyCheckStatus.Reachable,
            targetFound: false,
            resolvedPath: null,
            stackInfo: null,
            loc: loc,
            availableProxies: [],
            defaultProxy: null,
            launchAction: (_, _) => Task.CompletedTask,
            assignProxyAction: (_, _, _) => Task.CompletedTask,
            editAction: _ => Task.CompletedTask,
            duplicateAction: _ => Task.CompletedTask,
            deleteAction: _ => Task.CompletedTask,
            diagnosticsAction: _ => Task.CompletedTask,
            openFolderAction: _ => { });

        Assert.False(card.CanLaunch);
        Assert.False(card.LaunchCommand.CanExecute(null));
        Assert.False(card.LaunchDirectCommand.CanExecute(null));
        Assert.True(card.EditCommand.CanExecute(null));

        AvaloniaTestRuntime.RunOnUiThread(() =>
        {
            var flyout = AppCardMenuBuilder.Build(card);
            var launchDirectItem = flyout.Items.OfType<MenuItem>().First();
            Assert.False(launchDirectItem.Command!.CanExecute(null));
        });
    }

    [Fact]
    public void ToastNotifications_CompactFormat_AutoDismissesSuccess_AndKeepsErrors()
    {
        AvaloniaTestRuntime.RunOnUiThread(() =>
        {
            using var scope = new TempTestScope();
            var loc = new LocalizationService();
            var notifications = new AppNotificationService();
            var diagnostics = scope.Services.GetRequiredService<IConfigDiagnosticsStore>();
            var settingsService = scope.Services.GetRequiredService<ISettingsService>();
            var appService = scope.Services.GetRequiredService<IApplicationProfileService>();
            var proxyService = scope.Services.GetRequiredService<IProxyProfileService>();
            var discovery = scope.Services.GetRequiredService<IExecutableDiscoveryService>();
            var historyRepo = scope.Services.GetRequiredService<ILaunchHistoryRepository>();

            var delayGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TimeSpan? observedDelay = null;

            var iconCache = new IconCacheService(scope.Services.GetRequiredService<IAppIconExtractor>(), discovery);
            var appsVm = new ApplicationsViewModel(loc, appService, proxyService, discovery, iconCache, new NoOpDialogService(), new AppLaunchCoordinator(new NoOpLauncher(), appService, discovery, new NoOpDialogService(), notifications, loc), notifications);
            var proxiesVm = new ProxyProfilesViewModel(loc, proxyService, scope.Services.GetRequiredService<IProxyConnectivityTester>(), new NoOpDialogService(), notifications);
            var historyVm = new LaunchHistoryViewModel(loc, historyRepo, new NoOpDialogService(), notifications);
            var settingsVm = new SettingsViewModel(loc, settingsService, proxyService, scope.Services.GetRequiredService<IStartupManager>(), scope.Services.GetRequiredService<IPathProvider>(), scope.Services.GetRequiredService<IConfigExportService>(), scope.Services.GetRequiredService<IUpdateService>(), new ThemeService(), scope.Services.GetRequiredService<ProxyDock.Infrastructure.Logging.FileLoggerProvider>(), new NoOpDialogService(), notifications);

            var mainVm = new MainWindowViewModel(
                loc,
                diagnostics,
                settingsService,
                scope.Services.GetRequiredService<IUpdateService>(),
                new NoOpDialogService(),
                notifications,
                appsVm,
                proxiesVm,
                historyVm,
                settingsVm,
                delayFunc: async (delay, ct) =>
                {
                    observedDelay = delay;
                    await delayGate.Task.WaitAsync(ct);
                });

            var coordinator = new AppLaunchCoordinator(new NoOpLauncher(), appService, discovery, new NoOpDialogService(), notifications, loc);
            var codexProfile = new ApplicationProfile { Id = Guid.NewGuid(), Name = "Codex Desktop" };

            // 1. Обычный успешный запуск -> компактный toast на 3 секунды без технического шума MSIX/PID
            coordinator.PublishLaunchNotification(codexProfile, new LaunchResult
            {
                Status = LaunchStatus.Started,
                ProcessId = 12345,
                Strategy = LaunchRoutingStrategy.EnvironmentAndChromium,
                ProxyName = "USA Proxy",
                Message = "Длинное техническое сообщение с PID 12345"
            });

            Assert.Single(mainVm.Toasts);
            var successToast = mainVm.Toasts[0];
            Assert.Equal("Codex Desktop запущен", successToast.Title);
            Assert.Equal("USA Proxy", successToast.Message);
            Assert.DoesNotContain("MSIX", successToast.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("12345", successToast.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TimeSpan.FromSeconds(3), observedDelay);

            // Завершаем задержку и прокачиваем очередь Dispatcher.UIThread -> toast автоматически исчезает
            delayGate.SetResult(true);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (mainVm.Toasts.Count > 0 && DateTime.UtcNow < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(15);
            }
            Assert.Empty(mainVm.Toasts);

            // 2. Ошибка запуска не исчезает автоматически и имеет кнопку перехода в историю
            coordinator.PublishLaunchNotification(codexProfile, new LaunchResult
            {
                Status = LaunchStatus.Failed,
                Message = "Файл не найден",
                SuggestedAction = "Проверьте путь."
            });

            Assert.Single(mainVm.Toasts);
            var errorToast = mainVm.Toasts[0];
            Assert.Equal(NotificationSeverity.Error, errorToast.Severity);
            Assert.True(errorToast.ShowOpenHistoryAction);

            // Закрытие вручную
            errorToast.DismissCommand.Execute(null);
            Assert.Empty(mainVm.Toasts);

            // 3. Повторный запуск при работающем основном экземпляре -> компактное предупреждение
            delayGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.PublishLaunchNotification(codexProfile, new LaunchResult
            {
                Status = LaunchStatus.StartedWithWarning,
                Strategy = LaunchRoutingStrategy.EnvironmentAndChromium,
                ProxyName = "USA Proxy",
                AlreadyRunningMainInstanceDetected = true,
                AlreadyRunningProcessIds = [4444],
                Warnings = [loc["ToastAlreadyRunningProxyWarning"]]
            });

            Assert.Single(mainVm.Toasts);
            Assert.Equal(NotificationSeverity.Warning, mainVm.Toasts[0].Severity);
            Assert.Equal(loc["ToastAlreadyRunningProxyWarning"], mainVm.Toasts[0].Message);
        });
    }

    [Fact]
    public void ProcessInstanceClassifier_DistinguishesMainWindowTrayHelpersAndMsixIdentity()
    {
        var context = new ProcessMatchContext(
            ExpectedExecutablePath: @"C:\Users\demo\AppData\Local\Microsoft\WindowsApps\OpenAI.Codex_2w2\chatgpt-desktop.exe",
            ExpectedTargetExecutablePath: @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe",
            ExpectedPackageFamilyName: "OpenAI.Codex_2w2",
            ExpectedPackageRoot: @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0",
            IsWindowsAppsAlias: true);

        // 1. Только Chromium helper-процессы и companion codex.exe -> НЕ блокирует / НЕ считается активным главным экземпляром
        var helpersOnly = new List<RawProcessCandidate>
        {
            new(101, 100, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe", "\"ChatGPT.exe\" --type=renderer", "OpenAI.Codex_1.0_x64__2w2", "OpenAI.Codex_2w2", false, null, false),
            new(102, 100, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe", "\"ChatGPT.exe\" --type=gpu-process", "OpenAI.Codex_1.0_x64__2w2", "OpenAI.Codex_2w2", false, null, false),
            new(103, 100, "codex", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\resources\codex.exe", "codex.exe app-server", "OpenAI.Codex_1.0_x64__2w2", "OpenAI.Codex_2w2", false, null, false),
            // Отдельный CLI codex.exe без MSIX identity не должен смешиваться с Codex Desktop
            new(999, 50, "codex", @"C:\Users\demo\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.exe", "codex.exe", null, null, false, null, false)
        };

        var helperInspection = ProcessInstanceClassifier.Classify(helpersOnly, context);
        Assert.False(helperInspection.HasActiveMainInstance);
        Assert.True(helperInspection.HasOnlyBackgroundOrHelperProcesses);
        Assert.Equal(3, helperInspection.Processes.Count); // CLI codex.exe отфильтрован по MSIX identity

        // 2. Главный процесс с видимым окном + дочерний без --type=
        var withWindow = new List<RawProcessCandidate>(helpersOnly)
        {
            new(100, 1, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe", "\"ChatGPT.exe\"", "OpenAI.Codex_1.0_x64__2w2", "OpenAI.Codex_2w2", true, "Codex", true),
            new(104, 100, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe", null, "OpenAI.Codex_1.0_x64__2w2", "OpenAI.Codex_2w2", false, null, false)
        };

        var windowInspection = ProcessInstanceClassifier.Classify(withWindow, context);
        Assert.True(windowInspection.HasActiveMainInstance);
        Assert.True(windowInspection.HasVisibleWindowInstance);
        Assert.Equal([100], windowInspection.ActiveMainProcessIds);

        // 3. Главный корневой процесс в системном трее (окно скрыто, но процесс корневой и не helper)
        var trayOnly = new List<RawProcessCandidate>
        {
            new(200, 1, "ChatGPT", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0\app\ChatGPT.exe", "\"ChatGPT.exe\"", "OpenAI.Codex_1.0_x64__2w2", "OpenAI.Codex_2w2", false, null, true)
        };

        var trayInspection = ProcessInstanceClassifier.Classify(trayOnly, context);
        Assert.True(trayInspection.HasActiveMainInstance);
        Assert.False(trayInspection.HasVisibleWindowInstance);
        Assert.Single(trayInspection.PrimaryTrayOrBackgroundProcesses);
    }

    [Fact]
    public async Task CodexDesktop_BuildPlan_KeepsMsixCaveatsInDiagnosticNotes_NotRuntimeWarnings()
    {
        using var scope = new TempTestScope();
        var strategies = scope.Services.GetServices<ILaunchStrategy>().ToList();
        var desktopStrategy = strategies.OfType<DesktopLaunchStrategy>().Single();

        var appDir = Path.Combine(scope.RootPath, "CodexMsixSim");
        var resDir = Path.Combine(appDir, "resources");
        Directory.CreateDirectory(resDir);
        var exe = Path.Combine(appDir, "chatgpt-desktop.exe");
        await File.WriteAllBytesAsync(exe, [0x4D, 0x5A]);
        await File.WriteAllTextAsync(Path.Combine(appDir, "owl-shell-runtime.json"), "{}");
        await File.WriteAllBytesAsync(Path.Combine(resDir, "codex.exe"), [0x4D, 0x5A]);

        var app = BuiltInAppTemplates.CreateCodexDesktop(exe);
        var proxy = new ProxyProfile { Name = "USA Proxy", Host = "127.0.0.1", Port = 8888 };

        var plan = await desktopStrategy.BuildPlanAsync(app, proxy, null, new AppSettings(), isDirectNoProxy: false);

        Assert.Equal(LaunchRoutingStrategy.EnvironmentAndChromium, plan.Strategy);
        Assert.Equal("1", plan.EnvironmentSet["NODE_USE_ENV_PROXY"]);
        Assert.Contains("--proxy-server=http://127.0.0.1:8888", plan.Arguments);
        Assert.Empty(plan.Warnings);
        Assert.NotEmpty(plan.DiagnosticNotes);
    }

    [Fact]
    public void ThemeResources_PlaceholdersHaveSufficientWcagContrastInLightAndDarkThemes()
    {
        var themePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ProxyDock.App", "Styles", "ThemeResources.axaml"));
        Assert.True(File.Exists(themePath), $"ThemeResources.axaml not found at {themePath}");

        var doc = XDocument.Load(themePath);
        var dictionaries = doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attributes().Any(a => a.Name.LocalName == "Key")).ToList();

        foreach (var themeKey in new[] { "Light", "Dark" })
        {
            var dict = dictionaries.Single(d => d.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == themeKey));
            var brushes = dict.Elements().Where(e => e.Name.LocalName == "SolidColorBrush")
                .ToDictionary(
                    e => e.Attributes().First(a => a.Name.LocalName == "Key").Value,
                    e => e.Attribute("Color")!.Value);

            var bgHex = brushes["InputSurfaceBrush"];
            var placeholderHex = brushes["TextControlPlaceholderForeground"];
            var ratio = ComputeContrastRatio(bgHex, placeholderHex);

            Assert.True(ratio >= 4.5, $"{themeKey} theme placeholder contrast {ratio:F2}:1 ({placeholderHex} on {bgHex}) is below WCAG AA 4.5:1");
        }
    }

    private static double ComputeContrastRatio(string hex1, string hex2)
    {
        var l1 = RelativeLuminance(hex1);
        var l2 = RelativeLuminance(hex2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        var clean = hex.TrimStart('#');
        var r = int.Parse(clean[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        var g = int.Parse(clean[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        var b = int.Parse(clean[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

        static double Linearize(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    private sealed class NoOpDialogService : IDialogService
    {
        public Task<ApplicationProfile?> ShowApplicationEditorAsync(ApplicationProfile? initialProfile) => Task.FromResult<ApplicationProfile?>(null);
        public Task<ProxyProfile?> ShowProxyEditorAsync(ProxyProfile? initialProfile) => Task.FromResult<ProxyProfile?>(null);
        public Task ShowApplicationDiagnosticsAsync(ApplicationProfile profile, Func<Task>? onEditRequested = null) => Task.CompletedTask;
        public Task ShowUpdateDialogAsync(UpdateReleaseInfo release) => Task.CompletedTask;
        public Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "Продолжить", string? cancelText = "Отмена", bool isDanger = false) => Task.FromResult(false);
        public Task<string?> PickExecutableFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialPath = null) => Task.FromResult<string?>(null);
        public Task<string?> PickExportFileAsync(string defaultFileName) => Task.FromResult<string?>(null);
        public Task<string?> PickImportFileAsync() => Task.FromResult<string?>(null);
    }

    private sealed class NoOpLauncher : IApplicationLauncher
    {
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LaunchResult { Status = LaunchStatus.Started });
    }
}
