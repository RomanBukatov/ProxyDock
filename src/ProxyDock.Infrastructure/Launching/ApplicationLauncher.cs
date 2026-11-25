using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Launching;

/// <summary>
/// Координирует выбор прокси, проверку запущенных экземпляров, построение плана запуска и старт дочернего процесса
/// с полной изоляцией окружения от ProxyDock и системных настроек Windows.
/// </summary>
public sealed class ApplicationLauncher : IApplicationLauncher
{
    private readonly IEnumerable<ILaunchStrategy> _strategies;
    private readonly IProxyProfileService _proxyService;
    private readonly IApplicationProfileService _appService;
    private readonly ISettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly IExecutableDiscoveryService _discovery;
    private readonly IProcessTracker _processTracker;
    private readonly ILaunchHistoryRepository _historyRepository;
    private readonly ILogger<ApplicationLauncher> _logger;

    public ApplicationLauncher(
        IEnumerable<ILaunchStrategy> strategies,
        IProxyProfileService proxyService,
        IApplicationProfileService appService,
        ISettingsService settingsService,
        ICredentialStore credentialStore,
        IExecutableDiscoveryService discovery,
        IProcessTracker processTracker,
        ILaunchHistoryRepository historyRepository,
        ILogger<ApplicationLauncher> logger)
    {
        _strategies = strategies;
        _proxyService = proxyService;
        _appService = appService;
        _settingsService = settingsService;
        _credentialStore = credentialStore;
        _discovery = discovery;
        _processTracker = processTracker;
        _historyRepository = historyRepository;
        _logger = logger;
    }

    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var app = request.Application;
        var settings = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);

        ProxyProfile? effectiveProxy = null;
        var isDirectNoProxy = request.ForceDirectNoProxy || app.ProxyMode == ProxyAssignmentMode.DirectNoProxy;

        try
        {
            if (!isDirectNoProxy)
            {
                if (request.ProxyOverrideId is Guid overrideId)
                {
                    effectiveProxy = await _proxyService.GetByIdAsync(overrideId, cancellationToken).ConfigureAwait(false)
                        ?? throw new LaunchPreparationException(
                            "Выбранный прокси-профиль не найден.",
                            "Выберите существующий прокси в настройках.");
                }
                else if (app.ProxyMode == ProxyAssignmentMode.Specific)
                {
                    if (app.ProxyProfileId is not Guid specificId)
                    {
                        throw new LaunchPreparationException(
                            $"Для «{app.Name}» выбран режим индивидуального прокси, но профиль не назначен.",
                            "Откройте редактирование приложения и выберите прокси или режим «Без прокси».");
                    }

                    effectiveProxy = await _proxyService.GetByIdAsync(specificId, cancellationToken).ConfigureAwait(false)
                        ?? throw new LaunchPreparationException(
                            $"Назначенный для «{app.Name}» прокси-профиль был удалён.",
                            "Назначьте другой прокси-профиль в настройках приложения.");
                }
                else
                {
                    effectiveProxy = await _proxyService.GetDefaultAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new LaunchPreparationException(
                            "Прокси по умолчанию не настроен.",
                            "Создайте HTTP-прокси и назначьте его по умолчанию, либо запустите приложение без прокси.");
                }
            }

            RunningAppInspection? runningInspection = null;
            if (app.LaunchKind == LaunchKind.Desktop)
            {
                var probe = _discovery.Resolve(app);
                if (probe.Found && !string.IsNullOrWhiteSpace(probe.ResolvedPath))
                {
                    runningInspection = _processTracker.InspectApplication(app, probe.ResolvedPath);

                    if (runningInspection.HasActiveMainInstance
                        && settings.ConfirmBeforeRelaunchingRunningApp
                        && !request.IgnoreAlreadyRunningWarning)
                    {
                        var pidText = string.Join(", ", runningInspection.ActiveMainProcessIds);
                        _logger.LogInformation(
                            "Запуск «{AppName}» приостановлен по настройке подтверждения: {Summary}",
                            app.Name,
                            runningInspection.BuildDiagnosticSummary());

                        return new LaunchResult
                        {
                            Status = LaunchStatus.Cancelled,
                            RequiresExistingProcessConfirmation = true,
                            AlreadyRunningMainInstanceDetected = true,
                            AlreadyRunningProcessIds = runningInspection.ActiveMainProcessIds.ToList(),
                            RunningInstanceInspection = runningInspection,
                            Message = $"Приложение «{app.Name}» уже работает (PID: {pidText}). При повторном запуске управление часто передаётся существующему окну или процессу в трее, из-за чего новые настройки прокси могут не примениться.",
                            SuggestedAction = "Вы можете закрыть приложение полностью и повторить запуск, либо запустить всё равно."
                        };
                    }
                }
            }

            ProxyCredentials? credentials = null;
            if (!isDirectNoProxy && effectiveProxy is not null && (effectiveProxy.HasCredentials || !string.IsNullOrWhiteSpace(effectiveProxy.Username)))
            {
                credentials = await _credentialStore.LoadAsync(effectiveProxy.Id, cancellationToken).ConfigureAwait(false);
                if (credentials is null && effectiveProxy.HasCredentials)
                {
                    throw new LaunchPreparationException(
                        $"Не удалось прочитать сохранённый пароль для прокси «{effectiveProxy.Name}».",
                        "Откройте профиль прокси и сохраните пароль повторно.");
                }
            }

            var strategy = _strategies.FirstOrDefault(s => s.CanHandle(app))
                ?? throw new LaunchPreparationException($"Не найдена стратегия запуска для типа {app.LaunchKind}.");

            var plan = await strategy.BuildPlanAsync(
                app,
                effectiveProxy,
                credentials,
                settings,
                isDirectNoProxy,
                cancellationToken).ConfigureAwait(false);

            return await ExecutePlanAsync(app, effectiveProxy, plan, settings, runningInspection, cancellationToken).ConfigureAwait(false);
        }
        catch (LaunchPreparationException ex)
        {
            _logger.LogWarning("Ошибка подготовки запуска «{AppName}»: {Message}", app.Name, ex.Message);
            return await RecordFailureAsync(
                app,
                effectiveProxy,
                isDirectNoProxy ? LaunchRoutingStrategy.DirectNoProxy : LaunchRoutingStrategy.EnvironmentOnly,
                ex.Message,
                ex.SuggestedAction,
                settings,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Неожиданная ошибка при запуске «{AppName}».", app.Name);
            return await RecordFailureAsync(
                app,
                effectiveProxy,
                LaunchRoutingStrategy.EnvironmentOnly,
                $"Ошибка запуска: {ex.Message}",
                "Проверьте параметры профиля и журнал ProxyDock.",
                settings,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LaunchResult> ExecutePlanAsync(
        ApplicationProfile app,
        ProxyProfile? proxy,
        LaunchPlan plan,
        AppSettings settings,
        RunningAppInspection? runningInspection,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = plan.FileName,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };

        if (!string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            psi.WorkingDirectory = plan.WorkingDirectory;
        }

        foreach (var arg in plan.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        // Применяем изменения окружения ТОЛЬКО на psi.Environment будущего дочернего процесса.
        foreach (var keyToRemove in plan.EnvironmentRemove)
        {
            psi.Environment.Remove(keyToRemove);
        }

        foreach (var (key, value) in plan.EnvironmentSet)
        {
            psi.Environment[key] = value;
        }

        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            CleanupFailedScript(plan.GeneratedScriptPath);
            var (msg, suggestion) = TranslateWin32LaunchError(ex, plan.FileName);
            _logger.LogError(ex, "Win32 ошибка запуска «{AppName}» (NativeErrorCode={Code}).", app.Name, ex.NativeErrorCode);
            return await RecordFailureAsync(app, proxy, plan.Strategy, msg, suggestion, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CleanupFailedScript(plan.GeneratedScriptPath);
            _logger.LogError(ex, "Ошибка старта процесса «{AppName}».", app.Name);
            return await RecordFailureAsync(
                app,
                proxy,
                plan.Strategy,
                $"Не удалось запустить процесс: {ex.Message}",
                "Проверьте путь к исполняемому файлу и права доступа.",
                settings,
                cancellationToken).ConfigureAwait(false);
        }

        if (startedProcess is null)
        {
            CleanupFailedScript(plan.GeneratedScriptPath);
            return await RecordFailureAsync(
                app,
                proxy,
                plan.Strategy,
                "Операционная система не вернула дескриптор нового процесса.",
                "Повторите запуск или проверьте, не перехватывает ли запуск уже открытое окно.",
                settings,
                cancellationToken).ConfigureAwait(false);
        }

        var pid = startedProcess.Id;
        if (app.LaunchKind == LaunchKind.Desktop)
        {
            _processTracker.Track(startedProcess, app, plan.FileName);
        }
        else
        {
            startedProcess.Dispose();
        }

        var now = DateTimeOffset.UtcNow;
        await _appService.MarkLaunchedAsync(app.Id, now, cancellationToken).ConfigureAwait(false);

        var effectiveWarnings = new List<string>(plan.Warnings);
        var hasActiveMainInstance = runningInspection?.HasActiveMainInstance == true;
        var activeMainPids = runningInspection?.ActiveMainProcessIds.ToList() ?? [];
        var processSummary = runningInspection?.BuildDiagnosticSummary();

        if (hasActiveMainInstance && plan.Strategy != LaunchRoutingStrategy.DirectNoProxy)
        {
            const string alreadyRunningWarning = "Приложение уже работает. Новые настройки прокси могут не примениться до полного перезапуска.";
            if (!effectiveWarnings.Contains(alreadyRunningWarning, StringComparer.Ordinal))
            {
                effectiveWarnings.Insert(0, alreadyRunningWarning);
            }
        }

        var status = effectiveWarnings.Count > 0 ? LaunchStatus.StartedWithWarning : LaunchStatus.Started;
        var summaryMessage = plan.Strategy == LaunchRoutingStrategy.DirectNoProxy
            ? $"Процесс «{app.Name}» запущен без прокси (PID: {pid})."
            : $"Процесс «{app.Name}» запущен с профилем прокси «{proxy?.Name}» ({plan.ProxyDisplayAddress}, PID: {pid}). Учтите: успешный старт процесса не гарантирует, что сетевой стек программы соблюдает параметры прокси.";

        var redactedArgs = SecretRedactor.FormatRedactedArguments(plan.Arguments, plan.SensitiveValues);
        var historyEntry = new LaunchHistoryEntry
        {
            TimestampUtc = now,
            ApplicationId = app.Id,
            ApplicationName = app.Name,
            LaunchKind = app.LaunchKind,
            Strategy = plan.Strategy,
            Status = status,
            ProxyName = plan.Strategy == LaunchRoutingStrategy.DirectNoProxy ? "Без прокси" : proxy?.Name,
            ProxyDisplayAddress = plan.ProxyDisplayAddress,
            ResolvedTarget = plan.FileName,
            WorkingDirectory = plan.WorkingDirectory,
            RedactedArguments = string.IsNullOrWhiteSpace(redactedArgs) ? null : redactedArgs,
            ProcessId = pid,
            ApplicabilityLevel = plan.ApplicabilityLevel,
            ApplicabilityNote = plan.ApplicabilityNote,
            Message = summaryMessage,
            Warnings = [.. effectiveWarnings],
            DiagnosticNotes = [.. plan.DiagnosticNotes],
            ProcessDetectionSummary = processSummary
        };

        await _historyRepository.AppendAsync(historyEntry, settings.MaxHistoryEntries, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Запуск {AppName} ({Kind}, стратегия={Strategy}, прокси={ProxyAddr}, PID={Pid}, аргументы={Args}, процессы={ProcessSummary}).",
            app.Name,
            app.LaunchKind,
            plan.Strategy,
            plan.ProxyDisplayAddress ?? "direct",
            pid,
            redactedArgs,
            processSummary ?? "n/a");

        return new LaunchResult
        {
            Status = status,
            ProcessId = pid,
            Strategy = plan.Strategy,
            ProxyName = plan.Strategy == LaunchRoutingStrategy.DirectNoProxy ? null : proxy?.Name,
            Message = summaryMessage,
            ApplicabilityLevel = plan.ApplicabilityLevel,
            ApplicabilityNote = plan.ApplicabilityNote,
            Warnings = effectiveWarnings,
            DiagnosticNotes = [.. plan.DiagnosticNotes],
            AlreadyRunningMainInstanceDetected = hasActiveMainInstance,
            AlreadyRunningProcessIds = activeMainPids,
            RunningInstanceInspection = runningInspection,
            HistoryEntry = historyEntry
        };
    }

    private async Task<LaunchResult> RecordFailureAsync(
        ApplicationProfile app,
        ProxyProfile? proxy,
        LaunchRoutingStrategy strategy,
        string message,
        string? suggestedAction,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = new LaunchHistoryEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ApplicationId = app.Id,
            ApplicationName = app.Name,
            LaunchKind = app.LaunchKind,
            Strategy = strategy,
            Status = LaunchStatus.Failed,
            ProxyName = proxy?.Name,
            ProxyDisplayAddress = proxy is null ? null : ProxyUriBuilder.BuildMaskedUri(proxy),
            ResolvedTarget = app.ExecutableOrCommand,
            WorkingDirectory = app.WorkingDirectory,
            ApplicabilityLevel = app.ApplicabilityHint ?? ProxyApplicabilityLevel.Unverified,
            ApplicabilityNote = app.CompatibilityNote,
            Message = message
        };

        await _historyRepository.AppendAsync(entry, settings.MaxHistoryEntries, cancellationToken).ConfigureAwait(false);

        return new LaunchResult
        {
            Status = LaunchStatus.Failed,
            Strategy = strategy,
            Message = message,
            SuggestedAction = suggestedAction,
            ApplicabilityLevel = entry.ApplicabilityLevel,
            ApplicabilityNote = entry.ApplicabilityNote,
            HistoryEntry = entry
        };
    }

    private static (string Message, string Suggestion) TranslateWin32LaunchError(Win32Exception ex, string fileName)
    {
        return ex.NativeErrorCode switch
        {
            2 => ($"Файл не найден: {fileName}", "Проверьте путь к исполняемому файлу."),
            5 => ($"Отказано в доступе при запуске: {fileName}", "Проверьте права доступа к файлу или антивирус."),
            740 => ($"Для запуска «{Path.GetFileName(fileName)}» требуются повышенные права администратора (ERROR_ELEVATION_REQUIRED).", "Запустите целевое приложение с нужными правами или проверьте свойства ярлыка/exe."),
            _ => ($"Ошибка Windows ({ex.NativeErrorCode}) при запуске {fileName}: {ex.Message}", "Проверьте параметры запуска приложения.")
        };
    }

    private static void CleanupFailedScript(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return;
        }

        try
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
        catch
        {
            // Игнорируем ошибку очистки временного файла
        }
    }
}
