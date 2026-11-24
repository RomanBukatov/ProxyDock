namespace ProxyDock.App.Localization;

internal static class StringsRu
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AppTitle"] = "ProxyDock",
        ["AppSubtitle"] = "Desktop Proxy Launcher",

        ["NavApplications"] = "Приложения",
        ["NavProxies"] = "Прокси",
        ["NavHistory"] = "История",
        ["NavSettings"] = "Настройки",

        ["DefaultProxyLabel"] = "Прокси по умолчанию",
        ["NoDefaultProxy"] = "Не назначен",
        ["ManageProxiesShort"] = "Настроить",
        ["BtnAddProxy"] = "+ Новый прокси",

        ["AppsTitle"] = "Приложения",
        ["SearchAppsPlaceholder"] = "Поиск приложений...",
        ["FilterAll"] = "Все",
        ["FilterDesktop"] = "Desktop",
        ["FilterCli"] = "CLI",
        ["BtnAdd"] = "+ Добавить",
        ["MenuAddDesktop"] = "Desktop-приложение (.exe)...",
        ["MenuAddCli"] = "CLI-программа (терминал)...",
        ["MenuTemplatesHeader"] = "Готовые профили",

        ["DetectedAppsTitle"] = "Обнаруженные приложения",
        ["DetectedOnPc"] = "Обнаружено на этом ПК",
        ["BtnAddDetected"] = "Добавить",
        ["AddAllDetected"] = "Добавить все",

        ["AppsEmptyTitle"] = "Приложений пока нет",
        ["AppsEmptySubtitle"] = "Добавьте обнаруженное приложение ниже или выберите .exe вручную.",
        ["AppsNoMatches"] = "По вашему запросу приложения не найдены.",
        ["TargetMissingShort"] = "Файл не найден",

        ["BtnLaunch"] = "Запустить",
        ["BtnMoreActionsTooltip"] = "Дополнительные действия",
        ["MenuLaunchDirect"] = "Запустить без прокси",
        ["MenuAssignProxy"] = "Назначить прокси",
        ["ProxyModeDefault"] = "По умолчанию",
        ["ProxyModeDirect"] = "Без прокси",
        ["MenuEditDetails"] = "Изменить и диагностика...",
        ["MenuDuplicate"] = "Дублировать",
        ["MenuOpenFolder"] = "Открыть папку",
        ["MenuDelete"] = "Удалить",

        ["BtnEdit"] = "Изменить",
        ["BtnTest"] = "Проверить",
        ["BtnCancel"] = "Отмена",
        ["BtnSave"] = "Сохранить",
        ["BtnDelete"] = "Удалить",
        ["BtnDuplicate"] = "Дублировать",
        ["BtnClose"] = "Закрыть",
        ["BtnBrowse"] = "Обзор...",

        ["ProxiesTitle"] = "Прокси-профили",
        ["ProxiesEmptyTitle"] = "Прокси-профили не настроены",
        ["ProxiesEmptySubtitle"] = "Добавьте HTTP-прокси (например, 127.0.0.1:8080 или внешний сервер), чтобы запускать через него приложения.",
        ["BtnTestAll"] = "Проверить все",
        ["BtnSetDefault"] = "Сделать по умолчанию",
        ["DefaultBadge"] = "По умолчанию",

        ["HistoryTitle"] = "История запусков",
        ["HistorySearchPlaceholder"] = "Поиск по приложению, прокси или сообщению...",
        ["BtnClearHistory"] = "Очистить",
        ["HistoryDisclaimer"] = "Примечание: успешный старт процесса означает передачу параметров запуска, но не гарантирует соблюдение прокси закрытым сетевым стеком программы.",
        ["HistoryEmpty"] = "Записей в истории запусков пока нет.",
        ["StatusFilterAll"] = "Все статусы",
        ["StatusFilterStarted"] = "Успешный старт",
        ["StatusFilterWarning"] = "С предупреждением",
        ["StatusFilterError"] = "Ошибка",

        ["SettingsTitle"] = "Настройки",
        ["SettingsAppearance"] = "Оформление и язык",
        ["SettingsTheme"] = "Тема интерфейса",
        ["SettingsLanguage"] = "Язык интерфейса",
        ["SettingsLifecycleTitle"] = "Окно и системный трей",
        ["SettingsLifecycleHint"] = "По умолчанию ProxyDock полностью завершается при закрытии окна и не остаётся в фоне. Уже запущенные приложения продолжают работать независимо.",
        ["SettingsMinimizeToTray"] = "Сворачивать в системный трей при закрытии окна",
        ["SettingsStartMinimized"] = "Запускать свёрнутым в трей",
        ["SettingsRunAtStartup"] = "Запускать ProxyDock вместе с Windows (HKCU Run)",
        ["SettingsDefaults"] = "Параметры запуска по умолчанию",
        ["SettingsDefaultProxy"] = "HTTP-прокси по умолчанию",
        ["SettingsDefaultTerminal"] = "Терминал для CLI",
        ["SettingsDefaultCliDir"] = "Рабочая папка CLI по умолчанию",
        ["SettingsLogging"] = "Диагностика и перенос",
        ["SettingsLogLevel"] = "Уровень логирования",
        ["BtnOpenLogs"] = "Открыть логи",
        ["BtnOpenConfig"] = "Открыть конфиг",
        ["BtnExportConfig"] = "Экспорт JSON...",
        ["BtnImportConfig"] = "Импорт JSON...",
        ["SettingsLimitationsTitle"] = "Принцип работы",
        ["SettingsLimitationsBody"] = "ProxyDock задаёт переменные окружения процесса и опциональный флаг --proxy-server только для запускаемого приложения. Системный прокси Windows, реестр и глобальные сетевые настройки не изменяются.",

        ["SystemProxyUnchangedShort"] = "Системный прокси Windows не изменяется",
        ["TrayActiveBadge"] = "Трей активен",

        ["TrayOpen"] = "Открыть ProxyDock",
        ["TrayQuickLaunch"] = "Быстрый запуск",
        ["TraySettings"] = "Настройки",
        ["TrayExit"] = "Выход",
        ["TrayEmpty"] = "(Нет добавленных приложений)"
    };
}
