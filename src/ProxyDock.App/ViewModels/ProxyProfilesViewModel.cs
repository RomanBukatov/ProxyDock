using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDock.App.Services;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.App.ViewModels;

public sealed partial class ProxyProfilesViewModel : ViewModelBase
{
    private readonly IProxyProfileService _proxyService;
    private readonly IProxyConnectivityTester _tester;
    private readonly IDialogService _dialogs;
    private readonly AppNotificationService _notifications;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTests = new();

    [ObservableProperty]
    private bool _hasProxies;

    public ProxyProfilesViewModel(
        ILocalizationService loc,
        IProxyProfileService proxyService,
        IProxyConnectivityTester tester,
        IDialogService dialogs,
        AppNotificationService notifications)
        : base(loc)
    {
        _proxyService = proxyService;
        _tester = tester;
        _dialogs = dialogs;
        _notifications = notifications;

        _proxyService.ProxiesChanged += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<ProxyCardViewModel> Proxies { get; } = [];

    public async Task RefreshAsync()
    {
        var list = await _proxyService.GetAllAsync().ConfigureAwait(false);
        var cards = list
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .Select(p =>
            {
                var card = new ProxyCardViewModel(
                    p,
                    Loc,
                    TestOrCancelProxyAsync,
                    EditProxyAsync,
                    DuplicateProxyAsync,
                    SetDefaultProxyAsync,
                    DeleteProxyAsync);
                card.IsTesting = _activeTests.ContainsKey(p.Id);
                return card;
            })
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Proxies.Clear();
            foreach (var c in cards)
            {
                Proxies.Add(c);
            }

            HasProxies = Proxies.Count > 0;
        });
    }

    [RelayCommand]
    public async Task AddProxyAsync()
    {
        var created = await _dialogs.ShowProxyEditorAsync(null);
        if (created is null)
        {
            return;
        }

        // ProxyEditorWindow уже сохраняет результат внутри ProxyProfile? Нет, ProxyEditorWindow возвращает модель;
        // пароли передаются через ProxyEditorWindow.ViewModel — см. DialogService.
        _notifications.Show("Прокси сохранён", $"Профиль «{created.Name}» добавлен.", NotificationSeverity.Success);
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        var tasks = Proxies.Where(c => !c.IsTesting).Select(TestOrCancelProxyAsync).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task TestOrCancelProxyAsync(ProxyCardViewModel card)
    {
        if (_activeTests.TryGetValue(card.Profile.Id, out var existingCts))
        {
            existingCts.Cancel();
            return;
        }

        var cts = new CancellationTokenSource();
        if (!_activeTests.TryAdd(card.Profile.Id, cts))
        {
            cts.Dispose();
            return;
        }

        card.IsTesting = true;
        card.Status = ProxyCheckStatus.Testing;
        card.StatusMessage = "Выполняется TCP и HTTPS проверка через прокси...";

        try
        {
            var result = await _tester.TestAsync(card.Profile, cts.Token);
            card.Status = result.Status;
            card.StatusMessage = result.Message;
            card.LatencyDisplay = (result.HttpsLatencyMs ?? result.TcpLatencyMs) is long ms ? $"{ms} мс" : "—";
            card.ExternalIpDisplay = string.IsNullOrWhiteSpace(result.ExternalIp) ? "—" : result.ExternalIp;
            card.CheckedAtDisplay = result.CheckedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");

            await _proxyService.UpdateCheckResultAsync(card.Profile.Id, result);
        }
        catch (OperationCanceledException)
        {
            card.Status = card.Profile.LastCheckStatus;
            card.StatusMessage = "Проверка отменена пользователем.";
        }
        catch (Exception ex)
        {
            card.Status = ProxyCheckStatus.Unreachable;
            card.StatusMessage = $"Ошибка проверки: {ex.Message}";
        }
        finally
        {
            _activeTests.TryRemove(card.Profile.Id, out _);
            cts.Dispose();
            card.IsTesting = false;
        }
    }

    private async Task EditProxyAsync(ProxyCardViewModel card)
    {
        var updated = await _dialogs.ShowProxyEditorAsync(card.Profile);
        if (updated is not null)
        {
            _notifications.Show("Прокси обновлён", $"Изменения для «{updated.Name}» сохранены.", NotificationSeverity.Success);
        }
    }

    private async Task DuplicateProxyAsync(ProxyCardViewModel card)
    {
        var copy = await _proxyService.DuplicateAsync(card.Profile.Id);
        _notifications.Show("Прокси продублирован", $"Создана копия «{copy.Name}».", NotificationSeverity.Info);
    }

    private async Task SetDefaultProxyAsync(ProxyCardViewModel card)
    {
        await _proxyService.SetDefaultAsync(card.Profile.Id);
        _notifications.Show("Прокси по умолчанию", $"«{card.Name}» выбран как прокси по умолчанию.", NotificationSeverity.Success);
    }

    private async Task DeleteProxyAsync(ProxyCardViewModel card)
    {
        var ok = await _dialogs.ShowConfirmationAsync(
            "Удалить прокси-профиль",
            $"Удалить «{card.Name}» ({card.EndpointDisplay})? Сохранённые учётные данные DPAPI также будут удалены.",
            "Удалить",
            "Отмена",
            isDanger: true);
        if (!ok)
        {
            return;
        }

        if (_activeTests.TryRemove(card.Profile.Id, out var activeCts))
        {
            activeCts.Cancel();
            activeCts.Dispose();
        }

        await _proxyService.DeleteAsync(card.Profile.Id);
        _notifications.Show("Прокси удалён", $"Профиль «{card.Name}» удалён.", NotificationSeverity.Info);
    }
}
