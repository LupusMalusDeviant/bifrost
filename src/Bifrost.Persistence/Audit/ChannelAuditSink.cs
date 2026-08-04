using System.Threading.Channels;
using Bifrost.Abstractions;

namespace Bifrost.Persistence.Audit;

/// <summary>
/// Was an Audit-Ereignissen verlorenging — die Zahl, ohne die die Zusage „lückenloses Audit-Log"
/// nicht überprüfbar ist.
/// </summary>
/// <param name="LostAtIntake">
/// Bei der Annahme verworfen, weil der Channel voll war. Ursache ist Überlast: Der Schreiber kommt
/// nicht nach.
/// </param>
/// <param name="LostAfterWriteFailure">
/// Angenommen, aber nie geschrieben — der Batch ist nach allen Versuchen aufgegeben worden. Ursache
/// ist die Datenbank, nicht die Last.
/// </param>
/// <param name="FirstLossAt">
/// Wann der erste Verlust auftrat. <c>null</c>, solange nichts verlorenging. Die Angabe ist das
/// eigentlich Nützliche: Sie sagt, ab welchem Zeitpunkt dem Log nicht mehr zu trauen ist.
/// </param>
public sealed record AuditLossFacts(
    long LostAtIntake,
    long LostAfterWriteFailure,
    DateTimeOffset? FirstLossAt)
{
    public long Total => LostAtIntake + LostAfterWriteFailure;
}

/// <summary>
/// Hot-Path-Seite des Audits (ADR-0007): Best-Effort verwirft bei Überlast gezählt;
/// Compliance meldet einen vollen Channel explizit an den Aufrufer.
/// <para>
/// <b>Gezählt heißt hier auch: sichtbar.</b> Bis v0.6 gab es die Zähler, aber keinen Bericht, der
/// sie führte — ein Verlust stand in einer Logzeile und sonst nirgends. <see cref="Loss"/> ist die
/// Auskunft, aus der der Diagnosecheck BFR-DB-0007 seinen Befund bildet.
/// </para>
/// </summary>
public sealed class ChannelAuditSink : IAuditSink
{
    private readonly Channel<AuditEvent> _channel;
    private readonly AuditDeliveryMode _mode;
    private readonly TimeProvider _time;
    private long _dropped;
    private long _unwritten;
    private long _firstLossTicks;
    private int _persistenceHealthy = 1;

    public ChannelAuditSink(
        int capacity = 100_000,
        AuditDeliveryMode mode = AuditDeliveryMode.BestEffort,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        // FullMode.Wait + TryWrite: bei vollem Channel liefert TryWrite false (zählbar),
        // blockiert aber nie — DropWrite würde still verwerfen und true melden.
        _channel = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _mode = mode;
        _time = timeProvider ?? TimeProvider.System;
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Angenommene Ereignisse, deren Batch nach allen Versuchen aufgegeben wurde.</summary>
    public long UnwrittenCount => Interlocked.Read(ref _unwritten);

    public AuditDeliveryMode Mode => _mode;

    /// <summary>Die vollständige Verlustauskunft — beide Ursachen und der Zeitpunkt des ersten Verlusts.</summary>
    public AuditLossFacts Loss
    {
        get
        {
            var ticks = Interlocked.Read(ref _firstLossTicks);
            return new AuditLossFacts(
                DroppedCount,
                UnwrittenCount,
                ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        }
    }

    public bool IsHealthy => Volatile.Read(ref _persistenceHealthy) == 1
        && (_mode != AuditDeliveryMode.Compliance || DroppedCount == 0);

    internal ChannelReader<AuditEvent> Reader => _channel.Reader;

    public void Record(AuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _dropped);
            MarkFirstLoss();
            if (_mode == AuditDeliveryMode.Compliance)
            {
                throw new AuditUnavailableException(
                    "Audit-Channel ist voll; Compliance-Modus verweigert weitere Aktionen.");
            }
        }
    }

    /// <summary>Signalisiert dem Batch-Writer das Ende (Shutdown) — danach wird der Rest gedraint.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    internal void ReportPersistenceFailure()
        => Volatile.Write(ref _persistenceHealthy, 0);

    internal void ReportPersistenceSuccess()
        => Volatile.Write(ref _persistenceHealthy, 1);

    /// <summary>
    /// Ein Batch wurde endgültig aufgegeben. <b>Der einzige Weg, Ereignisse fallenzulassen</b> —
    /// und er hinterlässt eine Zahl, keine Lücke im Ungefähren.
    /// </summary>
    internal void ReportBatchDiscarded(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _unwritten, count);
        MarkFirstLoss();
        // Der Verlust ist ein Persistenzfehler und bleibt einer, bis wieder etwas ankommt.
        ReportPersistenceFailure();
    }

    /// <summary>Setzt den Zeitstempel des ERSTEN Verlusts; spätere Verluste ändern ihn nicht.</summary>
    private void MarkFirstLoss()
        => Interlocked.CompareExchange(ref _firstLossTicks, _time.GetUtcNow().UtcTicks, 0);
}

public sealed class AuditUnavailableException(string message) : InvalidOperationException(message);
