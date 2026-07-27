using System.Text.Json;

namespace McpMcp.Abstractions;

/// <summary>Stand einer Freigabe-Anfrage (FR-32, ADR-0012).</summary>
public enum ApprovalState
{
    /// <summary>Wartet auf eine menschliche Entscheidung.</summary>
    Pending = 0,

    /// <summary>Freigegeben; die nächste identische Anfrage läuft einmalig durch.</summary>
    Approved = 1,

    /// <summary>Abgelehnt.</summary>
    Denied = 2,

    /// <summary>Nach Freigabe eingelöst — verbraucht.</summary>
    Consumed = 3,
}

/// <summary>
/// Eine Freigabe-Anfrage. Trägt bewusst nur die <b>redigierten</b> Argumente und ihren
/// Fingerabdruck — nie die rohen (ADR-0012, sonst hielte die Queue Secrets im Klartext).
/// </summary>
public sealed record ApprovalRequest(
    Guid Id,
    IdentityId Caller,
    string CallerDescription,
    NamespacedToolName Tool,
    string ArgumentFingerprint,
    JsonElement? RedactedArguments,
    ApprovalState State,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Queue und Auswertung der Freigaben. Der Invoker fragt <see cref="TryConsumeApprovalAsync"/> vor
/// dem Upstream-Call; die UI bedient die Warteschlange.
/// </summary>
public interface IApprovalStore
{
    /// <summary>
    /// Sucht eine gültige (freigegebene, nicht abgelaufene, nicht widerrufene) Freigabe für genau
    /// diesen Aufruf und verbraucht sie.
    /// <para>
    /// Liefert die <b>Id des verbrauchten Vorgangs</b>, wenn der Call durchlaufen darf, sonst
    /// <c>null</c>. Die Id statt eines <c>bool</c>, damit der Aufrufer den Vorgang nach dem Aufruf
    /// abschließen kann — ohne sie blieb ein eingelöster Vorgang für immer auf <c>Working</c> stehen
    /// und lief still in den Verfall, obwohl er erfolgreich war.
    /// </para>
    /// </summary>
    Task<Guid?> TryConsumeApprovalAsync(
        IdentityId caller, NamespacedToolName tool, string argumentFingerprint, CancellationToken ct);

    /// <summary>
    /// Schließt einen eingelösten Vorgang ab. <paramref name="failure"/> gesetzt heißt: gescheitert,
    /// sonst erfolgreich. Fehler beim Abschließen dürfen den Aufruf nicht beeinflussen — er ist zu
    /// diesem Zeitpunkt bereits gelaufen.
    /// </summary>
    Task CompleteAsync(Guid taskId, TaskFailure? failure, CancellationToken ct);

    /// <summary>
    /// Legt eine neue wartende Anfrage an (oder liefert die bestehende, wenn schon eine identische
    /// wartet — kein Duplikat bei Retry). Gibt die Anfrage-Id zurück.
    /// </summary>
    Task<Guid> EnqueueAsync(ApprovalRequest request, CancellationToken ct);

    Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct);

    Task DecideAsync(Guid requestId, bool approved, CancellationToken ct);
}

/// <summary>
/// Pflegt, welche Tools eine Freigabe erfordern (FR-32) — zur Laufzeit über die UI, ohne Neustart.
/// </summary>
public interface IApprovalPolicy
{
    bool RequiresApproval(NamespacedToolName tool);

    IReadOnlyCollection<NamespacedToolName> All { get; }

    Task SetAsync(NamespacedToolName tool, bool required, CancellationToken ct);

    event EventHandler? Changed;
}
