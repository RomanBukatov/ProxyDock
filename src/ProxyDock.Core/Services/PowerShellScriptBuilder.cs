using System.Text;

namespace ProxyDock.Core.Services;

public sealed record CliScriptOptions(
    string ApplicationName,
    string TargetCommandOrPath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    Dictionary<string, string> NonSecretEnvironmentSet,
    List<string> EnvironmentRemove,
    string? MaskedProxyDisplay,
    bool IsDirectNoProxy,
    string? PlainProxyUri,
    string? DpapiHexProtectedProxyUri);

/// <summary>
/// Генерирует безопасный самоудаляющийся PowerShell-скрипт для интерактивного запуска CLI-приложений
/// без конкатенации пользовательского ввода и без записи пароля открытым текстом.
/// </summary>
public static class PowerShellScriptBuilder
{
    public static string EscapeSingleQuoted(string? value)
    {
        var normalized = value ?? string.Empty;
        return "'" + normalized.Replace("'", "''") + "'";
    }

    public static string SanitizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ProxyDock CLI";
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
    }

    public static string Build(CliScriptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("if ($MyInvocation.MyCommand.Path) { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue }");

        var title = SanitizeSingleLine(options.ApplicationName);
        sb.AppendLine($"try {{ $Host.UI.RawUI.WindowTitle = {EscapeSingleQuoted($"ProxyDock — {title}")} }} catch {{}}");

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            sb.AppendLine($"if (Test-Path -LiteralPath {EscapeSingleQuoted(options.WorkingDirectory)}) {{ Set-Location -LiteralPath {EscapeSingleQuoted(options.WorkingDirectory)} }}");
        }

        foreach (var varToRemove in options.EnvironmentRemove)
        {
            if (!string.IsNullOrWhiteSpace(varToRemove))
            {
                sb.AppendLine($"[Environment]::SetEnvironmentVariable({EscapeSingleQuoted(varToRemove)}, $null, 'Process')");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.DpapiHexProtectedProxyUri))
        {
            sb.AppendLine("$__pdSecMod = [System.IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security')");
            sb.AppendLine("if ([System.IO.Directory]::Exists($__pdSecMod)) { Microsoft.PowerShell.Core\\Import-Module -Name $__pdSecMod -ErrorAction SilentlyContinue }");
            sb.AppendLine($"$__pdSec = ConvertTo-SecureString {EscapeSingleQuoted(options.DpapiHexProtectedProxyUri)} -ErrorAction Stop");
            sb.AppendLine("$__pdUri = [System.Net.NetworkCredential]::new('', $__pdSec).Password");
            sb.AppendLine("if ([string]::IsNullOrEmpty($__pdUri)) { throw 'Не удалось расшифровать параметры прокси (DPAPI).' }");
            foreach (var proxyVar in ProxyEnvironmentBuilder.ProxyVariableNames)
            {
                sb.AppendLine($"[Environment]::SetEnvironmentVariable({EscapeSingleQuoted(proxyVar)}, $__pdUri, 'Process')");
            }
            sb.AppendLine("Remove-Variable __pdSecMod, __pdSec, __pdUri -ErrorAction SilentlyContinue");
        }
        else if (!options.IsDirectNoProxy && !string.IsNullOrWhiteSpace(options.PlainProxyUri))
        {
            foreach (var proxyVar in ProxyEnvironmentBuilder.ProxyVariableNames)
            {
                sb.AppendLine($"[Environment]::SetEnvironmentVariable({EscapeSingleQuoted(proxyVar)}, {EscapeSingleQuoted(options.PlainProxyUri)}, 'Process')");
            }
        }

        foreach (var (key, value) in options.NonSecretEnvironmentSet)
        {
            if (ProxyEnvironmentBuilder.ProxyVariableNames.Contains(key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(options.DpapiHexProtectedProxyUri) || !string.IsNullOrWhiteSpace(options.PlainProxyUri))
                {
                    continue;
                }
            }

            sb.AppendLine($"[Environment]::SetEnvironmentVariable({EscapeSingleQuoted(key)}, {EscapeSingleQuoted(value)}, 'Process')");
        }

        var bannerProxy = options.IsDirectNoProxy
            ? "без прокси (прямое подключение)"
            : (options.MaskedProxyDisplay ?? "системное окружение не задано");
        sb.AppendLine($"Write-Host {EscapeSingleQuoted($"[ProxyDock] Запуск: {title} | Прокси: {bannerProxy}")} -ForegroundColor Cyan");

        sb.AppendLine($"$__pdCmd = {EscapeSingleQuoted(options.TargetCommandOrPath)}");
        if (options.Arguments.Count == 0)
        {
            sb.AppendLine("& $__pdCmd");
        }
        else
        {
            var escapedArgs = string.Join(", ", options.Arguments.Select(EscapeSingleQuoted));
            sb.AppendLine($"$__pdArgs = @({escapedArgs})");
            sb.AppendLine("& $__pdCmd @__pdArgs");
        }

        sb.AppendLine("$__pdExit = $LASTEXITCODE");
        sb.AppendLine("if ($null -ne $__pdExit -and $__pdExit -ne 0) { Write-Host \"[ProxyDock] Процесс завершился с кодом: $__pdExit\" -ForegroundColor Yellow }");
        return sb.ToString();
    }
}
