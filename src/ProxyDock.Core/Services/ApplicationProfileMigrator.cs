using System.Text.Json;
using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;

namespace ProxyDock.Core.Services;

/// <summary>
/// Безопасная миграция профилей приложений (v0/v1 -> v2) для включения подтверждённой стратегии запуска
/// Codex Desktop и обновлённой стратегии Claude Desktop без перезаписи явных пользовательских переопределений.
/// </summary>
public static class ApplicationProfileMigrator
{
    public const int LatestApplicationsSchemaVersion = 2;

    public static bool IsCodexDesktopProfile(ApplicationProfile app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.LaunchKind != LaunchKind.Desktop)
        {
            return false;
        }

        if (string.Equals(app.TemplateKey, BuiltInAppTemplates.CodexDesktopKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = app.ExecutableOrCommand?.Trim().Trim('"') ?? string.Empty;
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "chatgpt-desktop.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "codex-desktop.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase)
               && (string.Equals(fileName, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(fileName, "Codex.exe", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsClaudeDesktopProfile(ApplicationProfile app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.LaunchKind != LaunchKind.Desktop)
        {
            return false;
        }

        if (string.Equals(app.TemplateKey, BuiltInAppTemplates.ClaudeDesktopKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = app.ExecutableOrCommand?.Trim().Trim('"') ?? string.Empty;
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "claude-desktop.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Contains(@"\Claude_", StringComparison.OrdinalIgnoreCase)
               && string.Equals(fileName, "Claude.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MigrateFromSchemaVersion(
        int fromVersion,
        IList<ApplicationProfile> profiles,
        IReadOnlyList<JsonElement>? rawElements = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        if (fromVersion >= LatestApplicationsSchemaVersion)
        {
            return false;
        }

        var anyChanged = false;
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < profiles.Count; i++)
        {
            var app = profiles[i];
            if (app is null)
            {
                continue;
            }

            var isCodex = IsCodexDesktopProfile(app);
            var isClaude = !isCodex && IsClaudeDesktopProfile(app);
            if (!isCodex && !isClaude)
            {
                continue;
            }

            var rawElement = rawElements is not null && i < rawElements.Count
                ? rawElements[i]
                : (JsonElement?)null;

            var profileChanged = false;

            if (!HasManualChromiumProxyArguments(app.Arguments) && !app.UseChromiumProxyFlag)
            {
                app.UseChromiumProxyFlag = true;
                profileChanged = true;
            }

            var hasExplicitNodeFlagInJson = rawElement is { ValueKind: JsonValueKind.Object } elem
                                            && HasPropertyIgnoreCase(elem, "useNodeEnvProxy");
            var hasCustomNodeEnvVar = app.CustomEnvironmentVariables.Any(e =>
                string.Equals(e.Key?.Trim(), ProxyEnvironmentBuilder.NodeUseEnvProxyVariableName, StringComparison.OrdinalIgnoreCase));

            if (!hasExplicitNodeFlagInJson && !hasCustomNodeEnvVar && !app.UseNodeEnvProxy)
            {
                app.UseNodeEnvProxy = true;
                profileChanged = true;
            }

            if (isCodex)
            {
                var template = BuiltInAppTemplates.CreateCodexDesktop();
                var effectiveNodeEnv = app.UseNodeEnvProxy || app.CustomEnvironmentVariables.Any(e =>
                    string.Equals(e.Key?.Trim(), ProxyEnvironmentBuilder.NodeUseEnvProxyVariableName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Value?.Trim(), "1", StringComparison.Ordinal));

                if (app.UseChromiumProxyFlag
                    && effectiveNodeEnv
                    && app.ApplicabilityHint is null or ProxyApplicabilityLevel.NeedsVerification or ProxyApplicabilityLevel.Unverified)
                {
                    app.ApplicabilityHint = ProxyApplicabilityLevel.Confirmed;
                    profileChanged = true;
                }

                if (IsLegacyDefaultCodexCompatibilityNote(app.CompatibilityNote))
                {
                    app.CompatibilityNote = template.CompatibilityNote;
                    profileChanged = true;
                }

                if (IsLegacyDefaultCodexNotes(app.Notes))
                {
                    app.Notes = template.Notes;
                    profileChanged = true;
                }
            }
            else if (isClaude)
            {
                var template = BuiltInAppTemplates.CreateClaudeDesktop();

                if (app.ApplicabilityHint is null or ProxyApplicabilityLevel.Unverified)
                {
                    app.ApplicabilityHint = ProxyApplicabilityLevel.NeedsVerification;
                    profileChanged = true;
                }

                if (IsLegacyDefaultClaudeCompatibilityNote(app.CompatibilityNote))
                {
                    app.CompatibilityNote = template.CompatibilityNote;
                    profileChanged = true;
                }
            }

            if (profileChanged)
            {
                app.UpdatedAtUtc = now;
                anyChanged = true;
            }
        }

        return anyChanged;
    }

    private static bool HasManualChromiumProxyArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        if (CommandLineTokenizer.TryTokenize(arguments, out var tokens, out _))
        {
            return tokens.Any(t =>
                t.StartsWith("--proxy-server", StringComparison.OrdinalIgnoreCase)
                || t.Equals("--no-proxy-server", StringComparison.OrdinalIgnoreCase));
        }

        return arguments.Contains("--proxy-server", StringComparison.OrdinalIgnoreCase)
               || arguments.Contains("--no-proxy-server", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPropertyIgnoreCase(JsonElement obj, string propertyName)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLegacyDefaultCodexCompatibilityNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return true;
        }

        var trimmed = note.Trim();
        return trimmed.StartsWith("MSIX (ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("MSIX Chromium / Owl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyDefaultCodexNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return true;
        }

        return notes.Trim().StartsWith("Если в интерфейсе чаты зависают", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyDefaultClaudeCompatibilityNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return true;
        }

        var trimmed = note.Trim();
        return trimmed.StartsWith("MSIX Electron + служба cowork-svc.exe:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("MSIX Electron / Chromium", StringComparison.OrdinalIgnoreCase);
    }
}
