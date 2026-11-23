using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;

namespace ProxyDock.App.ViewModels;

/// <summary>
/// Карточка предустановленного шаблона (в приоритете — desktop GUI-программы).
/// </summary>
public sealed partial class TemplateCardViewModel : ObservableObject
{
    private readonly Func<TemplateCardViewModel, Task> _addAction;

    public TemplateCardViewModel(
        string key,
        string name,
        string subtitle,
        bool isDetected,
        string? detectedPath,
        string stackSummary,
        ProxyApplicabilityLevel applicability,
        bool alreadyAdded,
        Bitmap? icon,
        Func<TemplateCardViewModel, Task> addAction)
    {
        Key = key;
        Name = name;
        Subtitle = subtitle;
        IsDetected = isDetected;
        DetectedPath = detectedPath;
        StackSummary = stackSummary;
        Applicability = applicability;
        AlreadyAdded = alreadyAdded;
        Icon = icon;
        _addAction = addAction;
    }

    public string Key { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public bool IsDetected { get; }
    public string? DetectedPath { get; }
    public string StackSummary { get; }
    public ProxyApplicabilityLevel Applicability { get; }
    public bool AlreadyAdded { get; }
    public Bitmap? Icon { get; }

    public string DetectionText => IsDetected ? "Найден на этом ПК" : "Не найден автоматически";
    public string PathDisplay => IsDetected ? (DetectedPath ?? string.Empty) : "Можно добавить и указать .exe вручную";
    public string ApplicabilityBadge => FormatHelpers.GetApplicabilityLabel(Applicability);
    public IBrush ApplicabilityBackground => FormatHelpers.GetApplicabilityBg(Applicability);
    public IBrush ApplicabilityForeground => FormatHelpers.GetApplicabilityFg(Applicability);
    public string AddButtonText => AlreadyAdded ? "Добавить ещё" : "Добавить";

    [RelayCommand]
    private Task AddAsync() => _addAction(this);
}

/// <summary>
/// Карточка приложения для Dashboard и списка Applications.
/// </summary>
public sealed partial class AppCardViewModel : ObservableObject
{
    private readonly Func<AppCardViewModel, bool, Task> _launchAction;
    private readonly Func<AppCardViewModel, Task> _editAction;
    private readonly Func<AppCardViewModel, Task> _duplicateAction;
    private readonly Func<AppCardViewModel, Task> _deleteAction;
    private readonly Action<AppCardViewModel> _openFolderAction;

    public AppCardViewModel(
        ApplicationProfile profile,
        Bitmap? icon,
        string assignedProxyLabel,
        ProxyCheckStatus proxyStatus,
        bool targetFound,
        string? resolvedPath,
        DesktopAppStackInfo? stackInfo,
        Func<AppCardViewModel, bool, Task> launchAction,
        Func<AppCardViewModel, Task> editAction,
        Func<AppCardViewModel, Task> duplicateAction,
        Func<AppCardViewModel, Task> deleteAction,
        Action<AppCardViewModel> openFolderAction)
    {
        Profile = profile;
        Icon = icon;
        AssignedProxyLabel = assignedProxyLabel;
        ProxyStatus = proxyStatus;
        TargetFound = targetFound;
        ResolvedPath = resolvedPath;
        StackInfo = stackInfo;
        _launchAction = launchAction;
        _editAction = editAction;
        _duplicateAction = duplicateAction;
        _deleteAction = deleteAction;
        _openFolderAction = openFolderAction;
    }

    public ApplicationProfile Profile { get; }
    public Bitmap? Icon { get; }
    public string AssignedProxyLabel { get; }
    public ProxyCheckStatus ProxyStatus { get; }
    public bool TargetFound { get; }
    public string? ResolvedPath { get; }
    public DesktopAppStackInfo? StackInfo { get; }

    public string Name => Profile.Name;
    public bool IsDesktop => Profile.LaunchKind == LaunchKind.Desktop;
    public bool IsCli => Profile.LaunchKind == LaunchKind.Cli;
    public string KindLabel => IsDesktop ? "Desktop GUI" : "CLI (терминал)";
    public string TargetDisplay => string.IsNullOrWhiteSpace(ResolvedPath) ? Profile.ExecutableOrCommand : ResolvedPath;
    public bool ShowChromiumBadge => IsDesktop && Profile.UseChromiumProxyFlag;
    public bool ShowMissingWarning => !TargetFound;
    public string? CompatibilitySummary => !string.IsNullOrWhiteSpace(Profile.CompatibilityNote)
        ? Profile.CompatibilityNote
        : StackInfo?.Summary;
    public bool HasCompatibilitySummary => !string.IsNullOrWhiteSpace(CompatibilitySummary);
    public bool HasWorkingDir => !string.IsNullOrWhiteSpace(Profile.WorkingDirectory);
    public string WorkingDirDisplay => Profile.WorkingDirectory ?? string.Empty;

    public ProxyApplicabilityLevel Applicability
        => Profile.ApplicabilityHint ?? StackInfo?.ApplicabilityLevel ?? ProxyApplicabilityLevel.Unverified;

    public string ApplicabilityBadge => IsCli ? "CLI-окружение" : FormatHelpers.GetApplicabilityLabel(Applicability);
    public IBrush ApplicabilityBackground => IsCli
        ? FormatHelpers.Brush("InfoSoftBrush")
        : FormatHelpers.GetApplicabilityBg(Applicability);
    public IBrush ApplicabilityForeground => IsCli
        ? FormatHelpers.Brush("InfoBrush")
        : FormatHelpers.GetApplicabilityFg(Applicability);

    public IBrush ProxyStatusDotBrush => FormatHelpers.GetProxyStatusFg(ProxyStatus);

    public string LastLaunchedText => Profile.LastLaunchedAtUtc is DateTimeOffset dt
        ? $"Запускалось: {dt.ToLocalTime():dd.MM HH:mm}"
        : "Ещё не запускалось";

    [RelayCommand]
    private Task LaunchAsync() => _launchAction(this, false);

    [RelayCommand]
    private Task LaunchDirectAsync() => _launchAction(this, true);

    [RelayCommand]
    private Task EditAsync() => _editAction(this);

    [RelayCommand]
    private Task DuplicateAsync() => _duplicateAction(this);

    [RelayCommand]
    private Task DeleteAsync() => _deleteAction(this);

    [RelayCommand]
    private void OpenFolder() => _openFolderAction(this);
}

/// <summary>
/// Карточка прокси-профиля с поддержкой отмены асинхронной проверки.
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
        Func<ProxyCardViewModel, Task> testOrCancelAction,
        Func<ProxyCardViewModel, Task> editAction,
        Func<ProxyCardViewModel, Task> duplicateAction,
        Func<ProxyCardViewModel, Task> setDefaultAction,
        Func<ProxyCardViewModel, Task> deleteAction)
    {
        Profile = profile;
        _testOrCancelAction = testOrCancelAction;
        _editAction = editAction;
        _duplicateAction = duplicateAction;
        _setDefaultAction = setDefaultAction;
        _deleteAction = deleteAction;

        _status = profile.LastCheckStatus;
        _statusMessage = profile.LastCheckMessage ?? "Проверка ещё не выполнялась.";
        _latencyDisplay = profile.LastLatencyMs is long ms ? $"{ms} мс" : "—";
        _externalIpDisplay = string.IsNullOrWhiteSpace(profile.LastExternalIp) ? "—" : profile.LastExternalIp;
        _checkedAtDisplay = profile.LastCheckedAtUtc is DateTimeOffset dt
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : "Не проверялся";
    }

    public ProxyProfile Profile { get; }
    public string Name => Profile.Name;
    public string EndpointDisplay => $"{Core.Services.ProxyUriBuilder.FormatHost(Profile.Host)}:{Profile.Port}";
    public bool IsDefault => Profile.IsDefault;
    public bool HasAuth => Profile.HasCredentials || !string.IsNullOrWhiteSpace(Profile.Username);
    public string AuthBadgeText => string.IsNullOrWhiteSpace(Profile.Username)
        ? "Авторизация"
        : $"Авторизация ({Profile.Username})";

    public string StatusLabel => IsTesting ? "Проверка..." : FormatHelpers.GetProxyStatusLabel(Status);
    public IBrush StatusBackground => FormatHelpers.GetProxyStatusBg(IsTesting ? ProxyCheckStatus.Testing : Status);
    public IBrush StatusForeground => FormatHelpers.GetProxyStatusFg(IsTesting ? ProxyCheckStatus.Testing : Status);
    public string TestButtonText => IsTesting ? "Отмена" : "Проверить";

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
        OnPropertyChanged(nameof(TestButtonText));
    }

    partial void OnStatusChanged(ProxyCheckStatus value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
    }

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
        LaunchRoutingStrategy.EnvironmentAndChromium => "Env + Chromium --proxy-server",
        LaunchRoutingStrategy.EnvironmentOnly => "Env процесса",
        LaunchRoutingStrategy.CliTerminal => "Интерактивный терминал",
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
        if (Avalonia.Application.Current is { } app
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
        ProxyCheckStatus.Reachable => "Доступен (HTTPS)",
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
