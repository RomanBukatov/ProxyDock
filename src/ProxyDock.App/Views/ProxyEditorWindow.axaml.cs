using Avalonia.Controls;
using ProxyDock.App.ViewModels;

namespace ProxyDock.App.Views;

public partial class ProxyEditorWindow : Window
{
    public ProxyEditorWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (DataContext is ProxyEditorViewModel vm && vm.IsTesting)
            {
                vm.CancelCommand.Execute(null);
            }
        };
    }
}
