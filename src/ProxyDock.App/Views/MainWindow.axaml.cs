using Avalonia.Controls;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsService? _settingsService;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ISettingsService settingsService)
        : this()
    {
        _settingsService = settingsService;
    }

    public bool AllowExit { get; set; }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowExit && _settingsService is not null)
        {
            e.Cancel = true;
            try
            {
                var settings = await _settingsService.GetAsync();
                if (settings.MinimizeToTray)
                {
                    Hide();
                    return;
                }
            }
            catch
            {
                // При ошибке чтения настроек безопаснее выполнить штатный выход
            }

            AllowExit = true;
            Close();
            return;
        }

        base.OnClosing(e);
    }
}
