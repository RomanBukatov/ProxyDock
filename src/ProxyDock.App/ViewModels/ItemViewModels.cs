using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.App.ViewModels;

/// <summary>
/// Компактный элемент автоматически обнаруженного приложения.
/// </summary>
public sealed partial class DetectedAppItemViewModel : ObservableObject
{
    private readonly Func<DetectedAppItemViewModel, Task> _addAction;

    public DetectedAppItemViewModel(
        string key,
        string name,
        Bitmap? icon,
        ILocalizationService loc,
        Func<DetectedAppItemViewModel, Task> addAction)
    {
        Key = key;
        Name = name;
        Icon = icon;
        Loc = loc;
        _addAction = addAction;
    }

    public string Key { get; }
    public string Name { get; }
    public Bitmap? Icon { get; }
    public ILocalizationService Loc { get; }

    [RelayCommand]
    private Task AddAsync() => _addAction(this);
}

/// <summary>
/// Элемент выбора прокси по умолчанию в компактном селекторе главного экрана.
/// </summary>
public sealed class DefaultProxyOptionViewModel
{
    public DefaultProxyOptionViewModel(ProxyProfile profile)
    {
        Profile = profile;
    }

    public ProxyProfile Profile { get; }
    public Guid Id => Profile.Id;
    public string DisplayName => $"{Profile.Name} ({ProxyUriBuilder.FormatHost(Profile.Host)}:{Profile.Port})";
    public IBrush StatusDotBrush => FormatHelpers.GetProxyStatusFg(Profile.LastCheckStatus);
}

/// <summary>
/// Интерактивная карточка с плюсом в конце сетки приложений.
/// </summary>
public sealed class AddAppCardTileViewModel
{
    public AddAppCardTileViewModel(ApplicationsViewModel parent)
    {
        Parent = parent;
    }

    public ApplicationsViewModel Parent { get; }
    public ILocalizationService Loc => Parent.Loc;
}

/// <summary>
/// Карточка приложения в адаптивной сетке лаунчера.
/// </summary>
public sealed partial class AppCardViewModel : ObservableObject
{
    private readonly Func<AppCardViewModel, bool, Task> _launchAction;
    private readonly Func<AppCardViewModel, ProxyAssignmentMode, Guid?, Task> _assignProxyAction;
    private readonly Func<AppCardViewModel, Task> _editAction;
    private readonly Func<AppCardViewModel, Task> _duplicateAction;
    private readonly Func<AppCardViewModel, Task> _deleteAction;
    private readonly Func<AppCardViewModel, Task> _diagnosticsAction;
    private readonly Action<AppCardViewModel> _openFolderAction;

    public AppCardViewModel(
        ApplicationProfile profile,
        Bitmap? icon,
        string assignedProxyLabel,
        ProxyCheckStatus proxyStatus,
        bool targetFound,
        string? resolvedPath,
        DesktopAppStackInfo? stackInfo,
        ILocalizationService loc,
        IReadOnlyList<ProxyProfile> availableProxies,
        ProxyProfile? defaultProxy,
        Func<AppCardViewModel, bool, Task> launchAction,
        Func<AppCardViewModel, ProxyAssignmentMode, Guid?, Task> assignProxyAction,
        Func<AppCardViewModel, Task> editAction,
        Func<AppCardViewModel, Task> duplicateAction,
        Func<AppCardViewModel, Task> deleteAction,
        Func<AppCardViewModel, Task> diagnosticsAction,
        Action<AppCardViewModel> openFolderAction)
    {
        Profile = profile;
        Icon = icon;
        AssignedProxyLabel = assignedProxyLabel;
        ProxyStatus = proxyStatus;
        TargetFound = targetFound;
        ResolvedPath = resolvedPath;
        StackInfo = stackInfo;
        Loc = loc;
        AvailableProxies = availableProxies;
        DefaultProxy = defaultProxy;
        _launchAction = launchAction;
        _assignProxyAction = assignProxyAction;
        _editAction = editAction;
        _duplicateAction = duplicateAction;
        _deleteAction = deleteAction;
        _diagnosticsAction = diagnosticsAction;
        _openFolderAction = openFolderAction;
    }

    public ApplicationProfile Profile { get; }
    public Bitmap? Icon { get; }
    public string AssignedProxyLabel { get; }
    public ProxyCheckStatus ProxyStatus { get; }
    public bool TargetFound { get; }
    public string? ResolvedPath { get; }
    public DesktopAppStackInfo? StackInfo { get; }
    public ILocalizationService Loc { get; }
    public IReadOnlyList<ProxyProfile> AvailableProxies { get; }
    public ProxyProfile? DefaultProxy { get; }

    public string Name => Profile.Name;
    public string FirstLetter => string.IsNullOrWhiteSpace(Profile.Name) ? "A" : Profile.Name.Trim()[..1].ToUpperInvariant();
    public bool IsDesktop => Profile.LaunchKind == LaunchKind.Desktop;
    public bool IsCli => Profile.LaunchKind == LaunchKind.Cli;
    public bool HasDesktopIcon => IsDesktop && Icon is not null;
    public bool HasDesktopFallbackIcon => IsDesktop && Icon is null;
    [ObservableProperty]
    private string _runtimeState = string.Empty;

    public Func<AppCardViewModel, LifecycleAction, Task>? ManageAsync { get; init; }
    public enum LifecycleAction { Close, Force, Restart }

    public string TargetDisplay => string.IsNullOrWhiteSpace(ResolvedPath) ? Profile.ExecutableOrCommand : ResolvedPath;
    public bool CanLaunch => TargetFound;
    public bool ShowMissingWarning => !TargetFound;
    public bool ShowProxyDot => TargetFound;
    public string SubtitleText => TargetFound ? AssignedProxyLabel : Loc["TargetMissingShort"];
    public IBrush SubtitleBrush => TargetFound ? FormatHelpers.Brush("TextSecondaryBrush") : FormatHelpers.Brush("ErrorBrush");
    public IBrush ProxyStatusDotBrush => Profile.ProxyMode == ProxyAssignmentMode.DirectNoProxy
        ? FormatHelpers.Brush("TextMutedBrush")
        : FormatHelpers.GetProxyStatusFg(ProxyStatus);

    public Task AssignProxyAsync(ProxyAssignmentMode mode, Guid? proxyId)
        => _assignProxyAction(this, mode, proxyId);

    public IAsyncRelayCommand CreateAssignProxyCommand(ProxyAssignmentMode mode, Guid? proxyId)
        => new AsyncRelayCommand(() => AssignProxyAsync(mode, proxyId));

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private Task LaunchAsync() => _launchAction(this, false);

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private Task LaunchDirectAsync() => _launchAction(this, true);

    [RelayCommand]
    private Task EditAsync() => _editAction(this);

    [RelayCommand]
    private Task DuplicateAsync() => _duplicateAction(this);

    [RelayCommand]
    private Task DiagnosticsAsync() => _diagnosticsAction(this);

    [RelayCommand]
    private Task DeleteAsync() => _deleteAction(this);

    [RelayCommand]
    private void OpenFolder() => _openFolderAction(this);
}

/// <summary>
/// Строка прокси-профиля с поддержкой отмены асинхронной проверки.
/// </summary>
public sealed partial class ProxyCardViewModel : ObservableObject
{
    private readonly Func<ProxyCardViewModel, Task> _testOrCancelAction;
    private readonly Func<ProxyCardViewModel, Task> _editAction;
    private readonly Func<ProxyCardViewModel, Task> _duplicateAction;
    private readonly Func<ProxyCardViewModel, Task> _setDefaultAction;
    private readonly Func<ProxyCardViewModel, Task> _deleteAction;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private ProxyCheckStatus _status;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _latencyDisplay = string.Empty;

    [ObservableProperty]
    private string _externalIpDisplay = string.Empty;

    [ObservableProperty]
    private string _checkedAtDisplay = string.Empty;

    public ProxyCardViewModel(
        ProxyProfile profile,
        ILocalizationService loc,
        Func<ProxyCardViewModel, Task> testOrCancelAction,
        Func<ProxyCardViewModel, Task> editAction,
        Func<ProxyCardViewModel, Task> duplicateAction,
        Func<ProxyCardViewModel, Task> setDefaultAction,
        Func<ProxyCardViewModel, Task> deleteAction)
    {
        Profile = profile;
        Loc = loc;
        _testOrCancelAction = testOrCancelAction;
        _editAction = editAction;
        _duplicateAction = duplicateAction;
        _setDefaultAction = setDefaultAction;
        _deleteAction = deleteAction;

        _status = profile.LastCheckStatus;
        _statusMessage = profile.LastCheckMessage ?? string.Empty;
        _latencyDisplay = profile.LastLatencyMs is long ms ? $"{ms} мс" : string.Empty;
        _externalIpDisplay = string.IsNullOrWhiteSpace(profile.LastExternalIp) ? string.Empty : profile.LastExternalIp;
        _checkedAtDisplay = profile.LastCheckedAtUtc is DateTimeOffset dt
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : string.Empty;
    }

    public ProxyProfile Profile { get; }
    public ILocalizationService Loc { get; }
    public string Name => Profile.Name;
    public string EndpointDisplay => $"{ProxyUriBuilder.FormatHost(Profile.Host)}:{Profile.Port}";
    public bool IsDefault => Profile.IsDefault;
    public bool HasAuth => Profile.HasCredentials || !string.IsNullOrWhiteSpace(Profile.Username);
    public string AuthBadgeText => string.IsNullOrWhiteSpace(Profile.Username)
        ? "Авторизация"
        : $"Авторизация ({Profile.Username})";

    public string StatusLabel => IsTesting ? "Проверка..." : FormatHelpers.GetProxyStatusLabel(Status);
    public IBrush StatusBackground => FormatHelpers.GetProxyStatusBg(IsTesting ? ProxyCheckStatus.Testing : Status);
    public IBrush StatusForeground => FormatHelpers.GetProxyStatusFg(IsTesting ? ProxyCheckStatus.Testing : Status);
    public IBrush StatusDotBrush => StatusForeground;
    public string TestButtonText => IsTesting ? "Отмена" : "Проверить";
    public bool HasStatusDetail => !string.IsNullOrWhiteSpace(StatusMessage);

    public string MetricsDisplay
    {
        get
        {
            var parts = new List<string> { EndpointDisplay };
            if (!string.IsNullOrWhiteSpace(LatencyDisplay))
            {
                parts.Add(LatencyDisplay);
            }

            if (!string.IsNullOrWhiteSpace(ExternalIpDisplay))
            {
                parts.Add($"IP {ExternalIpDisplay}");
            }

            return string.Join(" · ", parts);
        }
    }

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
        OnPropertyChanged(nameof(StatusDotBrush));
        OnPropertyChanged(nameof(TestButtonText));
    }

    partial void OnStatusChanged(ProxyCheckStatus value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
        OnPropertyChanged(nameof(StatusDotBrush));
    }

    partial void OnStatusMessageChanged(string value)
        => OnPropertyChanged(nameof(HasStatusDetail));

    partial void OnLatencyDisplayChanged(string value)
        => OnPropertyChanged(nameof(MetricsDisplay));

    partial void OnExternalIpDisplayChanged(string value)
        => OnPropertyChanged(nameof(MetricsDisplay));

    [RelayCommand]
    private Task TestOrCancelAsync() => _testOrCancelAction(this);

    [RelayCommand]
    private Task EditAsync() => _editAction(this);

    [RelayCommand]
    private Task DuplicateAsync() => _duplicateAction(this);

    [RelayCommand]
    private Task SetDefaultAsync() => _setDefaultAction(this);

    [RelayCommand]
    private Task DeleteAsync() => _deleteAction(this);
}

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(LaunchHistoryEntry entry)
    {
        Entry = entry;
    }

    public LaunchHistoryEntry Entry { get; }
    public string TimestampText => Entry.TimestampUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public string AppName => Entry.ApplicationName;
    public string KindLabel => Entry.LaunchKind == LaunchKind.Desktop ? "Desktop" : "CLI";
    public string StrategyLabel => Entry.Strategy switch
    {
        LaunchRoutingStrategy.EnvironmentAndChromium => "Env + --proxy-server",
        LaunchRoutingStrategy.EnvironmentOnly => "Env процесса",
        LaunchRoutingStrategy.CliTerminal => "Терминал",
        LaunchRoutingStrategy.DirectNoProxy => "Без прокси",
        _ => Entry.Strategy.ToString()
    };
    public string ProxyLabel => string.IsNullOrWhiteSpace(Entry.ProxyDisplayAddress)
        ? (Entry.ProxyName ?? "Без прокси")
        : $"{Entry.ProxyName} ({Entry.ProxyDisplayAddress})";
    public string PidText => Entry.ProcessId is int pid ? $"PID {pid}" : "—";
    public string StatusLabel => Entry.Status switch
    {
        LaunchStatus.Started => "Запущено",
        LaunchStatus.StartedWithWarning => "С предупреждением",
        LaunchStatus.Failed => "Ошибка",
        LaunchStatus.Cancelled => "Отменено",
        _ => Entry.Status.ToString()
    };
    public IBrush StatusBackground => Entry.Status switch
    {
        LaunchStatus.Started => FormatHelpers.Brush("SuccessSoftBrush"),
        LaunchStatus.StartedWithWarning => FormatHelpers.Brush("WarningSoftBrush"),
        LaunchStatus.Failed => FormatHelpers.Brush("ErrorSoftBrush"),
        _ => FormatHelpers.Brush("ElevatedSurfaceBrush")
    };
    public IBrush StatusForeground => Entry.Status switch
    {
        LaunchStatus.Started => FormatHelpers.Brush("SuccessBrush"),
        LaunchStatus.StartedWithWarning => FormatHelpers.Brush("WarningBrush"),
        LaunchStatus.Failed => FormatHelpers.Brush("ErrorBrush"),
        _ => FormatHelpers.Brush("TextSecondaryBrush")
    };
    public string ApplicabilityBadge => FormatHelpers.GetApplicabilityLabel(Entry.ApplicabilityLevel);
    public bool HasWarnings => Entry.Warnings.Count > 0;
    public string WarningsText => string.Join(" | ", Entry.Warnings);
    public bool HasProcessDetectionSummary => !string.IsNullOrWhiteSpace(Entry.ProcessDetectionSummary);
    public string ProcessDetectionSummary => Entry.ProcessDetectionSummary ?? string.Empty;
    public bool HasDiagnosticNotes => Entry.DiagnosticNotes.Count > 0;
    public string DiagnosticNotesText => string.Join(" | ", Entry.DiagnosticNotes);
}

public sealed partial class EnvVarRowViewModel : ObservableObject
{
    private readonly Action<EnvVarRowViewModel>? _onRemove;

    public EnvVarRowViewModel(Action<EnvVarRowViewModel>? onRemove = null)
    {
        _onRemove = onRemove;
    }

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isSecret;

    [RelayCommand]
    private void Remove() => _onRemove?.Invoke(this);
}

internal static class FormatHelpers
{
    public static IBrush Brush(string key)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
            && Avalonia.Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush b)
        {
            return b;
        }

        return Brushes.Gray;
    }

    public static string GetApplicabilityLabel(ProxyApplicabilityLevel level) => level switch
    {
        ProxyApplicabilityLevel.Confirmed => "Подтверждено",
        ProxyApplicabilityLevel.LikelyCompatible => "Совместимо",
        ProxyApplicabilityLevel.NeedsVerification => "Требует проверки",
        _ => "Не проверено"
    };

    public static IBrush GetApplicabilityBg(ProxyApplicabilityLevel level) => level switch
    {
        ProxyApplicabilityLevel.Confirmed => Brush("SuccessSoftBrush"),
        ProxyApplicabilityLevel.LikelyCompatible => Brush("InfoSoftBrush"),
        ProxyApplicabilityLevel.NeedsVerification => Brush("WarningSoftBrush"),
        _ => Brush("ElevatedSurfaceBrush")
    };

    public static IBrush GetApplicabilityFg(ProxyApplicabilityLevel level) => level switch
    {
        ProxyApplicabilityLevel.Confirmed => Brush("SuccessBrush"),
        ProxyApplicabilityLevel.LikelyCompatible => Brush("InfoBrush"),
        ProxyApplicabilityLevel.NeedsVerification => Brush("WarningBrush"),
        _ => Brush("TextMutedBrush")
    };

    public static string GetProxyStatusLabel(ProxyCheckStatus status) => status switch
    {
        ProxyCheckStatus.Reachable => "Доступен",
        ProxyCheckStatus.TcpOnly => "Только TCP",
        ProxyCheckStatus.AuthRequired => "Нужна авторизация (407)",
        ProxyCheckStatus.Unreachable => "Недоступен",
        ProxyCheckStatus.Invalid => "Некорректный",
        ProxyCheckStatus.Testing => "Проверка...",
        _ => "Не проверялся"
    };

    public static IBrush GetProxyStatusBg(ProxyCheckStatus status) => status switch
    {
        ProxyCheckStatus.Reachable => Brush("SuccessSoftBrush"),
        ProxyCheckStatus.TcpOnly or ProxyCheckStatus.AuthRequired => Brush("WarningSoftBrush"),
        ProxyCheckStatus.Unreachable or ProxyCheckStatus.Invalid => Brush("ErrorSoftBrush"),
        ProxyCheckStatus.Testing => Brush("InfoSoftBrush"),
        _ => Brush("ElevatedSurfaceBrush")
    };

    public static IBrush GetProxyStatusFg(ProxyCheckStatus status) => status switch
    {
        ProxyCheckStatus.Reachable => Brush("SuccessBrush"),
        ProxyCheckStatus.TcpOnly or ProxyCheckStatus.AuthRequired => Brush("WarningBrush"),
        ProxyCheckStatus.Unreachable or ProxyCheckStatus.Invalid => Brush("ErrorBrush"),
        ProxyCheckStatus.Testing => Brush("InfoBrush"),
        _ => Brush("TextMutedBrush")
    };
}
