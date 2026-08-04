namespace Bifrost.Abstractions.Usage;

/// <summary>
/// Der betrachtete Zeitraum. Er steht in jedem Ergebnis — eine Nutzungsaussage ohne Fenster ist
/// keine: „ungenutzt" heißt immer „ungenutzt <b>seit wann</b>".
/// </summary>
public sealed record UsageWindow(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Duration => To - From;
}

/// <param name="EstimatedSchemaTokens">
/// Die geschätzten Kosten dieses Werkzeugs im Kontext eines Agenten — <b>Exposure</b>.
/// </param>
/// <param name="Invocations">
/// Wie oft es im Fenster wirklich aufgerufen wurde — <b>Invocation</b>. Die beiden Größen werden
/// nie verrechnet: Das eine ist, was ein Profil zumutet, das andere, was davon benutzt wurde.
/// </param>
/// <param name="StillInCatalog">
/// <c>false</c> für ein Werkzeug, das im Fenster benutzt wurde und heute nicht mehr existiert. Es
/// kostet nichts mehr und darf trotzdem nicht verschwinden — sonst sieht eine Instanz, die gerade
/// einen Upstream verloren hat, aus wie eine, in der nie etwas benutzt wurde.
/// </param>
public sealed record ToolUsage(
    string Tool,
    int EstimatedSchemaTokens,
    long Invocations,
    DateTimeOffset? LastUsed,
    bool StillInCatalog = true)
{
    /// <summary>
    /// Ungenutzt <b>im Fenster</b>. Bewusst nicht „unnötig": Ein Werkzeug ohne Aufruf kann das sein,
    /// was einmal im Quartal den Betrieb rettet.
    /// </summary>
    public bool UnusedInWindow => Invocations == 0;
}

/// <summary>
/// Was ein Profil einer Identität zumutet und was davon ankommt.
/// </summary>
/// <param name="UsedButNotExposed">
/// Werkzeuge, die aufgerufen wurden, ohne im Profil zu stehen — der Lazy-Pfad über die Meta-Tools
/// (ADR-0003). Ohne diese Liste sähe ein sparsam gepinntes Profil aus, als würde niemand arbeiten.
/// </param>
/// <param name="Notes">
/// Grenzen, die dieser Bericht selbst gezogen hat: abgeschnittene Auditmenge, gekürzte Liste,
/// Aufbewahrungsdauer. Eine Zahl ohne ihre Grenze ist eine Behauptung.
/// </param>
public sealed record UsageReport(
    IdentityId Identity,
    UsageWindow Window,
    int ExposedTools,
    int ExposedTokens,
    long TotalInvocations,
    IReadOnlyList<ToolUsage> Exposed,
    IReadOnlyList<ToolUsage> UsedButNotExposed,
    IReadOnlyList<string> Notes);

/// <param name="RemovedButUsed">
/// <b>Die Konsequenz.</b> Werkzeuge, die der Vorschlag entfernt und die im Fenster benutzt wurden.
/// Ein Cockpit, das nur die Einsparung zeigt, ist ein Verkaufsprospekt.
/// </param>
public sealed record ProfileSimulation(
    IdentityId Identity,
    UsageWindow Window,
    int TokensBefore,
    int TokensAfter,
    IReadOnlyList<string> Removed,
    IReadOnlyList<ToolUsage> RemovedButUsed,
    IReadOnlyList<string> Notes)
{
    public int TokensSaved => TokensBefore - TokensAfter;
}

/// <summary>
/// Rechnet, zeigt und simuliert — <b>und ändert nichts</b> (Pflichtenheft WP6.4).
/// <para>
/// Kein Aufruf dieser Schnittstelle verändert ein Profil, eine Rolle oder einen Grant. Ein Vorschlag
/// ist ein Vorschlag; wer ihn übernimmt, tut das über die Profilverwaltung und sieht dabei, was er
/// tut.
/// </para>
/// </summary>
public interface IUsageCockpit
{
    Task<UsageReport> AnalyseAsync(IdentityId identity, UsageWindow window, CancellationToken ct);

    /// <summary>
    /// Rechnet einen vorgeschlagenen Profilschnitt durch: Was spart er, und was nimmt er weg, das
    /// benutzt wurde.
    /// </summary>
    Task<ProfileSimulation> SimulateAsync(
        IdentityId identity, UsageWindow window, IReadOnlyList<string> proposedTools, CancellationToken ct);
}
