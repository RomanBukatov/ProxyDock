using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

/// <summary>
/// Пользовательские настройки приложения ProxyDock.
/// </summary>
public sealed class AppSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.Dark;
    public string Language { get; set; } = "ru";
    public bool MinimizeToTray { get; set; } = false;
    public bool StartMinimized { get; set; }
    public bool RunAtWindowsStartup { get; set; }
    public Guid? DefaultProxyId { get; set; }
    public TerminalPreference DefaultTerminal { get; set; } = TerminalPreference.WindowsTerminal;
    public string? DefaultCliWorkingDirectory { get; set; }
    public string MinimumLogLevel { get; set; } = "Information";
    public int MaxHistoryEntries { get; set; } = 200;
}
