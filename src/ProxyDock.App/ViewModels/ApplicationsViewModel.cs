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
    private readonly IProcessTracker? _tracker;
    private readonly SemaphoreSlim _stateRefreshGate = new(1, 1);
    private readonly HashSet<string> _busyTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inspectionLock = new();
    private readonly Dictionary<Guid, RunningAppInspection> _lastInspections = new();

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
        AppNotificationService notifications,
        IProcessTracker? tracker = null)
        : base(loc)
    {
        _appService = appService;
        _proxyService = proxyService;
        _discovery = discovery;
        _iconCache = iconCache;
        _dialogs = dialogs;
        _coordinator = coordinator;
        _notifications = notifications;
        _tracker = tracker;

        _appService.ApplicationsChanged += (_, _) => _ = RefreshAsync();
        _proxyService.ProxiesChanged += (_, _) => _ = RefreshAsync();
        loc.PropertyChanged += (_, _) => _ = RefreshAsync();

        if (Avalonia.Application.Current is { } app)
        {
            app.ActualThemeVariantChanged += (_, _) => _ = RefreshAsync();
        }

        AddTile = new AddAppCardTileViewModel(this);
        GridItems.Add(AddTile);
    }

    public Action<string>? NavigateAction { get; set; }
    public AddAppCardTileViewModel AddTile { get; }

    public ObservableCollection<AppCardViewModel> FilteredApps { get; } = [];
    public ObservableCollection<object> GridItems { get; } = [];
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
        bool? preferLight = null;
        if (Avalonia.Application.Current is { } currentApp)
        {
            preferLight = Dispatcher.UIThread.CheckAccess()
                ? currentApp.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light
                : await Dispatcher.UIThread.InvokeAsync(() => currentApp.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light);
        }

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
                ? _iconCache.GetIcon(resolved.ResolvedPath ?? app.ExecutableOrCommand, app.TemplateKey, preferLightThemeOverride: preferLight)
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
                ShowDiagnosticsAsync,
                OpenFolder) { ManageAsync = ManageAppAsync });
        }

        var unaddedDetected = FilterUnaddedDetectedTemplates(apps, _discovery.DiscoverTemplate);
        var detectedItems = new List<DetectedAppItemViewModel>();
        foreach (var (tpl, resolvedPath) in unaddedDetected)
        {
            var icon = _iconCache.GetIcon(resolvedPath, tpl.Key, preferLightThemeOverride: preferLight);
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

            var previousCards = _allCards.ToDictionary(c => c.Profile.Id);
            foreach (var card in cards)
            {
                card.IsBusy = _busyTargets.Contains(OperationKey(card));
                if (previousCards.TryGetValue(card.Profile.Id, out var previous) &&
                    string.Equals(previous.ResolvedPath, card.ResolvedPath, StringComparison.OrdinalIgnoreCase) &&
                    previous.Profile.LaunchKind == card.Profile.LaunchKind)
                    card.State = previous.State;
            }
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

    public bool IsRefreshingStates => _stateRefreshGate.CurrentCount == 0;

    public async Task RefreshStatesAsync(CancellationToken cancellationToken = default)
    {
        if (_tracker is null) return;
        await _stateRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var cards = _allCards.ToList();
            foreach (var card in cards)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!card.IsDesktop || card.ResolvedPath is null || card.IsBusy) continue;
                var inspection = await Task.Run(() => Inspect(card), cancellationToken);
                if (!cancellationToken.IsCancellationRequested && !card.IsBusy)
                    ApplyState(card, inspection);
            }
        }
        finally
        {
            _stateRefreshGate.Release();
        }
    }

    private RunningAppInspection Inspect(AppCardViewModel card)
    {
        lock (_inspectionLock)
        {
            _lastInspections.TryGetValue(card.Profile.Id, out var previous);
            var result = _tracker!.InspectApplication(card.Profile, card.ResolvedPath!, previous);
            if (result.ConfirmedProcesses.Count > 0 || result.AmbiguousProcesses.Count > 0)
            {
                _lastInspections[card.Profile.Id] = result;
            }
            else
            {
                _lastInspections.Remove(card.Profile.Id);
            }

            return result;
        }
    }

    private static void ApplyState(AppCardViewModel card, RunningAppInspection inspection)
    {
        var newState = ComputeState(inspection);
        if (Dispatcher.UIThread.CheckAccess())
        {
            card.State = newState;
        }
        else
        {
            Dispatcher.UIThread.Post(() => card.State = newState);
        }
    }

    private static AppCardViewModel.ProcessState ComputeState(RunningAppInspection inspection)
    {
        var confirmed = inspection.ConfirmedProcesses;
        var ambiguous = inspection.AmbiguousProcesses;
        // Когда активен конкретно отслеживаемый экземпляр, отображаем состояние именно этого экземпляра,
        // а в общем случае учитываем как подтверждённые, так и неоднозначные процессы (чтобы не показывать ложное «Не запущено»).
        var active = inspection.HasActiveTrackedRootForTarget
            ? confirmed.ToList()
            : confirmed.Concat(ambiguous).ToList();

        return active.Any(p => p.Role == DetectedProcessRole.PrimaryWithVisibleWindow)
            ? AppCardViewModel.ProcessState.Running
            : active.Count > 0
                ? AppCardViewModel.ProcessState.Background
                : AppCardViewModel.ProcessState.Stopped;
    }

    private static string OperationKey(AppCardViewModel card) => card.Profile.Id.ToString();

    private async Task RunOperationAsync(AppCardViewModel card, Func<Task> operation)
    {
        var key = OperationKey(card);
        if (!_busyTargets.Add(key)) return;
        IEnumerable<AppCardViewModel> MatchingCards() => _allCards.Append(card).Distinct()
            .Where(c => string.Equals(OperationKey(c), key, StringComparison.OrdinalIgnoreCase));
        foreach (var item in MatchingCards()) item.IsBusy = true;
        try
        {
            await operation();
        }
        finally
        {
            try
            {
                if (_tracker is not null && card.IsDesktop && card.ResolvedPath is not null)
                {
                    var inspection = await Task.Run(() => Inspect(card));
                    foreach (var item in MatchingCards()) ApplyState(item, inspection);

                    // Синхронизируем другие карточки, указывающие на тот же путь (например, дубликаты профиля)
                    foreach (var sibling in _allCards.Where(c =>
                                 c.IsDesktop
                                 && !c.IsBusy
                                 && !string.Equals(OperationKey(c), key, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(c.ResolvedPath, card.ResolvedPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        var siblingInspection = await Task.Run(() => Inspect(sibling));
                        ApplyState(sibling, siblingInspection);
                    }
                }
            }
            finally
            {
                _busyTargets.Remove(key);
                foreach (var item in MatchingCards()) item.IsBusy = false;
            }
        }
    }

    private Task ManageAppAsync(AppCardViewModel card, AppCardViewModel.LifecycleAction action)
        => RunOperationAsync(card, () => ManageProcessesAsync(card, action));

    private async Task ManageProcessesAsync(AppCardViewModel card, AppCardViewModel.LifecycleAction action)
    {
        if (_tracker is null || card.ResolvedPath is null) return;
        try
        {
            var inspection = await Task.Run(() => Inspect(card));
            ApplyState(card, inspection);

            var confirmed = inspection.ConfirmedProcesses;
            if (confirmed.Count == 0)
            {
                if (inspection.AmbiguousProcesses.Count > 0)
                {
                    _notifications.Show(
                        card.Name,
                        Loc.Format("AmbiguousProcessesWarning", FormatProcessSummaryList(inspection.AmbiguousProcesses)),
                        NotificationSeverity.Warning,
                        autoDismiss: false,
                        actionLabel: Loc["MenuDiagnostics"],
                        actionCallback: () => _ = ShowDiagnosticsAsync(card),
                        relatedApplicationId: card.Profile.Id);
                    return;
                }

                if (action == AppCardViewModel.LifecycleAction.Restart)
                {
                    await LaunchDesktopAndSyncStateAsync(card, direct: false);
                }

                return;
            }

            if (action == AppCardViewModel.LifecycleAction.Force)
            {
                var confirmedForce = await _dialogs.ShowConfirmationAsync(
                    Loc["MenuForce"],
                    Loc.Format("ForceBody", card.Name, FormatProcessSummaryList(confirmed)),
                    Loc["MenuForce"],
                    Loc["BtnCancel"],
                    isDanger: true);

                if (!confirmedForce)
                {
                    return;
                }

                var forceResult = await _tracker.StopApplicationAsync(card.Profile, inspection, force: true);
                RecordInspectionAndApplyState(card, forceResult.FinalInspection);

                if (!forceResult.AllTargetProcessesStopped)
                {
                    PublishStopFailureOrRemainingNotification(card, forceResult);
                }

                return;
            }

            var stopResult = await _tracker.StopApplicationAsync(card.Profile, inspection, force: false);
            RecordInspectionAndApplyState(card, stopResult.FinalInspection);

            if (!stopResult.AllTargetProcessesStopped)
            {
                if (stopResult.RemainingConfirmedProcesses.Count > 0)
                {
                    var pidList = FormatProcessSummaryList(stopResult.RemainingConfirmedProcesses);
                    string promptTitle;
                    string promptBody;
                    if (action == AppCardViewModel.LifecycleAction.Restart)
                    {
                        promptTitle = Loc.Format("GracefulIncompleteTitle", card.Name);
                        promptBody = Loc.Format("RestartForcePromptBody", card.Name, pidList);
                    }
                    else if (stopResult.Outcome == ProcessStopOutcome.GracefulVetoed)
                    {
                        promptTitle = Loc.Format("GracefulIncompleteTitle", card.Name);
                        promptBody = Loc.Format("GracefulVetoedBody", card.Name, pidList);
                    }
                    else
                    {
                        promptTitle = Loc.Format("GracefulIncompleteTitle", card.Name);
                        promptBody = Loc.Format("GracefulIncompleteBody", card.Name, pidList);
                    }

                    var forceAfterGraceful = await _dialogs.ShowConfirmationAsync(
                        promptTitle,
                        promptBody,
                        Loc["MenuForce"],
                        action == AppCardViewModel.LifecycleAction.Restart ? Loc["BtnCancel"] : Loc["BtnKeepRunning"],
                        isDanger: true);

                    if (!forceAfterGraceful)
                    {
                        _notifications.Show(
                            card.Name,
                            action == AppCardViewModel.LifecycleAction.Restart
                                ? Loc["RestartAbortedStillRunning"]
                                : Loc["StillRunning"],
                            NotificationSeverity.Warning,
                            autoDismiss: false,
                            actionLabel: Loc["MenuDiagnostics"],
                            actionCallback: () => _ = ShowDiagnosticsAsync(card),
                            relatedApplicationId: card.Profile.Id);
                        return;
                    }

                    stopResult = await _tracker.StopApplicationAsync(card.Profile, stopResult.FinalInspection, force: true);
                    RecordInspectionAndApplyState(card, stopResult.FinalInspection);
                }

                if (!stopResult.AllTargetProcessesStopped)
                {
                    PublishStopFailureOrRemainingNotification(card, stopResult);
                    return;
                }
            }

            if (action == AppCardViewModel.LifecycleAction.Restart)
            {
                await LaunchDesktopAndSyncStateAsync(card, direct: false);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _notifications.Show(card.Name, ex.Message, NotificationSeverity.Error, autoDismiss: false, relatedApplicationId: card.Profile.Id);
        }
    }

    private void RecordInspectionAndApplyState(AppCardViewModel card, RunningAppInspection inspection)
    {
        lock (_inspectionLock)
        {
            if (inspection.ConfirmedProcesses.Count > 0 || inspection.AmbiguousProcesses.Count > 0)
            {
                _lastInspections[card.Profile.Id] = inspection;
            }
            else
            {
                _lastInspections.Remove(card.Profile.Id);
            }
        }

        ApplyState(card, inspection);
    }

    private void PublishStopFailureOrRemainingNotification(AppCardViewModel card, ProcessStopResult result)
    {
        if (result.Errors.Count > 0)
        {
            var errText = string.Join("; ", result.Errors.Select(e => $"{e.ProcessName} (PID {e.ProcessId}, Win32 {e.NativeErrorCode}): {e.Message}"));
            var remainingIds = FormatProcessSummaryList(result.RemainingConfirmedProcesses.Concat(result.RemainingAmbiguousProcesses));
            _notifications.Show(
                card.Name,
                Loc.Format("ForceFailedBody", string.IsNullOrWhiteSpace(remainingIds) ? "—" : remainingIds, errText),
                NotificationSeverity.Error,
                autoDismiss: false,
                actionLabel: Loc["MenuDiagnostics"],
                actionCallback: () => _ = ShowDiagnosticsAsync(card),
                relatedApplicationId: card.Profile.Id);
            return;
        }

        if (result.RemainingConfirmedProcesses.Count > 0)
        {
            _notifications.Show(
                card.Name,
                Loc["StillRunning"],
                NotificationSeverity.Warning,
                autoDismiss: false,
                actionLabel: Loc["MenuDiagnostics"],
                actionCallback: () => _ = ShowDiagnosticsAsync(card),
                relatedApplicationId: card.Profile.Id);
            return;
        }

        if (result.RemainingAmbiguousProcesses.Count > 0)
        {
            _notifications.Show(
                card.Name,
                Loc.Format("AmbiguousProcessesWarning", FormatProcessSummaryList(result.RemainingAmbiguousProcesses)),
                NotificationSeverity.Warning,
                autoDismiss: false,
                actionLabel: Loc["MenuDiagnostics"],
                actionCallback: () => _ = ShowDiagnosticsAsync(card),
                relatedApplicationId: card.Profile.Id);
        }
    }

    private async Task LaunchDesktopAndSyncStateAsync(AppCardViewModel card, bool direct)
    {
        var launchResult = await _coordinator.LaunchWithPromptsAsync(card.Profile, direct);
        if (_tracker is null
            || !card.IsDesktop
            || card.ResolvedPath is null
            || launchResult.Status is not (LaunchStatus.Started or LaunchStatus.StartedWithWarning))
        {
            return;
        }

        var isWindowsAppsAlias = card.ResolvedPath.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < 10; i++)
        {
            var inspection = await Task.Run(() => Inspect(card));
            if (inspection.ConfirmedProcesses.Count > 0 || inspection.AmbiguousProcesses.Count > 0)
            {
                ApplyState(card, inspection);
                return;
            }

            if (!isWindowsAppsAlias
                && launchResult.ProcessId is int launchedPid
                && !_tracker.ActiveProcesses.Any(p => p.ProcessId == launchedPid))
            {
                ApplyState(card, inspection);
                return;
            }

            await Task.Delay(150);
        }
    }

    private static string FormatProcessSummaryList(IEnumerable<DetectedProcessInfo> processes)
        => string.Join(", ", processes.Select(p => $"{p.ProcessName}#{p.ProcessId}"));

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
        GridItems.Clear();
        foreach (var item in list)
        {
            FilteredApps.Add(item);
            GridItems.Add(item);
        }

        GridItems.Add(AddTile);

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
        => RunOperationAsync(card, () => LaunchDesktopAndSyncStateAsync(card, direct));

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

    private Task ShowDiagnosticsAsync(AppCardViewModel card)
        => _dialogs.ShowApplicationDiagnosticsAsync(card.Profile, () => EditAppAsync(card));

    private async Task DeleteAppAsync(AppCardViewModel card)
    {
        var ok = await _dialogs.ShowConfirmationAsync(
            Loc["DeleteProfileConfirmTitle"],
            Loc.Format("DeleteProfileConfirmBody", card.Name),
            Loc["BtnDelete"],
            Loc["BtnCancel"],
            isDanger: true);
        if (ok)
        {
            await _appService.DeleteAsync(card.Profile.Id);
            _notifications.Show(
                Loc["ToastProfileDeletedTitle"],
                Loc.Format("ToastProfileDeletedBody", card.Name),
                NotificationSeverity.Info);
        }
    }

    private void OpenFolder(AppCardViewModel card)
    {
        if (!string.IsNullOrWhiteSpace(card.ResolvedPath) && File.Exists(card.ResolvedPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{card.ResolvedPath}\"",
                UseShellExecute = true
            });
            return;
        }

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
            _notifications.Show("Расположение не найдено", "Не удалось определить существующий файл или папку для открытия.", NotificationSeverity.Warning);
        }
    }

    public static (string Label, ProxyCheckStatus Status) ResolveProxySummary(
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
                return (matched.Name, matched.LastCheckStatus);
            }

            return (loc["ProxyUnassigned"], ProxyCheckStatus.Invalid);
        }

        if (defaultProxy is not null)
        {
            return (defaultProxy.Name, defaultProxy.LastCheckStatus);
        }

        return (loc["ProxyUnassigned"], ProxyCheckStatus.Unknown);
    }
}
