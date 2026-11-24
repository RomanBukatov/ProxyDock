using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;

namespace ProxyDock.Core.Interfaces;

public interface IUpdateService
{
    string CurrentVersion { get; }
    UpdateInstallMode InstallMode { get; }
    bool IsAutoCheckSuppressed { get; }
    string RepositoryUrl { get; }
    string ReleasesPageUrl { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(bool isManualCheck = false, CancellationToken cancellationToken = default);
    Task DownloadUpdateAsync(UpdateReleaseInfo release, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    void ApplyUpdateAndRestart(UpdateReleaseInfo release, Action requestAppShutdown);
}
