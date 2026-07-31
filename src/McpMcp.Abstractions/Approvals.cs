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
/// Wie die Schärfe eines Tools durchgesetzt wird. <b>Nicht zu verwechseln mit der Frage, OB ein
/// Tool scharf ist</b> — das ist die Markierung, dies hier ist der Weg.
/// <para>
/// Die Trennung ist der Kern von ADR-0022. Vorher bedeutete ein einziges Häkchen zweierlei
/// zugleich: „gefährlich" <em>und</em> „über die Warteschlange". Wer die Warteschlange abschaltete,
/// löschte damit auch das Wissen, welches Werkzeug überhaupt gefährlich ist — und dieses Wissen
/// ist genau das, was der schärfere Aufrufweg braucht.
/// </para>
/// </summary>
public enum ApprovalEnforcement
{
    /// <summary>
    /// Der Aufruf wartet, bis ein Mensch in Oberfläche oder CLI entscheidet (FR-32, ADR-0012).
    /// Der Schutz liegt im Gateway und gilt für <b>jeden</b> Client. Das bisherige Verhalten und
    /// weiterhin der Vorgabewert.
    /// </summary>
    Queue = 0,

    /// <summary>
    /// Das Gateway führt sofort aus, verlangt aber den Aufruf über <c>invoke_sensitive_tool</c>
    /// statt <c>invoke_tool</c> — damit ein Client seine eigene Rückfrage genau auf die scharfen
    /// Werkzeuge legen kann, statt auf alle oder auf keins.
    /// <para>
    /// <b>Das Gateway hält hier nichts mehr auf.</b> Der Schutz hängt daran, dass der Client
    /// tatsächlich fragt; ein Client, der nicht fragt, kommt ungebremst durch. Deshalb ist das
    /// eine bewusste Entscheidung je Werkzeug und nicht der Vorgabewert. Protokolliert wird
    /// unverändert alles.
    /// </para>
    /// </summary>
    Client = 1,
}

/// <summary>
/// Pflegt, welche Tools scharf sind und wie das durchgesetzt wird (FR-32) — zur Laufzeit über die
/// UI, ohne Neustart.
/// </summary>
public interface IApprovalPolicy
{
    /// <summary>
    /// Muss dieser Aufruf in die Warteschlange? Nur bei <see cref="ApprovalEnforcement.Queue"/>.
    /// <para>
    /// Bewusst <em>nicht</em> gleichbedeutend mit <see cref="IsSensitive"/>: Ein Werkzeug im
    /// Client-Modus bleibt scharf, wird hier aber nicht aufgehalten.
    /// </para>
    /// </summary>
    bool RequiresApproval(NamespacedToolName tool);

    /// <summary>
    /// Ist dieses Werkzeug als scharf markiert — unabhängig davon, wie das durchgesetzt wird?
    /// Das ist die Frage, an der der Aufrufweg hängt.
    /// </summary>
    bool IsSensitive(NamespacedToolName tool);

    /// <summary>Der ausdrücklich festgelegte Weg, oder <c>null</c>, wenn keiner hinterlegt ist.</summary>
    ApprovalEnforcement? EnforcementFor(NamespacedToolName tool);

    /// <summary>
    /// Der Weg für alles Scharfe <b>ohne</b> eigene Festlegung. Ausgeliefert wird
    /// <see cref="ApprovalEnforcement.Queue"/>.
    /// <para>
    /// Das ist eine <em>Absicht</em>, kein Sicherheitsnetz — nicht zu verwechseln mit den
    /// Rückfällen bei Unklarheit (kaputte Spalte, unbekannter Wert, Migration alter Zeilen). Die
    /// bleiben auf <see cref="ApprovalEnforcement.Queue"/>, egal was hier steht: Ein Tippfehler
    /// darf nicht dieselbe Wirkung haben wie eine Entscheidung.
    /// </para>
    /// </summary>
    ApprovalEnforcement DefaultEnforcement { get; }

    Task SetDefaultEnforcementAsync(ApprovalEnforcement enforcement, CancellationToken ct);

    /// <summary>
    /// Der Weg, der für diesen Aufruf tatsächlich gilt — <c>null</c> heißt: nicht scharf, keine
    /// Freigabe nötig.
    /// <para>
    /// <paramref name="declaredByCatalog"/> ist die Selbstauskunft eines Connector-Pakets
    /// (<c>ToolDescriptor.RequiresApproval</c>). Sie hatte bis dahin <b>keinen</b> Weg: Ein solches
    /// Werkzeug landete immer in der Warteschlange, weil es keine Politik-Zeile gibt, an der ein
    /// Mensch etwas anderes hätte hinterlegen können. Jetzt folgt es der Vorgabe.
    /// </para>
    /// <para>
    /// Die Zusammenführung steht hier und nicht bei den Aufrufern: Es gibt zwei Quellen für
    /// „scharf" und zwei für den Weg, und vier Stellen, die das je fuer sich kombinieren, driften
    /// auseinander.
    /// </para>
    /// </summary>
    ApprovalEnforcement? EffectiveFor(NamespacedToolName tool, bool declaredByCatalog);

    IReadOnlyCollection<NamespacedToolName> All { get; }

    /// <summary>
    /// Markiert oder entmarkiert. <c>required: true</c> markiert mit
    /// <see cref="ApprovalEnforcement.Queue"/> — der sichere Vorgabewert, damit ein Aufrufer, der
    /// den Weg nicht angibt, nicht versehentlich den schwächeren bekommt.
    /// </summary>
    Task SetAsync(NamespacedToolName tool, bool required, CancellationToken ct);

    /// <summary>Markiert mit einem ausdrücklich gewählten Weg; <c>null</c> entfernt die Markierung.</summary>
    Task SetAsync(NamespacedToolName tool, ApprovalEnforcement? enforcement, CancellationToken ct);

    event EventHandler? Changed;
}
