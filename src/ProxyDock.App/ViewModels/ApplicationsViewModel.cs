using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.App.ViewModels;

public sealed partial class ApplicationsViewModel : ViewModelBase
{
    private readonly IApplicationProfileService _appService;
    private readonly IProxyProfileService _proxyService;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly IconCacheService _iconCache;
    private readonly IDialogService _dialogs;
    private readonly AppLaunchCoordinator _coordinator;
    private readonly AppNotificationService _notifications;
    private List<AppCardViewModel> _allCards = [];
    private bool _isUpdatingProxies;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _kindFilterIndex; // 0 = All, 1 = Desktop, 2 = CLI

    [ObservableProperty]
    private bool _hasAnyApps;

    [ObservableProperty]
    private bool _hasCliApps;

    [ObservableProperty]
    private bool _hasFilteredApps;

    [ObservableProperty]
    private bool _showNoSearchMatches;

    [ObservableProperty]
    private bool _hasProxies;

    [ObservableProperty]
    private DefaultProxyOptionViewModel? _selectedDefaultProxy;

    [ObservableProperty]
    private bool _hasDetectedApps;

    [ObservableProperty]
    private bool _hasMultipleDetectedApps;

    public ApplicationsViewModel(
        ILocalizationService loc,
        IApplicationProfileService appService,
        IProxyProfileService proxyService,
        IExecutableDiscoveryService discovery,
        IconCacheService iconCache,
        IDialogService dialogs,
        AppLaunchCoordinator coordinator,
        AppNotificationService notifications)
        : base(loc)
    {
        _appService = appService;
        _proxyService = proxyService;
        _discovery = discovery;
        _iconCache = iconCache;
        _dialogs = dialogs;
        _coordinator = coordinator;
        _notifications = notifications;

        _appService.ApplicationsChanged += (_, _) => _ = RefreshAsync();
        _proxyService.ProxiesChanged += (_, _) => _ = RefreshAsync();
        loc.PropertyChanged += (_, _) => _ = RefreshAsync();
    }

    public Action<string>? NavigateAction { get; set; }

    public ObservableCollection<AppCardViewModel> FilteredApps { get; } = [];
    public ObservableCollection<DefaultProxyOptionViewModel> AvailableProxies { get; } = [];
    public ObservableCollection<DetectedAppItemViewModel> DetectedApps { get; } = [];

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnKindFilterIndexChanged(int value) => ApplyFilter();

    partial void OnSelectedDefaultProxyChanged(DefaultProxyOptionViewModel? value)
    {
        if (_isUpdatingProxies || value is null)
        {
            return;
        }

        _ = SetDefaultProxyAsync(value.Id);
    }

    public async Task RefreshAsync()
    {
        var apps = await _appService.GetAllAsync().ConfigureAwait(false);
        var proxies = await _proxyService.GetAllAsync().ConfigureAwait(false);
        var defaultProxy = await _proxyService.GetDefaultAsync().ConfigureAwait(false);
        var byId = proxies.ToDictionary(p => p.Id);

        var orderedProxies = proxies
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .ToList();

        var proxyOptions = orderedProxies.Select(p => new DefaultProxyOptionViewModel(p)).ToList();

        var cards = new List<AppCardViewModel>();
        foreach (var app in apps.OrderBy(a => a.LaunchKind).ThenBy(a => a.Name))
        {
            var resolved = _discovery.Resolve(app);
            var (proxyLabel, proxyStatus) = ResolveProxySummary(app, byId, defaultProxy, Loc);
            var icon = app.LaunchKind == LaunchKind.Desktop
                ? _iconCache.GetIcon(resolved.ResolvedPath ?? app.ExecutableOrCommand, app.TemplateKey)
                : null;

            cards.Add(new AppCardViewModel(
                app,
                icon,
                proxyLabel,
                proxyStatus,
                resolved.Found,
                resolved.ResolvedPath,
                resolved.StackInfo,
                Loc,
                orderedProxies,
                defaultProxy,
                LaunchAppAsync,
                AssignProxyAsync,
                EditAppAsync,
                DuplicateAppAsync,
                DeleteAppAsync,
                OpenFolder));
        }

        var unaddedDetected = FilterUnaddedDetectedTemplates(apps, _discovery.DiscoverTemplate);
        var detectedItems = new List<DetectedAppItemViewModel>();
        foreach (var (tpl, resolvedPath) in unaddedDetected)
        {
            var icon = _iconCache.GetIcon(resolvedPath, tpl.Key);
            detectedItems.Add(new DetectedAppItemViewModel(
                tpl.Key,
                tpl.DisplayName,
                icon,
                Loc,
                item => _coordinator.AddFromTemplateAsync(item.Key)));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _isUpdatingProxies = true;
            try
            {
                AvailableProxies.Clear();
                foreach (var option in proxyOptions)
                {
                    AvailableProxies.Add(option);
                }

                HasProxies = AvailableProxies.Count > 0;
                SelectedDefaultProxy = defaultProxy is not null
                    ? AvailableProxies.FirstOrDefault(o => o.Id == defaultProxy.Id)
                    : AvailableProxies.FirstOrDefault();
            }
            finally
            {
                _isUpdatingProxies = false;
            }

            DetectedApps.Clear();
            foreach (var detected in detectedItems)
            {
                DetectedApps.Add(detected);
            }

            HasDetectedApps = DetectedApps.Count > 0;
            HasMultipleDetectedApps = DetectedApps.Count > 1;

            _allCards = cards;
            HasAnyApps = _allCards.Count > 0;
            HasCliApps = _allCards.Any(c => c.IsCli);
            if (!HasCliApps && KindFilterIndex != 0)
            {
                KindFilterIndex = 0;
            }

            ApplyFilter();
        });
    }

    public static IReadOnlyList<(BuiltInAppTemplates.TemplateDescriptor Template, string ResolvedPath)> FilterUnaddedDetectedTemplates(
        IReadOnlyList<ApplicationProfile> existingApps,
        Func<string, ExecutableDiscoveryResult> discoverTemplate)
    {
        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in existingApps)
        {
            if (string.IsNullOrWhiteSpace(app.ExecutableOrCommand))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(app.ExecutableOrCommand.Trim().Trim('"'));
            try
            {
                if (Path.IsPathRooted(expanded))
                {
                    existingPaths.Add(Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar));
                }
            }
            catch
            {
                // Игнорируем некорректные пути при сопоставлении
            }
        }

        var result = new List<(BuiltInAppTemplates.TemplateDescriptor Template, string ResolvedPath)>();
        foreach (var tpl in BuiltInAppTemplates.DesktopTemplates)
        {
            if (existingApps.Any(a => string.Equals(a.TemplateKey, tpl.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var probe = discoverTemplate(tpl.Key);
            if (!probe.Found || string.IsNullOrWhiteSpace(probe.ResolvedPath))
            {
                continue;
            }

            var normalizedResolved = probe.ResolvedPath.Trim();
            try
            {
                normalizedResolved = Path.GetFullPath(normalizedResolved).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch
            {
                // Оставляем исходный путь
            }

            if (existingPaths.Contains(normalizedResolved))
            {
                continue;
            }

            result.Add((tpl, probe.ResolvedPath));
        }

        return result;
    }

    private void ApplyFilter()
    {
        IEnumerable<AppCardViewModel> query = _allCards;

        if (KindFilterIndex == 1)
        {
            query = query.Where(c => c.IsDesktop);
        }
        else if (KindFilterIndex == 2)
        {
            query = query.Where(c => c.IsCli);
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            query = query.Where(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.AssignedProxyLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.TargetDisplay.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = query.ToList();
        FilteredApps.Clear();
        foreach (var item in list)
        {
            FilteredApps.Add(item);
        }

        HasFilteredApps = FilteredApps.Count > 0;
        ShowNoSearchMatches = HasAnyApps && !HasFilteredApps;
    }

    private async Task SetDefaultProxyAsync(Guid proxyId)
    {
        await _proxyService.SetDefaultAsync(proxyId);
    }

    [RelayCommand]
    private void GoToProxies() => NavigateAction?.Invoke("Proxies");

    [RelayCommand]
    private async Task AddNewProxyAsync()
    {
        var saved = await _dialogs.ShowProxyEditorAsync(null);
        if (saved is not null)
        {
            _notifications.Show("Прокси добавлен", $"Профиль «{saved.Name}» ({saved.Host}:{saved.Port}) сохранён.", NotificationSeverity.Success);
        }
    }

    [RelayCommand]
    private async Task AddDesktopApplicationAsync()
    {
        var created = await _dialogs.ShowApplicationEditorAsync(null);
        if (created is not null)
        {
            await _appService.SaveAsync(created);
            _notifications.Show("Приложение добавлено", $"Профиль «{created.Name}» сохранён.", NotificationSeverity.Success);
        }
    }

    [RelayCommand]
    private async Task AddCliApplicationAsync()
    {
        var draft = new ApplicationProfile
        {
            LaunchKind = LaunchKind.Cli,
            LaunchInTerminal = true,
            KeepTerminalOpen = true
        };
        var created = await _dialogs.ShowApplicationEditorAsync(draft);
        if (created is not null)
        {
            await _appService.SaveAsync(created);
            _notifications.Show("CLI-профиль добавлен", $"Профиль «{created.Name}» сохранён.", NotificationSeverity.Success);
        }
    }

    [RelayCommand]
    private Task AddAllDetectedDesktopAsync() => _coordinator.AddAllDetectedDesktopTemplatesAsync();

    [RelayCommand]
    private Task AddAntigravityTemplateAsync() => _coordinator.AddFromTemplateAsync(BuiltInAppTemplates.GoogleAntigravityKey);

    [RelayCommand]
    private Task AddClaudeDesktopTemplateAsync() => _coordinator.AddFromTemplateAsync(BuiltInAppTemplates.ClaudeDesktopKey);

    [RelayCommand]
    private Task AddCodexDesktopTemplateAsync() => _coordinator.AddFromTemplateAsync(BuiltInAppTemplates.CodexDesktopKey);

    [RelayCommand]
    private Task AddClaudeCliTemplateAsync() => _coordinator.AddFromTemplateAsync(BuiltInAppTemplates.ClaudeCodeCliKey);

    [RelayCommand]
    private Task AddCodexCliTemplateAsync() => _coordinator.AddFromTemplateAsync(BuiltInAppTemplates.OpenAiCodexCliKey);

    private Task LaunchAppAsync(AppCardViewModel card, bool direct)
        => _coordinator.LaunchWithPromptsAsync(card.Profile, direct);

    private async Task AssignProxyAsync(AppCardViewModel card, ProxyAssignmentMode mode, Guid? proxyId)
    {
        card.Profile.ProxyMode = mode;
        card.Profile.ProxyProfileId = mode == ProxyAssignmentMode.Specific ? proxyId : null;
        await _appService.SaveAsync(card.Profile);
    }

    private async Task EditAppAsync(AppCardViewModel card)
    {
        var edited = await _dialogs.ShowApplicationEditorAsync(card.Profile);
        if (edited is not null)
        {
            await _appService.SaveAsync(edited);
            _notifications.Show("Профиль обновлён", $"Изменения для «{edited.Name}» сохранены.", NotificationSeverity.Success);
        }
    }

    private async Task DuplicateAppAsync(AppCardViewModel card)
    {
        var copy = await _appService.DuplicateAsync(card.Profile.Id);
        _notifications.Show("Профиль продублирован", $"Создана копия «{copy.Name}».", NotificationSeverity.Info);
    }

    private async Task DeleteAppAsync(AppCardViewModel card)
    {
        var ok = await _dialogs.ShowConfirmationAsync(
            "Удалить приложение",
            $"Вы уверены, что хотите удалить профиль «{card.Name}»?",
            "Удалить",
            "Отмена",
            isDanger: true);
        if (ok)
        {
            await _appService.DeleteAsync(card.Profile.Id);
            _notifications.Show("Профиль удалён", $"Профиль «{card.Name}» удалён.", NotificationSeverity.Info);
        }
    }

    private void OpenFolder(AppCardViewModel card)
    {
        var folder = card.Profile.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(card.ResolvedPath))
        {
            folder = Path.GetDirectoryName(card.ResolvedPath);
        }

        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        else
        {
            _notifications.Show("Папка не найдена", "Не удалось определить существующую папку для открытия.", NotificationSeverity.Warning);
        }
    }

    internal static (string Label, ProxyCheckStatus Status) ResolveProxySummary(
        ApplicationProfile app,
        IReadOnlyDictionary<Guid, ProxyProfile> byId,
        ProxyProfile? defaultProxy,
        ILocalizationService loc)
    {
        if (app.ProxyMode == ProxyAssignmentMode.DirectNoProxy)
        {
            return (loc["ProxyModeDirect"], ProxyCheckStatus.Unknown);
        }

        if (app.ProxyMode == ProxyAssignmentMode.Specific)
        {
            if (app.ProxyProfileId is Guid pid && byId.TryGetValue(pid, out var matched))
            {
                return ($"{matched.Name} ({ProxyUriBuilder.FormatHost(matched.Host)}:{matched.Port})", matched.LastCheckStatus);
            }

            return ("Прокси не назначен / удалён", ProxyCheckStatus.Invalid);
        }

        if (defaultProxy is not null)
        {
            return ($"{loc["ProxyModeDefault"]} · {defaultProxy.Name} ({ProxyUriBuilder.FormatHost(defaultProxy.Host)}:{defaultProxy.Port})", defaultProxy.LastCheckStatus);
        }

        return ($"{loc["ProxyModeDefault"]}: {loc["NoDefaultProxy"]}", ProxyCheckStatus.Invalid);
    }
}
