using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ProxyDock.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _polling;
    private bool _opened;

    public MainWindow()
    {
        InitializeComponent();
        _stateTimer.Tick += RefreshStates;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_opened && (change.Property == IsVisibleProperty || change.Property == WindowStateProperty))
            UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (_opened && IsVisible && WindowState != WindowState.Minimized)
        {
            if (_polling is not null) return;
            _polling = new CancellationTokenSource();
            _stateTimer.Start();
            RefreshStates(this, EventArgs.Empty);
        }
        else
        {
            _stateTimer.Stop();
            _polling?.Cancel();
            _polling?.Dispose();
            _polling = null;
        }
    }

    private async void RefreshStates(object? sender, EventArgs args)
    {
        if (_polling is { } polling && DataContext is ViewModels.MainWindowViewModel vm &&
            vm.CurrentPage == vm.Applications && !vm.Applications.IsRefreshingStates)
            await vm.Applications.RefreshStatesAsync(polling.Token);
    }

    private void OnFilteredAppsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshStates(this, EventArgs.Empty);

    protected override void OnClosed(EventArgs e)
    {
        _opened = false;
        UpdatePolling();
        _stateTimer.Tick -= RefreshStates;
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.Applications.FilteredApps.CollectionChanged -= OnFilteredAppsChanged;
        }
        base.OnClosed(e);
    }

    public bool AllowExit { get; set; }

    public bool MinimizeToTrayOnClose { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToWorkingArea();
        _opened = true;
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.Applications.FilteredApps.CollectionChanged += OnFilteredAppsChanged;
        }
        UpdatePolling();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowExit && MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void FitToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var logicalWorkWidth = screen.WorkingArea.Width / scaling;
        var logicalWorkHeight = screen.WorkingArea.Height / scaling;

        var maxWidth = Math.Max(MinWidth, logicalWorkWidth * 0.92);
        var maxHeight = Math.Max(MinHeight, logicalWorkHeight * 0.90);

        if (Width > maxWidth)
        {
            Width = Math.Round(maxWidth);
        }

        if (Height > maxHeight)
        {
            Height = Math.Round(maxHeight);
        }
    }
}
