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
    void RequestStop(RunningAppInspection inspection, bool force);
    IReadOnlyList<int> FindRunningInstances(string resolvedExecutablePath, IReadOnlyCollection<string>? relatedProcessNames = null);
    RunningAppInspection InspectRunningInstances(string resolvedExecutablePath, IReadOnlyCollection<string>? relatedProcessNames = null, string? templateKey = null);

    RunningAppInspection InspectApplication(
        ApplicationProfile application,
        string resolvedExecutablePath,
        RunningAppInspection? previousInspection = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        var inspection = InspectRunningInstances(resolvedExecutablePath, application.RelatedProcessNames, application.TemplateKey);
        inspection.RetainVerifiedInstances(previousInspection);
        return inspection;
    }

    Task<ProcessStopResult> StopApplicationAsync(
        ApplicationProfile application,
        RunningAppInspection inspection,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(inspection);
        RequestStop(inspection, force);
        var after = InspectApplication(application, inspection.ExpectedExecutablePath ?? application.ExecutableOrCommand, inspection);
        var remainingConfirmed = after.ConfirmedProcesses;
        var remainingAmbiguous = after.AmbiguousProcesses;
        var outcome = remainingConfirmed.Count == 0 && remainingAmbiguous.Count == 0
            ? ProcessStopOutcome.AllStopped
            : remainingConfirmed.Count == 0 && remainingAmbiguous.Count > 0
                ? ProcessStopOutcome.AmbiguousRemaining
                : force
                    ? ProcessStopOutcome.PartialOrFailedTermination
                    : ProcessStopOutcome.StillRunningAfterGraceful;

        return Task.FromResult(new ProcessStopResult
        {
            Outcome = outcome,
            FinalInspection = after,
            RemainingConfirmedProcesses = remainingConfirmed,
            RemainingAmbiguousProcesses = remainingAmbiguous,
            GracefulStrategyUsed = force ? null : "CloseMainWindow"
        });
    }
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
