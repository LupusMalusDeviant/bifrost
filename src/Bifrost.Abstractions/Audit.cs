using System.Text.Json;

namespace Bifrost.Abstractions;

public enum AuditEventKind
{
    ToolCall = 0,
    ServerLifecycle = 1,
    ConfigChanged = 2,
    RbacChanged = 3,
    Authentication = 4,
    AssetChanged = 5,
}

/// <summary>
/// Ein Audit-Ereignis (FR-21/22). <see cref="RedactedArguments"/> ist IMMER bereits durch
/// <see cref="IRedactionService"/> gelaufen — ungefilterte Argumente dürfen diesen Typ nie erreichen (DON'T Nr. 2).
/// </summary>
public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    IdentityId? Caller,
    CallOrigin Origin,
    AuditEventKind Kind,
    ServerId? Server,
    string? Tool,
    InvocationStatus? Status,
    JsonElement? RedactedArguments,
    long? RequestBytes,
    long? ResponseBytes,
    TimeSpan? Duration,
    /// <summary>Profil/Rollen des Aufrufers im Klartext (FR-21); null bei Systemereignissen ohne Aufrufer.</summary>
    string? CallerRoles = null,
    /// <summary>Klartext-Beschreibung bei Systemereignissen, z.B. Zustandswechsel eines Upstreams (FR-22).</summary>
    string? Detail = null,
    /// <summary>Maskierter Ergebnis-Payload — nur im Debug-Modus gefüllt (FR-24, <see cref="AuditOptions"/>).</summary>
    JsonElement? RedactedResponse = null,
    /// <summary>
    /// Verbindet mehrere Ereignisse mit **einer** Invocation (ADR-0019). Ein langlaufender Vorgang
    /// erzeugt Zeilen zu verschiedenen Zeiten — ohne diese Id ließe sich hinterher nicht sagen,
    /// welche davon zum selben Aufruf gehören. Null bei Ereignissen ohne Aufrufbezug.
    /// </summary>
    Guid? CorrelationId = null);

/// <summary>Filter für die Log-Ansicht/den Export (FR-23).</summary>
public sealed record AuditFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IdentityId? Caller = null,
    ServerId? Server = null,
    /// <summary>Präfix des namespaced Tool-Namens, z.B. <c>github__</c> — nicht exakter Vergleich.</summary>
    string? ToolPrefix = null,
    InvocationStatus? Status = null,
    AuditEventKind? Kind = null,
    CallOrigin? Origin = null,
    int Page = 1,
    int PageSize = 100);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Hot-Path-Seite des Audits: <see cref="Record"/> ist synchron, nicht-blockierend (Channel-Enqueue)
/// und darf einen Tool-Call NIE verzögern oder scheitern lassen (ADR-0007, DO Nr. 5 / DON'T Nr. 3).
/// </summary>
public interface IAuditSink
{
    void Record(AuditEvent evt);

    /// <summary>
    /// Bietet ein Ereignis an: <c>true</c>, wenn es angenommen ist, sonst <c>false</c> — ohne zu
    /// werfen und <b>ohne</b> dass eine Abweisung als Verlust zählt. Für Aufrufer, die ein
    /// abgewiesenes Ereignis selbst aufbewahren und später erneut anbieten (Vertrag
    /// „Selbsttätige Wiederverbindung", V3). Verloren ist es erst, wenn der Aufrufer es aufgibt;
    /// das meldet er über <see cref="ReportDiscarded"/>.
    /// </summary>
    bool TryRecord(AuditEvent evt)
    {
        try
        {
            Record(evt);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Meldet Ereignisse, die ein Aufrufer nach <see cref="TryRecord"/> endgültig aufgegeben hat —
    /// sie zählen als Verlust wie ein bei der Annahme verworfenes Ereignis.
    /// </summary>
    void ReportDiscarded(int count)
    {
    }
}

/// <summary>Abfrage-Seite des Audits für UI und Export (FR-23).</summary>
public interface IAuditQuery
{
    Task<PagedResult<AuditEvent>> QueryAsync(AuditFilter filter, CancellationToken ct);
}

/// <summary>Maskiert Secret-Felder in Tool-Argumenten vor der Persistierung (FR-24). Mutiert das Original nie.</summary>
public interface IRedactionService
{
    JsonElement RedactArguments(NamespacedToolName tool, JsonElement args);
}

/// <summary>
/// Pro Tool konfigurierbare, zusätzliche Redaction-Muster (FR-24) — additiv zu den globalen Defaults.
/// Wird im Hot Path gelesen; Implementierungen halten die Regeln im Speicher.
/// </summary>
public interface IRedactionRules
{
    IReadOnlyList<string>? GetPatterns(NamespacedToolName tool);

    IReadOnlyDictionary<NamespacedToolName, IReadOnlyList<string>> All { get; }

    /// <summary>Setzt die Muster eines Tools; leer/null entfernt die Regel.</summary>
    Task SetAsync(NamespacedToolName tool, IReadOnlyList<string>? patterns, CancellationToken ct);
}

/// <summary>
/// Betriebsschalter des Audits (FR-24). <see cref="CaptureResponsePayloads"/> ist der ausdrücklich
/// zu aktivierende Debug-Modus: nur dann werden vollständige — ebenfalls maskierte — Ergebnis-Payloads
/// persistiert. Default ist aus, weil Ergebnisse beliebig groß und sensibel sein können (NFR-04).
/// </summary>
public sealed record AuditOptions(bool CaptureResponsePayloads = false);
