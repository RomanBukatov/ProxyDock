namespace ProxyDock.App.Localization;

internal static class StringsEn
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AppTitle"] = "ProxyDock",
        ["AppSubtitle"] = "Desktop Proxy Launcher",

        ["NavApplications"] = "Apps",
        ["NavProxies"] = "Proxies",
        ["NavHistory"] = "History",
        ["NavSettings"] = "Settings",

        ["DefaultProxyLabel"] = "Default proxy",
        ["NoDefaultProxy"] = "Not assigned",
        ["ManageProxiesShort"] = "Manage",
        ["BtnAddProxy"] = "+ New Proxy",

        ["AppsTitle"] = "Applications",
        ["SearchAppsPlaceholder"] = "Search apps...",
        ["FilterAll"] = "All",
        ["FilterDesktop"] = "Desktop",
        ["FilterCli"] = "CLI",
        ["BtnAdd"] = "+ Add",
        ["MenuAddDesktop"] = "Desktop App (.exe)...",
        ["MenuAddCli"] = "CLI Tool (Terminal)...",
        ["MenuTemplatesHeader"] = "Built-in Profiles",

        ["DetectedAppsTitle"] = "Detected Applications",
        ["DetectedOnPc"] = "Detected on this PC",
        ["BtnAddDetected"] = "Add",
        ["AddAllDetected"] = "Add All",

        ["AppsEmptyTitle"] = "No applications yet",
        ["AppsEmptySubtitle"] = "Add a detected application below or pick an .exe file manually.",
        ["AppsNoMatches"] = "No matching applications found.",
        ["TargetMissingShort"] = "File not found",

        ["BtnLaunch"] = "Launch",
        ["BtnMoreActionsTooltip"] = "More actions",
        ["MenuLaunchDirect"] = "Launch without proxy",
        ["MenuAssignProxy"] = "Assign proxy",
        ["ProxyModeDefault"] = "Default",
        ["ProxyModeDirect"] = "No proxy",
        ["MenuEditDetails"] = "Edit & Diagnostics...",
        ["MenuDuplicate"] = "Duplicate",
        ["MenuOpenFolder"] = "Open Folder",
        ["MenuDelete"] = "Delete",

        ["BtnEdit"] = "Edit",
        ["BtnTest"] = "Test",
        ["BtnCancel"] = "Cancel",
        ["BtnSave"] = "Save",
        ["BtnDelete"] = "Delete",
        ["BtnDuplicate"] = "Duplicate",
        ["BtnClose"] = "Close",
        ["BtnBrowse"] = "Browse...",

        ["ProxiesTitle"] = "Proxy Profiles",
        ["ProxiesEmptyTitle"] = "No proxy profiles yet",
        ["ProxiesEmptySubtitle"] = "Add an HTTP proxy (e.g. 127.0.0.1:8080 or a remote server) to route launched apps.",
        ["BtnTestAll"] = "Test All",
        ["BtnSetDefault"] = "Set Default",
        ["DefaultBadge"] = "Default",

        ["HistoryTitle"] = "Launch History",
        ["HistorySearchPlaceholder"] = "Search by app, proxy, or message...",
        ["BtnClearHistory"] = "Clear",
        ["HistoryDisclaimer"] = "Note: process start confirmation means ProxyDock passed the configured environment/arguments, not a guarantee that an unverified app honors proxy settings.",
        ["HistoryEmpty"] = "No launch history entries yet.",
        ["StatusFilterAll"] = "All statuses",
        ["StatusFilterStarted"] = "Started",
        ["StatusFilterWarning"] = "With warning",
        ["StatusFilterError"] = "Failed",

        ["SettingsTitle"] = "Settings",
        ["SettingsAppearance"] = "Appearance & Language",
        ["SettingsTheme"] = "Theme",
        ["SettingsLanguage"] = "Language",
        ["SettingsLifecycleTitle"] = "Window & System Tray",
        ["SettingsLifecycleHint"] = "By default ProxyDock exits completely when closed and leaves no background processes. Already launched apps keep running independently.",
        ["SettingsMinimizeToTray"] = "Keep in system tray when closing window",
        ["SettingsStartMinimized"] = "Start minimized to tray",
        ["SettingsRunAtStartup"] = "Run ProxyDock at Windows startup (HKCU Run)",
        ["SettingsDefaults"] = "Launch Defaults",
        ["SettingsDefaultProxy"] = "Default HTTP proxy",
        ["SettingsDefaultTerminal"] = "Default terminal (CLI)",
        ["SettingsDefaultCliDir"] = "Default CLI working directory",
        ["SettingsLogging"] = "Diagnostics & Backup",
        ["SettingsLogLevel"] = "Logging level",
        ["BtnOpenLogs"] = "Open Logs",
        ["BtnOpenConfig"] = "Open Config",
        ["BtnExportConfig"] = "Export JSON...",
        ["BtnImportConfig"] = "Import JSON...",
        ["SettingsLimitationsTitle"] = "How ProxyDock Works",
        ["SettingsLimitationsBody"] = "ProxyDock applies per-process environment variables and optional --proxy-server flags only to launched child processes. Windows system proxy, registry, and global network settings are never modified.",

        ["SystemProxyUnchangedShort"] = "Windows system proxy is not modified",
        ["TrayActiveBadge"] = "Tray enabled",

        ["TrayOpen"] = "Open ProxyDock",
        ["TrayQuickLaunch"] = "Quick Launch",
        ["TraySettings"] = "Settings",
        ["TrayExit"] = "Exit",
        ["TrayEmpty"] = "(No saved apps)"
    };
}
