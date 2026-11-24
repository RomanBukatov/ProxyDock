using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ProxyDock.Core.Models;

namespace ProxyDock.App.Services;

public interface IDialogService
{
    Task<ApplicationProfile?> ShowApplicationEditorAsync(ApplicationProfile? initialProfile);
    Task<ProxyProfile?> ShowProxyEditorAsync(ProxyProfile? initialProfile);
    Task ShowApplicationDiagnosticsAsync(ApplicationProfile profile, Func<Task>? onEditRequested = null);
    Task ShowUpdateDialogAsync(UpdateReleaseInfo release);
    Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "Продолжить", string? cancelText = "Отмена", bool isDanger = false);
    Task<string?> PickExecutableFileAsync();
    Task<string?> PickFolderAsync(string title, string? initialPath = null);
    Task<string?> PickExportFileAsync(string defaultFileName);
    Task<string?> PickImportFileAsync();
}
