using System.Runtime.Versioning;
using Microsoft.Win32;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Platform;

/// <summary>
/// Обнаруживает desktop-приложения (включая MSIX App Execution Alias), CLI-команды, терминалы Windows
/// и анализирует реальный технологический стек программы для честной оценки применимости прокси.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsExecutableDiscoveryService : IExecutableDiscoveryService
{
    private static readonly string[] CliExtensions = [".exe", ".com", ".ps1", ".cmd", ".bat"];

    public ExecutableDiscoveryResult Resolve(ApplicationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ResolveTarget(profile.LaunchKind, profile.ExecutableOrCommand, profile.TemplateKey);
    }

    public ExecutableDiscoveryResult ResolveTarget(LaunchKind kind, string executableOrCommand, string? templateKey = null)
    {
        if (string.IsNullOrWhiteSpace(executableOrCommand))
        {
            return new ExecutableDiscoveryResult
            {
                Found = false,
                ErrorMessage = kind == LaunchKind.Desktop
                    ? "Не указан путь к исполняемому файлу (.exe)."
                    : "Не указана команда или путь к CLI-программе."
            };
        }

        var trimmed = Environment.ExpandEnvironmentVariables(executableOrCommand.Trim().Trim('"'));

        if (kind == LaunchKind.Desktop)
        {
            return ResolveDesktopTarget(trimmed, templateKey);
        }

        return ResolveCliTarget(trimmed);
    }

    public ExecutableDiscoveryResult DiscoverTemplate(string templateKey)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            return new ExecutableDiscoveryResult { Found = false, ErrorMessage = "Не указан шаблон." };
        }

        if (string.Equals(templateKey, BuiltInAppTemplates.GoogleAntigravityKey, StringComparison.OrdinalIgnoreCase))
        {
            return DiscoverDesktopFromCandidates(
                templateKey,
                GetAntigravityCandidatePaths(),
                exeNamesForPathLookup: ["Antigravity.exe"],
                appPathNames: ["Antigravity.exe"],
                notFoundHint: "Google Antigravity не найден в стандартных путях. Укажите путь к Antigravity.exe вручную.");
        }

        if (string.Equals(templateKey, BuiltInAppTemplates.ClaudeDesktopKey, StringComparison.OrdinalIgnoreCase))
        {
            return DiscoverDesktopFromCandidates(
                templateKey,
                GetClaudeDesktopCandidatePaths(),
                exeNamesForPathLookup: ["claude-desktop.exe"],
                appPathNames: ["Claude.exe", "claude-desktop.exe"],
                notFoundHint: "Claude Desktop не найден в стандартных путях (MSIX WindowsApps / LocalAppData). Укажите путь к файлу вручную.");
        }

        if (string.Equals(templateKey, BuiltInAppTemplates.CodexDesktopKey, StringComparison.OrdinalIgnoreCase))
        {
            return DiscoverDesktopFromCandidates(
                templateKey,
                GetCodexDesktopCandidatePaths(),
                exeNamesForPathLookup: [],
                appPathNames: ["Codex.exe", "chatgpt-desktop.exe"],
                notFoundHint: "Codex Desktop не найден в стандартных путях (OpenAI.Codex_* / LocalAppData). Укажите путь к файлу вручную.");
        }

        if (string.Equals(templateKey, BuiltInAppTemplates.ClaudeCodeCliKey, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveCliTarget("claude");
        }

        if (string.Equals(templateKey, BuiltInAppTemplates.OpenAiCodexCliKey, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveCliTarget("codex");
        }

        return new ExecutableDiscoveryResult { Found = false, ErrorMessage = $"Неизвестный шаблон: {templateKey}." };
    }

    public DesktopAppStackInfo InspectExecutable(string path, string? templateKey = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DesktopAppStackInfo
            {
                PackagingKind = AppPackagingKind.Unknown,
                RuntimeStack = AppRuntimeStack.NativeOrUnknown,
                ApplicabilityLevel = ProxyApplicabilityLevel.Unverified,
                SupportsChromiumProxyFlag = false,
                Summary = "Путь к файлу не задан."
            };
        }

        var normalized = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        var ext = Path.GetExtension(normalized);
        if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new DesktopAppStackInfo
            {
                PackagingKind = AppPackagingKind.ScriptOrShim,
                RuntimeStack = AppRuntimeStack.NativeOrUnknown,
                ApplicabilityLevel = ProxyApplicabilityLevel.NeedsVerification,
                SupportsChromiumProxyFlag = false,
                IconSourcePath = normalized,
                Summary = "Скрипт / оболочка запуска (.ps1/.cmd/.bat).",
                Caveats = ["Убедитесь, что запускаемая из скрипта программа учитывает HTTP_PROXY / HTTPS_PROXY."]
            };
        }

        var isMsixAlias = IsWindowsAppsAliasPath(normalized) || MsixPackageResolver.TryReadAppExecLink(normalized) is not null;
        var msixPkg = MsixPackageResolver.TryResolvePackage(normalized, templateKey);
        var packageRoot = msixPkg?.PackageRoot;
        var inspectDir = !string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot)
            ? packageRoot
            : Path.GetDirectoryName(normalized);

        var (stack, hasCoworkService, hasCodexCompanion) = InspectDirectoryStackMarkers(inspectDir);
        var iconSource = ResolveIconSourcePath(normalized, msixPkg);
        var caveats = new List<string>();

        if (isMsixAlias)
        {
            caveats.Add("Приложение упаковано как MSIX (WindowsApps App Execution Alias): переменные окружения и аргументы наследуются основным процессом и его дочерними процессами, но уже запущенный экземпляр в трее или отдельные системные службы Windows их не получают.");
        }

        if (hasCoworkService)
        {
            caveats.Add("В пакете присутствует фоновая служба/помощник cowork-svc.exe, сетевые вызовы которого могут вести себя независимо от GUI-процесса.");
        }

        if (hasCodexCompanion)
        {
            caveats.Add("В пакете присутствуют встроенный codex.exe (наследует HTTP_PROXY/HTTPS_PROXY от GUI-процесса) и фоновая служба Windows codex-windows-sandbox-service.exe (не наследует переменные запуска GUI-клиента).");
        }

        var supportsChromiumFlag = stack is AppRuntimeStack.Electron or AppRuntimeStack.ChromiumShell;
        if (!supportsChromiumFlag)
        {
            if (stack == AppRuntimeStack.WebView2)
            {
                caveats.Add("Обнаружен Microsoft Edge WebView2: обычно использует системные настройки сети Windows (WinINet) и может игнорировать HTTP_PROXY.");
            }
            else
            {
                caveats.Add("В каталоге не найдены маркеры Electron/Chromium. Если программа использует WinHTTP/WinINet или собственный сетевой стек, переменные окружения могут не сработать.");
            }
        }

        ProxyApplicabilityLevel applicability;
        string summary;

        if (!isMsixAlias && supportsChromiumFlag &&
            (string.Equals(templateKey, BuiltInAppTemplates.GoogleAntigravityKey, StringComparison.OrdinalIgnoreCase)
             || string.Equals(Path.GetFileName(normalized), "Antigravity.exe", StringComparison.OrdinalIgnoreCase)))
        {
            applicability = ProxyApplicabilityLevel.Confirmed;
            summary = "Win32 Electron / Chromium (подтверждённый сценарий: env + --proxy-server).";
        }
        else if (!isMsixAlias && supportsChromiumFlag)
        {
            applicability = ProxyApplicabilityLevel.LikelyCompatible;
            summary = stack == AppRuntimeStack.Electron
                ? "Win32 Electron / Chromium (переменные окружения + опциональный --proxy-server)."
                : "Win32 Chromium-оболочка (переменные окружения + опциональный --proxy-server).";
        }
        else if (isMsixAlias && supportsChromiumFlag)
        {
            var isCodexMsix = string.Equals(templateKey, BuiltInAppTemplates.CodexDesktopKey, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(Path.GetFileName(normalized), "chatgpt-desktop.exe", StringComparison.OrdinalIgnoreCase);
            if (isCodexMsix)
            {
                applicability = ProxyApplicabilityLevel.Confirmed;
                summary = "MSIX Chromium / Owl + codex.exe (подтверждённый сценарий: env + NODE_USE_ENV_PROXY=1 + --proxy-server).";
            }
            else
            {
                applicability = ProxyApplicabilityLevel.NeedsVerification;
                summary = stack == AppRuntimeStack.Electron
                    ? "MSIX Electron / Chromium (env + NODE_USE_ENV_PROXY=1 + --proxy-server; полная работа требует пользовательской проверки)."
                    : "MSIX Chromium / Owl оболочка (env + NODE_USE_ENV_PROXY=1 + --proxy-server; применимость требует проверки).";
            }
        }
        else if (isMsixAlias)
        {
            applicability = ProxyApplicabilityLevel.NeedsVerification;
            summary = "MSIX-пакет (App Execution Alias) — применимость прокси к сетевому стеку требует проверки.";
        }
        else if (stack == AppRuntimeStack.WebView2)
        {
            applicability = ProxyApplicabilityLevel.Unverified;
            summary = "Win32 + WebView2 (флаг --proxy-server не рекомендуется без проверки).";
        }
        else
        {
            applicability = ProxyApplicabilityLevel.Unverified;
            summary = "Стандартный Win32/.exe (стек сети не распознан как Electron/Chromium).";
        }

        return new DesktopAppStackInfo
        {
            PackagingKind = isMsixAlias ? AppPackagingKind.MsixExecutionAlias : AppPackagingKind.Win32Executable,
            RuntimeStack = stack,
            ApplicabilityLevel = applicability,
            SupportsChromiumProxyFlag = supportsChromiumFlag,
            ResolvedPackageRoot = packageRoot,
            IconSourcePath = iconSource,
            Summary = summary,
            Caveats = caveats
        };
    }

    public string? ResolveTerminalExecutable(TerminalPreference preference)
    {
        return preference switch
        {
            TerminalPreference.WindowsTerminal => FindWindowsTerminalPath(),
            TerminalPreference.PowerShellCore => FindOnPath("pwsh.exe"),
            TerminalPreference.PowerShell => FindWindowsPowerShellPath(),
            _ => FindWindowsPowerShellPath()
        };
    }

    public IReadOnlyList<TerminalPreference> GetAvailableTerminals()
    {
        var list = new List<TerminalPreference>();
        if (FindWindowsTerminalPath() is not null)
        {
            list.Add(TerminalPreference.WindowsTerminal);
        }

        if (FindOnPath("pwsh.exe") is not null)
        {
            list.Add(TerminalPreference.PowerShellCore);
        }

        if (FindWindowsPowerShellPath() is not null)
        {
            list.Add(TerminalPreference.PowerShell);
        }

        return list;
    }

    private ExecutableDiscoveryResult ResolveDesktopTarget(string target, string? templateKey)
    {
        if (Path.IsPathRooted(target) || target.Contains(Path.DirectorySeparatorChar) || target.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(target);
            if (File.Exists(fullPath))
            {
                var stack = InspectExecutable(fullPath, templateKey);
                return new ExecutableDiscoveryResult
                {
                    Found = true,
                    ResolvedPath = fullPath,
                    DisplaySource = stack.PackagingKind == AppPackagingKind.MsixExecutionAlias ? "MSIX WindowsApps" : "Указанный файл",
                    StackInfo = stack
                };
            }

            return new ExecutableDiscoveryResult
            {
                Found = false,
                ErrorMessage = $"Исполняемый файл не найден: {fullPath}"
            };
        }

        // Для шаблонов пробуем сначала известные пути конкретного продукта
        if (!string.IsNullOrWhiteSpace(templateKey))
        {
            var byTemplate = DiscoverTemplate(templateKey);
            if (byTemplate.Found)
            {
                return byTemplate;
            }
        }

        var pathHit = FindOnPath(target, desktopMode: true) ?? FindInAppPathsRegistry(target);
        if (pathHit is not null)
        {
            var stack = InspectExecutable(pathHit, templateKey);
            return new ExecutableDiscoveryResult
            {
                Found = true,
                ResolvedPath = pathHit,
                DisplaySource = stack.PackagingKind == AppPackagingKind.MsixExecutionAlias ? "MSIX / PATH" : "PATH / Реестр",
                StackInfo = stack
            };
        }

        return new ExecutableDiscoveryResult
        {
            Found = false,
            ErrorMessage = $"Не удалось найти исполняемый файл «{target}». Выберите .exe вручную."
        };
    }

    private ExecutableDiscoveryResult ResolveCliTarget(string target)
    {
        if (Path.IsPathRooted(target) || target.Contains(Path.DirectorySeparatorChar) || target.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(target);
            if (File.Exists(fullPath))
            {
                var ext = Path.GetExtension(fullPath);
                return new ExecutableDiscoveryResult
                {
                    Found = true,
                    ResolvedPath = fullPath,
                    DisplaySource = "Указанный файл",
                    IsBatchOrScript = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                };
            }

            return new ExecutableDiscoveryResult
            {
                Found = false,
                ErrorMessage = $"Файл CLI-программы не найден: {fullPath}"
            };
        }

        var candidate = FindCliCommandInSearchDirectories(target);
        if (candidate is not null)
        {
            var ext = Path.GetExtension(candidate);
            return new ExecutableDiscoveryResult
            {
                Found = true,
                ResolvedPath = candidate,
                DisplaySource = candidate.Contains("\\npm\\", StringComparison.OrdinalIgnoreCase) ? "npm (PATH)" : "PATH",
                IsBatchOrScript = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            };
        }

        return new ExecutableDiscoveryResult
        {
            Found = false,
            ErrorMessage = $"Команда «{target}» не найдена в PATH и каталогах npm/LocalAppData."
        };
    }

    private ExecutableDiscoveryResult DiscoverDesktopFromCandidates(
        string templateKey,
        IEnumerable<string> candidatePaths,
        string[] exeNamesForPathLookup,
        string[] appPathNames,
        string notFoundHint)
    {
        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                var stack = InspectExecutable(candidate, templateKey);
                return new ExecutableDiscoveryResult
                {
                    Found = true,
                    ResolvedPath = candidate,
                    DisplaySource = stack.PackagingKind == AppPackagingKind.MsixExecutionAlias ? "Автопоиск (MSIX WindowsApps)" : "Автопоиск",
                    StackInfo = stack
                };
            }
        }

        foreach (var appPathName in appPathNames)
        {
            var regHit = FindInAppPathsRegistry(appPathName);
            if (regHit is not null)
            {
                var stack = InspectExecutable(regHit, templateKey);
                return new ExecutableDiscoveryResult
                {
                    Found = true,
                    ResolvedPath = regHit,
                    DisplaySource = "Автопоиск (App Paths)",
                    StackInfo = stack
                };
            }
        }

        foreach (var exeName in exeNamesForPathLookup)
        {
            var pathHit = FindOnPath(exeName, desktopMode: true);
            if (pathHit is not null)
            {
                var stack = InspectExecutable(pathHit, templateKey);
                return new ExecutableDiscoveryResult
                {
                    Found = true,
                    ResolvedPath = pathHit,
                    DisplaySource = "Автопоиск (PATH)",
                    StackInfo = stack
                };
            }
        }

        return new ExecutableDiscoveryResult
        {
            Found = false,
            ErrorMessage = notFoundHint
        };
    }

    private static IEnumerable<string> GetAntigravityCandidatePaths()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        return
        [
            Path.Combine(localApp, "Programs", "antigravity", "Antigravity.exe"),
            Path.Combine(localApp, "Programs", "Antigravity", "Antigravity.exe"),
            Path.Combine(localApp, "Programs", "Antigravity IDE", "Antigravity.exe"),
            Path.Combine(localApp, "Programs", "Google Antigravity", "Antigravity.exe"),
            Path.Combine(localApp, "antigravity", "Antigravity.exe"),
            Path.Combine(programFiles, "Antigravity", "Antigravity.exe"),
            Path.Combine(programFiles, "Google", "Antigravity", "Antigravity.exe")
        ];
    }

    private static IEnumerable<string> GetClaudeDesktopCandidatePaths()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = Path.Combine(localApp, "Microsoft", "WindowsApps");

        if (Directory.Exists(windowsApps))
        {
            foreach (var dir in Directory.EnumerateDirectories(windowsApps, "Claude_*", SearchOption.TopDirectoryOnly))
            {
                yield return Path.Combine(dir, "claude-desktop.exe");
            }
        }

        yield return Path.Combine(windowsApps, "claude-desktop.exe");
        yield return Path.Combine(localApp, "AnthropicClaude", "claude.exe");
        yield return Path.Combine(localApp, "Programs", "Claude", "Claude.exe");
        yield return Path.Combine(localApp, "Claude", "Claude.exe");
        yield return Path.Combine(programFiles, "Claude", "Claude.exe");
    }

    private static IEnumerable<string> GetCodexDesktopCandidatePaths()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = Path.Combine(localApp, "Microsoft", "WindowsApps");

        if (Directory.Exists(windowsApps))
        {
            foreach (var dir in Directory.EnumerateDirectories(windowsApps, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly))
            {
                yield return Path.Combine(dir, "chatgpt-desktop.exe");
                yield return Path.Combine(dir, "codex-desktop.exe");
                yield return Path.Combine(dir, "Codex.exe");
            }
        }

        yield return Path.Combine(localApp, "Programs", "OpenAI", "Codex", "Codex.exe");
        yield return Path.Combine(localApp, "Programs", "Codex", "Codex.exe");
        yield return Path.Combine(localApp, "Programs", "OpenAI Codex", "Codex.exe");
        yield return Path.Combine(programFiles, "OpenAI", "Codex", "Codex.exe");
    }

    private static bool IsWindowsAppsAliasPath(string path)
        => MsixPackageResolver.IsWindowsAppsAliasPath(path);

    private static (AppRuntimeStack Stack, bool HasCoworkService, bool HasCodexCompanion) InspectDirectoryStackMarkers(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return (AppRuntimeStack.NativeOrUnknown, false, false);
        }

        // Проверяем как сам каталог, так и подкаталог app/ (типичная структура MSIX-упаковки Electron/Chromium)
        var probeDirs = new List<string> { rootDirectory };
        var appSubDir = Path.Combine(rootDirectory, "app");
        if (Directory.Exists(appSubDir))
        {
            probeDirs.Add(appSubDir);
        }

        var isElectron = false;
        var isChromium = false;
        var isWebView2 = false;
        var hasCowork = false;
        var hasCodexCompanion = false;

        foreach (var dir in probeDirs)
        {
            if (File.Exists(Path.Combine(dir, "LICENSE.electron.txt"))
                || File.Exists(Path.Combine(dir, "electron.exe")))
            {
                isElectron = true;
            }

            if (File.Exists(Path.Combine(dir, "chrome_100_percent.pak"))
                || File.Exists(Path.Combine(dir, "chrome.dll"))
                || File.Exists(Path.Combine(dir, "owl-shell-runtime.json"))
                || File.Exists(Path.Combine(dir, "LICENSES.chromium.html")))
            {
                isChromium = true;
            }

            if (File.Exists(Path.Combine(dir, "WebView2Loader.dll")))
            {
                isWebView2 = true;
            }

            var resourcesDir = Path.Combine(dir, "resources");
            if (Directory.Exists(resourcesDir))
            {
                if (File.Exists(Path.Combine(resourcesDir, "cowork-svc.exe")))
                {
                    hasCowork = true;
                }

                if (File.Exists(Path.Combine(resourcesDir, "codex.exe"))
                    || File.Exists(Path.Combine(resourcesDir, "codex-windows-sandbox-service.exe")))
                {
                    hasCodexCompanion = true;
                }
            }
        }

        var stack = isElectron
            ? AppRuntimeStack.Electron
            : isChromium
                ? AppRuntimeStack.ChromiumShell
                : isWebView2
                    ? AppRuntimeStack.WebView2
                    : AppRuntimeStack.NativeOrUnknown;

        return (stack, hasCowork, hasCodexCompanion);
    }

    private static string ResolveIconSourcePath(string executablePath, MsixPackageInfo? msixPkg)
    {
        if (msixPkg is null || string.IsNullOrWhiteSpace(msixPkg.PackageRoot) || !Directory.Exists(msixPkg.PackageRoot))
        {
            return executablePath;
        }

        var manifestVisuals = MsixPackageResolver.ParseManifestVisuals(msixPkg);
        if (manifestVisuals is not null)
        {
            var bestLogo = MsixPackageResolver.ResolveBestLogoAsset(manifestVisuals, desiredPixelSize: 96, preferLightTheme: false);
            if (bestLogo is not null && File.Exists(bestLogo.FilePath))
            {
                return bestLogo.FilePath;
            }

            if (!string.IsNullOrWhiteSpace(manifestVisuals.ApplicationExecutablePath)
                && File.Exists(manifestVisuals.ApplicationExecutablePath))
            {
                return manifestVisuals.ApplicationExecutablePath;
            }
        }

        var packageRoot = msixPkg.PackageRoot;
        string[] preferredGuiBins =
        [
            Path.Combine(packageRoot, "app", "Claude.exe"),
            Path.Combine(packageRoot, "app", "ChatGPT.exe"),
            Path.Combine(packageRoot, "app", "Codex.exe"),
            Path.Combine(packageRoot, "Claude.exe"),
            Path.Combine(packageRoot, "ChatGPT.exe"),
            Path.Combine(packageRoot, "Codex.exe")
        ];

        foreach (var bin in preferredGuiBins)
        {
            if (File.Exists(bin))
            {
                return bin;
            }
        }

        return executablePath;
    }

    private static string? FindCliCommandInSearchDirectories(string command)
    {
        var searchDirs = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            searchDirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var npmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        if (Directory.Exists(npmDir))
        {
            searchDirs.Add(npmDir);
        }

        var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (Directory.Exists(localPrograms))
        {
            searchDirs.Add(localPrograms);
        }

        var hasExplicitExtension = Path.HasExtension(command);
        foreach (var dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            if (hasExplicitExtension)
            {
                var candidate = Path.Combine(dir, command);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            else
            {
                // На Windows строго предпочитаем .exe / .com / .ps1 / .cmd / .bat — пропускаем POSIX-скрипты без расширения!
                foreach (var ext in CliExtensions)
                {
                    var candidate = Path.Combine(dir, command + ext);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName, bool desktopMode = false)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        var hasExt = Path.HasExtension(fileName);
        foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(rawDir))
            {
                continue;
            }

            if (hasExt)
            {
                var candidate = Path.Combine(rawDir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            else if (desktopMode)
            {
                var exeCandidate = Path.Combine(rawDir, fileName + ".exe");
                if (File.Exists(exeCandidate))
                {
                    return exeCandidate;
                }
            }
        }

        return null;
    }

    private static string? FindInAppPathsRegistry(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return null;
        }

        string[] roots =
        [
            $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}"
        ];

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var subPath in roots)
            {
                try
                {
                    using var key = root.OpenSubKey(subPath, writable: false);
                    var value = (key?.GetValue(null) as string)?.Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                    {
                        return value;
                    }
                }
                catch
                {
                    // Игнорируем недоступные ветки реестра
                }
            }
        }

        return null;
    }

    private static string? FindWindowsTerminalPath()
    {
        var localAppAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");
        if (File.Exists(localAppAlias))
        {
            return localAppAlias;
        }

        return FindOnPath("wt.exe");
    }

    private static string? FindWindowsPowerShellPath()
    {
        var sysPs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (File.Exists(sysPs))
        {
            return sysPs;
        }

        return FindOnPath("powershell.exe");
    }
}
