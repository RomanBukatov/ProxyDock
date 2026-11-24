using Avalonia.Controls;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Services;

namespace ProxyDock.App.Views;

public partial class ApplicationsView : UserControl
{
    public ApplicationsView()
    {
        InitializeComponent();
    }

    private void OnAppRowMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout flyout || flyout.Target?.DataContext is not AppCardViewModel card)
        {
            return;
        }

        flyout.Items.Clear();

        var loc = card.Loc;

        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuLaunchDirect"],
            Command = card.LaunchDirectCommand
        });

        var assignRoot = new MenuItem { Header = loc["MenuAssignProxy"] };

        var defaultLabel = card.DefaultProxy is null
            ? loc["ProxyModeDefault"]
            : $"{loc["ProxyModeDefault"]} ({card.DefaultProxy.Name})";

        var defaultItem = new MenuItem
        {
            Header = defaultLabel,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = card.Profile.ProxyMode == ProxyAssignmentMode.InheritDefault
        };
        defaultItem.Click += async (_, _) => await card.AssignProxyAsync(ProxyAssignmentMode.InheritDefault, null);
        assignRoot.Items.Add(defaultItem);

        if (card.AvailableProxies.Count > 0)
        {
            assignRoot.Items.Add(new Separator());
            foreach (var proxy in card.AvailableProxies)
            {
                var capturedProxy = proxy;
                var specificItem = new MenuItem
                {
                    Header = $"{capturedProxy.Name} ({ProxyUriBuilder.FormatHost(capturedProxy.Host)}:{capturedProxy.Port})",
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = card.Profile.ProxyMode == ProxyAssignmentMode.Specific
                        && card.Profile.ProxyProfileId == capturedProxy.Id
                };
                specificItem.Click += async (_, _) => await card.AssignProxyAsync(ProxyAssignmentMode.Specific, capturedProxy.Id);
                assignRoot.Items.Add(specificItem);
            }
        }

        assignRoot.Items.Add(new Separator());
        var directItem = new MenuItem
        {
            Header = loc["ProxyModeDirect"],
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = card.Profile.ProxyMode == ProxyAssignmentMode.DirectNoProxy
        };
        directItem.Click += async (_, _) => await card.AssignProxyAsync(ProxyAssignmentMode.DirectNoProxy, null);
        assignRoot.Items.Add(directItem);

        flyout.Items.Add(assignRoot);
        flyout.Items.Add(new Separator());

        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuEditDetails"],
            Command = card.EditCommand
        });
        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuDuplicate"],
            Command = card.DuplicateCommand
        });
        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuOpenFolder"],
            Command = card.OpenFolderCommand
        });
        flyout.Items.Add(new Separator());
        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuDelete"],
            Command = card.DeleteCommand
        });
    }
}
