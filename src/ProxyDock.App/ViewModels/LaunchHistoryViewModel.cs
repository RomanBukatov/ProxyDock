using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.ViewModels;

public sealed partial class LaunchHistoryViewModel : ViewModelBase
{
    private readonly ILaunchHistoryRepository _historyRepo;
    private readonly IDialogService _dialogs;
    private readonly AppNotificationService _notifications;
    private List<HistoryItemViewModel> _all = [];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _statusFilterIndex;

    [ObservableProperty]
    private bool _hasEntries;

    public LaunchHistoryViewModel(
        ILocalizationService loc,
        ILaunchHistoryRepository historyRepo,
        IDialogService dialogs,
        AppNotificationService notifications)
        : base(loc)
    {
        _historyRepo = historyRepo;
        _dialogs = dialogs;
        _notifications = notifications;
    }

    public ObservableCollection<HistoryItemViewModel> Entries { get; } = [];

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnStatusFilterIndexChanged(int value) => ApplyFilter();

    public async Task RefreshAsync()
    {
        var items = await _historyRepo.GetAllAsync().ConfigureAwait(false);
        var mapped = items.Select(i => new HistoryItemViewModel(i)).ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _all = mapped;
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        IEnumerable<HistoryItemViewModel> query = _all;

        query = StatusFilterIndex switch
        {
            1 => query.Where(e => e.Entry.Status == LaunchStatus.Started),
            2 => query.Where(e => e.Entry.Status == LaunchStatus.StartedWithWarning),
            3 => query.Where(e => e.Entry.Status == LaunchStatus.Failed),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            query = query.Where(e =>
                e.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.Entry.ProxyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || e.Entry.Message.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Entries.Clear();
        foreach (var item in query)
        {
            Entries.Add(item);
        }

        HasEntries = Entries.Count > 0;
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var ok = await _dialogs.ShowConfirmationAsync(
            "Очистить историю запусков",
            "Удалить все записи из истории запусков ProxyDock?",
            "Очистить",
            "Отмена",
            isDanger: true);
        if (!ok)
        {
            return;
        }

        await _historyRepo.ClearAsync();
        await RefreshAsync();
        _notifications.Show("История очищена", "Журнал запусков очищен.", NotificationSeverity.Info);
    }
}
