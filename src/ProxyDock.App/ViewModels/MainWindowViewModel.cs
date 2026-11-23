using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IProxyProfileService _proxyService;
    private readonly IConfigDiagnosticsStore _diagnostics;

    [ObservableProperty]
    private int _selectedNavIndex;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _defaultProxyText = string.Empty;

    [ObservableProperty]
    private bool _bannerVisible;

    [ObservableProperty]
    private string _bannerTitle = string.Empty;

    [ObservableProperty]
    private string _bannerMessage = string.Empty;

    [ObservableProperty]
    private NotificationSeverity _bannerSeverity;

    public MainWindowViewModel(
        ILocalizationService loc,
        IProxyProfileService proxyService,
        IConfigDiagnosticsStore diagnostics,
        ISettingsService settingsService,
        AppNotificationService notifications,
        Func<Action<string>, DashboardViewModel> dashboardFactory,
        ApplicationsViewModel applicationsVm,
        ProxyProfilesViewModel proxiesVm,
        LaunchHistoryViewModel historyVm,
        SettingsViewModel settingsVm)
        : base(loc)
    {
        _proxyService = proxyService;
        _diagnostics = diagnostics;

        Dashboard = dashboardFactory(NavigateByKey);
        Applications = applicationsVm;
        Proxies = proxiesVm;
        History = historyVm;
        Settings = settingsVm;

        _currentPage = Dashboard;

        notifications.NotificationRaised += (_, n) => Dispatcher.UIThread.Post(() =>
        {
            BannerTitle = n.Title;
            BannerMessage = n.Message;
            BannerSeverity = n.Severity;
            BannerVisible = true;
        });

        _proxyService.ProxiesChanged += (_, _) => _ = UpdateDefaultProxyHeaderAsync();
        _diagnostics.WarningsChanged += (_, _) => ShowConfigWarningsIfAny();
        settingsService.SettingsChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(BannerBackground));
            OnPropertyChanged(nameof(BannerBorder));
            _ = RefreshCurrentPageAsync();
        });
    }

    public DashboardViewModel Dashboard { get; }
    public ApplicationsViewModel Applications { get; }
    public ProxyProfilesViewModel Proxies { get; }
    public LaunchHistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public IBrush BannerBackground => BannerSeverity switch
    {
        NotificationSeverity.Success => FormatHelpers.Brush("SuccessSoftBrush"),
        NotificationSeverity.Warning => FormatHelpers.Brush("WarningSoftBrush"),
        NotificationSeverity.Error => FormatHelpers.Brush("ErrorSoftBrush"),
        _ => FormatHelpers.Brush("InfoSoftBrush")
    };

    public IBrush BannerBorder => BannerSeverity switch
    {
        NotificationSeverity.Success => FormatHelpers.Brush("SuccessBrush"),
        NotificationSeverity.Warning => FormatHelpers.Brush("WarningBrush"),
        NotificationSeverity.Error => FormatHelpers.Brush("ErrorBrush"),
        _ => FormatHelpers.Brush("InfoBrush")
    };

    partial void OnBannerSeverityChanged(NotificationSeverity value)
    {
        OnPropertyChanged(nameof(BannerBackground));
        OnPropertyChanged(nameof(BannerBorder));
    }

    public bool IsDashboardSelected
    {
        get => SelectedNavIndex == 0;
        set { if (value) SelectedNavIndex = 0; }
    }

    public bool IsApplicationsSelected
    {
        get => SelectedNavIndex == 1;
        set { if (value) SelectedNavIndex = 1; }
    }

    public bool IsProxiesSelected
    {
        get => SelectedNavIndex == 2;
        set { if (value) SelectedNavIndex = 2; }
    }

    public bool IsHistorySelected
    {
        get => SelectedNavIndex == 3;
        set { if (value) SelectedNavIndex = 3; }
    }

    public bool IsSettingsSelected
    {
        get => SelectedNavIndex == 4;
        set { if (value) SelectedNavIndex = 4; }
    }

    partial void OnSelectedNavIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsDashboardSelected));
        OnPropertyChanged(nameof(IsApplicationsSelected));
        OnPropertyChanged(nameof(IsProxiesSelected));
        OnPropertyChanged(nameof(IsHistorySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));

        CurrentPage = value switch
        {
            0 => Dashboard,
            1 => Applications,
            2 => Proxies,
            3 => History,
            4 => Settings,
            _ => Dashboard
        };

        _ = RefreshCurrentPageAsync();
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            Dashboard.RefreshAsync(),
            Applications.RefreshAsync(),
            Proxies.RefreshAsync(),
            History.RefreshAsync(),
            Settings.RefreshAsync(),
            UpdateDefaultProxyHeaderAsync());

        ShowConfigWarningsIfAny();
    }

    public void NavigateByKey(string pageKey)
    {
        SelectedNavIndex = pageKey switch
        {
            "Applications" => 1,
            "Proxies" => 2,
            "History" => 3,
            "Settings" => 4,
            _ => 0
        };
    }

    private async Task RefreshCurrentPageAsync()
    {
        switch (SelectedNavIndex)
        {
            case 0:
                await Dashboard.RefreshAsync();
                break;
            case 1:
                await Applications.RefreshAsync();
                break;
            case 2:
                await Proxies.RefreshAsync();
                break;
            case 3:
                await History.RefreshAsync();
                break;
            case 4:
                await Settings.RefreshAsync();
                break;
        }
    }

    private async Task UpdateDefaultProxyHeaderAsync()
    {
        var def = await _proxyService.GetDefaultAsync().ConfigureAwait(false);
        var text = def is null ? Loc["NoDefaultProxy"] : $"{def.Name} ({def.Host}:{def.Port})";
        await Dispatcher.UIThread.InvokeAsync(() => DefaultProxyText = text);
    }

    private void ShowConfigWarningsIfAny()
    {
        var warnings = _diagnostics.Warnings;
        if (warnings.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            BannerTitle = "Восстановление повреждённой конфигурации";
            BannerMessage = string.Join(" ", warnings.Select(w => w.Message));
            BannerSeverity = NotificationSeverity.Warning;
            BannerVisible = true;
        });
    }

    [RelayCommand]
    private void DismissBanner()
    {
        BannerVisible = false;
        _diagnostics.ClearWarnings();
    }
}
