using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IConfigDiagnosticsStore _diagnostics;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private int _selectedNavIndex;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private bool _isTrayEnabled;

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
        IConfigDiagnosticsStore diagnostics,
        ISettingsService settingsService,
        AppNotificationService notifications,
        ApplicationsViewModel applicationsVm,
        ProxyProfilesViewModel proxiesVm,
        LaunchHistoryViewModel historyVm,
        SettingsViewModel settingsVm)
        : base(loc)
    {
        _diagnostics = diagnostics;
        _settingsService = settingsService;

        Applications = applicationsVm;
        Applications.NavigateAction = NavigateByKey;
        Proxies = proxiesVm;
        History = historyVm;
        Settings = settingsVm;

        _currentPage = Applications;

        notifications.NotificationRaised += (_, n) => Dispatcher.UIThread.Post(() =>
        {
            BannerTitle = n.Title;
            BannerMessage = n.Message;
            BannerSeverity = n.Severity;
            BannerVisible = true;
        });

        _diagnostics.WarningsChanged += (_, _) => ShowConfigWarningsIfAny();
        _settingsService.SettingsChanged += (_, updated) => Dispatcher.UIThread.Post(() =>
        {
            IsTrayEnabled = updated.MinimizeToTray;
            OnPropertyChanged(nameof(BannerBackground));
            OnPropertyChanged(nameof(BannerBorder));
            _ = RefreshCurrentPageAsync();
        });
    }

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
