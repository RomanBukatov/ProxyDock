using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ProxyDock.App.ViewModels;
using ProxyDock.App.Views;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly ILocalizationService _loc;
    private readonly IProxyProfileService _proxyService;
    private readonly IProxyConnectivityTester _proxyTester;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly IProcessTracker _processTracker;
    private readonly ILaunchHistoryRepository _historyRepo;
    private readonly IUpdateService _updateService;
    private Window? _owner;
    private Action? _requestAppShutdown;

    public DialogService(
        ILocalizationService loc,
        IProxyProfileService proxyService,
        IProxyConnectivityTester proxyTester,
        IExecutableDiscoveryService discovery,
        IProcessTracker processTracker,
        ILaunchHistoryRepository historyRepo,
        IUpdateService updateService)
    {
        _loc = loc;
        _proxyService = proxyService;
        _proxyTester = proxyTester;
        _discovery = discovery;
        _processTracker = processTracker;
        _historyRepo = historyRepo;
        _updateService = updateService;
    }

    public void AttachOwner(Window owner, Action? requestAppShutdown = null)
    {
        _owner = owner;
        _requestAppShutdown = requestAppShutdown;
    }

    public async Task<ApplicationProfile?> ShowApplicationEditorAsync(ApplicationProfile? initialProfile)
    {
        if (_owner is null)
        {
            return null;
        }

        var proxies = await _proxyService.GetAllAsync();
        var vm = new ApplicationEditorViewModel(_loc, _discovery, this, proxies, initialProfile);
        var window = new ApplicationEditorWindow { DataContext = vm };
        vm.CloseRequested = ok => window.Close(ok);

        var accepted = await window.ShowDialog<bool>(_owner);
        return accepted ? vm.SavedProfile : null;
    }

    public async Task<ProxyProfile?> ShowProxyEditorAsync(ProxyProfile? initialProfile)
    {
        if (_owner is null)
        {
            return null;
        }

        var existingProxies = await _proxyService.GetAllAsync();
        var defaultByInitialState = initialProfile is null && existingProxies.Count == 0;
        var window = new ProxyEditorWindow();
        var vm = new ProxyEditorViewModel(
            _loc,
            _proxyTester,
            initialProfile,
            suggestDefault: defaultByInitialState,
            readClipboardAsync: async () =>
            {
                var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
                return clipboard is null ? null : await clipboard.TryGetTextAsync();
            });

        window.DataContext = vm;
        vm.CloseRequested = ok => window.Close(ok);

        var accepted = await window.ShowDialog<bool>(_owner);
        if (!accepted || vm.SavedProfile is null)
        {
            return null;
        }

        return await _proxyService.SaveAsync(vm.SavedProfile, vm.EnteredNewPassword, vm.ShouldClearPassword);
    }

    public async Task ShowApplicationDiagnosticsAsync(ApplicationProfile profile, Func<Task>? onEditRequested = null)
    {
        if (_owner is null)
        {
            return;
        }

        var dialog = new AppDiagnosticsDialog();
        var vm = new AppDiagnosticsViewModel(
            profile,
            _loc,
            _discovery,
            _processTracker,
            _historyRepo,
            _proxyService,
            copyToClipboardAsync: async text =>
            {
                var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(text);
                }
            },
            editCallback: onEditRequested);

        dialog.DataContext = vm;
        vm.CloseAction = () => dialog.Close();
        await vm.RefreshAsync();
        await dialog.ShowDialog(_owner);
    }

    public async Task ShowUpdateDialogAsync(UpdateReleaseInfo release)
    {
        if (_owner is null)
        {
            return;
        }

        var dialog = new UpdateDialog();
        var vm = new UpdateDialogViewModel(
            release,
            _updateService,
            _loc,
            _requestAppShutdown ?? (() => _owner.Close()));
        dialog.DataContext = vm;
        vm.CloseAction = () => dialog.Close();
        await dialog.ShowDialog(_owner);
    }

    public async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string confirmText = "Продолжить",
        string? cancelText = "Отмена",
        bool isDanger = false)
    {
        if (_owner is null)
        {
            return false;
        }

        var vm = new ConfirmationDialogViewModel(title, message, confirmText, cancelText, isDanger);
        var dialog = new ConfirmationDialog { DataContext = vm };
        vm.CloseAction = ok => dialog.Close(ok);

        return await dialog.ShowDialog<bool>(_owner);
    }

    public async Task<string?> PickExecutableFileAsync()
    {
        var storage = _owner is null ? null : TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите исполняемый файл или скрипт",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Исполняемые файлы Windows") { Patterns = ["*.exe", "*.cmd", "*.bat", "*.ps1"] },
                FilePickerFileTypes.All
            ]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? initialPath = null)
    {
        var storage = _owner is null ? null : TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            startFolder = await storage.TryGetFolderFromPathAsync(initialPath);
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickExportFileAsync(string defaultFileName)
    {
        var storage = _owner is null ? null : TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт конфигурации ProxyDock (без паролей)",
            SuggestedFileName = defaultFileName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON Configuration") { Patterns = ["*.json"] }]
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickImportFileAsync()
    {
        var storage = _owner is null ? null : TopLevel.GetTopLevel(_owner)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт конфигурации ProxyDock",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON Configuration") { Patterns = ["*.json"] }]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
