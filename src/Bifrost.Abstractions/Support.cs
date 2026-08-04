namespace Bifrost.Abstractions.Operations;

/// <summary>
/// Was in ein Support-Bundle darf. Die Liste ist <b>abschließend</b>, und zwei Kategorien fehlen
/// hier ausdrücklich: Auditinhalte und Credentialmaterial.
/// <para>
/// Das ist keine Vergesslichkeit, sondern die Entscheidung aus dem M5-Vertrag §2. Ein Schalter, der
/// den Auditexport in dasselbe Archiv legt, macht aus dem weitergabefähigen Bundle über kurz oder
/// lang das, was jemand aus Bequemlichkeit mitschickt — und FR-P075 verlangt genau die Trennung.
/// Wer Auditdaten braucht, nimmt den Auditexport.
/// </para>
/// </summary>
public enum SupportBundleCategory
{
    /// <summary>Produkt-, Protokoll- und Laufzeitversion.</summary>
    Version,

    /// <summary>Konfiguration ohne Secretwerte (derselbe Weg wie der Konfigurationsexport).</summary>
    Configuration,

    /// <summary>Der Diagnosebericht mit Codes, Status und sicheren Zusatzangaben.</summary>
    Diagnostics,

    /// <summary>Erreichbarkeit von Datenbank, Auditpfad und Upstreams — der Zustand, nicht die Historie.</summary>
    Health,

    /// <summary>Anwendungslog, soweit die Installation eines schreibt.</summary>
    Logs,

    /// <summary>Angewandter Migrationsstand der Datenbank.</summary>
    Migrations,

    /// <summary>Betriebssystem, .NET, Containerruntime, WASI-Host.</summary>
    Runtime,
}

/// <param name="Categories">
/// Leer heißt: der Standardsatz. Eine ausdrückliche Auswahl nimmt genau diese Kategorien.
/// </param>
/// <param name="LogTailLines">
/// Wie viele Zeilen vom Ende des Anwendungslogs. Ein ganzes Log ist selten das, was jemand
/// weitergeben will — und nie das, was jemand liest.
/// </param>
public sealed record SupportBundleRequest(
    IReadOnlySet<SupportBundleCategory>? Categories = null,
    int LogTailLines = 200,
    string? OutputPath = null);

public sealed record SupportBundleEntry(string Path, SupportBundleCategory Category, long Bytes);

/// <param name="Reason">
/// Warum die Kategorie fehlt. Eine übersprungene Kategorie wird <b>benannt</b>, nicht still
/// weggelassen — sonst liest sich ein unvollständiges Bundle wie ein vollständiges.
/// </param>
public sealed record SupportBundleSkip(SupportBundleCategory Category, string Reason);

public sealed record SupportBundlePreview(
    IReadOnlyList<SupportBundleEntry> Entries,
    IReadOnlyList<SupportBundleSkip> SkippedCategories,
    long EstimatedBytes);

public sealed record SupportBundleFile(string Path, string Category, long Bytes, string Sha256);

/// <param name="RedactedFields">
/// Wie oft der zentrale Durchgang maskiert hat — nicht <em>was</em>. Die Zahl sagt einem Empfänger,
/// dass der Durchgang lief; der maskierte Wert selbst gehört nicht in ein Manifest.
/// </param>
public sealed record SupportBundleRedaction(string Pass, int RedactedFields);

public sealed record SupportBundleManifest(
    int FormatVersion,
    string ProductVersion,
    DateTimeOffset CreatedAt,
    string InstanceId,
    IReadOnlyList<string> Categories,
    IReadOnlyList<SupportBundleFile> Files,
    SupportBundleRedaction Redaction,
    string ChecksumAlgorithm);

public sealed record SupportBundleResult(
    string ArchivePath,
    long Bytes,
    string Sha256,
    SupportBundleManifest Manifest,
    IReadOnlyList<SupportBundleSkip> SkippedCategories);

/// <summary>
/// Erzeugt ein weitergabefähiges Bundle für die Fehlersuche (WP5.6, FR-P075).
/// <para>
/// <b><see cref="PreviewAsync"/> vor <see cref="CreateAsync"/> ist Pflicht</b> und kein Komfort —
/// dieselbe Begründung wie <c>PlanAsync</c> vor <c>ApplyAsync</c> beim Restore: Ein Bundle, dessen
/// Inhalt man erst nach dem Schreiben sieht, ist bereits geschrieben. Und danach verlässt es das
/// Haus.
/// </para>
/// </summary>
public interface ISupportBundleService
{
    Task<SupportBundlePreview> PreviewAsync(SupportBundleRequest request, CancellationToken ct);

    Task<SupportBundleResult> CreateAsync(SupportBundleRequest request, CancellationToken ct);
}

/// <summary>
/// Die Angaben, die nur der Serverprozess kennt — Instanzkennung, Datenverzeichnis, Migrationsstand
/// und Laufzeitumgebung.
/// <para>
/// Der Dienst liegt im Kern und darf die Persistenz nicht kennen; diese Grenze ist der Grund für
/// den Port. Dieselbe Bauform wie bei den Konfigurationsports des Servers.
/// </para>
/// </summary>
public interface ISupportBundleEnvironment
{
    string InstanceId { get; }

    /// <summary>Wo die Installation ihre Dateien hält — Grundlage für die Logsuche.</summary>
    string DataDirectory { get; }

    Task<IReadOnlyList<string>> AppliedMigrationsAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<string, string>> RuntimeFactsAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<string, string>> HealthFactsAsync(CancellationToken ct);
}
