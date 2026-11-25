using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.App.ViewModels;

public sealed class DetectedProcessRowViewModel
{
    public DetectedProcessRowViewModel(DetectedProcessInfo info, bool? isConfirmedOwnership = null)
    {
        Info = info;
        IsConfirmedOwnership = isConfirmedOwnership;
    }

    public DetectedProcessInfo Info { get; }
    public bool? IsConfirmedOwnership { get; }
    public string PidText => Info.ParentProcessId is int ppid
        ? $"PID {Info.ProcessId} (PPID {ppid})"
        : $"PID {Info.ProcessId}";
    public string Name => Info.ProcessName;
    public string RoleLabel => Info.Role switch
    {
        DetectedProcessRole.PrimaryWithVisibleWindow => "Основное окно",
        DetectedProcessRole.PrimaryTrayOrBackground => "Фон / трей",
        DetectedProcessRole.ChromiumHelper => string.IsNullOrWhiteSpace(Info.ProcessTypeFlag)
            ? "Chromium helper"
            : $"Chromium ({Info.ProcessTypeFlag})",
        DetectedProcessRole.AuxiliaryOrCompanion => "Компонент / служба",
        _ => "Процесс"
    };

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (IsConfirmedOwnership == true)
            {
                parts.Add("Принадлежность: подтверждена");
            }
            else if (IsConfirmedOwnership == false)
            {
                parts.Add("Принадлежность: неоднозначно");
            }

            if (Info.StartedUtc is DateTime started)
            {
                parts.Add($"Старт: {started.ToLocalTime():HH:mm:ss}");
            }

            if (!string.IsNullOrWhiteSpace(Info.MainWindowTitle))
            {
                parts.Add($"Окно: «{Info.MainWindowTitle}»");
            }

            if (!string.IsNullOrWhiteSpace(Info.PackageFamilyName))
            {
                parts.Add($"MSIX: {Info.PackageFamilyName}");
            }

            if (!string.IsNullOrWhiteSpace(Info.ExecutablePath))
            {
                parts.Add(Info.ExecutablePath);
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailText);
}

public sealed partial class AppDiagnosticsViewModel : ViewModelBase
{
    private readonly ApplicationProfile _profile;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly IProcessTracker _processTracker;
    private readonly ILaunchHistoryRepository _historyRepo;
    private readonly IProxyProfileService _proxyService;
    private readonly Func<string, Task>? _copyToClipboardAsync;
    private readonly Func<Task>? _editCallback;

    [ObservableProperty]
    private string _resolvedTargetDisplay = string.Empty;

    [ObservableProperty]
    private bool _targetFound;

    [ObservableProperty]
    private string _packagingDisplay = string.Empty;

    [ObservableProperty]
    private string _runtimeStackDisplay = string.Empty;

    [ObservableProperty]
    private string _applicabilityDisplay = string.Empty;

    [ObservableProperty]
    private string _compatibilitySummary = string.Empty;

    [ObservableProperty]
    private string _proxySummary = string.Empty;

    [ObservableProperty]
    private string _strategySummary = string.Empty;

    [ObservableProperty]
    private string _processDetectionSummary = string.Empty;

    [ObservableProperty]
    private string _msixInfoDisplay = string.Empty;

    [ObservableProperty]
    private bool _hasMsixInfo;

    [ObservableProperty]
    private bool _hasCaveats;

    [ObservableProperty]
    private bool _hasDetectedProcesses;

    [ObservableProperty]
    private bool _hasRecentHistory;

    [ObservableProperty]
    private bool _copiedToClipboard;

    public AppDiagnosticsViewModel(
        ApplicationProfile profile,
        ILocalizationService loc,
        IExecutableDiscoveryService discovery,
        IProcessTracker processTracker,
        ILaunchHistoryRepository historyRepo,
        IProxyProfileService proxyService,
        Func<string, Task>? copyToClipboardAsync = null,
        Func<Task>? editCallback = null)
        : base(loc)
    {
        _profile = profile;
        _discovery = discovery;
        _processTracker = processTracker;
        _historyRepo = historyRepo;
        _proxyService = proxyService;
        _copyToClipboardAsync = copyToClipboardAsync;
        _editCallback = editCallback;
    }

    public string WindowTitle => $"{Loc["DiagnosticsTitle"]}: {_profile.Name}";
    public string AppName => _profile.Name;
    public string LaunchKindLabel => _profile.LaunchKind == LaunchKind.Desktop ? "Desktop GUI" : "CLI (Terminal)";
    public bool CanEdit => _editCallback is not null;

    public ObservableCollection<string> Caveats { get; } = [];
    public ObservableCollection<DetectedProcessRowViewModel> DetectedProcesses { get; } = [];
    public ObservableCollection<HistoryItemViewModel> RecentHistory { get; } = [];

    public Action? CloseAction { get; set; }

    public async Task RefreshAsync()
    {
        var probe = _discovery.Resolve(_profile);
        TargetFound = probe.Found;
        ResolvedTargetDisplay = probe.Found && !string.IsNullOrWhiteSpace(probe.ResolvedPath)
            ? probe.ResolvedPath
            : probe.ErrorMessage ?? _profile.ExecutableOrCommand;

        var stack = probe.StackInfo;
        PackagingDisplay = stack is null
            ? (_profile.LaunchKind == LaunchKind.Cli ? "CLI" : "Не определено")
            : stack.PackagingKind.ToString();
        RuntimeStackDisplay = stack is null
            ? (_profile.LaunchKind == LaunchKind.Cli ? "Terminal / Shell" : "Не определено")
            : stack.RuntimeStack.ToString();
        ApplicabilityDisplay = FormatHelpers.GetApplicabilityLabel(
            _profile.ApplicabilityHint ?? stack?.ApplicabilityLevel ?? ProxyApplicabilityLevel.Unverified);
        CompatibilitySummary = !string.IsNullOrWhiteSpace(_profile.CompatibilityNote)
            ? _profile.CompatibilityNote
            : stack?.Summary ?? probe.DisplaySource ?? "Нет сведений.";

        MsixInfoDisplay = stack?.ResolvedPackageRoot ?? string.Empty;
        HasMsixInfo = !string.IsNullOrWhiteSpace(MsixInfoDisplay);

        Caveats.Clear();
        if (stack is not null)
        {
            foreach (var caveat in stack.Caveats)
            {
                Caveats.Add(caveat);
            }
        }
        HasCaveats = Caveats.Count > 0;

        var proxies = await _proxyService.GetAllAsync();
        var defaultProxy = await _proxyService.GetDefaultAsync();
        var byId = proxies.ToDictionary(p => p.Id);
        var (proxyLabel, _) = ApplicationsViewModel.ResolveProxySummary(_profile, byId, defaultProxy, Loc);
        ProxySummary = proxyLabel;

        var flags = new List<string> { "HTTP_PROXY / HTTPS_PROXY / ALL_PROXY" };
        if (_profile.UseNodeEnvProxy)
        {
            flags.Add("NODE_USE_ENV_PROXY=1");
        }
        if (_profile.LaunchKind == LaunchKind.Desktop && _profile.UseChromiumProxyFlag)
        {
            flags.Add("--proxy-server");
        }
        StrategySummary = _profile.ProxyMode == ProxyAssignmentMode.DirectNoProxy
            ? Loc["ProxyModeDirect"]
            : string.Join(" + ", flags);

        DetectedProcesses.Clear();
        if (_profile.LaunchKind == LaunchKind.Desktop && probe.Found && !string.IsNullOrWhiteSpace(probe.ResolvedPath))
        {
            var inspection = _processTracker.InspectApplication(_profile, probe.ResolvedPath);
            var confirmedPids = inspection.ConfirmedProcesses.Select(p => p.ProcessId).ToHashSet();
            var ambiguousPids = inspection.AmbiguousProcesses.Select(p => p.ProcessId).ToHashSet();

            ProcessDetectionSummary = inspection.BuildDiagnosticSummary();
            foreach (var proc in inspection.Processes)
            {
                bool? ownership = confirmedPids.Contains(proc.ProcessId)
                    ? true
                    : ambiguousPids.Contains(proc.ProcessId)
                        ? false
                        : null;
                DetectedProcesses.Add(new DetectedProcessRowViewModel(proc, ownership));
            }
        }
        else
        {
            ProcessDetectionSummary = _profile.LaunchKind == LaunchKind.Cli
                ? "Для CLI-профилей отслеживание процессов не требуется."
                : "Исполняемый файл не найден — инспекция процессов не выполнена.";
        }
        HasDetectedProcesses = DetectedProcesses.Count > 0;

        var allHistory = await _historyRepo.GetAllAsync();
        RecentHistory.Clear();
        foreach (var entry in allHistory.Where(h => h.ApplicationId == _profile.Id).Take(5))
        {
            RecentHistory.Add(new HistoryItemViewModel(entry));
        }
        HasRecentHistory = RecentHistory.Count > 0;
        CopiedToClipboard = false;
    }

    [RelayCommand]
    private Task RefreshDiagnosticsAsync() => RefreshAsync();

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (_copyToClipboardAsync is null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"ProxyDock Diagnostics — {AppName}");
        sb.AppendLine($"Kind: {LaunchKindLabel}");
        sb.AppendLine($"Target: {ResolvedTargetDisplay} (Found: {TargetFound})");
        sb.AppendLine($"Packaging: {PackagingDisplay} | Stack: {RuntimeStackDisplay}");
        sb.AppendLine($"Applicability: {ApplicabilityDisplay}");
        sb.AppendLine($"Proxy: {ProxySummary}");
        sb.AppendLine($"Strategy: {StrategySummary}");
        if (HasMsixInfo)
        {
            sb.AppendLine($"MSIX Package Root: {MsixInfoDisplay}");
        }
        sb.AppendLine($"Compatibility: {CompatibilitySummary}");
        sb.AppendLine($"Process Detection: {ProcessDetectionSummary}");
        foreach (var p in DetectedProcesses)
        {
            sb.AppendLine($"  - {p.PidText} {p.Name} [{p.RoleLabel}] {p.DetailText}");
        }

        await _copyToClipboardAsync(sb.ToString());
        CopiedToClipboard = true;
    }

    [RelayCommand]
    private async Task EditProfileAsync()
    {
        CloseAction?.Invoke();
        if (_editCallback is not null)
        {
            await _editCallback();
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}
