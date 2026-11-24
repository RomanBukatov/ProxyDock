using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using ProxyDock.App.ViewModels;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Services;

namespace ProxyDock.App.Views;

/// <summary>
/// Строит полностью заполненный MenuFlyout до вызова ShowAt,
/// чтобы MenuFlyoutPresenter в Avalonia 12 гарантированно отображал все пункты.
/// </summary>
public static class AppCardMenuBuilder
{
    public static MenuFlyout Build(AppCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var loc = card.Loc;
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            PlacementConstraintAdjustment =
                PopupPositionerConstraintAdjustment.FlipX
                | PopupPositionerConstraintAdjustment.FlipY
                | PopupPositionerConstraintAdjustment.SlideX
                | PopupPositionerConstraintAdjustment.SlideY
        };

        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuLaunchDirect"],
            Command = card.LaunchDirectCommand
        });

        var changeProxyRoot = new MenuItem { Header = loc["MenuChangeProxy"] };

        var defaultLabel = card.DefaultProxy is null
            ? loc["ProxyModeDefault"]
            : $"{loc["ProxyModeDefault"]} ({card.DefaultProxy.Name})";

        changeProxyRoot.Items.Add(new MenuItem
        {
            Header = defaultLabel,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = card.Profile.ProxyMode == ProxyAssignmentMode.InheritDefault,
            Command = card.CreateAssignProxyCommand(ProxyAssignmentMode.InheritDefault, null)
        });

        if (card.AvailableProxies.Count > 0)
        {
            changeProxyRoot.Items.Add(new Separator());
            foreach (var proxy in card.AvailableProxies)
            {
                changeProxyRoot.Items.Add(new MenuItem
                {
                    Header = $"{proxy.Name} ({ProxyUriBuilder.FormatHost(proxy.Host)}:{proxy.Port})",
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = card.Profile.ProxyMode == ProxyAssignmentMode.Specific
                        && card.Profile.ProxyProfileId == proxy.Id,
                    Command = card.CreateAssignProxyCommand(ProxyAssignmentMode.Specific, proxy.Id)
                });
            }
        }

        changeProxyRoot.Items.Add(new Separator());
        changeProxyRoot.Items.Add(new MenuItem
        {
            Header = loc["ProxyModeDirect"],
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = card.Profile.ProxyMode == ProxyAssignmentMode.DirectNoProxy,
            Command = card.CreateAssignProxyCommand(ProxyAssignmentMode.DirectNoProxy, null)
        });

        flyout.Items.Add(changeProxyRoot);
        flyout.Items.Add(new Separator());

        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuEdit"],
            Command = card.EditCommand
        });
        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuOpenFileLocation"],
            Command = card.OpenFolderCommand
        });
        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuDuplicate"],
            Command = card.DuplicateCommand
        });
        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuDiagnostics"],
            Command = card.DiagnosticsCommand
        });

        flyout.Items.Add(new Separator());
        flyout.Items.Add(new MenuItem
        {
            Header = loc["MenuDelete"],
            Command = card.DeleteCommand
        });

        return flyout;
    }
}
