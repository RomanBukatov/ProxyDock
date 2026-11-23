using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.App.Services;

/// <summary>
/// Управляет иконкой в системном трее и быстрым запуском сохранённых программ.
/// Полный выход завершает только ProxyDock и не затрагивает запущенные приложения.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IApplicationProfileService _appService;
    private readonly IApplicationLauncher _launcher;
    private readonly ILocalizationService _loc;
    private TrayIcon? _trayIcon;
    private WindowIcon? _windowIcon;
    private Action? _showMainWindowAction;
    private Action? _openSettingsAction;
    private Action? _exitAction;
    private Action<ApplicationProfile, LaunchResult>? _launchNotificationAction;

    public TrayIconService(
        IApplicationProfileService appService,
        IApplicationLauncher launcher,
        ILocalizationService loc)
    {
        _appService = appService;
        _launcher = launcher;
        _loc = loc;
        _appService.ApplicationsChanged += OnDataChanged;
        _loc.PropertyChanged += OnLocalizationChanged;
    }

    public void Initialize(
        Application app,
        Action showMainWindow,
        Action openSettings,
        Action exitApp,
        Action<ApplicationProfile, LaunchResult>? onLaunchResult = null)
    {
        _showMainWindowAction = showMainWindow;
        _openSettingsAction = openSettings;
        _exitAction = exitApp;
        _launchNotificationAction = onLaunchResult;

        using var iconStream = AssetLoader.Open(new Uri("avares://ProxyDock/Assets/proxydock.ico"));
        _windowIcon = new WindowIcon(iconStream);

        _trayIcon = new TrayIcon
        {
            Icon = _windowIcon,
            ToolTipText = "ProxyDock — Desktop Proxy Launcher",
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => _showMainWindowAction?.Invoke();

        TrayIcon.SetIcons(app, [_trayIcon]);
        _ = RefreshMenuAsync();
    }

    public async Task RefreshMenuAsync()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var apps = await _appService.GetAllAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_trayIcon is null)
            {
                return;
            }

            var menu = new NativeMenu();

            var openItem = new NativeMenuItem(_loc["TrayOpen"]);
            openItem.Click += (_, _) => _showMainWindowAction?.Invoke();
            menu.Add(openItem);

            menu.Add(new NativeMenuItemSeparator());

            var quickLaunchRoot = new NativeMenuItem(_loc["TrayQuickLaunch"]);
            var subMenu = new NativeMenu();
            if (apps.Count == 0)
            {
                subMenu.Add(new NativeMenuItem(_loc["TrayEmpty"]) { IsEnabled = false });
            }
            else
            {
                foreach (var app in apps.OrderBy(a => a.LaunchKind).ThenBy(a => a.Name))
                {
                    var captured = app;
                    var item = new NativeMenuItem(captured.Name);
                    item.Click += async (_, _) =>
                    {
                        var result = await _launcher.LaunchAsync(new LaunchRequest { Application = captured }).ConfigureAwait(false);
                        Dispatcher.UIThread.Post(() =>
                        {
                            _launchNotificationAction?.Invoke(captured, result);
                            if (result.RequiresExistingProcessConfirmation)
                            {
                                _showMainWindowAction?.Invoke();
                            }
                        });
                    };
                    subMenu.Add(item);
                }
            }

            quickLaunchRoot.Menu = subMenu;
            menu.Add(quickLaunchRoot);

            var settingsItem = new NativeMenuItem(_loc["TraySettings"]);
            settingsItem.Click += (_, _) => _openSettingsAction?.Invoke();
            menu.Add(settingsItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem(_loc["TrayExit"]);
            exitItem.Click += (_, _) => _exitAction?.Invoke();
            menu.Add(exitItem);

            _trayIcon.Menu = menu;
        });
    }

    private void OnDataChanged(object? sender, EventArgs e) => _ = RefreshMenuAsync();
    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _ = RefreshMenuAsync();

    public void Dispose()
    {
        _appService.ApplicationsChanged -= OnDataChanged;
        _loc.PropertyChanged -= OnLocalizationChanged;

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _windowIcon = null;
    }
}
