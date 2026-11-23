using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDock.Core.Interfaces;

namespace ProxyDock.App.Localization;

/// <summary>
/// Сервис локализации интерфейса (основной язык — русский, с возможностью переключения на английский без перезапуска UI).
/// </summary>
public sealed class LocalizationService : ObservableObject, ILocalizationService
{
    private string _currentLanguage = "ru";

    public string CurrentLanguage => _currentLanguage;

    [IndexerName("Item")]
    public string this[string key]
    {
        get
        {
            var dict = _currentLanguage == "en" ? StringsEn.Map : StringsRu.Map;
            if (dict.TryGetValue(key, out var value))
            {
                return value;
            }

            if (StringsRu.Map.TryGetValue(key, out var fallback))
            {
                return fallback;
            }

            return key;
        }
    }

    public string Format(string key, params object[] args)
        => string.Format(this[key], args);

    public void SetLanguage(string languageCode)
    {
        var normalized = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        if (string.Equals(_currentLanguage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _currentLanguage = normalized;
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged("Item");
        OnPropertyChanged("Item[]");
    }
}
