using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Pickyfin.Server;

/// <summary>Container log lines like "2026-11-10 13:11:28  INFO  Tagging movies 1–20 of 2133".</summary>
public sealed class LogFormat() : ConsoleFormatter(Name)
{
    public const string Name = "pickyfin";

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        if (string.IsNullOrEmpty(message) && entry.Exception is null) return;
        var level = entry.LogLevel switch
        {
            LogLevel.Warning => "WARN ",
            LogLevel.Error or LogLevel.Critical => "ERROR",
            LogLevel.Debug or LogLevel.Trace => "DEBUG",
            _ => "INFO ",
        };
        // Our own messages speak for themselves; others keep their source for context
        var source = entry.Category.StartsWith("Pickyfin", StringComparison.Ordinal) ? "" : $"[{entry.Category}] ";
        writer.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level}  {source}{message}");
        if (entry.Exception is not null) writer.Write($" ({entry.Exception.GetType().Name}: {entry.Exception.Message})");
        writer.WriteLine();
    }
}
