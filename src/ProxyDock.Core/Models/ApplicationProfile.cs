using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

/// <summary>
/// Профиль запускаемого приложения (основной сценарий — Desktop, дополнительный — CLI).
/// </summary>
public sealed class ApplicationProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public LaunchKind LaunchKind { get; set; } = LaunchKind.Desktop;
    public string ExecutableOrCommand { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public string? Arguments { get; set; }
    public ProxyAssignmentMode ProxyMode { get; set; } = ProxyAssignmentMode.InheritDefault;
    public Guid? ProxyProfileId { get; set; }
    public bool UseChromiumProxyFlag { get; set; }
    public bool LaunchInTerminal { get; set; }
    public bool KeepTerminalOpen { get; set; } = true;
    public TerminalPreference? TerminalOverride { get; set; }
    public string? NoProxyOverride { get; set; }
    public List<EnvironmentVariableEntry> CustomEnvironmentVariables { get; set; } = [];
    public List<string> RelatedProcessNames { get; set; } = [];
    public string? TemplateKey { get; set; }
    public ProxyApplicabilityLevel? ApplicabilityHint { get; set; }
    public string? CompatibilityNote { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLaunchedAtUtc { get; set; }
}
