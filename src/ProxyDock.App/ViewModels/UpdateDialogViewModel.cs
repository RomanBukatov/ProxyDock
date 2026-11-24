using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Infrastructure.Updates;

namespace ProxyDock.App.ViewModels;

public sealed partial class UpdateDialogViewModel : ViewModelBase
{
    private readonly IUpdateService _updateService;
    private readonly UpdateReleaseInfo _release;
    private readonly Action _requestAppShutdown;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private int _downloadPercentage;

    [ObservableProperty]
    private string _downloadStatusText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public UpdateDialogViewModel(
        UpdateReleaseInfo release,
        IUpdateService updateService,
        ILocalizationService loc,
        Action requestAppShutdown)
        : base(loc)
    {
        _release = release;
        _updateService = updateService;
        _requestAppShutdown = requestAppShutdown;
    }

    public string WindowTitle => Loc["UpdateDialogTitle"];
    public string CurrentVersionDisplay => _release.CurrentVersion;
    public string AvailableVersionDisplay => _release.Version;
    public bool CanSelfUpdate => _release.CanSelfUpdate;
    public bool IsStandalone => !_release.CanSelfUpdate;
    public bool HasDownloadSize => _release.DownloadSizeBytes is > 0;
    public string DownloadSizeDisplay => _release.DownloadSizeBytes is > 0
        ? FormatBytes(_release.DownloadSizeBytes.Value)
        : string.Empty;

    public string ReleaseNotesDisplay => string.IsNullOrWhiteSpace(_release.ReleaseNotesMarkdown)
        ? Loc["UpdateNoReleaseNotes"]
        : _release.ReleaseNotesMarkdown.Trim();

    public bool CanStartUpdate => CanSelfUpdate && !IsDownloading;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public Action? CloseAction { get; set; }

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(CanStartUpdate));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private async Task UpdateAndRestartAsync()
    {
        if (!CanSelfUpdate || IsDownloading)
        {
            return;
        }

        ErrorMessage = null;
        IsDownloading = true;
        DownloadPercentage = 0;
        DownloadStatusText = Loc["UpdateDownloading"];

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    DownloadPercentage = p.Percentage;
                    DownloadStatusText = p.StatusMessage;
                });
            });

            await _updateService.DownloadUpdateAsync(_release, progress, _downloadCts.Token);
            DownloadStatusText = Loc["UpdateApplyingAndRestarting"];
            _updateService.ApplyUpdateAndRestart(_release, _requestAppShutdown);
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void Later()
    {
        try
        {
            _downloadCts?.Cancel();
        }
        catch
        {
            // Игнорируем отмену
        }

        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var trustedUrl = ProxyAwareFileDownloader.ValidateTrustedReleaseUrl(_release.ReleasePageUrl);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = trustedUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:F1} KB";
        }

        var mb = kb / 1024.0;
        return $"{mb:F1} MB";
    }
}
