using System.Threading.Channels;
using Bifrost.Abstractions;

namespace Bifrost.Persistence.Audit;

/// <summary>
/// Hot-Path-Seite des Audits (ADR-0007): Best-Effort verwirft bei Überlast gezählt;
/// Compliance meldet einen vollen Channel explizit an den Aufrufer.
/// </summary>
public sealed class ChannelAuditSink : IAuditSink
{
    private readonly Channel<AuditEvent> _channel;
    private readonly AuditDeliveryMode _mode;
    private long _dropped;
    private int _persistenceHealthy = 1;

    public ChannelAuditSink(
        int capacity = 100_000,
        AuditDeliveryMode mode = AuditDeliveryMode.BestEffort)
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
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public AuditDeliveryMode Mode => _mode;

    public bool IsHealthy => Volatile.Read(ref _persistenceHealthy) == 1
        && (_mode != AuditDeliveryMode.Compliance || DroppedCount == 0);

    internal ChannelReader<AuditEvent> Reader => _channel.Reader;

    public void Record(AuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _dropped);
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
}

public sealed class AuditUnavailableException(string message) : InvalidOperationException(message);
