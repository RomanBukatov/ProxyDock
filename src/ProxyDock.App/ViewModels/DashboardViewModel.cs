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

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IApplicationProfileService _appService;
    private readonly IProxyProfileService _proxyService;
    private readonly ILaunchHistoryRepository _historyRepo;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly IconCacheService _iconCache;
    private readonly IDialogService _dialogs;
    private readonly AppLaunchCoordinator _coordinator;
    private readonly AppNotificationService _notifications;
    private readonly IProxyConnectivityTester _proxyTester;
    private readonly Action<string> _navigateAction;

    [ObservableProperty]
    private int _applicationCount;

    [ObservableProperty]
    private int _proxyCount;

    [ObservableProperty]
    private int _historyCount;

    [ObservableProperty]
    private bool _hasApps;

    [ObservableProperty]
    private bool _hasProxies;

    [ObservableProperty]
    private bool _hasHistory;

    public DashboardViewModel(
        ILocalizationService loc,
        IApplicationProfileService appService,
        IProxyProfileService proxyService,
        ILaunchHistoryRepository historyRepo,
        IExecutableDiscoveryService discovery,
        IconCacheService iconCache,
        IDialogService dialogs,
        AppLaunchCoordinator coordinator,
        AppNotificationService notifications,
        IProxyConnectivityTester proxyTester,
        Action<string> navigateAction)
        : base(loc)
    {
        _appService = appService;
        _proxyService = proxyService;
        _historyRepo = historyRepo;
        _discovery = discovery;
        _iconCache = iconCache;
        _dialogs = dialogs;
        _coordinator = coordinator;
        _notifications = notifications;
        _proxyTester = proxyTester;
        _navigateAction = navigateAction;

        _appService.ApplicationsChanged += (_, _) => _ = RefreshAsync();
        _proxyService.ProxiesChanged += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<TemplateCardViewModel> DesktopTemplates { get; } = [];
    public ObservableCollection<AppCardViewModel> QuickLaunchApps { get; } = [];
    public ObservableCollection<ProxyCardViewModel> ProxyStatuses { get; } = [];
    public ObservableCollection<HistoryItemViewModel> RecentLaunches { get; } = [];

    public async Task RefreshAsync()
    {
        var apps = await _appService.GetAllAsync().ConfigureAwait(false);
        var proxies = await _proxyService.GetAllAsync().ConfigureAwait(false);
        var defaultProxy = await _proxyService.GetDefaultAsync().ConfigureAwait(false);
        var history = await _historyRepo.GetAllAsync().ConfigureAwait(false);

        var templateCards = new List<TemplateCardViewModel>();
        foreach (var tpl in BuiltInAppTemplates.DesktopTemplates)
        {
            var probe = _discovery.DiscoverTemplate(tpl.Key);
            var alreadyAdded = apps.Any(a => string.Equals(a.TemplateKey, tpl.Key, StringComparison.OrdinalIgnoreCase));
            var defaultProfile = tpl.CreateProfile(probe.Found ? probe.ResolvedPath : null);
            var applicability = probe.StackInfo?.ApplicabilityLevel ?? defaultProfile.ApplicabilityHint ?? ProxyApplicabilityLevel.NeedsVerification;
            var stackSummary = probe.StackInfo?.Summary ?? defaultProfile.CompatibilityNote ?? tpl.Subtitle;
            var icon = probe.Found ? _iconCache.GetIcon(probe.ResolvedPath, tpl.Key) : null;

            templateCards.Add(new TemplateCardViewModel(
                tpl.Key,
                tpl.DisplayName,
                tpl.Subtitle,
                probe.Found,
                probe.ResolvedPath,
                stackSummary,
                applicability,
                alreadyAdded,
                icon,
                t => _coordinator.AddFromTemplateAsync(t.Key)));
        }

        var proxyById = proxies.ToDictionary(p => p.Id);
        var appCards = new List<AppCardViewModel>();
        foreach (var app in apps.OrderBy(a => a.LaunchKind).ThenBy(a => a.Name).Take(8))
        {
            var resolved = _discovery.Resolve(app);
            var (proxyLabel, proxyStatus) = ResolveProxySummary(app, proxyById, defaultProxy);
            var icon = app.LaunchKind == LaunchKind.Desktop
                ? _iconCache.GetIcon(resolved.ResolvedPath ?? app.ExecutableOrCommand, app.TemplateKey)
                : null;

            appCards.Add(new AppCardViewModel(
                app,
                icon,
                proxyLabel,
                proxyStatus,
                resolved.Found,
                resolved.ResolvedPath,
                resolved.StackInfo,
                (card, direct) => LaunchFromDashboardAsync(card, direct),
                EditAppAsync,
                DuplicateAppAsync,
                DeleteAppAsync,
                OpenFolder));
        }

        var proxyCards = proxies.Select(p => new ProxyCardViewModel(
            p,
            TestProxyAsync,
            EditProxyAsync,
            async c => await _proxyService.DuplicateAsync(c.Profile.Id),
            async c => await _proxyService.SetDefaultAsync(c.Profile.Id),
            async _ => { })).ToList();

        var recent = history.Take(5).Select(h => new HistoryItemViewModel(h)).ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplicationCount = apps.Count;
            ProxyCount = proxies.Count;
            HistoryCount = history.Count;
            HasApps = apps.Count > 0;
            HasProxies = proxies.Count > 0;
            HasHistory = recent.Count > 0;

            DesktopTemplates.Clear();
            foreach (var c in templateCards) DesktopTemplates.Add(c);

            QuickLaunchApps.Clear();
            foreach (var c in appCards) QuickLaunchApps.Add(c);

            ProxyStatuses.Clear();
            foreach (var c in proxyCards) ProxyStatuses.Add(c);

            RecentLaunches.Clear();
            foreach (var h in recent) RecentLaunches.Add(h);
        });
    }

    private async Task LaunchFromDashboardAsync(AppCardViewModel card, bool direct)
    {
        await _coordinator.LaunchWithPromptsAsync(card.Profile, direct);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task AddNewApplicationAsync()
    {
        var created = await _dialogs.ShowApplicationEditorAsync(null);
        if (created is not null)
        {
            await _appService.SaveAsync(created);
            _notifications.Show("Приложение добавлено", $"Профиль «{created.Name}» сохранён.", NotificationSeverity.Success);
        }
    }

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
    private Task AddAllDetectedDesktopAsync() => _coordinator.AddAllDetectedDesktopTemplatesAsync();

    [RelayCommand]
    private void GoToApplications() => _navigateAction("Applications");

    [RelayCommand]
    private void GoToProxies() => _navigateAction("Proxies");

    [RelayCommand]
    private void GoToHistory() => _navigateAction("History");

    private async Task EditAppAsync(AppCardViewModel card)
    {
        var edited = await _dialogs.ShowApplicationEditorAsync(card.Profile);
        if (edited is not null)
        {
            await _appService.SaveAsync(edited);
        }
    }

    private async Task DuplicateAppAsync(AppCardViewModel card)
    {
        await _appService.DuplicateAsync(card.Profile.Id);
    }

    private async Task DeleteAppAsync(AppCardViewModel card)
    {
        var confirmed = await _dialogs.ShowConfirmationAsync(
            "Удаление приложения",
            $"Удалить профиль «{card.Name}»?",
            "Удалить",
            "Отмена",
            isDanger: true);
        if (confirmed)
        {
            await _appService.DeleteAsync(card.Profile.Id);
        }
    }

    private static void OpenFolder(AppCardViewModel card)
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
    }

    private async Task TestProxyAsync(ProxyCardViewModel card)
    {
        if (card.IsTesting)
        {
            return;
        }

        card.IsTesting = true;
        try
        {
            var result = await _proxyTester.TestAsync(card.Profile);
            await _proxyService.UpdateCheckResultAsync(card.Profile.Id, result);
        }
        finally
        {
            card.IsTesting = false;
        }
    }

    private Task EditProxyAsync(ProxyCardViewModel card)
    {
        _navigateAction("Proxies");
        return Task.CompletedTask;
    }

    internal static (string Label, ProxyCheckStatus Status) ResolveProxySummary(
        ApplicationProfile app,
        IReadOnlyDictionary<Guid, ProxyProfile> byId,
        ProxyProfile? defaultProxy)
    {
        if (app.ProxyMode == ProxyAssignmentMode.DirectNoProxy)
        {
            return ("Без прокси", ProxyCheckStatus.Unknown);
        }

        if (app.ProxyMode == ProxyAssignmentMode.Specific)
        {
            if (app.ProxyProfileId is Guid pid && byId.TryGetValue(pid, out var matched))
            {
                return ($"{matched.Name} ({matched.Host}:{matched.Port})", matched.LastCheckStatus);
            }

            return ("Прокси не назначен / удалён", ProxyCheckStatus.Invalid);
        }

        if (defaultProxy is not null)
        {
            return ($"По умолчанию: {defaultProxy.Name}", defaultProxy.LastCheckStatus);
        }

        return ("Прокси по умолчанию не настроен", ProxyCheckStatus.Invalid);
    }
}
