using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.App.Services;

/// <summary>
/// Единый координатор запуска приложений и создания профилей из шаблонов для Dashboard и Applications.
/// </summary>
public sealed class AppLaunchCoordinator
{
    private readonly IApplicationLauncher _launcher;
    private readonly IApplicationProfileService _appService;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly IDialogService _dialogs;
    private readonly AppNotificationService _notifications;
    private readonly ILocalizationService _loc;

    public AppLaunchCoordinator(
        IApplicationLauncher launcher,
        IApplicationProfileService appService,
        IExecutableDiscoveryService discovery,
        IDialogService dialogs,
        AppNotificationService notifications,
        ILocalizationService loc)
    {
        _launcher = launcher;
        _appService = appService;
        _discovery = discovery;
        _dialogs = dialogs;
        _notifications = notifications;
        _loc = loc;
    }

    public async Task<LaunchResult> LaunchWithPromptsAsync(ApplicationProfile profile, bool forceDirectNoProxy = false)
    {
        var result = await _launcher.LaunchAsync(new LaunchRequest
        {
            Application = profile,
            ForceDirectNoProxy = forceDirectNoProxy
        });

        if (result.RequiresExistingProcessConfirmation)
        {
            var pids = string.Join(", ", result.AlreadyRunningProcessIds);
            var message =
                $"{result.Message}\n\n{result.SuggestedAction}\n\n" +
                $"Если вы уверены, что хотите открыть второй экземпляр (PID: {pids}), нажмите «Запустить всё равно».";

            var proceed = await _dialogs.ShowConfirmationAsync(
                title: $"Приложение «{profile.Name}» уже работает",
                message: message,
                confirmText: "Запустить всё равно",
                cancelText: "Отмена (закрою сам)");

            if (!proceed)
            {
                _notifications.Show(
                    "Запуск отложен",
                    $"Закройте «{profile.Name}» (включая значок в трее) и повторите запуск.",
                    NotificationSeverity.Info);
                return result;
            }

            result = await _launcher.LaunchAsync(new LaunchRequest
            {
                Application = profile,
                ForceDirectNoProxy = forceDirectNoProxy,
                IgnoreAlreadyRunningWarning = true
            });
        }

        PublishLaunchNotification(profile, result);
        return result;
    }

    public void PublishLaunchNotification(ApplicationProfile profile, LaunchResult result)
    {
        if (result.Status == LaunchStatus.Failed)
        {
            var detail = string.IsNullOrWhiteSpace(result.SuggestedAction)
                ? result.Message
                : $"{result.Message} {result.SuggestedAction}";
            _notifications.Show(
                _loc.Format("ToastLaunchErrorTitle", profile.Name),
                detail,
                NotificationSeverity.Error,
                autoDismiss: false,
                showOpenHistoryAction: true,
                relatedApplicationId: profile.Id);
            return;
        }

        if (result.Status is not (LaunchStatus.Started or LaunchStatus.StartedWithWarning))
        {
            return;
        }

        var launchedTitle = _loc.Format("ToastAppLaunchedTitle", profile.Name);
        var proxySubtitle = result.Strategy == LaunchRoutingStrategy.DirectNoProxy
            ? _loc["ProxyModeDirect"]
            : result.ProxyName ?? _loc["ProxyModeDefault"];

        if (result.AlreadyRunningMainInstanceDetected && result.Strategy != LaunchRoutingStrategy.DirectNoProxy)
        {
            _notifications.Show(
                launchedTitle,
                _loc["ToastAlreadyRunningProxyWarning"],
                NotificationSeverity.Warning,
                autoDismiss: true,
                relatedApplicationId: profile.Id);
            return;
        }

        var actionableWarning = result.Warnings.FirstOrDefault();
        if (result.Status == LaunchStatus.StartedWithWarning && !string.IsNullOrWhiteSpace(actionableWarning))
        {
            _notifications.Show(
                launchedTitle,
                actionableWarning,
                NotificationSeverity.Warning,
                autoDismiss: true,
                showOpenHistoryAction: true,
                relatedApplicationId: profile.Id);
            return;
        }

        _notifications.Show(
            launchedTitle,
            proxySubtitle,
            NotificationSeverity.Success,
            autoDismiss: true,
            relatedApplicationId: profile.Id);
    }

    public async Task<ApplicationProfile?> AddFromTemplateAsync(string templateKey)
    {
        var discovery = _discovery.DiscoverTemplate(templateKey);
        var profile = BuiltInAppTemplates.CreateByKey(templateKey, discovery.Found ? discovery.ResolvedPath : null);
        if (profile is null)
        {
            return null;
        }

        if (discovery.Found && discovery.StackInfo is not null)
        {
            profile.ApplicabilityHint = discovery.StackInfo.ApplicabilityLevel;
            profile.CompatibilityNote = discovery.StackInfo.Summary;
        }

        var existingApps = await _appService.GetAllAsync();
        var duplicateCount = existingApps.Count(a => string.Equals(a.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase));
        if (duplicateCount > 0)
        {
            profile.Name = $"{profile.Name} #{duplicateCount + 1}";
        }

        // Если путь не найден автоматически, сразу открываем редактор для ручного выбора .exe или папки.
        if (!discovery.Found && profile.LaunchKind == LaunchKind.Desktop)
        {
            var edited = await _dialogs.ShowApplicationEditorAsync(profile);
            if (edited is null)
            {
                return null;
            }

            var savedManual = await _appService.SaveAsync(edited);
            _notifications.Show("Профиль добавлен", $"Приложение «{savedManual.Name}» сохранено.", NotificationSeverity.Success);
            return savedManual;
        }

        var saved = await _appService.SaveAsync(profile);
        _notifications.Show(
            "Профиль добавлен",
            discovery.Found
                ? $"«{saved.Name}» добавлен ({discovery.ResolvedPath})."
                : $"«{saved.Name}» добавлен.",
            NotificationSeverity.Success);
        return saved;
    }

    public async Task<int> AddAllDetectedDesktopTemplatesAsync()
    {
        var existingApps = await _appService.GetAllAsync();
        var added = 0;

        foreach (var tpl in BuiltInAppTemplates.DesktopTemplates)
        {
            if (existingApps.Any(a => string.Equals(a.TemplateKey, tpl.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var discovery = _discovery.DiscoverTemplate(tpl.Key);
            if (!discovery.Found || string.IsNullOrWhiteSpace(discovery.ResolvedPath))
            {
                continue;
            }

            var profile = tpl.CreateProfile(discovery.ResolvedPath);
            if (discovery.StackInfo is not null)
            {
                profile.ApplicabilityHint = discovery.StackInfo.ApplicabilityLevel;
                profile.CompatibilityNote = discovery.StackInfo.Summary;
            }

            await _appService.SaveAsync(profile);
            added++;
        }

        if (added > 0)
        {
            _notifications.Show(
                "Готовые профили добавлены",
                $"Автоматически добавлено найденных desktop-приложений: {added}.",
                NotificationSeverity.Success);
        }
        else
        {
            _notifications.Show(
                "Новых установленных шаблонов не найдено",
                "Либо все обнаруженные приложения уже добавлены, либо их путь нужно указать вручную.",
                NotificationSeverity.Info);
        }

        return added;
    }
}
