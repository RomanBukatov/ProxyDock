using Avalonia.Controls;

namespace ProxyDock.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public bool AllowExit { get; set; }

    public bool MinimizeToTrayOnClose { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToWorkingArea();
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
