namespace Bifrost.Abstractions.Operations;

/// <summary>
/// Verträge der Betriebsdienste (M2, ADR-0024). Die Fachlogik liegt HIER dahinter — CLI, REST-API
/// und Weboberfläche sind Adapter und bauen keine zweite Variante derselben Regeln.
/// <para>
/// Diese Datei wird vom Lead gelegt und ist für die Dauer der Welle eingefroren. Wer einen Vertrag
/// ändern will, meldet das, statt ihn zu erweitern: An diesen Typen hängen vier parallel arbeitende
/// Pakete.
/// </para>
/// </summary>
public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>Bereiche, die ein Backup enthalten kann. Ein Restore stellt genau die enthaltenen her.</summary>
[Flags]
public enum BackupSections
{
    None = 0,
    Database = 1,
    KeyRing = 2,
    Packages = 4,
    Config = 8,

    /// <summary>
    /// Alles. <b>Enthält den Key-Ring und ist damit so schützenswert wie die Instanz selbst</b>
    /// (ADR-0024 E3) — ohne ihn wäre die gesicherte Datenbank beim Zurückspielen unlesbar.
    /// </summary>
    All = Database | KeyRing | Packages | Config,
}

/// <param name="Passphrase">
/// Fehlt sie, entsteht ein <b>unverschlüsseltes</b> Archiv. Das ist erlaubt (automatische Sicherung
/// auf ein bereits verschlüsseltes Ziel), muss aber vom Aufrufer benannt werden — nicht still
/// passieren.
/// </param>
public sealed record BackupRequest(
    string TargetPath,
    BackupSections Sections = BackupSections.All,
    string? Passphrase = null);

public sealed record BackupResult(
    string ArchivePath,
    long SizeBytes,
    BackupManifest Manifest);

/// <summary>
/// Was im Archiv steht, bevor irgendetwas ausgepackt wird. Liegt <b>unverschlüsselt</b> im Archiv,
/// auch wenn die Nutzlast verschlüsselt ist: Ein Werkzeug muss prüfen können, was es vor sich hat.
/// </summary>
public sealed record BackupManifest(
    int FormatVersion,
    string ProductVersion,
    string MinimumRestoreVersion,
    DateTimeOffset CreatedAt,
    string InstanceId,
    DatabaseProvider Provider,
    string? MigrationId,
    BackupSections Sections,
    bool Encrypted,
    string ChecksumAlgorithm);

/// <summary>
/// Ergebnis der Prüfung eines Archivs <b>ohne</b> Restore. Ein Teilarchiv wird nie als gültig
/// gemeldet (ADR-0024 E4).
/// </summary>
public sealed record BackupInspection(
    bool Valid,
    BackupManifest? Manifest,
    IReadOnlyList<string> Problems);

public enum RestoreMode
{
    /// <summary>Vorgabe: nur auf eine leere Zielinstallation.</summary>
    EmptyTargetOnly,

    /// <summary>Bestehende Daten überschreiben — verlangt eine ausdrückliche Bestätigung.</summary>
    Replace,
}

public sealed record RestoreRequest(
    string ArchivePath,
    RestoreMode Mode = RestoreMode.EmptyTargetOnly,
    string? Passphrase = null);

/// <summary>
/// Das Ergebnis der Vorprüfung. <see cref="IRestoreService.PlanAsync"/> vor
/// <see cref="IRestoreService.ApplyAsync"/> ist Pflicht und kein Komfort: Ein Restore, der erst
/// beim Schreiben merkt, dass er nicht passt, hat bereits geschrieben.
/// </summary>
/// <param name="PreBackupPath">
/// Sicherung des vorhandenen Zustands, die vor einem <see cref="RestoreMode.Replace"/> entsteht.
/// Ohne Ausweg kein Überschreiben (ADR-0024 E5).
/// </param>
/// <param name="Token">
/// Undurchsichtiges Handle auf den vorgemerkten Vorgang. <see cref="IRestoreService.ApplyAsync"/>
/// findet darüber Archivpfad und Passphrase wieder, die der Plan bewusst <b>nicht</b> trägt — eine
/// Passphrase, die durch eine API-Antwort läuft, steht danach in jedem Log.
/// <para>
/// Der Zustand bleibt beim Dienst; nur das Handle reist. Damit überlebt der Plan den Weg über eine
/// HTTP-Schnittstelle, wo er als JSON hinausgeht und als neues Objekt zurückkommt. Ein unbekanntes
/// oder abgelaufenes Handle führt zu einer klaren Absage, nicht zu einem Restore auf geratenen
/// Daten.
/// </para>
/// </param>
public sealed record RestorePlan(
    bool CanApply,
    BackupManifest? Manifest,
    RestoreMode Mode,
    bool TargetIsEmpty,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string? PreBackupPath = null,
    string? Token = null);

public sealed record RestoreResult(
    bool Applied,
    BackupSections RestoredSections,
    string? PreBackupPath,
    IReadOnlyList<string> Notes);

public interface IBackupService
{
    Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken ct);

    Task<BackupInspection> InspectAsync(string archivePath, string? passphrase, CancellationToken ct);
}

public interface IRestoreService
{
    Task<RestorePlan> PlanAsync(RestoreRequest request, CancellationToken ct);

    Task<RestoreResult> ApplyAsync(RestorePlan plan, CancellationToken ct);
}

// ── Diagnose ────────────────────────────────────────────────────────────────────────────────────

public enum CheckStatus
{
    Pass,
    Warning,
    Fail,

    /// <summary>Nicht anwendbar oder Voraussetzung fehlt — ausdrücklich KEIN stilles Bestehen.</summary>
    Skipped,
}

[Flags]
public enum DiagnosticScope
{
    None = 0,
    Configuration = 1,
    Database = 2,
    KeyRing = 4,
    Network = 8,
    Runtime = 16,
    Upstreams = 32,
    All = Configuration | Database | KeyRing | Network | Runtime | Upstreams,
}

/// <param name="Code">
/// Stabil und maschinenlesbar (<c>BFR-DB-0003</c>). Stabil heißt: Der Code überlebt Umformulierungen
/// des Textes — er ist das, worauf ein Betreiber ein Runbook oder eine Suche stützt.
/// </param>
/// <param name="SafeDetails">
/// Zusatzangaben, die den Vorfall erklären. <b>Nie Credentials</b>, auch nicht gekürzt: Ein halbes
/// Secret in einer Diagnoseausgabe ist ein Secret in einer Diagnoseausgabe.
/// </param>
public sealed record DiagnosticCheck(
    string Code,
    CheckStatus Status,
    string Summary,
    string? Remediation = null,
    IReadOnlyDictionary<string, string>? SafeDetails = null);

public sealed record DiagnosticReport(
    DiagnosticScope Scope,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<DiagnosticCheck> Checks)
{
    public bool HasFailures => Checks.Any(c => c.Status is CheckStatus.Fail);

    public bool HasWarnings => Checks.Any(c => c.Status is CheckStatus.Warning);
}

public interface IDiagnosticService
{
    Task<DiagnosticReport> RunAsync(DiagnosticScope scope, CancellationToken ct);
}

// ── Konfigurationsexport (ADR-0024 E8) ──────────────────────────────────────────────────────────

/// <summary>
/// Export ist <b>nicht</b> Backup. Das Backup stellt dieselbe Instanz wieder her und enthält dafür
/// den Key-Ring; der Export baut eine gleichartige Instanz auf und enthält deshalb
/// <b>keine Secretwerte</b>, sondern Referenzen oder Masken.
/// </summary>
public sealed record ConfigurationExportRequest(
    bool IncludeSecrets = false,
    string? Passphrase = null);

public sealed record ConfigurationExport(
    int FormatVersion,
    string ProductVersion,
    DateTimeOffset CreatedAt,
    bool ContainsSecrets,
    string Payload);

/// <param name="Unchanged">
/// Objekte, die auf der Zielinstanz inhaltsgleich bereits vorliegen. Ohne diese Liste wäre auf einer
/// vorbelegten Instanz — etwa mit dem mitgelieferten Guard-Regelsatz — <b>kein einziger Export je
/// anwendbar</b>, weil jede Übereinstimmung als Konflikt zählte.
/// </param>
/// <param name="Token">
/// Undurchsichtiges Handle auf die vorgemerkte Nutzlast. Siehe <see cref="RestorePlan.Token"/> —
/// dieselbe Begründung, dieselbe Wirkung.
/// </param>
/// <param name="Updates">
/// Objekte, die <b>ersetzt</b> würden (nur im Update-Modus). Bewusst eine eigene Liste neben den
/// Zugängen: „wird angelegt" und „wird überschrieben" sind für einen Betreiber zwei verschiedene
/// Sätze, und der zweite ist der, bei dem man hinsieht.
/// </param>
public sealed record ConfigurationImportPlan(
    bool CanApply,
    IReadOnlyList<string> Additions,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> MissingDependencies,
    IReadOnlyList<string>? Unchanged = null,
    string? Token = null,
    IReadOnlyList<string>? Updates = null);

/// <summary>
/// Die instanzweiten Schalter, die der Export überträgt.
/// <para>
/// Liegt hier und nicht im Kern, weil der Rückweg eines Imports sie tragen muss
/// (<see cref="ConfigurationImportUndo.PreviousSettings"/>) — und der ist Teil des Vertrags.
/// </para>
/// </summary>
public sealed record InstanceSettings(GuardOptions Guard, ApprovalEnforcement DefaultApprovalEnforcement)
{
    /// <summary>
    /// Der Auslieferungszustand. Er ist der Maßstab für die Frage, ob auf der Zielinstanz jemand
    /// eine eigene Entscheidung getroffen hat, die ein Import nicht überfahren darf.
    /// </summary>
    public static InstanceSettings Defaults { get; } = new(new GuardOptions(), ApprovalEnforcement.Queue);
}

/// <summary>
/// Wie ein Import mit Objekten umgeht, die es auf der Zielinstanz schon gibt.
/// <para>
/// <b><see cref="AddOnly"/> ist und bleibt die Vorgabe.</b> Auf ihr beruhte die Zusage, mit der
/// dieser Weg gebaut wurde: Jede Rücknahme entfernt genau das, was der Vorgang Sekunden zuvor
/// angelegt hat, und kann nichts treffen, was vorher da war. Wer diese Zusage aufgibt, muss die
/// Rücknahme neu begründen — genau das leistet <see cref="ConfigurationImportUndo"/>.
/// </para>
/// </summary>
public enum ConfigurationImportMode
{
    /// <summary>Bestehendes ist ein Konflikt. Der Import legt nur an.</summary>
    AddOnly = 0,

    /// <summary>
    /// Bestehende <b>Rollen und Profile</b> werden ersetzt, statt zu kollidieren (M6/WP6.1). Der
    /// vorige Inhalt wird dabei mitgeschrieben; ohne ihn wäre das Ersetzen eine Einbahnstraße durch
    /// die Rechteverwaltung.
    /// <para>
    /// Ausdrücklich <b>nur</b> Rollen und Profile: Upstreams, Guard-Regeln, Freigaben und Skills
    /// bleiben additiv. Ein Modus, der alles ersetzt, wäre eine Synchronisierung — und die nimmt
    /// auch weg, was die Nutzlast nur nicht kennt.
    /// </para>
    /// </summary>
    Update = 1,
}

/// <param name="ReplacedRoles">Rollen in ihrem Zustand <b>vor</b> der Übernahme.</param>
/// <param name="ReplacedProfiles">Profile in ihrem Zustand <b>vor</b> der Übernahme.</param>
/// <param name="CreatedRoles">Rollen, die dieser Vorgang angelegt hat — der Rückweg entfernt sie.</param>
/// <param name="CreatedProfiles">Profile, die dieser Vorgang angelegt hat.</param>
/// <param name="CreatedUpstreams">
/// Angelegte Upstreams — nur die Kennung, nicht die Konfiguration: In der stehen Zugangsdaten, und
/// zum Entfernen braucht es sie nicht.
/// </param>
/// <param name="CreatedGuardRules">Angelegte Guard-Regeln.</param>
/// <param name="CreatedSkills">Angelegte Skills.</param>
/// <param name="SetApprovals">
/// Werkzeuge, für die dieser Vorgang eine Freigabepflicht gesetzt hat. Der Rückweg nimmt die
/// Markierung zurück — das ist deckungsgleich mit dem Zustand davor, <b>weil</b> der Plan bereits
/// gesetzte Freigaben nicht anfasst.
/// </param>
/// <param name="PreviousSettings">
/// Die Instanzeinstellungen von vorher, falls der Import welche geschrieben hat; sonst <c>null</c>.
/// </param>
/// <summary>
/// Der Rückweg <b>eines bestimmten Imports</b> — abgeleitet aus dem, was er wirklich getan hat.
/// <para>
/// Bewusst nicht „der Stand von vorher": Eine ganze Momentaufnahme wieder einzuspielen entfernt
/// nichts, was inzwischen dazugekommen ist, und fasst Objekte an, die mit diesem Vorgang nie zu tun
/// hatten. Dieser Rückweg berührt genau die Objekte, die der Import berührt hat — und nur die.
/// </para>
/// <para>
/// <b>Was er nicht kann:</b> Er ist kein Zeitpunkt. Wer nach dem Import dieselbe Rolle von Hand
/// ändert, dessen Änderung wird von der Rücknahme überschrieben — die Rücknahme schreibt den Stand
/// zurück, den <em>dieser Import</em> vorgefunden hat.
/// </para>
/// </summary>
public sealed record ConfigurationImportUndo(
    IReadOnlyList<Role> ReplacedRoles,
    IReadOnlyList<ToolProfile> ReplacedProfiles,
    IReadOnlyList<RoleId> CreatedRoles,
    IReadOnlyList<ProfileId> CreatedProfiles,
    IReadOnlyList<ServerId> CreatedUpstreams,
    IReadOnlyList<string> CreatedGuardRules,
    IReadOnlyList<AssetId> CreatedSkills,
    IReadOnlyList<NamespacedToolName> SetApprovals,
    InstanceSettings? PreviousSettings)
{
    public static ConfigurationImportUndo Nothing { get; } = new([], [], [], [], [], [], [], [], null);

    public bool IsEmpty =>
        ReplacedRoles.Count == 0 && ReplacedProfiles.Count == 0
        && CreatedRoles.Count == 0 && CreatedProfiles.Count == 0
        && CreatedUpstreams.Count == 0 && CreatedGuardRules.Count == 0
        && CreatedSkills.Count == 0 && SetApprovals.Count == 0
        && PreviousSettings is null;
}

public interface IConfigurationExportService
{
    Task<ConfigurationExport> ExportAsync(ConfigurationExportRequest request, CancellationToken ct);

    /// <summary>Plant additiv — die Vorgabe seit M2.</summary>
    Task<ConfigurationImportPlan> PlanImportAsync(string payload, string? passphrase, CancellationToken ct);

    /// <summary>Wie oben, aber mit ausdrücklichem Umgang mit Bestehendem (M6/WP6.1).</summary>
    Task<ConfigurationImportPlan> PlanImportAsync(
        string payload, string? passphrase, ConfigurationImportMode mode, CancellationToken ct);

    /// <summary>
    /// Wendet an und liefert den Rückweg <b>dieses</b> Vorgangs: bei
    /// <see cref="ConfigurationImportMode.AddOnly"/> die angelegten Objekte, bei
    /// <see cref="ConfigurationImportMode.Update"/> zusätzlich den vorigen Inhalt der ersetzten.
    /// </summary>
    Task<ConfigurationImportUndo> ApplyImportAsync(ConfigurationImportPlan plan, CancellationToken ct);

    /// <summary>
    /// Nimmt einen angewendeten Import zurück: setzt Ersetztes auf den vorigen Inhalt und entfernt
    /// Angelegtes. Scheitert etwas dabei, wird der Rest trotzdem zurückgenommen und der Rückstand
    /// benannt — ein verschluckter Fehler hinterlässt hier Rechte, von denen niemand weiß.
    /// </summary>
    Task UndoImportAsync(ConfigurationImportUndo undo, CancellationToken ct);
}

/// <summary>
/// Handles auf vorgemerkte Vorgänge — <see cref="RestorePlan.Token"/> und
/// <see cref="ConfigurationImportPlan.Token"/>.
/// </summary>
public static class PlanTokens
{
    /// <summary>
    /// Wie lange ein vorgemerkter Vorgang anwendbar bleibt. Ein Plan beschreibt einen Zustand, den
    /// er nur zum Zeitpunkt der Prüfung kannte; je länger er gilt, desto eher wendet ihn jemand auf
    /// eine Instanz an, die inzwischen eine andere ist.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Kryptografisch zufällig, nicht fortlaufend: Wer ein fremdes Handle raten kann, kann einen
    /// fremden Restore auslösen.
    /// </summary>
    public static string New()
        => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
}

/// <summary>Einheitliche Exit-Codes für alle Operations-Befehle der CLI (M2-Vertrag §4).</summary>
public static class OperationsExitCode
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int UsageError = 2;
    public const int DiagnosticWarning = 3;
    public const int DiagnosticFailure = 4;
    public const int ArchiveInvalid = 5;
    public const int TargetNotEmpty = 6;
}
