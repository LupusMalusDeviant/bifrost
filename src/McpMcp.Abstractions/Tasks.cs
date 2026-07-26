using System.Text.Json;

namespace McpMcp.Abstractions;

/// <summary>
/// Zustand eines langlaufenden Vorgangs ([ADR-0019](../../docs/adr/0019-langlaufende-tasks-und-events.md)).
/// <para>
/// Die Zahlen liegen persistiert in der DB — neue Werte gehören ans Ende, nicht dazwischen.
/// </para>
/// </summary>
public enum TaskState
{
    /// <summary>Angelegt, aber noch nicht in Arbeit.</summary>
    Created = 0,

    /// <summary>In Arbeit.</summary>
    Working = 1,

    /// <summary>
    /// Wartet auf eine Eingabe — bei einem freigabepflichtigen Tool auf die menschliche Freigabe,
    /// sonst auf Folge-Input nach <see cref="TaskRecord.ExpectedInputSchema"/>. Das ist derselbe
    /// Zustand, den die Freigabe-Queue vor ADR-0019 <c>Pending</c> nannte.
    /// </summary>
    InputRequired = 2,

    /// <summary>Fertig, mit Ergebnis. Terminal.</summary>
    Completed = 3,

    /// <summary>Gescheitert, mit strukturiertem Fehler. Terminal.</summary>
    Failed = 4,

    /// <summary>Abgebrochen. Terminal.</summary>
    Cancelled = 5,

    /// <summary>Frist abgelaufen, ohne Ergebnis. Terminal.</summary>
    Expired = 6,
}

/// <summary>
/// Stand eines Abbruchs (ADR-0019). Bewusst zwei Stufen: <see cref="Requested"/> heißt „wir haben
/// es dem Upstream gesagt", <see cref="Confirmed"/> heißt „er hat wirklich aufgehört". Ein
/// <c>Confirmed</c>, das kein Transport einlöst, wäre ein Feld, das Sicherheit vortäuscht.
/// </summary>
public enum TaskCancellation
{
    /// <summary>Kein Abbruch verlangt.</summary>
    None = 0,

    /// <summary>Abbruch verlangt; ob der Upstream gestoppt hat, ist offen.</summary>
    Requested = 1,

    /// <summary>Der Upstream hat den Abbruch bestätigt.</summary>
    Confirmed = 2,
}

/// <summary>
/// Ein persistierter Vorgang, der einen Request überlebt (ADR-0019, TaskV1).
/// <para>
/// Trägt bewusst nur die <b>redigierte</b> Eingabe und ihren Fingerabdruck — nie die rohe. Das war
/// schon die Regel der Freigabe-Queue, die hier aufgeht: Sonst hielte die Tabelle Secrets im
/// Klartext.
/// </para>
/// <para>
/// <see cref="Revision"/> ist monoton und dient der optimistischen Konkurrenzkontrolle: Wer einen
/// Task fortschreibt, nennt die Revision, die er gelesen hat. Terminalzustände
/// (<see cref="TaskState.Completed"/>, <see cref="TaskState.Failed"/>,
/// <see cref="TaskState.Cancelled"/>, <see cref="TaskState.Expired"/>) sind unveränderlich.
/// </para>
/// </summary>
public sealed record TaskRecord(
    Guid Id,
    /// <summary>Eigentümer — nur er (und Operator/Admin) darf lesen, folgen, canceln, nachliefern.</summary>
    IdentityId Owner,
    string OwnerDescription,
    /// <summary>Namespaced Tool-Name; die stabile Capability-Id, solange ADR-0015 nicht umgesetzt ist.</summary>
    NamespacedToolName Tool,
    /// <summary>Upstream, der den Vorgang ausführt; null, wenn er den Upstream noch nicht erreicht hat.</summary>
    ServerId? Server,
    CallOrigin Origin,
    /// <summary>Verbindet jede Zustandsänderung mit der ursprünglichen Invocation im Audit.</summary>
    Guid CorrelationId,
    TaskState State,
    int Revision,
    /// <summary>Fortschritt 0–100, wenn der Ausführende ihn meldet; sonst null.</summary>
    int? Progress,
    string InputFingerprint,
    JsonElement? RedactedInput,
    /// <summary>Ergebnis bei <see cref="TaskState.Completed"/> — redigiert wie jede Antwort.</summary>
    JsonElement? RedactedResult,
    /// <summary>Strukturierter Fehler bei <see cref="TaskState.Failed"/>.</summary>
    TaskFailure? Failure,
    /// <summary>Erwartetes Folge-Input-Schema bei <see cref="TaskState.InputRequired"/>.</summary>
    JsonElement? ExpectedInputSchema,
    TaskCancellation Cancellation,
    /// <summary>
    /// Gesetzt, sobald ein Aufruf diesen Vorgang eingelöst hat. Nötig, weil der Zustandsautomat
    /// „freigegeben" und „schon eingelöst" beide als <see cref="TaskState.Working"/> führt — die
    /// Freigabe ist aber einmalig (ADR-0012).
    /// </summary>
    DateTimeOffset? ClaimedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Terminalzustände sind unveränderlich — jede weitere Fortschreibung ist ein Fehler.</summary>
    public bool IsTerminal => State
        is TaskState.Completed or TaskState.Failed or TaskState.Cancelled or TaskState.Expired;
}

/// <summary>Strukturierter Fehler eines Vorgangs — Code für Maschinen, Meldung für Menschen.</summary>
public sealed record TaskFailure(string Code, string Message);

/// <summary>Filter für die Task-Liste. Offset-Paginierung wie im Audit (ADR-0019, Darstellung).</summary>
public sealed record TaskFilter(
    IdentityId? Owner = null,
    TaskState? State = null,
    string? ToolPrefix = null,
    int Page = 1,
    int PageSize = 100);

/// <summary>
/// Persistenz der Vorgänge. Der Vertrag ist <b>Polling</b> (ADR-0019, Entscheidung 2): Wer den Stand
/// wissen will, holt ihn. Notifications sind nur Hinweise und tragen keine Nutzlast, auf die sich
/// jemand verlassen darf — deshalb hat dieser Store keine Zustellsemantik.
/// </summary>
public interface ITaskStore
{
    /// <summary>
    /// Legt einen Vorgang an. Existiert für <c>(Owner, Tool, InputFingerprint)</c> bereits ein
    /// nicht-terminaler Vorgang, wird dieser zurückgegeben statt ein zweiter angelegt — ein Retry
    /// des Aufrufers darf keine Dublette erzeugen (dasselbe Verhalten wie die Freigabe-Queue).
    /// </summary>
    Task<TaskRecord> CreateOrGetAsync(TaskRecord record, CancellationToken ct);

    Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct);

    Task<PagedResult<TaskRecord>> ListAsync(TaskFilter filter, CancellationToken ct);

    /// <summary>
    /// Schreibt einen Vorgang fort. <paramref name="expectedRevision"/> muss der gelesenen Revision
    /// entsprechen, sonst schlägt der Aufruf fehl (optimistische Konkurrenzkontrolle) — zwei
    /// Schreiber überschreiben sich damit nicht gegenseitig. Ein Terminalzustand lässt sich nicht
    /// mehr ändern.
    /// </summary>
    Task<TaskUpdateOutcome> UpdateAsync(TaskUpdate update, int expectedRevision, CancellationToken ct);

    /// <summary>
    /// Sucht einen freigegebenen, nicht abgelaufenen Vorgang für genau diesen Aufruf und verbraucht
    /// ihn. Liegt auf dem <b>heißen Pfad jedes Tool-Calls</b> — deshalb ein eigener, indexgestützter
    /// Weg statt einer allgemeinen Abfrage.
    /// </summary>
    Task<bool> TryConsumeApprovedAsync(
        IdentityId owner, NamespacedToolName tool, string inputFingerprint, CancellationToken ct);

    /// <summary>
    /// Setzt abgelaufene, nicht-terminale Vorgänge auf <see cref="TaskState.Expired"/> und gibt
    /// deren Anzahl zurück. Für den periodischen Lauf.
    /// </summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Eine Fortschreibung. Nicht gesetzte Felder bleiben, wie sie sind.</summary>
public sealed record TaskUpdate(
    Guid Id,
    TaskState? State = null,
    int? Progress = null,
    JsonElement? RedactedResult = null,
    TaskFailure? Failure = null,
    JsonElement? ExpectedInputSchema = null,
    TaskCancellation? Cancellation = null,
    ServerId? Server = null);

/// <summary>Ergebnis einer Fortschreibung.</summary>
public enum TaskUpdateOutcome
{
    Applied = 0,

    /// <summary>Kein Vorgang mit dieser Id.</summary>
    NotFound = 1,

    /// <summary>Die Revision passte nicht — jemand anderes war schneller. Erneut lesen und wiederholen.</summary>
    RevisionMismatch = 2,

    /// <summary>Der Vorgang ist terminal und damit unveränderlich.</summary>
    Terminal = 3,
}
