using ProxyDock.Core.Enums;
using ProxyDock.Core.Models;
using ProxyDock.Core.Services;

namespace ProxyDock.Core.Validation;

public static class ApplicationProfileValidator
{
    public static ValidationResult Validate(ApplicationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            result.AddError(nameof(profile.Name), "Укажите название приложения.");
        }

        if (string.IsNullOrWhiteSpace(profile.ExecutableOrCommand))
        {
            var msg = profile.LaunchKind == LaunchKind.Desktop
                ? "Укажите путь к исполняемому файлу (.exe)."
                : "Укажите команду или путь к CLI-программе.";
            result.AddError(nameof(profile.ExecutableOrCommand), msg);
        }

        if (profile.ProxyMode == ProxyAssignmentMode.Specific && profile.ProxyProfileId is null)
        {
            result.AddError(nameof(profile.ProxyProfileId), "Выберите профиль прокси или измените режим прокси.");
        }

        if (!CommandLineTokenizer.TryTokenize(profile.Arguments, out _, out var argError))
        {
            result.AddError(nameof(profile.Arguments), argError ?? "Ошибка разбора аргументов запуска.");
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in profile.CustomEnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) && string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                result.AddError(nameof(profile.CustomEnvironmentVariables), "Имя переменной окружения не может быть пустым.");
                break;
            }

            var key = entry.Key.Trim();
            if (key.Contains('=') || key.Contains('\0') || key.Any(char.IsWhiteSpace))
            {
                result.AddError(nameof(profile.CustomEnvironmentVariables), $"Некорректное имя переменной окружения: «{key}».");
                break;
            }

            if (!seenKeys.Add(key))
            {
                result.AddError(nameof(profile.CustomEnvironmentVariables), $"Дублирующаяся переменная окружения: «{key}».");
                break;
            }
        }

        return result;
    }
}
