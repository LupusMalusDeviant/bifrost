using McpMcp.Abstractions;

namespace McpMcp.Persistence;

/// <summary>
/// Die Freigabe-Queue auf dem Task-Modell (ADR-0019, Entscheidung 1).
/// <para>
/// <see cref="IApprovalStore"/> bleibt als Vertrag stehen — Invoker, REST-Fassade und UI ändern sich
/// nicht. Was sich ändert, ist der Unterbau: Es gibt nur noch <b>eine</b> Tabelle für Vorgänge, und
/// eine Freigabe ist ein Task-Zustand. Vorher standen zwei Warteschlangen nebeneinander, und die
/// Frage „warum steht mein Aufruf in zwei Listen" wäre nie wieder verschwunden.
/// </para>
/// <para>
/// Die Zustandsabbildung:
/// <list type="bullet">
/// <item><c>Pending</c> → <see cref="TaskState.InputRequired"/> — wartet auf einen Menschen.</item>
/// <item><c>Approved</c> → <see cref="TaskState.Working"/>, noch nicht eingelöst.</item>
/// <item><c>Consumed</c> → <see cref="TaskState.Working"/>, eingelöst (Claim gesetzt).</item>
/// <item><c>Denied</c> → <see cref="TaskState.Failed"/> mit dem Code <c>approval-denied</c>.</item>
/// </list>
/// Der Zustandsautomat von ADR-0019 unterscheidet „freigegeben" und „eingelöst" nicht — deshalb
/// hängt die Einmaligkeit am Claim-Zeitpunkt und nicht an einem zusätzlichen Zustand.
/// </para>
/// </summary>
public sealed class TaskBackedApprovalStore : IApprovalStore
{
    /// <summary>Fehlercode einer abgelehnten Freigabe — maschinenlesbar, nicht bloß eine Meldung.</summary>
    public const string DeniedCode = "approval-denied";

    private readonly ITaskStore _tasks;
    private readonly TimeProvider _time;

    public TaskBackedApprovalStore(ITaskStore tasks, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        _tasks = tasks;
        _time = time ?? TimeProvider.System;
    }

    public Task<Guid?> TryConsumeApprovalAsync(
        IdentityId caller, NamespacedToolName tool, string argumentFingerprint, CancellationToken ct)
        => _tasks.TryConsumeApprovedAsync(caller, tool, argumentFingerprint, ct);

    /// <summary>
    /// Schließt den Vorgang ab. Ein Fehlschlag hier wird verschluckt: Der Aufruf ist bereits
    /// gelaufen, und ihn nachträglich scheitern zu lassen, weil eine Statuszeile nicht schrieb,
    /// wäre die falsche Reihenfolge.
    /// </summary>
    public async Task CompleteAsync(Guid taskId, TaskFailure? failure, CancellationToken ct)
    {
        var task = await _tasks.GetAsync(taskId, ct).ConfigureAwait(false);
        if (task is null || task.IsTerminal)
        {
            return;
        }

        await _tasks.UpdateAsync(
            new TaskUpdate(
                taskId,
                State: failure is null ? TaskState.Completed : TaskState.Failed,
                Failure: failure),
            task.Revision,
            ct).ConfigureAwait(false);
    }

    public async Task<Guid> EnqueueAsync(ApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // `CreateOrGetAsync` ist idempotent über (Owner, Tool, Fingerprint) — ein Retry bekommt
        // dieselbe Id zurück, statt die Liste mit Wiederholungen zu fluten.
        var created = await _tasks.CreateOrGetAsync(
            new TaskRecord(
                request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
                request.Caller,
                request.CallerDescription,
                request.Tool,
                Server: null,
                Origin: CallOrigin.Mcp,
                // Ohne einen Aufrufkontext an dieser Stelle ist die Correlation der Vorgang selbst.
                // Sobald der Invoker sie mitgibt, tritt sie an diese Stelle.
                CorrelationId: Guid.NewGuid(),
                State: TaskState.InputRequired,
                Revision: 0,
                Progress: null,
                InputFingerprint: request.ArgumentFingerprint,
                RedactedInput: request.RedactedArguments,
                RedactedResult: null,
                Failure: null,
                ExpectedInputSchema: null,
                Cancellation: TaskCancellation.None,
                ClaimedAt: null,
                CreatedAt: request.RequestedAt,
                UpdatedAt: request.RequestedAt,
                ExpiresAt: request.ExpiresAt),
            ct).ConfigureAwait(false);
        return created.Id;
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct)
    {
        var filter = state switch
        {
            ApprovalState.Pending => new TaskFilter(State: TaskState.InputRequired),
            ApprovalState.Approved => new TaskFilter(State: TaskState.Working, Claimed: false),
            ApprovalState.Consumed => new TaskFilter(State: TaskState.Working, Claimed: true),
            ApprovalState.Denied => new TaskFilter(State: TaskState.Failed),
            _ => new TaskFilter(),
        };

        var page = await _tasks.ListAsync(filter with { PageSize = 500 }, ct).ConfigureAwait(false);
        var records = page.Items.AsEnumerable();
        // `Failed` trägt auch Vorgänge, die aus anderen Gründen gescheitert sind — als „abgelehnt"
        // gilt nur, was der Freigabe-Weg abgelehnt hat.
        if (state is ApprovalState.Denied)
        {
            records = records.Where(r => r.Failure?.Code == DeniedCode);
        }

        return [.. records.Select(ToRequest)];
    }

    public async Task DecideAsync(Guid requestId, bool approved, CancellationToken ct)
    {
        var task = await _tasks.GetAsync(requestId, ct).ConfigureAwait(false);
        // Schon entschieden oder weg — idempotent, wie vorher.
        if (task is null || task.State is not TaskState.InputRequired)
        {
            return;
        }

        var update = approved
            ? new TaskUpdate(requestId, State: TaskState.Working)
            : new TaskUpdate(
                requestId,
                State: TaskState.Failed,
                Failure: new TaskFailure(DeniedCode, "Die Freigabe wurde abgelehnt."));
        await _tasks.UpdateAsync(update, task.Revision, ct).ConfigureAwait(false);
    }

    /// <summary>Bildet einen Vorgang auf die Freigabe-Sicht zurück, die UI und REST kennen.</summary>
    private static ApprovalRequest ToRequest(TaskRecord task) => new(
        task.Id,
        task.Owner,
        task.OwnerDescription,
        task.Tool,
        task.InputFingerprint,
        task.RedactedInput,
        ToApprovalState(task),
        task.CreatedAt,
        task.ExpiresAt);

    private static ApprovalState ToApprovalState(TaskRecord task) => task.State switch
    {
        TaskState.InputRequired => ApprovalState.Pending,
        TaskState.Working when task.ClaimedAt is null => ApprovalState.Approved,
        TaskState.Working => ApprovalState.Consumed,
        TaskState.Failed when task.Failure?.Code == DeniedCode => ApprovalState.Denied,

        // Fertig heisst: freigegeben, eingeloest, durchgelaufen. Das stand hier bis 2026-07-30 im
        // Sammelfall darunter und wurde damit als `Denied` gemeldet — ein erfolgreich freigegebener
        // Aufruf erschien in der Freigabe-Ansicht als abgelehnt. Aufgefallen ist es erst, als ein
        // Test zum ersten Mal den ganzen Weg bis zur Ausfuehrung ging.
        TaskState.Completed => ApprovalState.Consumed,

        // Abgelaufen oder anderweitig beendet: für die Freigabe-Sicht ist das kein offener Vorgang
        // mehr. `Denied` ist die ehrlichste Näherung — durchgelaufen ist er jedenfalls nicht.
        _ => ApprovalState.Denied,
    };
}
