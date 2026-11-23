using System.Text;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Launching;

/// <summary>
/// Дополнительная универсальная стратегия запуска CLI-программ в отдельном интерактивном терминале
/// (Windows Terminal / PowerShell) с безопасным экранированием и передачей прокси-окружения без перенаправления потоков.
/// </summary>
public sealed class CliTerminalLaunchStrategy : ILaunchStrategy
{
    private readonly IExecutableDiscoveryService _discovery;
    private readonly ICredentialStore _credentialStore;
    private readonly IPathProvider _paths;

    public CliTerminalLaunchStrategy(
        IExecutableDiscoveryService discovery,
        ICredentialStore credentialStore,
        IPathProvider paths)
    {
        _discovery = discovery;
        _credentialStore = credentialStore;
        _paths = paths;
    }

    public bool CanHandle(ApplicationProfile application)
        => application.LaunchKind == LaunchKind.Cli;

    public async Task<LaunchPlan> BuildPlanAsync(
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
                discovery.ErrorMessage ?? $"CLI-команда «{application.ExecutableOrCommand}» не найдена.",
                "Проверьте, что программа установлена и доступна в PATH, или укажите полный путь к файлу.");
        }

        if (!CommandLineTokenizer.TryTokenize(application.Arguments, out var parsedArgs, out var tokenError))
        {
            throw new LaunchPreparationException(
                tokenError ?? "Не удалось разобрать аргументы запуска CLI.",
                "Проверьте парность кавычек в аргументах запуска.");
        }

        var workingDir = !string.IsNullOrWhiteSpace(application.WorkingDirectory)
            ? Environment.ExpandEnvironmentVariables(application.WorkingDirectory.Trim())
            : !string.IsNullOrWhiteSpace(settings.DefaultCliWorkingDirectory)
                ? Environment.ExpandEnvironmentVariables(settings.DefaultCliWorkingDirectory.Trim())
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(workingDir))
        {
            throw new LaunchPreparationException(
                $"Рабочая директория не существует: {workingDir}",
                "Выберите существующую папку проекта в настройках профиля.");
        }

        var envDelta = ProxyEnvironmentBuilder.Build(
            proxy,
            credentials,
            application.NoProxyOverride,
            application.CustomEnvironmentVariables,
            isDirectNoProxy);

        var warnings = new List<string>();
        var targetPath = discovery.ResolvedPath;

        if (!application.LaunchInTerminal)
        {
            return new LaunchPlan
            {
                FileName = targetPath,
                WorkingDirectory = workingDir,
                Arguments = parsedArgs,
                EnvironmentSet = envDelta.Set,
                EnvironmentRemove = envDelta.Remove,
                Strategy = isDirectNoProxy || proxy is null ? LaunchRoutingStrategy.DirectNoProxy : LaunchRoutingStrategy.CliTerminal,
                UseInteractiveTerminal = false,
                ProxyDisplayAddress = isDirectNoProxy || proxy is null ? null : ProxyUriBuilder.BuildMaskedUri(proxy),
                ApplicabilityLevel = application.ApplicabilityHint ?? ProxyApplicabilityLevel.LikelyCompatible,
                ApplicabilityNote = application.CompatibilityNote ?? "CLI-запуск с переменными окружения процесса.",
                Warnings = warnings,
                SensitiveValues = envDelta.SensitiveValues
            };
        }

        CleanupStaleRuntimeScripts();

        var preferredTerminal = application.TerminalOverride ?? settings.DefaultTerminal;
        var wtPath = preferredTerminal == TerminalPreference.WindowsTerminal
            ? _discovery.ResolveTerminalExecutable(TerminalPreference.WindowsTerminal)
            : null;

        if (preferredTerminal == TerminalPreference.WindowsTerminal && wtPath is null)
        {
            warnings.Add("Windows Terminal (wt.exe) не найден; выполнен автоматический переход на PowerShell.");
        }

        string? shellPath = preferredTerminal == TerminalPreference.PowerShellCore
            ? _discovery.ResolveTerminalExecutable(TerminalPreference.PowerShellCore)
            : null;

        if (shellPath is null)
        {
            if (preferredTerminal == TerminalPreference.PowerShellCore)
            {
                warnings.Add("PowerShell 7 (pwsh.exe) не найден; выполнен переход на Windows PowerShell.");
            }

            shellPath = _discovery.ResolveTerminalExecutable(TerminalPreference.PowerShell)
                ?? _discovery.ResolveTerminalExecutable(TerminalPreference.PowerShellCore);
        }

        if (string.IsNullOrWhiteSpace(shellPath))
        {
            throw new LaunchPreparationException(
                "Не найден ни один совместимый терминал (Windows Terminal, pwsh.exe или powershell.exe).",
                "Установите Windows Terminal или проверьте доступность Windows PowerShell.");
        }

        string? plainProxyUri = null;
        string? dpapiHexUri = null;
        if (!isDirectNoProxy && proxy is not null)
        {
            var runtimeUri = ProxyUriBuilder.BuildRuntimeUri(proxy, credentials);
            if (credentials is not null && !string.IsNullOrEmpty(credentials.Password))
            {
                // Шифруем URI (с логином/паролем) через DPAPI — открытый пароль не попадает в .ps1 на диске.
                dpapiHexUri = _credentialStore.ProtectForPowerShell(runtimeUri);
                if (dpapiHexUri is null)
                {
                    plainProxyUri = ProxyUriBuilder.BuildDisplayUri(proxy);
                    warnings.Add("Не удалось защитить учётные данные прокси для терминала; передан адрес без пароля.");
                }
            }
            else
            {
                plainProxyUri = runtimeUri;
            }
        }

        var scriptContent = PowerShellScriptBuilder.Build(new CliScriptOptions(
            ApplicationName: application.Name,
            TargetCommandOrPath: targetPath,
            Arguments: parsedArgs,
            WorkingDirectory: workingDir,
            NonSecretEnvironmentSet: envDelta.Set,
            EnvironmentRemove: envDelta.Remove,
            MaskedProxyDisplay: isDirectNoProxy || proxy is null ? null : ProxyUriBuilder.BuildMaskedUri(proxy),
            IsDirectNoProxy: isDirectNoProxy || proxy is null,
            PlainProxyUri: plainProxyUri,
            DpapiHexProtectedProxyUri: dpapiHexUri));

        Directory.CreateDirectory(_paths.RuntimeDirectory);
        var scriptPath = Path.Combine(_paths.RuntimeDirectory, $"launch-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
            .ConfigureAwait(false);

        var terminalArgs = new List<string>();
        string launcherExe;

        if (wtPath is not null)
        {
            launcherExe = wtPath;
            terminalArgs.Add("new-tab");
            terminalArgs.Add("-d");
            terminalArgs.Add(workingDir);
            terminalArgs.Add("--title");
            terminalArgs.Add($"ProxyDock — {PowerShellScriptBuilder.SanitizeSingleLine(application.Name)}");
            terminalArgs.Add(shellPath);
            terminalArgs.Add("-NoLogo");
            if (application.KeepTerminalOpen)
            {
                terminalArgs.Add("-NoExit");
            }
            terminalArgs.Add("-ExecutionPolicy");
            terminalArgs.Add("Bypass");
            terminalArgs.Add("-File");
            terminalArgs.Add(scriptPath);
        }
        else
        {
            launcherExe = shellPath;
            terminalArgs.Add("-NoLogo");
            if (application.KeepTerminalOpen)
            {
                terminalArgs.Add("-NoExit");
            }
            terminalArgs.Add("-ExecutionPolicy");
            terminalArgs.Add("Bypass");
            terminalArgs.Add("-File");
            terminalArgs.Add(scriptPath);
        }

        return new LaunchPlan
        {
            FileName = launcherExe,
            WorkingDirectory = workingDir,
            Arguments = terminalArgs,
            EnvironmentSet = envDelta.Set,
            EnvironmentRemove = envDelta.Remove,
            Strategy = isDirectNoProxy || proxy is null ? LaunchRoutingStrategy.DirectNoProxy : LaunchRoutingStrategy.CliTerminal,
            UseInteractiveTerminal = true,
            GeneratedScriptPath = scriptPath,
            ProxyDisplayAddress = isDirectNoProxy || proxy is null ? null : ProxyUriBuilder.BuildMaskedUri(proxy),
            ApplicabilityLevel = application.ApplicabilityHint ?? ProxyApplicabilityLevel.LikelyCompatible,
            ApplicabilityNote = application.CompatibilityNote ?? "CLI запущен в отдельном интерактивном терминале с изолированным окружением.",
            Warnings = warnings,
            SensitiveValues = envDelta.SensitiveValues
        };
    }

    private void CleanupStaleRuntimeScripts()
    {
        try
        {
            if (!Directory.Exists(_paths.RuntimeDirectory))
            {
                return;
            }

            var threshold = DateTime.UtcNow.AddHours(-1);
            foreach (var file in Directory.EnumerateFiles(_paths.RuntimeDirectory, "launch-*.ps1"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < threshold)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Игнорируем файлы, занятые другим терминалом
                }
            }
        }
        catch
        {
            // Очистка временных скриптов некритична
        }
    }
}
