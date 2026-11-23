using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using ProxyDock.Core.Interfaces;
using ProxyDock.Core.Services;

namespace ProxyDock.Infrastructure.Logging;

/// <summary>
/// Потокобезопасный файловый логгер с автоматической фильтрацией паролей и секретов.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly IPathProvider _paths;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _writeLock = new();
    private volatile LogLevel _minimumLevel = LogLevel.Information;

    public FileLoggerProvider(IPathProvider paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.LogsDirectory);
    }

    public void SetMinimumLevel(string? levelName)
    {
        if (Enum.TryParse<LogLevel>(levelName, ignoreCase: true, out var parsed))
        {
            _minimumLevel = parsed;
        }
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    internal void WriteLine(string category, LogLevel level, Exception? exception, string message)
    {
        var redactedMessage = SecretRedactor.Redact(message);
        var redactedException = exception is null ? null : SecretRedactor.Redact(exception.ToString());
        var now = DateTimeOffset.Now;
        var filePath = Path.Combine(_paths.LogsDirectory, $"proxydock-{now:yyyyMMdd}.log");

        var sb = new StringBuilder();
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        sb.Append(" [").Append(level).Append("] ");
        sb.Append(category).Append(": ").AppendLine(redactedMessage);
        if (!string.IsNullOrWhiteSpace(redactedException))
        {
            sb.AppendLine(redactedException);
        }

        lock (_writeLock)
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
    }

    public void Dispose()
    {
        _loggers.Clear();
    }

    private sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = formatter(state, exception);
            provider.WriteLine(categoryName, logLevel, exception, text);
        }
    }
}
