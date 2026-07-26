using System.Text.Json;
using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpMcp.Persistence.Audit;

/// <summary>
/// Liest den Audit-Channel und persistiert in Batches (Flush ≤ FlushInterval oder ≤ MaxBatchSize,
/// ADR-0007). Beim Shutdown wird der Channel vollständig gedraint — Audit-Vollständigkeit geht
/// vor Shutdown-Geschwindigkeit. Best-Effort loggt fehlgeschlagene Batches; Compliance retryt
/// sie mit Backpressure.
/// </summary>
public sealed partial class AuditBatchWriter
{
    private readonly ChannelAuditSink _sink;
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly PersistenceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditBatchWriter> _logger;

    public AuditBatchWriter(
        ChannelAuditSink sink,
        IDbContextFactory<McpMcpDbContext> factory,
        PersistenceOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<AuditBatchWriter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(factory);
        _sink = sink;
        _factory = factory;
        _options = options ?? new PersistenceOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AuditBatchWriter>.Instance;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var reader = _sink.Reader;
        var batch = new List<AuditEvent>(_options.AuditMaxBatchSize);

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                using var flushWindow = new CancellationTokenSource(_options.AuditFlushInterval, _time);
                while (batch.Count < _options.AuditMaxBatchSize)
                {
                    if (reader.TryRead(out var evt))
                    {
                        batch.Add(evt);
                        continue;
                    }

                    try
                    {
                        if (!await reader.WaitToReadAsync(flushWindow.Token).ConfigureAwait(false))
                        {
                            break; // Channel abgeschlossen
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Flush-Fenster abgelaufen
                    }
                }

                await WriteBatchAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown → Drain unten
        }

        while (_sink.Reader.TryRead(out var evt))
        {
            batch.Add(evt);
            if (batch.Count >= _options.AuditMaxBatchSize)
            {
                await WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                batch.Clear();
            }
        }

        await WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);

        if (_sink.DroppedCount > 0)
        {
            Log.EventsDropped(_logger, _sink.DroppedCount);
        }
    }

    private async Task WriteBatchAsync(List<AuditEvent> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        while (true)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                db.AuditEvents.AddRange(batch.Select(ToRow));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                _sink.ReportPersistenceSuccess();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _sink.ReportPersistenceFailure();
                Log.BatchWriteFailed(_logger, ex, batch.Count, _options.AuditMode.ToString());
                if (_options.AuditMode == AuditDeliveryMode.BestEffort)
                {
                    return;
                }

                await Task.Delay(_options.AuditRetryInterval, _time, ct).ConfigureAwait(false);
            }
        }
    }

    internal static AuditEventRow ToRow(AuditEvent evt) => new()
    {
        Timestamp = evt.Timestamp,
        CallerId = evt.Caller?.Value,
        Origin = (int)evt.Origin,
        Kind = (int)evt.Kind,
        ServerId = evt.Server?.Value,
        Tool = evt.Tool,
        Status = evt.Status is { } s ? (int)s : null,
        RedactedArgumentsJson = evt.RedactedArguments?.GetRawText(),
        RequestBytes = evt.RequestBytes,
        ResponseBytes = evt.ResponseBytes,
        DurationMs = evt.Duration?.TotalMilliseconds,
        CallerRoles = evt.CallerRoles,
        Detail = evt.Detail,
        RedactedResponseJson = evt.RedactedResponse?.GetRawText(),
        CorrelationId = evt.CorrelationId,
    };

    internal static AuditEvent ToEvent(AuditEventRow row) => new(
        row.Timestamp,
        row.CallerId is { } c ? new IdentityId(c) : null,
        (CallOrigin)row.Origin,
        (AuditEventKind)row.Kind,
        row.ServerId is { } s ? new ServerId(s) : null,
        row.Tool,
        row.Status is { } st ? (InvocationStatus)st : null,
        row.RedactedArgumentsJson is { } json ? JsonSerializer.Deserialize<JsonElement>(json) : null,
        row.RequestBytes,
        row.ResponseBytes,
        row.DurationMs is { } d ? TimeSpan.FromMilliseconds(d) : null,
        row.CallerRoles,
        row.Detail,
        row.RedactedResponseJson is { } resp ? JsonSerializer.Deserialize<JsonElement>(resp) : null,
        row.CorrelationId);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error,
            Message = "Audit-Batch mit {Count} Ereignissen konnte nicht persistiert werden (Modus {Mode}).")]
        public static partial void BatchWriteFailed(
            ILogger logger, Exception ex, int count, string mode);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Audit-Channel war voll: {Count} Ereignisse wurden verworfen (Kapazität erhöhen oder DB-Last prüfen).")]
        public static partial void EventsDropped(ILogger logger, long count);
    }
}
