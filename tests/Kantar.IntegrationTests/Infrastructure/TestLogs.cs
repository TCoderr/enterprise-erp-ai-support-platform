using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>One structured log event of the application under test.</summary>
public sealed record CapturedLog(int EventId, LogLevel Level, IReadOnlyDictionary<string, object?> State)
{
    public int Number(string name) => Convert.ToInt32(State[name], System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Keeps the events of the transaction retry loop so a test can read what the unit of work decided
/// (retry or give up, and for which SQL error). Every other category is dropped.
/// </summary>
public sealed class TestLogs : ILoggerProvider
{
    public const string UnitOfWorkCategory = "Kantar.Infrastructure.Persistence.SqlUnitOfWork";
    public const int RetryEvent = 1101;
    public const int GiveUpEvent = 1102;

    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public void Clear() => _entries.Clear();

    public CapturedLog[] Of(int eventId) => [.. _entries.Where(e => e.EventId == eventId)];

    public ILogger CreateLogger(string categoryName) =>
        string.Equals(categoryName, UnitOfWorkCategory, StringComparison.Ordinal) ? new Sink(_entries) : NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class Sink(ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)
                : [];
            entries.Enqueue(new CapturedLog(eventId.Id, logLevel, values));
        }
    }
}
