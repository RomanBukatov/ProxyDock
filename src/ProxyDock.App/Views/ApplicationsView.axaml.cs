using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxyDock.App.ViewModels;

namespace ProxyDock.App.Views;

public partial class ApplicationsView : UserControl
{
    public ApplicationsView()
    {
        InitializeComponent();
    }

    private void OnAppMenuButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not AppCardViewModel card)
        {
            return;
        }

        var flyout = AppCardMenuBuilder.Build(card);
        flyout.ShowAt(button);
    }
}
