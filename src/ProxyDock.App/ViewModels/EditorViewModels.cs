using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;
using ProxyDock.Core.Validation;

namespace ProxyDock.App.ViewModels;

public sealed partial class ConfirmationDialogViewModel : ObservableObject
{
    public ConfirmationDialogViewModel(
        string title,
        string message,
        string confirmText,
        string? cancelText,
        bool isDanger)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        IsDanger = isDanger;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string? CancelText { get; }
    public bool ShowCancel => !string.IsNullOrWhiteSpace(CancelText);
    public bool IsDanger { get; }
    public bool Result { get; private set; }

    public Action<bool>? CloseAction { get; set; }

    [RelayCommand]
    private void Confirm()
    {
        Result = true;
        CloseAction?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        CloseAction?.Invoke(false);
    }
}

public sealed partial class ProxyEditorViewModel : ViewModelBase
{
    private readonly ProxyProfile _working;
    private readonly IProxyConnectivityTester? _tester;
    private readonly Func<Task<string?>>? _readClipboardAsync;
    private CancellationTokenSource? _testCts;
    private bool _isApplyingPaste;

    [ObservableProperty]
    private string _quickPasteInput = string.Empty;

    [ObservableProperty]
    private bool _quickPasteApplied;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port = 8080;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _hasSavedPassword;

    [ObservableProperty]
    private bool _clearSavedPassword;

    [ObservableProperty]
    private string _noProxy = "localhost,127.0.0.1,::1";

    [ObservableProperty]
    private int _checkTimeoutSeconds = 10;

    [ObservableProperty]
    private string _diagnosticEndpointUrl = "https://api.ipify.org?format=json";

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    private bool _isAdvancedExpanded;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _hasTestResult;

    [ObservableProperty]
    private ProxyCheckStatus _testStatus = ProxyCheckStatus.Unknown;

    [ObservableProperty]
    private string _testResultTitle = string.Empty;

    [ObservableProperty]
    private string _testResultMessage = string.Empty;

    [ObservableProperty]
    private string? _validationError;

    public ProxyEditorViewModel(
        ILocalizationService loc,
        IProxyConnectivityTester? tester = null,
        ProxyProfile? initial = null,
        bool suggestDefault = false,
        Func<Task<string?>>? readClipboardAsync = null)
        : base(loc)
    {
        _tester = tester;
        _readClipboardAsync = readClipboardAsync;
        IsEditMode = initial is not null;
        _working = initial is null
            ? new ProxyProfile { IsDefault = suggestDefault }
            : new ProxyProfile
            {
                Id = initial.Id,
                Name = initial.Name,
                Protocol = initial.Protocol,
                Host = initial.Host,
                Port = initial.Port,
                Username = initial.Username,
                HasCredentials = initial.HasCredentials,
                NoProxy = initial.NoProxy,
                IsDefault = initial.IsDefault,
                CheckTimeoutSeconds = initial.CheckTimeoutSeconds,
                DiagnosticEndpointUrl = initial.DiagnosticEndpointUrl,
                LastCheckStatus = initial.LastCheckStatus,
                LastCheckedAtUtc = initial.LastCheckedAtUtc,
                LastLatencyMs = initial.LastLatencyMs,
                LastExternalIp = initial.LastExternalIp,
                LastCheckMessage = initial.LastCheckMessage,
                CreatedAtUtc = initial.CreatedAtUtc,
                UpdatedAtUtc = initial.UpdatedAtUtc
            };

        _name = _working.Name;
        _host = _working.Host;
        _port = _working.Port;
        _username = _working.Username ?? string.Empty;
        _hasSavedPassword = _working.HasCredentials;
        _noProxy = _working.NoProxy;
        _checkTimeoutSeconds = _working.CheckTimeoutSeconds;
        _diagnosticEndpointUrl = _working.DiagnosticEndpointUrl;
        _isDefault = _working.IsDefault;
    }

    public bool IsEditMode { get; }
    public string WindowTitle => IsEditMode ? Loc["ProxyEditorEditTitle"] : Loc["ProxyEditorNewTitle"];
    public string PasswordWatermark => HasSavedPassword ? Loc["ProxyPasswordPlaceholderKeep"] : Loc["ProxyPasswordPlaceholderNew"];
    public string TestButtonText => IsTesting ? Loc["BtnTestingCancel"] : Loc["ProxyTestBtn"];
    public IBrush TestStatusForeground => FormatHelpers.GetProxyStatusFg(IsTesting ? ProxyCheckStatus.Testing : TestStatus);
    public IBrush TestStatusBackground => FormatHelpers.GetProxyStatusBg(IsTesting ? ProxyCheckStatus.Testing : TestStatus);

    public ProxyProfile? SavedProfile { get; private set; }
    public string? EnteredNewPassword { get; private set; }
    public bool ShouldClearPassword { get; private set; }
    public Action<bool>? CloseRequested { get; set; }

    partial void OnQuickPasteInputChanged(string value)
    {
        if (_isApplyingPaste || string.IsNullOrWhiteSpace(value))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                QuickPasteApplied = false;
            }

            return;
        }

        _ = TryApplyRawProxyString(value);
    }

    partial void OnHostChanged(string value)
    {
        if (_isApplyingPaste || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Если пользователь вставил полный IP:PORT или HTTP URL прямо в поле Host — автоматически разбираем и распределяем по полям
        _ = TryApplyRawProxyString(value);
    }

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(TestButtonText));
        OnPropertyChanged(nameof(TestStatusForeground));
        OnPropertyChanged(nameof(TestStatusBackground));
    }

    partial void OnTestStatusChanged(ProxyCheckStatus value)
    {
        OnPropertyChanged(nameof(TestStatusForeground));
        OnPropertyChanged(nameof(TestStatusBackground));
    }

    public bool TryApplyRawProxyString(string? rawInput)
    {
        if (!ProxyInputParser.TryParse(rawInput, out var parsed) || parsed is null)
        {
            return false;
        }

        _isApplyingPaste = true;
        try
        {
            Host = parsed.Host;
            Port = parsed.Port;

            if (!string.IsNullOrEmpty(parsed.Username))
            {
                Username = parsed.Username;
            }

            if (parsed.Password is not null)
            {
                Password = parsed.Password;
                ClearSavedPassword = false;
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = $"{ProxyUriBuilder.FormatHost(parsed.Host)}:{parsed.Port}";
            }

            QuickPasteApplied = true;
            ValidationError = null;
            return true;
        }
        finally
        {
            _isApplyingPaste = false;
        }
    }

    [RelayCommand]
    private async Task PasteFromClipboardAsync()
    {
        if (_readClipboardAsync is null)
        {
            return;
        }

        var text = await _readClipboardAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _isApplyingPaste = true;
        try
        {
            QuickPasteInput = text.Trim();
        }
        finally
        {
            _isApplyingPaste = false;
        }

        if (!TryApplyRawProxyString(text))
        {
            ValidationError = "Не удалось распознать формат прокси в буфере обмена. Ожидается IP:PORT или http://login:pass@host:port.";
        }
    }

    [RelayCommand]
    private async Task TestOrCancelAsync()
    {
        if (_tester is null)
        {
            return;
        }

        if (_testCts is not null)
        {
            _testCts.Cancel();
            return;
        }

        var draft = BuildDraftProfile(allowFallbackNameForTest: true);
        var newPass = string.IsNullOrEmpty(Password) ? null : Password;
        var clear = ClearSavedPassword || string.IsNullOrWhiteSpace(Username);
        var validation = ProxyProfileValidator.Validate(draft, newPass, clear);
        if (!validation.IsValid)
        {
            ExpandAdvancedIfHasErrors(validation);
            ValidationError = string.Join(Environment.NewLine, validation.Errors.Select(e => "• " + e.Message));
            return;
        }

        ValidationError = null;
        ProxyCredentials? credsOverride = null;
        if (!clear && !string.IsNullOrWhiteSpace(draft.Username) && newPass is not null)
        {
            credsOverride = new ProxyCredentials(draft.Username.Trim(), newPass);
        }

        using var cts = new CancellationTokenSource();
        _testCts = cts;
        IsTesting = true;
        HasTestResult = true;
        TestStatus = ProxyCheckStatus.Testing;
        TestResultTitle = FormatHelpers.GetProxyStatusLabel(ProxyCheckStatus.Testing);
        TestResultMessage = $"{ProxyUriBuilder.FormatHost(draft.Host)}:{draft.Port}";

        try
        {
            var result = await _tester.TestAsync(draft, credsOverride, ignoreSavedCredentials: clear, cts.Token);
            TestStatus = result.Status;

            var latencyMs = result.HttpsLatencyMs ?? result.TcpLatencyMs;
            var metaParts = new List<string> { FormatHelpers.GetProxyStatusLabel(result.Status) };
            if (latencyMs is long ms)
            {
                metaParts.Add($"{ms} мс");
            }

            if (!string.IsNullOrWhiteSpace(result.ExternalIp))
            {
                metaParts.Add($"IP {result.ExternalIp}");
            }

            TestResultTitle = string.Join(" · ", metaParts);
            TestResultMessage = result.Message;

            _working.LastCheckStatus = result.Status;
            _working.LastCheckedAtUtc = result.CheckedAtUtc;
            _working.LastLatencyMs = latencyMs;
            _working.LastExternalIp = result.ExternalIp;
            _working.LastCheckMessage = result.Message;
        }
        catch (OperationCanceledException)
        {
            TestStatus = ProxyCheckStatus.Unknown;
            TestResultTitle = "Проверка отменена";
            TestResultMessage = string.Empty;
        }
        catch (Exception ex)
        {
            TestStatus = ProxyCheckStatus.Unreachable;
            TestResultTitle = FormatHelpers.GetProxyStatusLabel(ProxyCheckStatus.Unreachable);
            TestResultMessage = ex.Message;
        }
        finally
        {
            _testCts = null;
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        CancelRunningTest();

        _working.Name = Name;
        _working.Host = Host;
        _working.Port = Port;
        _working.Username = Username;
        _working.NoProxy = NoProxy;
        _working.CheckTimeoutSeconds = CheckTimeoutSeconds;
        _working.DiagnosticEndpointUrl = DiagnosticEndpointUrl;
        _working.IsDefault = IsDefault;

        var newPass = string.IsNullOrEmpty(Password) ? null : Password;
        var clear = ClearSavedPassword || string.IsNullOrWhiteSpace(Username);
        var validation = ProxyProfileValidator.Validate(_working, newPass, clear);
        if (!validation.IsValid)
        {
            ExpandAdvancedIfHasErrors(validation);
            ValidationError = string.Join(Environment.NewLine, validation.Errors.Select(e => "• " + e.Message));
            return;
        }

        ValidationError = null;
        SavedProfile = _working;
        EnteredNewPassword = newPass;
        ShouldClearPassword = clear;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRunningTest();
        CloseRequested?.Invoke(false);
    }

    private ProxyProfile BuildDraftProfile(bool allowFallbackNameForTest)
    {
        var draftName = string.IsNullOrWhiteSpace(Name) && allowFallbackNameForTest
            ? "Проверка прокси"
            : Name;

        return new ProxyProfile
        {
            Id = _working.Id,
            Name = draftName,
            Protocol = _working.Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            HasCredentials = _working.HasCredentials,
            NoProxy = NoProxy,
            IsDefault = IsDefault,
            CheckTimeoutSeconds = CheckTimeoutSeconds,
            DiagnosticEndpointUrl = DiagnosticEndpointUrl
        };
    }

    private void ExpandAdvancedIfHasErrors(ValidationResult validation)
    {
        if (validation.Errors.Any(e =>
                e.PropertyName is nameof(ProxyProfile.NoProxy)
                    or nameof(ProxyProfile.CheckTimeoutSeconds)
                    or nameof(ProxyProfile.DiagnosticEndpointUrl)))
        {
            IsAdvancedExpanded = true;
        }
    }

    private void CancelRunningTest()
    {
        try
        {
            _testCts?.Cancel();
        }
        catch
        {
            // Игнорируем при закрытии диалога
        }
    }
}

public sealed partial class ApplicationEditorViewModel : ViewModelBase
{
    private readonly IExecutableDiscoveryService _discovery;
    private readonly IDialogService _dialogs;
    private readonly ApplicationProfile _working;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private LaunchKind _launchKind = LaunchKind.Desktop;

    [ObservableProperty]
    private string _executableOrCommand = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private ProxyAssignmentMode _proxyMode = ProxyAssignmentMode.InheritDefault;

    [ObservableProperty]
    private ProxyProfile? _selectedProxy;

    [ObservableProperty]
    private bool _useChromiumProxyFlag;

    [ObservableProperty]
    private bool _launchInTerminal;

    [ObservableProperty]
    private bool _keepTerminalOpen = true;

    [ObservableProperty]
    private int _terminalOverrideIndex;

    [ObservableProperty]
    private string _noProxyOverride = string.Empty;

    [ObservableProperty]
    private string _relatedProcessNamesText = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string? _validationError;

    [ObservableProperty]
    private string _discoverySummary = string.Empty;

    [ObservableProperty]
    private bool _discoverySucceeded;

    [ObservableProperty]
    private DesktopAppStackInfo? _inspectedStack;

    public ApplicationEditorViewModel(
        ILocalizationService loc,
        IExecutableDiscoveryService discovery,
        IDialogService dialogs,
        IReadOnlyList<ProxyProfile> availableProxies,
        ApplicationProfile? initial = null)
        : base(loc)
    {
        _discovery = discovery;
        _dialogs = dialogs;
        AvailableProxies = new ObservableCollection<ProxyProfile>(availableProxies);

        IsEditMode = initial is not null;
        _working = initial is null
            ? new ApplicationProfile { LaunchKind = LaunchKind.Desktop }
            : new ApplicationProfile
            {
                Id = initial.Id,
                Name = initial.Name,
                LaunchKind = initial.LaunchKind,
                ExecutableOrCommand = initial.ExecutableOrCommand,
                WorkingDirectory = initial.WorkingDirectory,
                Arguments = initial.Arguments,
                ProxyMode = initial.ProxyMode,
                ProxyProfileId = initial.ProxyProfileId,
                UseChromiumProxyFlag = initial.UseChromiumProxyFlag,
                LaunchInTerminal = initial.LaunchInTerminal,
                KeepTerminalOpen = initial.KeepTerminalOpen,
                TerminalOverride = initial.TerminalOverride,
                NoProxyOverride = initial.NoProxyOverride,
                CustomEnvironmentVariables = initial.CustomEnvironmentVariables
                    .Select(e => new EnvironmentVariableEntry { Key = e.Key, Value = e.Value, IsSecret = e.IsSecret })
                    .ToList(),
                RelatedProcessNames = [.. initial.RelatedProcessNames],
                TemplateKey = initial.TemplateKey,
                ApplicabilityHint = initial.ApplicabilityHint,
                CompatibilityNote = initial.CompatibilityNote,
                Notes = initial.Notes,
                CreatedAtUtc = initial.CreatedAtUtc,
                UpdatedAtUtc = initial.UpdatedAtUtc,
                LastLaunchedAtUtc = initial.LastLaunchedAtUtc
            };

        _name = _working.Name;
        _launchKind = _working.LaunchKind;
        _executableOrCommand = _working.ExecutableOrCommand;
        _workingDirectory = _working.WorkingDirectory ?? string.Empty;
        _arguments = _working.Arguments ?? string.Empty;
        _proxyMode = _working.ProxyMode;
        _selectedProxy = AvailableProxies.FirstOrDefault(p => p.Id == _working.ProxyProfileId)
            ?? AvailableProxies.FirstOrDefault(p => p.IsDefault);
        _useChromiumProxyFlag = _working.UseChromiumProxyFlag;
        _launchInTerminal = _working.LaunchInTerminal;
        _keepTerminalOpen = _working.KeepTerminalOpen;
        _terminalOverrideIndex = _working.TerminalOverride switch
        {
            TerminalPreference.WindowsTerminal => 1,
            TerminalPreference.PowerShell => 2,
            TerminalPreference.PowerShellCore => 3,
            _ => 0
        };
        _noProxyOverride = _working.NoProxyOverride ?? string.Empty;
        _relatedProcessNamesText = string.Join(", ", _working.RelatedProcessNames);
        _notes = _working.Notes ?? string.Empty;

        foreach (var entry in _working.CustomEnvironmentVariables)
        {
            EnvironmentVariables.Add(new EnvVarRowViewModel(RemoveEnvVar)
            {
                Key = entry.Key,
                Value = entry.Value,
                IsSecret = entry.IsSecret
            });
        }

        InspectCurrentTarget();
    }

    public bool IsEditMode { get; }
    public string WindowTitle => IsEditMode ? "Редактирование приложения" : "Добавить приложение";
    public ObservableCollection<ProxyProfile> AvailableProxies { get; }
    public ObservableCollection<EnvVarRowViewModel> EnvironmentVariables { get; } = [];
    public ApplicationProfile? SavedProfile { get; private set; }
    public Action<bool>? CloseRequested { get; set; }

    public int LaunchKindIndex
    {
        get => LaunchKind == LaunchKind.Desktop ? 0 : 1;
        set => LaunchKind = value == 1 ? LaunchKind.Cli : LaunchKind.Desktop;
    }

    public int ProxyModeIndex
    {
        get => ProxyMode switch
        {
            ProxyAssignmentMode.InheritDefault => 0,
            ProxyAssignmentMode.Specific => 1,
            _ => 2
        };
        set => ProxyMode = value switch
        {
            1 => ProxyAssignmentMode.Specific,
            2 => ProxyAssignmentMode.DirectNoProxy,
            _ => ProxyAssignmentMode.InheritDefault
        };
    }

    public bool IsDesktopMode => LaunchKind == LaunchKind.Desktop;
    public bool IsCliMode => LaunchKind == LaunchKind.Cli;
    public bool ShowSpecificProxyPicker => ProxyMode == ProxyAssignmentMode.Specific;
    public string TargetFieldLabel => IsDesktopMode ? "Путь к .exe файлу" : "CLI-команда или путь к файлу";
    public string TargetWatermark => IsDesktopMode ? @"C:\Path\To\App.exe" : "claude, codex или путь к exe/cmd/ps1";
    public bool ShowAuthChromiumWarning => IsDesktopMode && UseChromiumProxyFlag && ProxyMode != ProxyAssignmentMode.DirectNoProxy
        && (SelectedProxy?.HasCredentials == true || (ProxyMode == ProxyAssignmentMode.InheritDefault && AvailableProxies.Any(p => p.IsDefault && p.HasCredentials)));
    public bool ShowNonChromiumFlagWarning => IsDesktopMode && UseChromiumProxyFlag && InspectedStack is not null && !InspectedStack.SupportsChromiumProxyFlag;
    public string StackCaveatsText => InspectedStack is null || InspectedStack.Caveats.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, InspectedStack.Caveats.Select(c => "• " + c));
    public bool HasStackCaveats => !string.IsNullOrWhiteSpace(StackCaveatsText);

    partial void OnLaunchKindChanged(LaunchKind value)
    {
        if (value == LaunchKind.Cli && !LaunchInTerminal)
        {
            LaunchInTerminal = true;
        }

        OnPropertyChanged(nameof(LaunchKindIndex));
        OnPropertyChanged(nameof(IsDesktopMode));
        OnPropertyChanged(nameof(IsCliMode));
        OnPropertyChanged(nameof(TargetFieldLabel));
        OnPropertyChanged(nameof(TargetWatermark));
        OnPropertyChanged(nameof(ShowAuthChromiumWarning));
        OnPropertyChanged(nameof(ShowNonChromiumFlagWarning));
        InspectCurrentTarget();
    }

    partial void OnProxyModeChanged(ProxyAssignmentMode value)
    {
        OnPropertyChanged(nameof(ProxyModeIndex));
        OnPropertyChanged(nameof(ShowSpecificProxyPicker));
        OnPropertyChanged(nameof(ShowAuthChromiumWarning));
    }

    partial void OnSelectedProxyChanged(ProxyProfile? value)
        => OnPropertyChanged(nameof(ShowAuthChromiumWarning));

    partial void OnUseChromiumProxyFlagChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAuthChromiumWarning));
        OnPropertyChanged(nameof(ShowNonChromiumFlagWarning));
    }

    partial void OnInspectedStackChanged(DesktopAppStackInfo? value)
    {
        OnPropertyChanged(nameof(ShowNonChromiumFlagWarning));
        OnPropertyChanged(nameof(StackCaveatsText));
        OnPropertyChanged(nameof(HasStackCaveats));
    }

    [RelayCommand]
    private async Task BrowseExecutableAsync()
    {
        var path = await _dialogs.PickExecutableFileAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ExecutableOrCommand = path;
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = Path.GetFileNameWithoutExtension(path);
            }

            InspectCurrentTarget();
        }
    }

    [RelayCommand]
    private async Task BrowseWorkingDirectoryAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Выберите рабочую папку", WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            WorkingDirectory = folder;
        }
    }

    [RelayCommand]
    private void InspectCurrentTarget()
    {
        if (string.IsNullOrWhiteSpace(ExecutableOrCommand))
        {
            DiscoverySucceeded = false;
            DiscoverySummary = "Укажите файл или команду для проверки.";
            InspectedStack = null;
            return;
        }

        var result = _discovery.ResolveTarget(LaunchKind, ExecutableOrCommand, _working.TemplateKey);
        DiscoverySucceeded = result.Found;
        InspectedStack = result.StackInfo;
        if (!result.Found)
        {
            DiscoverySummary = result.ErrorMessage ?? "Цель не найдена.";
            return;
        }

        if (LaunchKind == LaunchKind.Desktop && result.StackInfo is not null)
        {
            DiscoverySummary = $"Найдено ({result.DisplaySource}): {result.ResolvedPath}\nСтек: {result.StackInfo.Summary}";
        }
        else
        {
            DiscoverySummary = $"Найдено ({result.DisplaySource}): {result.ResolvedPath}";
        }
    }

    [RelayCommand]
    private void AddEnvVar() => EnvironmentVariables.Add(new EnvVarRowViewModel(RemoveEnvVar));

    [RelayCommand]
    private void RemoveEnvVar(EnvVarRowViewModel? row)
    {
        if (row is not null)
        {
            EnvironmentVariables.Remove(row);
        }
    }

    [RelayCommand]
    private void Save()
    {
        _working.Name = Name;
        _working.LaunchKind = LaunchKind;
        _working.ExecutableOrCommand = ExecutableOrCommand;
        _working.WorkingDirectory = WorkingDirectory;
        _working.Arguments = Arguments;
        _working.ProxyMode = ProxyMode;
        _working.ProxyProfileId = ProxyMode == ProxyAssignmentMode.Specific ? SelectedProxy?.Id : null;
        _working.UseChromiumProxyFlag = LaunchKind == LaunchKind.Desktop && UseChromiumProxyFlag;
        _working.LaunchInTerminal = LaunchKind == LaunchKind.Cli && LaunchInTerminal;
        _working.KeepTerminalOpen = KeepTerminalOpen;
        _working.TerminalOverride = TerminalOverrideIndex switch
        {
            1 => TerminalPreference.WindowsTerminal,
            2 => TerminalPreference.PowerShell,
            3 => TerminalPreference.PowerShellCore,
            _ => null
        };
        _working.NoProxyOverride = NoProxyOverride;
        _working.Notes = Notes;
        _working.RelatedProcessNames = RelatedProcessNamesText
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _working.CustomEnvironmentVariables = EnvironmentVariables
            .Where(r => !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new EnvironmentVariableEntry
            {
                Key = r.Key.Trim(),
                Value = r.Value ?? string.Empty,
                IsSecret = r.IsSecret
            })
            .ToList();

        if (InspectedStack is not null && LaunchKind == LaunchKind.Desktop)
        {
            _working.ApplicabilityHint = InspectedStack.ApplicabilityLevel;
            if (string.IsNullOrWhiteSpace(_working.CompatibilityNote))
            {
                _working.CompatibilityNote = InspectedStack.Summary;
            }
        }

        var validation = ApplicationProfileValidator.Validate(_working);
        if (!validation.IsValid)
        {
            ValidationError = string.Join(Environment.NewLine, validation.Errors.Select(e => "• " + e.Message));
            return;
        }

        ValidationError = null;
        SavedProfile = _working;
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
