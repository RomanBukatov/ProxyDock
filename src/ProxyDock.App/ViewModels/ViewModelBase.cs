using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected ViewModelBase(ILocalizationService loc)
    {
        Loc = loc;
    }

    public ILocalizationService Loc { get; }
}
