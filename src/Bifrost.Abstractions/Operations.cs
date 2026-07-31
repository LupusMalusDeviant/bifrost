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
public sealed record RestorePlan(
    bool CanApply,
    BackupManifest? Manifest,
    RestoreMode Mode,
    bool TargetIsEmpty,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string? PreBackupPath = null);

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

public sealed record ConfigurationImportPlan(
    bool CanApply,
    IReadOnlyList<string> Additions,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> MissingDependencies);

public interface IConfigurationExportService
{
    Task<ConfigurationExport> ExportAsync(ConfigurationExportRequest request, CancellationToken ct);

    Task<ConfigurationImportPlan> PlanImportAsync(string payload, string? passphrase, CancellationToken ct);

    Task ApplyImportAsync(ConfigurationImportPlan plan, CancellationToken ct);
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
