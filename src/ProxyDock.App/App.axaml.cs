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

            var services = new ServiceCollection();
            ConfigureServices(services);
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
            _mainWindow = new MainWindow(settingsService)
            {
                DataContext = mainVm
            };

            var dialogService = _serviceProvider.GetRequiredService<DialogService>();
            dialogService.AttachOwner(_mainWindow);

            _trayService = _serviceProvider.GetRequiredService<TrayIconService>();
            var coordinator = _serviceProvider.GetRequiredService<AppLaunchCoordinator>();

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => CleanupOnExit();

            _trayService.Initialize(
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

            desktop.MainWindow = _mainWindow;
            await mainVm.InitializeAsync();

            if (!verifyStartup)
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

            if (!startMinimizedArg && !settings.StartMinimized)
            {
                _mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddProxyDockInfrastructure();

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
        services.AddSingleton<Func<Action<string>, DashboardViewModel>>(sp => navigate => new DashboardViewModel(
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IApplicationProfileService>(),
            sp.GetRequiredService<IProxyProfileService>(),
            sp.GetRequiredService<ILaunchHistoryRepository>(),
            sp.GetRequiredService<IExecutableDiscoveryService>(),
            sp.GetRequiredService<IconCacheService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<AppLaunchCoordinator>(),
            sp.GetRequiredService<AppNotificationService>(),
            sp.GetRequiredService<IProxyConnectivityTester>(),
            navigate));
        services.AddSingleton<MainWindowViewModel>();
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
        if (_appLifetimeCts.IsCancellationRequested)
        {
            return;
        }

        _appLifetimeCts.Cancel();
        _activationEvent?.Set();
        _trayService?.Dispose();
        _serviceProvider?.Dispose();
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
                    if (_activationEvent.WaitOne())
                    {
                        if (_appLifetimeCts.IsCancellationRequested)
                        {
                            break;
                        }

                        ShowMainWindow();
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
