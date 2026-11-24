using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Launching;

/// <summary>
/// Основная стратегия запуска Windows GUI Desktop-приложений с изолированным окружением процесса
/// и опциональной поддержкой Chromium/Electron --proxy-server.
/// </summary>
public sealed class DesktopLaunchStrategy : ILaunchStrategy
{
    private readonly IExecutableDiscoveryService _discovery;

    public DesktopLaunchStrategy(IExecutableDiscoveryService discovery)
    {
        _discovery = discovery;
    }

    public bool CanHandle(ApplicationProfile application)
        => application.LaunchKind == LaunchKind.Desktop;

    public Task<LaunchPlan> BuildPlanAsync(
        ApplicationProfile application,
        ProxyProfile? proxy,
        ProxyCredentials? credentials,
        AppSettings settings,
        bool isDirectNoProxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);

        var discovery = _discovery.Resolve(application);
        if (!discovery.Found || string.IsNullOrWhiteSpace(discovery.ResolvedPath))
        {
            throw new LaunchPreparationException(
                discovery.ErrorMessage ?? $"Исполняемый файл приложения «{application.Name}» не найден.",
                "Проверьте путь к .exe в настройках профиля приложения.");
        }

        string? workingDir = null;
        if (!string.IsNullOrWhiteSpace(application.WorkingDirectory))
        {
            var expandedWd = Environment.ExpandEnvironmentVariables(application.WorkingDirectory.Trim());
            if (!Directory.Exists(expandedWd))
            {
                throw new LaunchPreparationException(
                    $"Рабочая директория не существует: {expandedWd}",
                    "Выберите существующую папку в параметрах приложения.");
            }

            workingDir = expandedWd;
        }
        else
        {
            var exeDir = Path.GetDirectoryName(discovery.ResolvedPath);
            if (!string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir))
            {
                workingDir = exeDir;
            }
        }

        if (!CommandLineTokenizer.TryTokenize(application.Arguments, out var parsedArgs, out var tokenError))
        {
            throw new LaunchPreparationException(
                tokenError ?? "Не удалось разобрать аргументы запуска.",
                "Проверьте парность кавычек в аргументах запуска.");
        }

        var envDelta = ProxyEnvironmentBuilder.Build(
            proxy,
            credentials,
            application.NoProxyOverride,
            application.CustomEnvironmentVariables,
            isDirectNoProxy,
            application.UseNodeEnvProxy);

        var stack = discovery.StackInfo ?? _discovery.InspectExecutable(discovery.ResolvedPath, application.TemplateKey);
        var warnings = new List<string>();
        var diagnosticNotes = new List<string>();
        var strategy = isDirectNoProxy || proxy is null
            ? LaunchRoutingStrategy.DirectNoProxy
            : LaunchRoutingStrategy.EnvironmentOnly;

        if (!isDirectNoProxy && proxy is not null && application.UseChromiumProxyFlag)
        {
            strategy = LaunchRoutingStrategy.EnvironmentAndChromium;
            var hasManualFlag = parsedArgs.Any(a =>
                a.StartsWith("--proxy-server", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--no-proxy-server", StringComparison.OrdinalIgnoreCase));
            if (!hasManualFlag)
            {
                // Никогда не передаём логин и пароль в аргументах командной строки --proxy-server.
                parsedArgs.Add($"--proxy-server={ProxyUriBuilder.BuildDisplayUri(proxy)}");
            }

            if (credentials is not null || proxy.HasCredentials)
            {
                warnings.Add(
                    "Передан флаг Chromium `--proxy-server` без логина и пароля (учётные данные не передаются в командной строке из соображений безопасности). Учтите, что `--proxy-server` в Chromium/Electron не обеспечивает универсальную прокси-авторизацию без системного запроса или локального ретранслятора.");
            }

            if (!stack.SupportsChromiumProxyFlag)
            {
                warnings.Add(
                    "Для выбранного исполняемого файла не подтверждена архитектура Electron/Chromium: параметр `--proxy-server` может быть проигнорирован или вызвать ошибку запуска.");
            }
        }
        else if (!isDirectNoProxy && proxy is not null && !application.UseChromiumProxyFlag && stack.SupportsChromiumProxyFlag)
        {
            warnings.Add(
                "Обнаружен стек Electron/Chromium, но флаг `--proxy-server` отключён: при выключенном системном прокси Windows сетевой процесс Chromium (NetworkService) обычно игнорирует HTTP_PROXY/HTTPS_PROXY.");
        }

        if (!isDirectNoProxy && proxy is not null)
        {
            foreach (var caveat in stack.Caveats)
            {
                diagnosticNotes.Add(caveat);
            }
        }

        var effectiveApplicability = application.ApplicabilityHint ?? stack.ApplicabilityLevel;
        if (!stack.SupportsChromiumProxyFlag && effectiveApplicability == ProxyApplicabilityLevel.Confirmed)
        {
            effectiveApplicability = ProxyApplicabilityLevel.NeedsVerification;
        }

        var note = !string.IsNullOrWhiteSpace(application.CompatibilityNote)
            ? application.CompatibilityNote
            : stack.Summary;

        var plan = new LaunchPlan
        {
            FileName = discovery.ResolvedPath,
            WorkingDirectory = workingDir,
            Arguments = parsedArgs,
            EnvironmentSet = envDelta.Set,
            EnvironmentRemove = envDelta.Remove,
            Strategy = strategy,
            UseInteractiveTerminal = false,
            ProxyDisplayAddress = isDirectNoProxy || proxy is null ? null : ProxyUriBuilder.BuildMaskedUri(proxy),
            ApplicabilityLevel = effectiveApplicability,
            ApplicabilityNote = note,
            Warnings = warnings,
            DiagnosticNotes = diagnosticNotes,
            SensitiveValues = envDelta.SensitiveValues
        };

        return Task.FromResult(plan);
    }
}
