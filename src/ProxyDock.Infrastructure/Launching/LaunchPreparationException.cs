namespace ProxyDock.Infrastructure.Launching;

/// <summary>
/// Контролируемая ошибка подготовки запуска с понятным сообщением и подсказкой пользователю.
/// </summary>
public sealed class LaunchPreparationException : Exception
{
    public LaunchPreparationException(string message, string? suggestedAction = null)
        : base(message)
    {
        SuggestedAction = suggestedAction;
    }

    public string? SuggestedAction { get; }
}
