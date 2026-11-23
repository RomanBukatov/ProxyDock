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

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _kindFilterIndex; // 0 = All, 1 = Desktop, 2 = CLI

    [ObservableProperty]
    private bool _hasFilteredApps;

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
    }

    public ObservableCollection<AppCardViewModel> FilteredApps { get; } = [];

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnKindFilterIndexChanged(int value) => ApplyFilter();

    public async Task RefreshAsync()
    {
        var apps = await _appService.GetAllAsync().ConfigureAwait(false);
        var proxies = await _proxyService.GetAllAsync().ConfigureAwait(false);
        var defaultProxy = await _proxyService.GetDefaultAsync().ConfigureAwait(false);
        var byId = proxies.ToDictionary(p => p.Id);

        var cards = new List<AppCardViewModel>();
        foreach (var app in apps.OrderBy(a => a.LaunchKind).ThenBy(a => a.Name))
        {
            var resolved = _discovery.Resolve(app);
            var (proxyLabel, proxyStatus) = DashboardViewModel.ResolveProxySummary(app, byId, defaultProxy);
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
                LaunchAppAsync,
                EditAppAsync,
                DuplicateAppAsync,
                DeleteAppAsync,
                OpenFolder));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _allCards = cards;
            ApplyFilter();
        });
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
                || c.TargetDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (c.Profile.WorkingDirectory?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = query.ToList();
        FilteredApps.Clear();
        foreach (var item in list)
        {
            FilteredApps.Add(item);
        }

        HasFilteredApps = FilteredApps.Count > 0;
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
}
