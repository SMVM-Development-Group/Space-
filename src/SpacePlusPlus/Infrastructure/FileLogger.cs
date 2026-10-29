using System.IO;
using Microsoft.Extensions.Logging;

namespace SpacePlusPlus.Infrastructure;

/// <summary>
/// Minimal file logger with daily files and automatic trimming of old logs.
/// Errors during logging are swallowed by design: diagnostics must never break the app.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly Func<LogLevel> _minLevel;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateTime _day;

    public FileLoggerProvider(string directory, Func<LogLevel> minLevel)
    {
        _directory = directory;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var day = DateTime.Today;
                if (_writer is null || _day != day)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_directory);
                    TrimOld();
                    _writer = new StreamWriter(Path.Combine(_directory, $"spacepp-{day:yyyyMMdd}.log"), append: true) { AutoFlush = true };
                    _day = day;
                }
                _writer.WriteLine(line);
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }

    private void TrimOld()
    {
        try
        {
            var files = Directory.GetFiles(_directory, "spacepp-*.log").OrderBy(f => f).ToArray();
            for (int i = 0; i < files.Length - 7; i++) File.Delete(files[i]);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minLevel();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message = formatter(state, exception);
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {category}: {message}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
