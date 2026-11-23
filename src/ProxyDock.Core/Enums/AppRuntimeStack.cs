namespace ProxyDock.Core.Enums;

/// <summary>
/// Обнаруженный технологический стек пользовательского интерфейса и сети desktop-приложения.
/// </summary>
public enum AppRuntimeStack
{
    NativeOrUnknown = 0,
    Electron = 1,
    ChromiumShell = 2,
    WebView2 = 3
}
