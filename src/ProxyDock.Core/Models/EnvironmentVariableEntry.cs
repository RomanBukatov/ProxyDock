namespace ProxyDock.Core.Models;

/// <summary>
/// Пользовательская переменная окружения, применяемая только к запускаемому процессу.
/// </summary>
public sealed class EnvironmentVariableEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
}
