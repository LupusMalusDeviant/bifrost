using System.Text.Json;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Persistence.Audit;

/// <summary>
/// Liest den Audit-Channel und persistiert in Batches (Flush ≤ FlushInterval oder ≤ MaxBatchSize,
/// ADR-0007). Beim Shutdown wird der Channel vollständig gedraint — Audit-Vollständigkeit geht
/// vor Shutdown-Geschwindigkeit. Best-Effort loggt fehlgeschlagene Batches; Compliance retryt
/// sie mit Backpressure.
/// </summary>
public sealed partial class AuditBatchWriter
{
    private readonly ChannelAuditSink _sink;
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly PersistenceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditBatchWriter> _logger;

    /// <summary>
    /// Der Kettenabschnitt <b>dieses</b> Schreiberlaufs (M6/WP6.2, FR-P073).
    /// <para>
    /// <b>Warum je Lauf und nicht global:</b> Eine durchgehende Kette setzt genau einen Schreiber
    /// voraus. Zwei Instanzen auf derselben Datenbank — oder ein Neustart — verschränkten ihre
    /// Ketten, und der Prüfer meldete Manipulation, wo keine war. Ein falscher Alarm über
    /// Manipulation ist schlimmer als keine Kette: Er entwertet den echten Fund.
    /// </para>
    /// </summary>
    private readonly Guid _chainId = Guid.NewGuid();

    private long _chainIndex;

    private string _previousHash = AuditChain.Genesis;

    public AuditBatchWriter(
        ChannelAuditSink sink,
        IDbContextFactory<BifrostDbContext> factory,
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

        // Ein Batch, der schon in die Kette gehängt ist, aber noch nicht geschrieben (Plan 0005
        // D14). Bricht das Herunterfahren seine Schreibung ab, schreibt der Drain unten genau diese
        // Zeilen — mit ihren Kettenstellen —, statt dieselben Ereignisse ein zweites Mal einzuhängen.
        // Sonst stünden sie an späterer Stelle, und die übersprungenen Stellen sähen aus wie
        // verlorene Zeilen, ohne dass ein Verlust gezählt wäre.
        List<AuditEventRow>? pending = null;

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

                if (batch.Count > 0)
                {
                    pending = Chain(batch);
                    batch.Clear();
                    await WriteRowsAsync(pending, ct).ConfigureAwait(false);
                    pending = null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown → Drain unten
        }

        if (pending is not null)
        {
            await WriteRowsAsync(pending, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Schreibt einen Batch — und gibt ihn im Modus <see cref="AuditDeliveryMode.BestEffort"/> erst
    /// nach <see cref="PersistenceOptions.AuditMaxWriteAttempts"/> Versuchen auf.
    /// <para>
    /// <b>Warum überhaupt Versuche, wenn Best-Effort verwerfen darf:</b> Der Vertrag der Hot-Path-
    /// Seite (<c>IAuditSink.Record</c>) verlangt, dass ein Tool-Aufruf nie am Audit scheitert oder
    /// wartet. Diese Methode läuft aber nicht im Hot Path, sondern im Hintergrunddienst — sie hält
    /// keinen einzigen Aufruf auf. Der Verzicht auf jeden zweiten Versuch war deshalb nie durch den
    /// Vertrag gedeckt, sondern nur bequem: Der häufigste Grund für einen Fehlschlag ist ein
    /// vorübergehend gesperrter Schreib-Lock, und der ist beim zweiten Anlauf weg.
    /// </para>
    /// <para>
    /// <b>Und warum nicht unbegrenzt:</b> Das ist der Compliance-Modus. Unbegrenzt heißt bei einer
    /// dauerhaft kaputten Datenbank, dass der Channel vollläuft und die Verluste stattdessen bei der
    /// Annahme entstehen — verschoben, nicht vermieden. Best-Effort gibt darum irgendwann auf,
    /// <b>zählt aber jedes aufgegebene Ereignis</b> (BFR-DB-0007).
    /// </para>
    /// </summary>
    private async Task WriteBatchAsync(List<AuditEvent> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // Die Kette entsteht EINMAL je Batch, nicht je Versuch: Ein zweiter Anlauf nach einem
        // gesperrten Schreib-Lock darf nicht dieselben Ereignisse ein zweites Mal einhängen —
        // sonst zählte der Abschnitt Positionen hoch, die nie in der Ablage landen, und der Prüfer
        // meldete Lücken, wo nur ein Wiederholungsversuch war.
        await WriteRowsAsync(Chain(batch), ct).ConfigureAwait(false);
    }

    /// <summary>Schreibt schon verkettete Zeilen — mit den Versuchen aus <see cref="WriteBatchAsync"/>.</summary>
    private async Task WriteRowsAsync(List<AuditEventRow> rows, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                db.AuditEvents.AddRange(rows);
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
                attempt++;
                _sink.ReportPersistenceFailure();
                Log.BatchWriteFailed(_logger, ex, rows.Count, _options.AuditMode.ToString(), attempt);

                if (_options.AuditMode == AuditDeliveryMode.BestEffort
                    && attempt >= _options.AuditMaxWriteAttempts)
                {
                    // Ab hier ist das Log nachweislich lückenhaft. Die Zahl geht in die
                    // Verlustauskunft der Senke und von dort in den Diagnosebericht — ein Verwerfen
                    // ohne Spur gibt es nicht mehr.
                    _sink.ReportBatchDiscarded(rows.Count);
                    Log.BatchDiscarded(_logger, rows.Count, attempt, _sink.UnwrittenCount);
                    return;
                }

                await Task.Delay(_options.AuditRetryInterval, _time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Hängt einen Batch an die Kette dieses Laufs. Läuft im Hintergrunddienst — der Hot Path
    /// (<c>IAuditSink.Record</c>) sieht davon nichts und wartet auf nichts.
    /// </summary>
    private List<AuditEventRow> Chain(List<AuditEvent> batch)
    {
        var rows = new List<AuditEventRow>(batch.Count);
        foreach (var evt in batch)
        {
            var row = ToRow(evt);
            row.ChainId = _chainId;
            row.ChainIndex = _chainIndex++;
            row.PreviousHash = _previousHash;
            row.EntryHash = AuditChain.Link(_previousHash, evt);
            _previousHash = row.EntryHash;
            rows.Add(row);
        }

        return rows;
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
            Message = "Audit-Batch mit {Count} Ereignissen konnte nicht persistiert werden "
                + "(Modus {Mode}, Versuch {Attempt}).")]
        public static partial void BatchWriteFailed(
            ILogger logger, Exception ex, int count, string mode, int attempt);

        [LoggerMessage(Level = LogLevel.Critical,
            Message = "Audit-Batch mit {Count} Ereignissen nach {Attempts} Versuchen aufgegeben — "
                + "diese Ereignisse fehlen im Log dauerhaft. Ungeschrieben insgesamt: {Total} "
                + "(siehe Diagnosecode BFR-DB-0007).")]
        public static partial void BatchDiscarded(
            ILogger logger, int count, int attempts, long total);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Audit-Channel war voll: {Count} Ereignisse wurden verworfen (Kapazität erhöhen oder DB-Last prüfen).")]
        public static partial void EventsDropped(ILogger logger, long count);
    }
}
