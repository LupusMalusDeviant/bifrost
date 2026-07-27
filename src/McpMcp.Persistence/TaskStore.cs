using System.Text.Json;
using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// Persistenz der langlaufenden Vorgänge (ADR-0019, TaskV1).
/// <para>
/// Der Vertrag ist Polling: Dieser Store hält den Zustand, er stellt nichts zu. Fortschreibungen
/// laufen über eine monotone Revision — wer schreibt, nennt die Revision, die er gelesen hat, und
/// verliert gegen einen schnelleren Schreiber statt ihn zu überschreiben.
/// </para>
/// </summary>
public sealed class TaskStore : ITaskStore
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly TimeProvider _time;

    public TaskStore(IDbContextFactory<McpMcpDbContext> factory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = time ?? TimeProvider.System;
    }

    public async Task<TaskRecord> CreateOrGetAsync(TaskRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Ein Retry des Aufrufers darf keine Dublette erzeugen. Gesucht wird über denselben Index,
        // den auch der Consume-Pfad nutzt.
        var existing = await db.Tasks
            .Where(r => r.OwnerId == record.Owner.Value
                && r.Tool == record.Tool.Value
                && r.InputFingerprint == record.InputFingerprint
                && (r.State == (int)TaskState.Created
                    || r.State == (int)TaskState.Working
                    || r.State == (int)TaskState.InputRequired))
            .OrderBy(r => r.CreatedAtTicks)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return ToRecord(existing);
        }

        var row = ToRow(record);
        db.Tasks.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToRecord(row);
    }

    public async Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Tasks.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    public async Task<PagedResult<TaskRecord>> ListAsync(TaskFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = db.Tasks.AsQueryable();
        if (filter.Owner is { } owner)
        {
            query = query.Where(r => r.OwnerId == owner.Value);
        }

        if (filter.State is { } state)
        {
            query = query.Where(r => r.State == (int)state);
        }

        if (!string.IsNullOrWhiteSpace(filter.ToolPrefix))
        {
            var prefix = filter.ToolPrefix;
            query = query.Where(r => r.Tool.StartsWith(prefix));
        }

        if (filter.Claimed is { } claimed)
        {
            query = claimed
                ? query.Where(r => r.ClaimedAtTicks != null)
                : query.Where(r => r.ClaimedAtTicks == null);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 500);
        var rows = await query
            .OrderByDescending(r => r.CreatedAtTicks)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<TaskRecord>([.. rows.Select(ToRecord)], total, page, size);
    }

    public async Task<TaskUpdateOutcome> UpdateAsync(
        TaskUpdate update, int expectedRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Tasks.FirstOrDefaultAsync(r => r.Id == update.Id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return TaskUpdateOutcome.NotFound;
        }

        if (row.Revision != expectedRevision)
        {
            return TaskUpdateOutcome.RevisionMismatch;
        }

        // Terminal heißt unveränderlich (ADR-0019). Ohne diese Sperre könnte ein spät eintreffender
        // Schreiber ein abgeschlossenes Ergebnis überschreiben oder einen Abbruch zurücknehmen.
        if (IsTerminal(row.State))
        {
            return TaskUpdateOutcome.Terminal;
        }

        if (update.State is { } state)
        {
            row.State = (int)state;
        }

        if (update.Progress is { } progress)
        {
            row.Progress = Math.Clamp(progress, 0, 100);
        }

        if (update.RedactedResult is { } result)
        {
            row.RedactedResultJson = result.GetRawText();
        }

        if (update.Failure is { } failure)
        {
            row.FailureCode = failure.Code;
            row.FailureMessage = failure.Message;
        }

        if (update.ExpectedInputSchema is { } schema)
        {
            row.ExpectedInputSchemaJson = schema.GetRawText();
        }

        if (update.Cancellation is { } cancellation)
        {
            row.Cancellation = (int)cancellation;
        }

        if (update.Server is { } server)
        {
            row.ServerId = server.Value;
        }

        row.Revision++;
        row.UpdatedAtTicks = _time.GetUtcNow().UtcTicks;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TaskUpdateOutcome.Applied;
    }

    /// <summary>
    /// Bricht einen Vorgang ab (siehe <see cref="ITaskStore.CancelAsync"/>). Läuft nichts, ist der
    /// Abbruch sofort endgültig; ein bereits eingelöster Vorgang lässt sich nicht mehr abbrechen.
    /// </summary>
    public async Task<TaskUpdateOutcome> CancelAsync(Guid id, int expectedRevision, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Tasks.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return TaskUpdateOutcome.NotFound;
        }

        if (row.Revision != expectedRevision)
        {
            return TaskUpdateOutcome.RevisionMismatch;
        }

        if (IsTerminal(row.State))
        {
            return TaskUpdateOutcome.Terminal;
        }

        // Eingelöst heißt: Der Aufruf ist bereits durch die Pipeline gegangen. Da ist nichts mehr
        // zu stoppen, und es als abgebrochen zu führen wäre eine Unwahrheit über einen Aufruf, der
        // stattgefunden hat.
        if (row.ClaimedAtTicks is not null)
        {
            return TaskUpdateOutcome.NotCancellable;
        }

        var now = _time.GetUtcNow().UtcTicks;
        row.State = (int)TaskState.Cancelled;
        // Bestätigt, nicht nur verlangt: Es gibt keinen Ausführenden, der noch etwas bestätigen
        // müsste — es läuft nichts. Genau das meint ADR-0019 mit „confirmed nur wo belegbar".
        row.Cancellation = (int)TaskCancellation.Confirmed;
        row.Revision++;
        row.UpdatedAtTicks = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TaskUpdateOutcome.Applied;
    }

    public async Task<bool> TryConsumeApprovedAsync(
        IdentityId owner, NamespacedToolName tool, string inputFingerprint, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var nowTicks = _time.GetUtcNow().UtcTicks;

        // Freigegeben und noch nicht eingelöst: Zustand `working`, `ClaimedAt` leer, Frist offen.
        // Die Freigabe ist einmalig (ADR-0012) — deshalb wird hier geclaimt, nicht bloß gelesen.
        var match = await db.Tasks
            .Where(r => r.OwnerId == owner.Value
                && r.Tool == tool.Value
                && r.InputFingerprint == inputFingerprint
                && r.State == (int)TaskState.Working
                && r.ClaimedAtTicks == null
                // Ein widerrufener Vorgang ist nicht einlösbar. Der Zustandsvergleich oben würde
                // das schon abdecken, weil ein Abbruch auf `Cancelled` setzt — aber diese Bedingung
                // hält auch dann, wenn ein späterer Pfad einen Abbruch nur vermerkt, ohne den
                // Zustand zu wechseln. An einer Freigabe ist das die Bedingung, die man doppelt
                // haben will.
                && r.Cancellation == (int)TaskCancellation.None
                && r.ExpiresAtTicks > nowTicks)
            .OrderBy(r => r.CreatedAtTicks)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (match is null)
        {
            return false;
        }

        match.ClaimedAtTicks = nowTicks;
        match.Revision++;
        match.UpdatedAtTicks = nowTicks;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var nowTicks = now.UtcTicks;
        var due = await db.Tasks
            .Where(r => r.ExpiresAtTicks <= nowTicks
                && r.State != (int)TaskState.Completed
                && r.State != (int)TaskState.Failed
                && r.State != (int)TaskState.Cancelled
                && r.State != (int)TaskState.Expired)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in due)
        {
            row.State = (int)TaskState.Expired;
            row.Revision++;
            row.UpdatedAtTicks = nowTicks;
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return due.Count;
    }

    private static bool IsTerminal(int state) => state
        is (int)TaskState.Completed
        or (int)TaskState.Failed
        or (int)TaskState.Cancelled
        or (int)TaskState.Expired;

    private static TaskRow ToRow(TaskRecord record) => new()
    {
        Id = record.Id,
        OwnerId = record.Owner.Value,
        OwnerDescription = record.OwnerDescription,
        Tool = record.Tool.Value,
        ServerId = record.Server?.Value,
        Origin = (int)record.Origin,
        CorrelationId = record.CorrelationId,
        State = (int)record.State,
        Revision = record.Revision,
        Progress = record.Progress,
        InputFingerprint = record.InputFingerprint,
        RedactedInputJson = record.RedactedInput?.GetRawText(),
        RedactedResultJson = record.RedactedResult?.GetRawText(),
        FailureCode = record.Failure?.Code,
        FailureMessage = record.Failure?.Message,
        ExpectedInputSchemaJson = record.ExpectedInputSchema?.GetRawText(),
        Cancellation = (int)record.Cancellation,
        ClaimedAtTicks = record.ClaimedAt?.UtcTicks,
        CreatedAtTicks = record.CreatedAt.UtcTicks,
        UpdatedAtTicks = record.UpdatedAt.UtcTicks,
        ExpiresAtTicks = record.ExpiresAt.UtcTicks,
    };

    private static TaskRecord ToRecord(TaskRow row) => new(
        row.Id,
        new IdentityId(row.OwnerId),
        row.OwnerDescription,
        new NamespacedToolName(row.Tool),
        row.ServerId is { } server ? new ServerId(server) : null,
        (CallOrigin)row.Origin,
        row.CorrelationId,
        (TaskState)row.State,
        row.Revision,
        row.Progress,
        row.InputFingerprint,
        Parse(row.RedactedInputJson),
        Parse(row.RedactedResultJson),
        row.FailureCode is { } code ? new TaskFailure(code, row.FailureMessage ?? string.Empty) : null,
        Parse(row.ExpectedInputSchemaJson),
        (TaskCancellation)row.Cancellation,
        row.ClaimedAtTicks is { } claimed ? new DateTimeOffset(claimed, TimeSpan.Zero) : null,
        new DateTimeOffset(row.CreatedAtTicks, TimeSpan.Zero),
        new DateTimeOffset(row.UpdatedAtTicks, TimeSpan.Zero),
        new DateTimeOffset(row.ExpiresAtTicks, TimeSpan.Zero));

    private static JsonElement? Parse(string? json)
        => json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
