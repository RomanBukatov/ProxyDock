using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Models;

namespace ProxyDock.Infrastructure.Persistence;

/// <summary>
/// Атомарное хранилище JSON-конфигурации с резервным копированием, версионированием схемы и защитой от битых файлов.
/// </summary>
public sealed class JsonConfigStore
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IPathProvider _paths;
    private readonly IConfigDiagnosticsStore _diagnostics;
    private readonly ILogger<JsonConfigStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public JsonConfigStore(
        IPathProvider paths,
        IConfigDiagnosticsStore diagnostics,
        ILogger<JsonConfigStore> logger)
    {
        _paths = paths;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public static JsonSerializerOptions DefaultSerializerOptions => SerializerOptions;

    public async Task<T> LoadAsync<T>(
        string filePath,
        Func<T> defaultFactory,
        Func<int, JsonElement, T>? migrateFromElement = null,
        CancellationToken cancellationToken = default)
    {
        var gate = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                return defaultFactory();
            }

            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return QuarantineCorruptFile(filePath, "Файл конфигурации пуст.", defaultFactory);
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElem))
                {
                    var version = root.TryGetProperty("schemaVersion", out var verElem) && verElem.TryGetInt32(out var v)
                        ? v
                        : 0;

                    if (version < CurrentSchemaVersion && migrateFromElement is not null)
                    {
                        var migrated = migrateFromElement(version, dataElem);
                        await SaveInternalAsync(filePath, migrated, cancellationToken).ConfigureAwait(false);
                        return migrated;
                    }

                    var envelope = JsonSerializer.Deserialize<ConfigEnvelope<T>>(json, SerializerOptions);
                    if (envelope is null || envelope.Data is null)
                    {
                        return QuarantineCorruptFile(filePath, "Некорректная структура секции data.", defaultFactory);
                    }

                    return envelope.Data;
                }

                // Миграция legacy v0 (без обёртки ConfigEnvelope)
                if (migrateFromElement is not null)
                {
                    var migratedV0 = migrateFromElement(0, root);
                    await SaveInternalAsync(filePath, migratedV0, cancellationToken).ConfigureAwait(false);
                    return migratedV0;
                }

                var direct = JsonSerializer.Deserialize<T>(json, SerializerOptions);
                if (direct is null)
                {
                    return QuarantineCorruptFile(filePath, "Не удалось разобрать JSON-данные.", defaultFactory);
                }

                await SaveInternalAsync(filePath, direct, cancellationToken).ConfigureAwait(false);
                return direct;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Повреждённый JSON в файле конфигурации {FilePath}.", filePath);
                return QuarantineCorruptFile(filePath, $"Повреждён JSON: {ex.Message}", defaultFactory);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                _logger.LogError(ex, "Ошибка десериализации конфигурации {FilePath}.", filePath);
                return QuarantineCorruptFile(filePath, $"Несовместимый формат конфигурации: {ex.Message}", defaultFactory);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync<T>(string filePath, T data, CancellationToken cancellationToken = default)
    {
        var gate = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(filePath, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveInternalAsync<T>(string filePath, T data, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Directory.CreateDirectory(_paths.BackupsDirectory);

        var envelope = new ConfigEnvelope<T>
        {
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Data = data
        };

        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        var tempPath = $"{filePath}.tmp.{Guid.NewGuid():N}";

        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        await using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(filePath))
        {
            var backupName = $"{Path.GetFileNameWithoutExtension(filePath)}.bak.json";
            var backupPath = Path.Combine(_paths.BackupsDirectory, backupName);
            File.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, filePath, overwrite: true);
        }
    }

    private T QuarantineCorruptFile<T>(string filePath, string reason, Func<T> defaultFactory)
    {
        string? quarantinePath = null;
        try
        {
            Directory.CreateDirectory(_paths.BackupsDirectory);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            quarantinePath = Path.Combine(_paths.BackupsDirectory, $"{fileName}.corrupt.{stamp}.{Guid.NewGuid():N}.json");
            File.Copy(filePath, quarantinePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось скопировать повреждённый файл {FilePath} в backups.", filePath);
        }

        var shortName = Path.GetFileName(filePath);
        _diagnostics.AddWarning(new ConfigLoadWarning(
            shortName,
            $"Файл «{shortName}» повреждён ({reason}). Создана резервная копия, загружена конфигурация по умолчанию.",
            quarantinePath));

        return defaultFactory();
    }
}
