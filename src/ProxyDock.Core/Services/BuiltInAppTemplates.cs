using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;

namespace ProxyDock.Core.Services;

/// <summary>
/// Предустановленные шаблоны приложений с приоритетом на Windows Desktop GUI-сценарии.
/// </summary>
public static class BuiltInAppTemplates
{
    public const string GoogleAntigravityKey = "google-antigravity";
    public const string ClaudeDesktopKey = "claude-desktop";
    public const string CodexDesktopKey = "codex-desktop";
    public const string ClaudeCodeCliKey = "claude-code";
    public const string OpenAiCodexCliKey = "openai-codex";

    public sealed record TemplateDescriptor(
        string Key,
        string DisplayName,
        string Subtitle,
        LaunchKind LaunchKind,
        bool IsPrimaryDesktop,
        Func<string?, ApplicationProfile> CreateProfile);

    public static IReadOnlyList<TemplateDescriptor> DesktopTemplates { get; } =
    [
        new(
            GoogleAntigravityKey,
            "Google Antigravity Desktop",
            "Win32 Electron / Chromium — подтверждённый сценарий (env + --proxy-server)",
            LaunchKind.Desktop,
            true,
            CreateGoogleAntigravity),
        new(
            ClaudeDesktopKey,
            "Claude Desktop",
            "MSIX Electron + cowork-svc — переменные окружения (Chromium-флаг опционально)",
            LaunchKind.Desktop,
            true,
            CreateClaudeDesktop),
        new(
            CodexDesktopKey,
            "Codex Desktop",
            "MSIX Chromium/Owl + codex.exe + сервис — переменные окружения (проверить стек)",
            LaunchKind.Desktop,
            true,
            CreateCodexDesktop)
    ];

    public static IReadOnlyList<TemplateDescriptor> CliTemplates { get; } =
    [
        new(
            ClaudeCodeCliKey,
            "Claude Code (CLI)",
            "Дополнительный режим: интерактивный терминал (claude)",
            LaunchKind.Cli,
            false,
            _ => CreateClaudeCodeCli()),
        new(
            OpenAiCodexCliKey,
            "OpenAI Codex (CLI)",
            "Дополнительный режим: интерактивный терминал (codex)",
            LaunchKind.Cli,
            false,
            _ => CreateOpenAiCodexCli())
    ];

    public static ApplicationProfile CreateGoogleAntigravity(string? resolvedExecutablePath = null) => new()
    {
        Name = "Google Antigravity",
        LaunchKind = LaunchKind.Desktop,
        ExecutableOrCommand = resolvedExecutablePath ?? "Antigravity.exe",
        ProxyMode = ProxyAssignmentMode.InheritDefault,
        UseChromiumProxyFlag = true,
        LaunchInTerminal = false,
        TemplateKey = GoogleAntigravityKey,
        ApplicabilityHint = ProxyApplicabilityLevel.Confirmed,
        RelatedProcessNames = ["Antigravity"],
        CompatibilityNote = "Electron/Chromium (Win32): передаются HTTP_PROXY/HTTPS_PROXY/ALL_PROXY и --proxy-server.",
        Notes = "Подтверждённый сценарий: Google Antigravity работает через HTTP-прокси при передаче переменных окружения и --proxy-server."
    };

    public static ApplicationProfile CreateClaudeDesktop(string? resolvedExecutablePath = null) => new()
    {
        Name = "Claude Desktop",
        LaunchKind = LaunchKind.Desktop,
        ExecutableOrCommand = resolvedExecutablePath ?? "claude-desktop.exe",
        ProxyMode = ProxyAssignmentMode.InheritDefault,
        UseChromiumProxyFlag = false,
        LaunchInTerminal = false,
        TemplateKey = ClaudeDesktopKey,
        ApplicabilityHint = ProxyApplicabilityLevel.NeedsVerification,
        RelatedProcessNames = ["Claude"],
        CompatibilityNote = "MSIX Electron + служба cowork-svc.exe: запускается с переменными окружения; поддержка --proxy-server через MSIX App Execution Alias зависит от версии пакета и не гарантируется без проверки.",
        Notes = "При первом запуске проверьте фактический трафик: Claude Desktop часто остаётся в системном трее (Claude.exe) и может игнорировать env при передаче управления существующему процессу."
    };

    public static ApplicationProfile CreateCodexDesktop(string? resolvedExecutablePath = null) => new()
    {
        Name = "Codex Desktop",
        LaunchKind = LaunchKind.Desktop,
        ExecutableOrCommand = resolvedExecutablePath ?? "chatgpt-desktop.exe",
        ProxyMode = ProxyAssignmentMode.InheritDefault,
        UseChromiumProxyFlag = false,
        LaunchInTerminal = false,
        TemplateKey = CodexDesktopKey,
        ApplicabilityHint = ProxyApplicabilityLevel.NeedsVerification,
        RelatedProcessNames = ["ChatGPT", "Codex"],
        CompatibilityNote = "MSIX (ChatGPT.exe / Codex.exe) + встроенный codex.exe + служба codex-windows-sandbox-service: передаются переменные окружения; фоновые службы Windows и изоляция MSIX могут требовать дополнительной проверки (или %USERPROFILE%\\.codex\\.env).",
        Notes = "Если в интерфейсе чаты зависают при выключенном системном прокси, проверьте поведение с включённым --proxy-server или настройкой %USERPROFILE%\\.codex\\.env."
    };

    public static ApplicationProfile CreateClaudeCodeCli(string? workingDirectory = null) => new()
    {
        Name = "Claude Code (CLI)",
        LaunchKind = LaunchKind.Cli,
        ExecutableOrCommand = "claude",
        WorkingDirectory = workingDirectory,
        ProxyMode = ProxyAssignmentMode.InheritDefault,
        UseChromiumProxyFlag = false,
        LaunchInTerminal = true,
        KeepTerminalOpen = true,
        TemplateKey = ClaudeCodeCliKey,
        ApplicabilityHint = ProxyApplicabilityLevel.LikelyCompatible,
        CompatibilityNote = "CLI-клиент запускается в интерактивном терминале и считывает HTTP_PROXY / HTTPS_PROXY / ALL_PROXY.",
        Notes = "Дополнительный CLI-сценарий запуска Claude Code в отдельном терминале."
    };

    public static ApplicationProfile CreateOpenAiCodexCli(string? workingDirectory = null) => new()
    {
        Name = "OpenAI Codex (CLI)",
        LaunchKind = LaunchKind.Cli,
        ExecutableOrCommand = "codex",
        WorkingDirectory = workingDirectory,
        ProxyMode = ProxyAssignmentMode.InheritDefault,
        UseChromiumProxyFlag = false,
        LaunchInTerminal = true,
        KeepTerminalOpen = true,
        TemplateKey = OpenAiCodexCliKey,
        ApplicabilityHint = ProxyApplicabilityLevel.LikelyCompatible,
        CompatibilityNote = "CLI-клиент запускается в интерактивном терминале и считывает HTTP_PROXY / HTTPS_PROXY / ALL_PROXY.",
        Notes = "Дополнительный CLI-сценарий запуска OpenAI Codex в отдельном терминале."
    };

    public static ApplicationProfile? CreateByKey(string templateKey, string? resolvedPath = null)
    {
        var desktop = DesktopTemplates.FirstOrDefault(t => string.Equals(t.Key, templateKey, StringComparison.OrdinalIgnoreCase));
        if (desktop is not null)
        {
            return desktop.CreateProfile(resolvedPath);
        }

        var cli = CliTemplates.FirstOrDefault(t => string.Equals(t.Key, templateKey, StringComparison.OrdinalIgnoreCase));
        return cli?.CreateProfile(resolvedPath);
    }
}
