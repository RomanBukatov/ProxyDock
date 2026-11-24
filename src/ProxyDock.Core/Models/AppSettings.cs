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
    public bool ConfirmBeforeRelaunchingRunningApp { get; set; } = false;

    public bool AutoCheckUpdates { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;
    public UpdateNetworkMode UpdateNetworkMode { get; set; } = UpdateNetworkMode.SystemDefault;
    public Guid? UpdateProxyProfileId { get; set; }
    public DateTimeOffset? LastUpdateCheckAtUtc { get; set; }
    public UpdateCheckStatus LastUpdateCheckStatus { get; set; } = UpdateCheckStatus.NeverChecked;
    public string? LastUpdateCheckSummary { get; set; }
    public string? LastAvailableUpdateVersion { get; set; }
}
