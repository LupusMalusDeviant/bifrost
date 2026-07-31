using Bifrost.Abstractions;

namespace Bifrost.Core.Configuration;

/// <summary>
/// Das portable, versionierte Exportmodell (ADR-0024 E8, WP2.5).
/// <para>
/// <b>Export ist nicht Backup.</b> Ein Backup stellt <em>dieselbe</em> Instanz wieder her und
/// enthält dafür den Key-Ring; dieses Dokument baut eine <em>gleichartige</em> Instanz auf. Der
/// Regelfall landet in einem Git-Repository — deshalb steht hier im Standardfall kein einziges
/// Zugangsdatum, sondern an jeder Stelle, an der eines gebraucht wird, eine Referenz
/// (<see cref="ConfigurationSecretScrubber"/>).
/// </para>
/// <para>
/// <b>Warum eigene DTOs und nicht die Domänentypen:</b> <see cref="ServerId"/>,
/// <see cref="RoleId"/> und <see cref="NamespacedToolName"/> sind Wrapper, die als JSON
/// entweder unleserlich (<c>{"Value":"…"}</c>) oder gar nicht rücklesbar wären. Ein Format, das
/// jemand im Diff eines Pull Requests beurteilen soll, muss flach und lesbar sein. Die einzige
/// Ausnahme ist <see cref="ExportedUpstream.Config"/> — dieser Baum wird bereits in der Persistenz
/// so serialisiert (<c>EfUpstreamConfigStore</c>), und ihn ein zweites Mal nachzubauen hieße, zwei
/// Wahrheiten über dasselbe Format zu pflegen.
/// </para>
/// </summary>
/// <param name="ContainsSecrets">
/// <c>true</c> heißt: Dieses Dokument ist ein <b>Credential-Export</b> und so schützenswert wie die
/// Instanz selbst. Es entsteht nur verschlüsselt (siehe <see cref="ConfigurationExportEnvelope"/>).
/// </param>
/// <param name="SecretReferences">
/// Jede Stelle, an der ein Zugangsdatum entfernt wurde — mit dem Platzhalter, der dort jetzt steht.
/// Die Liste ist der Grund, warum ein Standardexport benutzbar bleibt: Sie sagt einem Betreiber,
/// was er auf der Zielinstanz nachtragen muss, ohne ihm zu verraten, was auf der Quelle stand.
/// </param>
/// <param name="NotExportable">
/// Was diese Instanz hält, aber bewusst <b>nicht</b> mitgeht. Steht ausdrücklich im Dokument, weil
/// ein Export, der schweigt, wie ein vollständiger aussieht.
/// </param>
public sealed record ConfigurationExportDocument(
    int FormatVersion,
    string ProductVersion,
    DateTimeOffset CreatedAt,
    bool ContainsSecrets,
    IReadOnlyList<ExportedUpstream> Upstreams,
    IReadOnlyList<ExportedRole> Roles,
    IReadOnlyList<ExportedProfile> Profiles,
    IReadOnlyList<ExportedGuardRule> GuardRules,
    IReadOnlyList<ExportedApproval> Approvals,
    IReadOnlyList<ExportedSkill> Skills,
    ExportedSettings Settings,
    IReadOnlyList<SecretPlaceholder> SecretReferences,
    IReadOnlyList<NotExportableNote> NotExportable);

/// <summary>
/// Ein Upstream-Server. <see cref="Id"/> wird <b>erhalten</b> — die Grants der Rollen zeigen über
/// genau diese Id auf den Server (<see cref="PermissionScope.Server"/>). Würde sie beim Import neu
/// vergeben, zeigten alle mitgelieferten Berechtigungen ins Leere, und zwar lautlos.
/// </summary>
public sealed record ExportedUpstream(Guid Id, string Slug, UpstreamServerConfig Config);

public sealed record ExportedRole(
    Guid Id,
    string Name,
    IReadOnlyList<ExportedGrant> Grants,
    int? RateLimitPerMinute = null);

/// <summary>
/// Ein Allow-Grant in flacher Form. <c>Server == null &amp;&amp; Tool == null</c> heißt global.
/// </summary>
public sealed record ExportedGrant(
    Guid? Server,
    string? Tool,
    IReadOnlyList<ToolAction> Actions);

public sealed record ExportedProfile(
    Guid Id,
    string Name,
    IReadOnlyList<string> PinnedTools,
    bool LazyToolsEnabled);

public sealed record ExportedGuardRule(
    string Id,
    string Description,
    string Pattern,
    string? Keyword,
    GuardDirection Direction,
    GuardMode Mode,
    bool Enabled,
    bool IsCustom);

/// <summary>Die Markierung eines scharfen Werkzeugs samt Durchsetzungsweg (ADR-0022).</summary>
public sealed record ExportedApproval(string Tool, ApprovalEnforcement Enforcement);

/// <summary>
/// Ein Skill in seiner <b>neuesten</b> Fassung. Die Historie geht bewusst nicht mit: Sie gehört zu
/// der Instanz, auf der sie entstanden ist, und ein Export ist keine Instanz.
/// <para>
/// Der Text wird unverändert übernommen. <c>IAssetStore</c> sagt ausdrücklich zu, dass Skills für
/// jede authentifizierte Identität sichtbar sind und deshalb <b>keine Secrets</b> enthalten dürfen —
/// diese Zusage wird hier vorausgesetzt, nicht nachgeprüft.
/// </para>
/// </summary>
public sealed record ExportedSkill(
    Guid Id,
    string Name,
    string? Description,
    string Content,
    ExportedSkillMetadata? Metadata);

public sealed record ExportedSkillMetadata(
    string? WhenToUse,
    IReadOnlyList<string>? References,
    IReadOnlyList<string>? RequiredTools);

/// <summary>
/// Instanzweite Schalter. Bewusst schmal: Hier steht nur, was zur Laufzeit tatsächlich pflegbar ist
/// — Verbindungszeichenfolgen, Ports und Datenverzeichnis sind Eigenschaften der <em>Maschine</em>,
/// nicht der Konfiguration, und hätten auf der Zielinstanz keine sinnvolle Bedeutung.
/// </summary>
public sealed record ExportedSettings(
    bool GuardEnabled,
    int GuardMaxScanChars,
    int GuardMatchTimeoutMs,
    bool GuardAllowCustomPatterns,
    ApprovalEnforcement DefaultApprovalEnforcement);

/// <param name="Reference">Der Platzhalter, der im Dokument an der Stelle des Werts steht.</param>
/// <param name="Location">Wo er steht, in Worten — für den Menschen, der ihn nachträgt.</param>
public sealed record SecretPlaceholder(string Reference, string Location);

/// <param name="Subject">Was betroffen ist, z. B. „3 Webhooks".</param>
/// <param name="Reason">Warum es nicht mitgeht.</param>
public sealed record NotExportableNote(string Subject, string Reason);
