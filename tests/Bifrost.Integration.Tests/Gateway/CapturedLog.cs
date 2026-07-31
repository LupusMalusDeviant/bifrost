using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Ein Logger-Provider, der alles einsammelt, was der Gateway waehrend eines Tests schreibt.
/// <para>
/// <b>Warum ein Test Logs liest:</b> Manche Aussagen dieses Systems stehen nirgends sonst. Ob eine
/// Freigabe-Rueckfrage <em>gar nicht erst versucht</em> wurde, ob sie <em>gescheitert</em> ist oder
/// ob ein Mensch <em>Nein</em> gesagt hat — von aussen sieht das dreimal gleich aus: ein Aufruf, der
/// in der Warteschlange landet. Genau diese Verwechslung hat den Pfad zweimal falsch dastehen
/// lassen. Der Grund steht im Log, also gehoert er auch in die Pruefung.
/// </para>
/// </summary>
public sealed class CapturedLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>Alle Zeilen einer Kategorie, kleingeschrieben gesucht.</summary>
    public IReadOnlyList<string> From(string categoryFragment)
        => [.. _lines.Where(l => l.Contains(categoryFragment, StringComparison.OrdinalIgnoreCase))];

    public void Clear() => _lines.Clear();

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

    public void Dispose()
    {
        // Nichts zu schliessen — die Zeilen liegen im Speicher.
    }

    private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" | {exception.GetType().Name}: {exception.Message}";
            }

            lines.Enqueue($"[{logLevel}] {category}: {message}");
        }
    }
}
