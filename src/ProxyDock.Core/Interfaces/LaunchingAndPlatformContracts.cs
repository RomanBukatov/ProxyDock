using System.Diagnostics;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;

namespace ProxyDock.Core.Interfaces;

public interface IExecutableDiscoveryService
{
    ExecutableDiscoveryResult Resolve(ApplicationProfile profile);
    ExecutableDiscoveryResult ResolveTarget(LaunchKind kind, string executableOrCommand, string? templateKey = null);
    ExecutableDiscoveryResult DiscoverTemplate(string templateKey);
    DesktopAppStackInfo InspectExecutable(string path, string? templateKey = null);
    string? ResolveTerminalExecutable(TerminalPreference preference);
    IReadOnlyList<TerminalPreference> GetAvailableTerminals();
}

public interface IProcessTracker : IDisposable
{
    event EventHandler<TrackedProcessInfo>? ProcessExited;
    IReadOnlyCollection<TrackedProcessInfo> ActiveProcesses { get; }
    void Track(Process process, ApplicationProfile application, string resolvedTarget);
    IReadOnlyList<int> FindRunningInstances(string resolvedExecutablePath, IReadOnlyCollection<string>? relatedProcessNames = null);
    RunningAppInspection InspectRunningInstances(string resolvedExecutablePath, IReadOnlyCollection<string>? relatedProcessNames = null, string? templateKey = null);
}

public interface ILaunchStrategy
{
    bool CanHandle(ApplicationProfile application);
    Task<LaunchPlan> BuildPlanAsync(
        ApplicationProfile application,
        ProxyProfile? proxy,
        ProxyCredentials? credentials,
        AppSettings settings,
        bool isDirectNoProxy,
        CancellationToken cancellationToken = default);
}

public interface IApplicationLauncher
{
    Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default);
}

public interface IStartupManager
{
    bool IsSupported { get; }
    bool IsEnabled();
    void SetEnabled(bool enabled, string? executablePath = null);
}

public interface IAppIconExtractor
{
    (int Width, int Height, byte[] BgraPixels)? ExtractIconPixels(
        string filePath,
        int desiredPixelSize = 96,
        bool preferLightTheme = false);
}

public interface ILocalizationService : System.ComponentModel.INotifyPropertyChanged
{
    string CurrentLanguage { get; }
    string this[string key] { get; }
    string Format(string key, params object[] args);
    void SetLanguage(string languageCode);
}
