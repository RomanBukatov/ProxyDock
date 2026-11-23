namespace ProxyDock.Core.Models;

public sealed record ValidationError(string PropertyName, string Message);

public sealed class ValidationResult
{
    public List<ValidationError> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;

    public void AddError(string propertyName, string message)
    {
        Errors.Add(new ValidationError(propertyName, message));
    }

    public static ValidationResult Success() => new();
}

/// <summary>
/// Обёртка для версионирования схемы JSON-конфигурации и миграций.
/// </summary>
public sealed class ConfigEnvelope<T>
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public T Data { get; set; } = default!;
}

public sealed record ConfigLoadWarning(string FileName, string Message, string? BackupPath);

/// <summary>
/// Экспортируемый пакет конфигурации без паролей и секретов.
/// </summary>
public sealed class ExportBundle
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<ApplicationProfile> Applications { get; set; } = [];
    public List<ProxyProfile> Proxies { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
