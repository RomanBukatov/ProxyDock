using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.App.ViewModels;

public sealed partial class ToastNotificationViewModel : ObservableObject
{
    private readonly Action<ToastNotificationViewModel> _dismissAction;
    private readonly Action? _openHistoryAction;
    private readonly Action? _customAction;
    private readonly CancellationTokenSource _timerCts = new();

    public ToastNotificationViewModel(
        AppNotification notification,
        ILocalizationService loc,
        Action<ToastNotificationViewModel> dismissAction,
        Action? openHistoryAction = null)
    {
        Id = Guid.NewGuid();
        Title = notification.Title;
        Message = notification.Message;
        Severity = notification.Severity;
        ShowOpenHistoryAction = notification.ShowOpenHistoryAction && openHistoryAction is not null;
        ActionLabel = notification.ActionLabel ?? string.Empty;
        _customAction = notification.ActionCallback;
        ShowCustomAction = !string.IsNullOrWhiteSpace(ActionLabel) && _customAction is not null;
        Loc = loc;
        _dismissAction = dismissAction;
        _openHistoryAction = openHistoryAction;
    }

    public Guid Id { get; }
    public string Title { get; }
    public string Message { get; }
    public NotificationSeverity Severity { get; }
    public bool ShowOpenHistoryAction { get; }
    public string ActionLabel { get; }
    public bool ShowCustomAction { get; }
    public ILocalizationService Loc { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public CancellationToken TimerToken => _timerCts.Token;

    public IBrush AccentBrush => Severity switch
    {
        NotificationSeverity.Success => FormatHelpers.Brush("SuccessBrush"),
        NotificationSeverity.Warning => FormatHelpers.Brush("WarningBrush"),
        NotificationSeverity.Error => FormatHelpers.Brush("ErrorBrush"),
        _ => FormatHelpers.Brush("InfoBrush")
    };

    public IBrush SurfaceBrush => FormatHelpers.Brush("ToastSurfaceBrush");
    public IBrush BorderBrush => Severity switch
    {
        NotificationSeverity.Error => FormatHelpers.Brush("ErrorBrush"),
        NotificationSeverity.Warning => FormatHelpers.Brush("WarningBrush"),
        _ => FormatHelpers.Brush("ToastBorderBrush")
    };

    public void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(SurfaceBrush));
        OnPropertyChanged(nameof(BorderBrush));
    }

    public void CancelAutoDismiss()
    {
        try
        {
            _timerCts.Cancel();
        }
        catch
        {
            // Игнорируем повторную отмену
        }
        finally
        {
            _timerCts.Dispose();
        }
    }

    [RelayCommand]
    private void Dismiss() => _dismissAction(this);

    [RelayCommand]
    private void OpenHistory()
    {
        _openHistoryAction?.Invoke();
        _dismissAction(this);
    }

    [RelayCommand]
    private void ExecuteCustomAction()
    {
        _customAction?.Invoke();
        _dismissAction(this);
    }
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const int MaxVisibleToasts = 3;

    private readonly IConfigDiagnosticsStore _diagnostics;
    private readonly ISettingsService _settingsService;
    private readonly IUpdateService _updateService;
    private readonly IDialogService _dialogs;
    private readonly AppNotificationService _notifications;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayFunc;

    [ObservableProperty]
    private int _selectedNavIndex;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private bool _isTrayEnabled;

    public MainWindowViewModel(
        ILocalizationService loc,
        IConfigDiagnosticsStore diagnostics,
        ISettingsService settingsService,
        IUpdateService updateService,
        IDialogService dialogs,
        AppNotificationService notifications,
        ApplicationsViewModel applicationsVm,
        ProxyProfilesViewModel proxiesVm,
        LaunchHistoryViewModel historyVm,
        SettingsViewModel settingsVm,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null)
        : base(loc)
    {
        _diagnostics = diagnostics;
        _settingsService = settingsService;
        _updateService = updateService;
        _dialogs = dialogs;
        _notifications = notifications;
        _delayFunc = delayFunc ?? ((d, ct) => Task.Delay(d, ct));

        Applications = applicationsVm;
        Applications.NavigateAction = NavigateByKey;
        Proxies = proxiesVm;
        History = historyVm;
        Settings = settingsVm;

        _currentPage = Applications;

        notifications.NotificationRaised += (_, n) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                EnqueueToast(n);
            }
            else
            {
                Dispatcher.UIThread.Post(() => EnqueueToast(n));
            }
        };

        _diagnostics.WarningsChanged += (_, _) => ShowConfigWarningsIfAny();
        _settingsService.SettingsChanged += (_, updated) => Dispatcher.UIThread.Post(() =>
        {
            IsTrayEnabled = updated.MinimizeToTray;
            RefreshToastBrushes();
            _ = RefreshCurrentPageAsync();
        });

        if (Application.Current is { } app)
        {
            app.ActualThemeVariantChanged += (_, _) => Dispatcher.UIThread.Post(RefreshToastBrushes);
        }
    }

    public ApplicationsViewModel Applications { get; }
    public ProxyProfilesViewModel Proxies { get; }
    public LaunchHistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<ToastNotificationViewModel> Toasts { get; } = [];
    public bool HasToasts => Toasts.Count > 0;

    public bool IsApplicationsSelected
    {
        get => SelectedNavIndex == 0;
        set { if (value) SelectedNavIndex = 0; }
    }

    public bool IsProxiesSelected
    {
        get => SelectedNavIndex == 1;
        set { if (value) SelectedNavIndex = 1; }
    }

    public bool IsHistorySelected
    {
        get => SelectedNavIndex == 2;
        set { if (value) SelectedNavIndex = 2; }
    }

    public bool IsSettingsSelected
    {
        get => SelectedNavIndex == 3;
        set { if (value) SelectedNavIndex = 3; }
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsApplicationsSelected));
        OnPropertyChanged(nameof(IsProxiesSelected));
        OnPropertyChanged(nameof(IsHistorySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));

        CurrentPage = value switch
        {
            1 => Proxies,
            2 => History,
            3 => Settings,
            _ => Applications
        };

        _ = RefreshCurrentPageAsync();
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.GetAsync();
        IsTrayEnabled = settings.MinimizeToTray;

        await Task.WhenAll(
            Applications.RefreshAsync(),
            Proxies.RefreshAsync(),
            History.RefreshAsync(),
            Settings.RefreshAsync());

        ShowConfigWarningsIfAny();
    }

    public async Task RunStartupUpdateCheckAsync(CancellationToken cancellationToken = default)
    {
        if (_updateService.IsAutoCheckSuppressed)
        {
            return;
        }

        try
        {
            var settings = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.AutoCheckUpdates)
            {
                return;
            }

            var result = await _updateService.CheckForUpdatesAsync(isManualCheck: false, cancellationToken).ConfigureAwait(false);
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.AvailableRelease is { } release)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _notifications.Show(
                        Loc.Format("ToastUpdateAvailableTitle", release.Version),
                        Loc.Format("ToastUpdateAvailableBody", release.CurrentVersion),
                        NotificationSeverity.Info,
                        autoDismiss: false,
                        actionLabel: Loc["ToastUpdateDetailsAction"],
                        actionCallback: () => _ = _dialogs.ShowUpdateDialogAsync(release));
                });
            }
        }
        catch
        {
            // Фоновая проверка при старте никогда не должна показывать ошибки или падать
        }
    }

    public void NavigateByKey(string pageKey)
    {
        SelectedNavIndex = pageKey switch
        {
            "Proxies" => 1,
            "History" => 2,
            "Settings" => 3,
            _ => 0
        };
    }

    public void EnqueueToast(AppNotification notification)
    {
        while (Toasts.Count >= MaxVisibleToasts)
        {
            var victim = Toasts.FirstOrDefault(t => t.Severity != NotificationSeverity.Error) ?? Toasts[0];
            RemoveToast(victim);
        }

        var vm = new ToastNotificationViewModel(
            notification,
            Loc,
            RemoveToast,
            notification.ShowOpenHistoryAction ? () => NavigateByKey("History") : null);

        Toasts.Add(vm);
        OnPropertyChanged(nameof(HasToasts));

        if (notification.AutoDismissAfter is TimeSpan delay && delay > TimeSpan.Zero)
        {
            _ = AutoDismissAfterDelayAsync(vm, delay);
        }
    }

    private async Task AutoDismissAfterDelayAsync(ToastNotificationViewModel toast, TimeSpan delay)
    {
        try
        {
            await _delayFunc(delay, toast.TimerToken);
            if (!toast.TimerToken.IsCancellationRequested)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    RemoveToast(toast);
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(() => RemoveToast(toast));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Закрыто вручную
        }
        catch (ObjectDisposedException)
        {
            // Тост уже удалён
        }
    }

    private void RemoveToast(ToastNotificationViewModel toast)
    {
        if (Toasts.Remove(toast))
        {
            toast.CancelAutoDismiss();
            OnPropertyChanged(nameof(HasToasts));
        }
    }

    private void RefreshToastBrushes()
    {
        foreach (var toast in Toasts)
        {
            toast.RefreshThemeBrushes();
        }
    }

    private async Task RefreshCurrentPageAsync()
    {
        switch (SelectedNavIndex)
        {
            case 0:
                await Applications.RefreshAsync();
                break;
            case 1:
                await Proxies.RefreshAsync();
                break;
            case 2:
                await History.RefreshAsync();
                break;
            case 3:
                await Settings.RefreshAsync();
                break;
        }
    }

    private void ShowConfigWarningsIfAny()
    {
        var warnings = _diagnostics.Warnings;
        if (warnings.Count == 0)
        {
            return;
        }

        var notification = new AppNotification(
            "Восстановление повреждённой конфигурации",
            string.Join(" ", warnings.Select(w => w.Message)),
            NotificationSeverity.Warning,
            AutoDismissAfter: null);

        if (Dispatcher.UIThread.CheckAccess())
        {
            EnqueueToast(notification);
        }
        else
        {
            Dispatcher.UIThread.Post(() => EnqueueToast(notification));
        }
    }
}
