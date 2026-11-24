namespace ProxyDock.Core.Enums;

public enum UpdateChannel
{
    Stable = 0
}

public enum UpdateNetworkMode
{
    SystemDefault = 0,
    DirectNoProxy = 1,
    SelectedProxy = 2
}

public enum UpdateCheckStatus
{
    NeverChecked = 0,
    Checking = 1,
    UpToDate = 2,
    UpdateAvailable = 3,
    UnsupportedStandalone = 4,
    Failed = 5,
    Cancelled = 6
}

public enum UpdateInstallMode
{
    VelopackManaged = 0,
    Standalone = 1
}
