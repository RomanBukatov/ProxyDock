using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.App.Services;

/// <summary>
/// Управляет необязательной иконкой в системном трее и быстрым запуском сохранённых программ.
/// По умолчанию иконка не создаётся, а полный выход завершает только ProxyDock без влияния на запущенные приложения.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IApplicationProfileService _appService;
    private readonly IApplicationLauncher _launcher;
    private readonly ILocalizationService _loc;
    private Application? _app;
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

    public bool IsEnabled => _trayIcon is not null;

    public void Configure(
        Application app,
        Action showMainWindow,
        Action openSettings,
        Action exitApp,
        Action<ApplicationProfile, LaunchResult>? onLaunchResult = null)
    {
        _app = app;
        _showMainWindowAction = showMainWindow;
        _openSettingsAction = openSettings;
        _exitAction = exitApp;
        _launchNotificationAction = onLaunchResult;
    }

    public void SetEnabled(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetEnabled(enabled));
            return;
        }

        if (enabled)
        {
            if (_app is null || _trayIcon is not null)
            {
                return;
            }

            using var iconStream = AssetLoader.Open(new Uri("avares://ProxyDock/Assets/proxydock.ico"));
            _windowIcon = new WindowIcon(iconStream);

            _trayIcon = new TrayIcon
            {
                Icon = _windowIcon,
                ToolTipText = "ProxyDock",
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => _showMainWindowAction?.Invoke();

            TrayIcon.SetIcons(_app, [_trayIcon]);
            _ = RefreshMenuAsync();
            return;
        }

        RemoveTrayIcon();
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

    private void RemoveTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.IsVisible = false;
            if (_app is not null)
            {
                TrayIcon.SetIcons(_app, new TrayIcons());
            }

            _trayIcon.Dispose();
        }
        catch
        {
            // Ошибка очистки иконки трея при завершении приложения не должна прерывать выход
        }
        finally
        {
            _trayIcon = null;
            _windowIcon = null;
        }
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        if (_trayIcon is not null)
        {
            _ = RefreshMenuAsync();
        }
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _ = RefreshMenuAsync();
        }
    }

    public void Dispose()
    {
        _appService.ApplicationsChanged -= OnDataChanged;
        _loc.PropertyChanged -= OnLocalizationChanged;

        if (_trayIcon is not null)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                RemoveTrayIcon();
            }
            else
            {
                try
                {
                    _trayIcon.IsVisible = false;
                    _trayIcon.Dispose();
                }
                catch
                {
                    // Игнорируем при завершении из не-UI потока
                }
                finally
                {
                    _trayIcon = null;
                    _windowIcon = null;
                }
            }
        }
    }
}
