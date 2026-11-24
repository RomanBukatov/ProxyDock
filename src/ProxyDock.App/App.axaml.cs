using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ProxyDock.App.Localization;
using ProxyDock.App.Services;
using ProxyDock.App.ViewModels;
using ProxyDock.App.Views;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Infrastructure;
using ProxyDock.Infrastructure.Logging;

namespace ProxyDock.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayService;
    private EventWaitHandle? _activationEvent;
    private Thread? _activationListenerThread;
    private readonly CancellationTokenSource _appLifetimeCts = new();
    private bool _minimizeToTrayEnabled;
    private int _cleanupDone;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var verifyStartup = args.Any(a => a.Equals("--verify-startup", StringComparison.OrdinalIgnoreCase));
            var startMinimizedArg = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            var customDataDir = Program.TryParseDataDirArgument(args);
            var testUpdateSource = Program.TryParseTestUpdateSourceArgument(args);
            var isUpdateAutoCheckSuppressed = verifyStartup
                || (customDataDir is not null && string.IsNullOrWhiteSpace(testUpdateSource))
                || string.Equals(Environment.GetEnvironmentVariable("PROXYDOCK_DISABLE_UPDATES"), "1", StringComparison.Ordinal);

            var restartArgs = customDataDir is not null
                ? new[] { "--data-dir", customDataDir }
                : Array.Empty<string>();

            var updateOptions = new UpdateRuntimeOptions
            {
                IsAutoCheckSuppressed = isUpdateAutoCheckSuppressed,
                TestUpdateSourcePath = testUpdateSource,
                RestartArguments = restartArgs
            };

            var services = new ServiceCollection();
            ConfigureServices(services, customDataDir, updateOptions);
            _serviceProvider = services.BuildServiceProvider();

            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            var settings = await settingsService.GetAsync();

            var loc = _serviceProvider.GetRequiredService<ILocalizationService>();
            loc.SetLanguage(settings.Language);

            var themeService = _serviceProvider.GetRequiredService<ThemeService>();
            themeService.ApplyTheme(settings.Theme);

            var loggerProvider = _serviceProvider.GetRequiredService<FileLoggerProvider>();
            loggerProvider.SetMinimumLevel(settings.MinimumLogLevel);

            var mainVm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            _mainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            var dialogService = _serviceProvider.GetRequiredService<DialogService>();
            dialogService.AttachOwner(_mainWindow, () => ShutdownCompletely(desktop));

            _trayService = _serviceProvider.GetRequiredService<TrayIconService>();
            var coordinator = _serviceProvider.GetRequiredService<AppLaunchCoordinator>();

            desktop.Exit += (_, _) => CleanupOnExit();

            _trayService.Configure(
                this,
                ShowMainWindow,
                () =>
                {
                    mainVm.NavigateByKey("Settings");
                    ShowMainWindow();
                },
                () => ShutdownCompletely(desktop),
                (launchedApp, result) =>
                {
                    if (result.RequiresExistingProcessConfirmation)
                    {
                        _ = coordinator.LaunchWithPromptsAsync(launchedApp);
                    }
                    else
                    {
                        coordinator.PublishLaunchNotification(launchedApp, result);
                    }
                });

            ApplyTrayAndShutdownMode(desktop, settings.MinimizeToTray);

            settingsService.SettingsChanged += (_, updated) =>
                Dispatcher.UIThread.Post(() => ApplyTrayAndShutdownMode(desktop, updated.MinimizeToTray));

            _mainWindow.Closed += (_, _) =>
            {
                if (!_minimizeToTrayEnabled || _mainWindow.AllowExit)
                {
                    CleanupOnExit();
                }
            };

            desktop.MainWindow = _mainWindow;
            await mainVm.InitializeAsync();

            if (!verifyStartup && customDataDir is null)
            {
                StartActivationListener();
            }

            if (verifyStartup)
            {
                _mainWindow.AllowExit = true;
                _mainWindow.Show();
                await Dispatcher.UIThread.InvokeAsync(() => ShutdownCompletely(desktop), DispatcherPriority.Background);
                return;
            }

            var startHiddenInTray = settings.MinimizeToTray && (startMinimizedArg || settings.StartMinimized);
            if (!startHiddenInTray)
            {
                if (startMinimizedArg)
                {
                    _mainWindow.WindowState = WindowState.Minimized;
                }

                _mainWindow.Show();
            }

            if (!updateOptions.IsAutoCheckSuppressed)
            {
                _ = mainVm.RunStartupUpdateCheckAsync(_appLifetimeCts.Token);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(
        IServiceCollection services,
        string? customDataRoot,
        UpdateRuntimeOptions? updateOptions = null)
    {
        services.AddProxyDockInfrastructure(customDataRoot, updateOptions);

        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IconCacheService>();
        services.AddSingleton<AppNotificationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<AppLaunchCoordinator>();
        services.AddSingleton<TrayIconService>();

        services.AddSingleton<ApplicationsViewModel>();
        services.AddSingleton<ProxyProfilesViewModel>();
        services.AddSingleton<LaunchHistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
    }

    private void ApplyTrayAndShutdownMode(IClassicDesktopStyleApplicationLifetime desktop, bool trayEnabled)
    {
        if (Volatile.Read(ref _cleanupDone) != 0)
        {
            return;
        }

        _minimizeToTrayEnabled = trayEnabled;
        if (_mainWindow is not null)
        {
            _mainWindow.MinimizeToTrayOnClose = trayEnabled;
        }

        _trayService?.SetEnabled(trayEnabled);
        desktop.ShutdownMode = trayEnabled
            ? ShutdownMode.OnExplicitShutdown
            : ShutdownMode.OnMainWindowClose;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
        });
    }

    private void ShutdownCompletely(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowExit = true;
            _mainWindow.Close();
        }

        CleanupOnExit();
        desktop.Shutdown(0);
    }

    private void CleanupOnExit()
    {
        if (Interlocked.Exchange(ref _cleanupDone, 1) != 0)
        {
            return;
        }

        _appLifetimeCts.Cancel();
        try
        {
            _activationEvent?.Set();
        }
        catch
        {
            // Игнорируем при завершении
        }

        try
        {
            _activationListenerThread?.Join(TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            // Игнорируем при завершении
        }

        _activationEvent?.Dispose();
        _activationEvent = null;
        _trayService?.Dispose();
        _serviceProvider?.Dispose();
        _appLifetimeCts.Dispose();
    }

    private void StartActivationListener()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivationEventName);
            _activationListenerThread = new Thread(() =>
            {
                while (!_appLifetimeCts.IsCancellationRequested)
                {
                    try
                    {
                        if (_activationEvent.WaitOne())
                        {
                            if (_appLifetimeCts.IsCancellationRequested)
                            {
                                break;
                            }

                            ShowMainWindow();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ProxyDockActivationListener"
            };
            _activationListenerThread.Start();
        }
        catch
        {
            // Ошибка создания межпроцессного события не должна мешать запуску UI
        }
    }
}
