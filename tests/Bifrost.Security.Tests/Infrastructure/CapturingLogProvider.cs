using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Bifrost.Security.Tests.Infrastructure;

/// <summary>
/// Faengt <b>alles</b> ab, was der Dienst schreibt: jede Logzeile jeder Kategorie samt Ausnahme,
/// Zustand und formatierter Meldung.
/// <para>
/// <b>Warum die Rohbestandteile mitgeschrieben werden und nicht nur der fertige Satz:</b> Ein
/// Geheimnis reist am haeufigsten <em>nicht</em> in der Meldungsvorlage, sondern in einem
/// Platzhalterwert oder im Text einer Ausnahme. Wer nur <c>formatter(state, exception)</c>
/// vergleicht, prueft die Vorlage. Wer den Zustand mitliest, prueft den Wert.
/// </para>
/// </summary>
public sealed class CapturingLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Alles Mitgeschriebene als ein Text — die Form, in der der Korpus dagegen laeuft.</summary>
    public string Text => string.Join('\n', _lines);

    public int Count => _lines.Count;

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose() => GC.SuppressFinalize(this);

    private void Append(string line) => _lines.Enqueue(line);

    private sealed class Sink(CapturingLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var builder = new StringBuilder()
                .Append(logLevel).Append(" [").Append(category).Append("] ")
                .Append(formatter(state, exception));

            // Die einzelnen Platzhalterwerte: Genau hier steht der Wert, den die Vorlage nicht
            // zeigt.
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    builder.Append(" | ").Append(key).Append('=').Append(value);
                }
            }

            for (var current = exception; current is not null; current = current.InnerException)
            {
                builder.Append(" | ex=").Append(current.GetType().Name)
                    .Append(':').Append(current.Message);
                foreach (var entry in current.Data.Keys)
                {
                    builder.Append(" | ex.data=").Append(entry).Append('=').Append(current.Data[entry]);
                }
            }

            owner.Append(builder.ToString());
        }
    }
}

/// <summary>
/// Der zweite Kanal: <see cref="Trace"/>. Nicht jede Ausgabe geht durch die Logfabrik — das
/// Framework und einige Bibliotheken schreiben hierhin, und ein Geheimnis unterscheidet nicht,
/// welchen Kanal es nimmt.
/// </summary>
public sealed class CapturingTraceListener : TraceListener
{
    private readonly StringBuilder _buffer = new();
    private readonly Lock _gate = new();

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _buffer.ToString();
            }
        }
    }

    public override void Write(string? message)
    {
        lock (_gate)
        {
            _buffer.Append(message);
        }
    }

    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            _buffer.AppendLine(message);
        }
    }
}
