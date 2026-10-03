using Microsoft.Extensions.Logging;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// An <see cref="ILogger{T}"/> that keeps every entry it is given, formatted, so a test can
    /// assert what was logged and at which level.
    /// </summary>
    public sealed class CollectingLogger<T> : ILogger<T>
    {
        /// <summary>Every entry logged, in order.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        /// <summary>The formatted messages logged at <paramref name="level"/>, in order.</summary>
        public IEnumerable<string> At(LogLevel level) =>
            Entries.Where(e => e.Level == level).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
