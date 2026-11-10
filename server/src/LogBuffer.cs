using System.Collections.Concurrent;

namespace Pickyfin.Server;

/// <summary>Keeps the last few hundred log lines for the status page.</summary>
public sealed class LogBuffer : ILoggerProvider
{
    private const int Capacity = 300;
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public void Add(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > Capacity) _lines.TryDequeue(out _);
    }

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    public void Dispose() { }

    private sealed class BufferLogger(LogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information && category.StartsWith("Pickyfin", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch { LogLevel.Warning => "warn", LogLevel.Error or LogLevel.Critical => "error", _ => "info" };
            buffer.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {formatter(state, exception)}");
        }
    }
}
