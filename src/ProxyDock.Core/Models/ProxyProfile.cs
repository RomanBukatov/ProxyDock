using ProxyDock.Core.Enums;

namespace ProxyDock.Core.Models;

/// <summary>
/// Конфигурация HTTP-прокси без секретов (пароль хранится отдельно через DPAPI).
/// </summary>
public sealed class ProxyProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Http;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 8080;
    public string? Username { get; set; }
    public bool HasCredentials { get; set; }
    public string NoProxy { get; set; } = "localhost,127.0.0.1,::1";
    public bool IsDefault { get; set; }
    public int CheckTimeoutSeconds { get; set; } = 10;
    public string DiagnosticEndpointUrl { get; set; } = "https://api.ipify.org?format=json";
    public ProxyCheckStatus LastCheckStatus { get; set; } = ProxyCheckStatus.Unknown;
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public long? LastLatencyMs { get; set; }
    public string? LastExternalIp { get; set; }
    public string? LastCheckMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
