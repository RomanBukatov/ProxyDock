using Avalonia;
using Avalonia.Styling;
using ProxyDock.Core.Enums;

namespace ProxyDock.App.Services;

public sealed class ThemeService
{
    public void ApplyTheme(AppThemeMode mode)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = mode switch
        {
            AppThemeMode.Dark => ThemeVariant.Dark,
            AppThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }
}
