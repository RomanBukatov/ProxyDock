using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Infrastructure.Logging;

namespace ProxyDock.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IProxyProfileService _proxyService;
    private readonly IStartupManager _startupManager;
    private readonly IPathProvider _paths;
    private readonly IConfigExportService _exportService;
    private readonly ThemeService _themeService;
    private readonly FileLoggerProvider _loggerProvider;
    private readonly IDialogService _dialogs;
    private readonly AppNotificationService _notifications;
    private bool _isLoading;

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private int _languageIndex;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _runAtWindowsStartup;

    [ObservableProperty]
    private ProxyProfile? _selectedDefaultProxy;

    [ObservableProperty]
    private int _defaultTerminalIndex;

    [ObservableProperty]
    private string _defaultCliWorkingDirectory = string.Empty;

    [ObservableProperty]
    private int _logLevelIndex = 2; // Information

    public SettingsViewModel(
        ILocalizationService loc,
        ISettingsService settingsService,
        IProxyProfileService proxyService,
        IStartupManager startupManager,
        IPathProvider paths,
        IConfigExportService exportService,
        ThemeService themeService,
        FileLoggerProvider loggerProvider,
        IDialogService dialogs,
        AppNotificationService notifications)
        : base(loc)
    {
        _settingsService = settingsService;
        _proxyService = proxyService;
        _startupManager = startupManager;
        _paths = paths;
        _exportService = exportService;
        _themeService = themeService;
        _loggerProvider = loggerProvider;
        _dialogs = dialogs;
        _notifications = notifications;

        _proxyService.ProxiesChanged += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<ProxyProfile> Proxies { get; } = [];
    public bool IsStartupSupported => _startupManager.IsSupported;

    public async Task RefreshAsync()
    {
        _isLoading = true;
        try
        {
            var settings = await _settingsService.GetAsync();
            var proxies = await _proxyService.GetAllAsync();

            Proxies.Clear();
            foreach (var p in proxies)
            {
                Proxies.Add(p);
            }

            ThemeIndex = settings.Theme switch
            {
                AppThemeMode.Dark => 0,
                AppThemeMode.Light => 1,
                _ => 2
            };
            LanguageIndex = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            MinimizeToTray = settings.MinimizeToTray;
            StartMinimized = settings.StartMinimized;
            RunAtWindowsStartup = _startupManager.IsSupported ? _startupManager.IsEnabled() : settings.RunAtWindowsStartup;
            SelectedDefaultProxy = Proxies.FirstOrDefault(p => p.Id == settings.DefaultProxyId)
                ?? Proxies.FirstOrDefault(p => p.IsDefault);
            DefaultTerminalIndex = settings.DefaultTerminal switch
            {
                TerminalPreference.WindowsTerminal => 0,
                TerminalPreference.PowerShell => 1,
                _ => 2
            };
            DefaultCliWorkingDirectory = settings.DefaultCliWorkingDirectory ?? string.Empty;
            LogLevelIndex = settings.MinimumLogLevel switch
            {
                "Trace" => 0,
                "Debug" => 1,
                "Information" => 2,
                "Warning" => 3,
                "Error" => 4,
                _ => 2
            };
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (!_isLoading)
        {
            var mode = value switch { 1 => AppThemeMode.Light, 2 => AppThemeMode.System, _ => AppThemeMode.Dark };
            _themeService.ApplyTheme(mode);
            _ = SaveCurrentSettingsAsync();
        }
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (!_isLoading)
        {
            var lang = value == 1 ? "en" : "ru";
            Loc.SetLanguage(lang);
            _ = SaveCurrentSettingsAsync();
        }
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (!_isLoading)
        {
            if (!value && StartMinimized)
            {
                StartMinimized = false;
            }

            _ = SaveCurrentSettingsAsync();
        }
    }
    partial void OnStartMinimizedChanged(bool value) { if (!_isLoading) _ = SaveCurrentSettingsAsync(); }
    partial void OnDefaultTerminalIndexChanged(int value) { if (!_isLoading) _ = SaveCurrentSettingsAsync(); }
    partial void OnLogLevelIndexChanged(int value) { if (!_isLoading) _ = SaveCurrentSettingsAsync(); }

    partial void OnRunAtWindowsStartupChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            _startupManager.SetEnabled(value);
            _ = SaveCurrentSettingsAsync();
        }
        catch (Exception ex)
        {
            _notifications.Show("Ошибка настройки автозапуска", ex.Message, NotificationSeverity.Error);
        }
    }

    partial void OnSelectedDefaultProxyChanged(ProxyProfile? value)
    {
        if (_isLoading || value is null)
        {
            return;
        }

        _ = SetDefaultProxyAsync(value.Id);
    }

    private async Task SetDefaultProxyAsync(Guid proxyId)
    {
        await _proxyService.SetDefaultAsync(proxyId);
        await SaveCurrentSettingsAsync();
    }

    [RelayCommand]
    private async Task BrowseDefaultCliDirectoryAsync()
    {
        var dir = await _dialogs.PickFolderAsync("Выберите папку по умолчанию для CLI", DefaultCliWorkingDirectory);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            DefaultCliWorkingDirectory = dir;
            await SaveCurrentSettingsAsync();
        }
    }

    [RelayCommand]
    private Task SaveCliDirectoryAsync() => SaveCurrentSettingsAsync();

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenConfigFolder() => OpenFolder(_paths.ConfigDirectory);

    [RelayCommand]
    private async Task ExportConfigurationAsync()
    {
        var path = await _dialogs.PickExportFileAsync($"proxydock-config-{DateTime.Now:yyyyMMdd}.json");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await _exportService.ExportAsync(path);
        _notifications.Show(
            "Конфигурация экспортирована",
            $"Файл сохранён: {path}. Пароли прокси и DPAPI-секреты в экспорт не включались.",
            NotificationSeverity.Success);
    }

    [RelayCommand]
    private async Task ImportConfigurationAsync()
    {
        var path = await _dialogs.PickImportFileAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var warnings = await _exportService.ImportAsync(path, replaceExisting: false);
            await RefreshAsync();
            var msg = warnings.Count == 0
                ? "Конфигурация успешно импортирована."
                : $"Импорт выполнен с примечаниями:\n{string.Join("\n", warnings)}";
            _notifications.Show("Импорт завершён", msg, warnings.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            _notifications.Show("Ошибка импорта", ex.Message, NotificationSeverity.Error);
        }
    }

    private async Task SaveCurrentSettingsAsync()
    {
        var settings = await _settingsService.GetAsync();
        settings.Theme = ThemeIndex switch { 1 => AppThemeMode.Light, 2 => AppThemeMode.System, _ => AppThemeMode.Dark };
        settings.Language = LanguageIndex == 1 ? "en" : "ru";
        settings.MinimizeToTray = MinimizeToTray;
        settings.StartMinimized = StartMinimized;
        settings.RunAtWindowsStartup = RunAtWindowsStartup;
        settings.DefaultProxyId = SelectedDefaultProxy?.Id;
        settings.DefaultTerminal = DefaultTerminalIndex switch
        {
            1 => TerminalPreference.PowerShell,
            2 => TerminalPreference.PowerShellCore,
            _ => TerminalPreference.WindowsTerminal
        };
        settings.DefaultCliWorkingDirectory = string.IsNullOrWhiteSpace(DefaultCliWorkingDirectory)
            ? null
            : DefaultCliWorkingDirectory.Trim();
        settings.MinimumLogLevel = LogLevelIndex switch
        {
            0 => "Trace",
            1 => "Debug",
            3 => "Warning",
            4 => "Error",
            _ => "Information"
        };

        _loggerProvider.SetMinimumLevel(settings.MinimumLogLevel);
        await _settingsService.SaveAsync(settings);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
