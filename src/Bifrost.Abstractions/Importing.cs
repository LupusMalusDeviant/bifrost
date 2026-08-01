namespace Bifrost.Abstractions.Importing;

/// <summary>
/// Verträge des providerneutralen Imports (M4, WP4.1). Eine fremde MCP-Konfiguration wird
/// <b>analysiert, bevor sie irgendwo landet</b> — der Kern dieses Meilensteins.
/// <para>
/// Diese Datei wird vom Lead gelegt und ist für die Dauer der Welle eingefroren. Wer einen Vertrag
/// ändern will, meldet das, statt ihn zu erweitern.
/// </para>
/// </summary>
public enum ImportSeverity
{
    /// <summary>Nur zur Kenntnis. Der Import geht so durch.</summary>
    Info,

    /// <summary>
    /// Geht durch, aber jemand sollte es gesehen haben — clientexklusive Felder, verlustbehaftete
    /// Abbildungen, geratene Vorgaben.
    /// </summary>
    Warning,

    /// <summary>
    /// Verlangt eine ausdrückliche Bestätigung, bevor angelegt wird. Kein Fehler: Ein
    /// <c>npx -y</c>-Server ist eine legitime Konfiguration und zugleich eine, die beliebigen Code
    /// aus dem Netz nachlädt. Der Unterschied gehört sichtbar gemacht, nicht wegentschieden.
    /// </summary>
    Risk,

    /// <summary>Nicht importierbar. Der Plan bleibt hier stehen.</summary>
    Error,
}

/// <param name="Code">
/// Stabil und maschinenlesbar (<c>BFR-IMP-0007</c>). Stabil heißt: Der Code überlebt
/// Umformulierungen des Textes.
/// </param>
/// <param name="Path">
/// Wo im Quelldokument — etwa <c>mcpServers/github/args[2]</c>. Ohne Ort ist ein Befund über eine
/// Datei mit dreißig Servern eine Suchaufgabe.
/// </param>
/// <param name="Remediation">Die nächste Handlung, wenn es eine gibt.</param>
public sealed record ImportFinding(
    string Code,
    ImportSeverity Severity,
    string Summary,
    string? Path = null,
    string? Remediation = null);

/// <summary>
/// Eine Stelle, an der die Quelle ein Geheimnis trägt oder zu tragen scheint.
/// </summary>
/// <param name="Location">
/// Wo es hingehört — Header-Name, Umgebungsvariable, Feld. <b>Nicht der Wert.</b>
/// </param>
/// <param name="Looked">
/// Woran es erkannt wurde (Feldname, Muster, Maske). Für die Rückfrage an den Betreiber: „Ich halte
/// das für ein Zugangsdatum, weil …" ist beantwortbar, „das ist ein Zugangsdatum" nicht.
/// </param>
/// <param name="ValuePresent">
/// Ob die Quelle einen Wert mitbringt. <c>false</c> heißt: Die Quelle war bereits maskiert.
/// <b>Aus einer Maske wird nichts rekonstruiert</b> — ein erratener Wert, der fast stimmt, ist
/// schlimmer als ein fehlender.
/// </param>
public sealed record ImportSecret(
    string Location,
    string Looked,
    bool ValuePresent);

/// <summary>Ein einzelner Server aus der Quelle, normalisiert.</summary>
/// <param name="Config">
/// Die abgebildete Konfiguration. <b>Nicht persistiert und nicht aktiv</b> — dass dieser Typ hier
/// auftaucht, macht ihn nicht zu einem angelegten Upstream. Die Aktivierung läuft über die
/// bestehenden Stores und den Supervisor (WP4.1-DoD: kein Providerparser erzeugt direkt eine aktive
/// Upstreamkonfiguration).
/// </param>
public sealed record ImportCandidate(
    string SourceName,
    UpstreamServerConfig Config,
    IReadOnlyList<ImportFinding> Findings,
    IReadOnlyList<ImportSecret> Secrets);

/// <summary>Woher die Konfiguration stammt.</summary>
/// <param name="Provider">
/// Erkanntes Format (<c>claude</c>, <c>cursor</c>, <c>vscode</c>, <c>codex</c>, <c>mcp</c>).
/// </param>
/// <param name="Confidence">
/// Wie sicher die Erkennung ist. Ein geratenes Format wird als geraten gemeldet — ein Parser, der
/// bei Unklarheit einfach den nächstbesten nimmt, verschiebt den Fehler in die Abbildung.
/// </param>
public sealed record ImportSource(
    string Provider,
    string? SchemaVersion,
    double Confidence,
    string? OriginPath = null);

/// <summary>
/// Das Ergebnis der Analyse. <b>Ein Plan ist keine Änderung</b>: Er lässt sich validieren und
/// testen, ohne dass irgendetwas gespeichert wird.
/// </summary>
public sealed record ImportPlan(
    ImportSource Source,
    IReadOnlyList<ImportCandidate> Candidates,
    IReadOnlyList<ImportFinding> Findings,
    string? Token = null)
{
    /// <summary>
    /// Anwendbar, solange kein Befund den Import verhindert. <see cref="ImportSeverity.Risk"/>
    /// blockiert <b>nicht</b> — es verlangt eine Bestätigung, und die trifft der Betreiber, nicht
    /// dieser Typ.
    /// </summary>
    public bool CanApply => !Findings.Concat(Candidates.SelectMany(c => c.Findings))
        .Any(f => f.Severity is ImportSeverity.Error);

    /// <summary>Die Befunde, die eine ausdrückliche Bestätigung verlangen.</summary>
    public IReadOnlyList<ImportFinding> RequiresConfirmation =>
        Findings.Concat(Candidates.SelectMany(c => c.Findings))
            .Where(f => f.Severity is ImportSeverity.Risk)
            .ToArray();
}

/// <summary>
/// Analysiert eine fremde Konfiguration. <b>Schreibt nie.</b> Wer aus einem Plan Wirklichkeit
/// macht, geht über die bestehenden Stores — es gibt keinen zweiten Weg an der Validierung und der
/// Ausführungs-Policy (ADR-0025 E4) vorbei.
/// </summary>
public interface IConfigurationImporter
{
    /// <summary>Erkennt das Format, ohne es zu verarbeiten.</summary>
    ImportSource Detect(string document);

    /// <summary>Analysiert und normalisiert. Kein Schreibzugriff, kein Netzzugriff.</summary>
    ImportPlan Plan(string document, string? originPath = null);
}

/// <summary>
/// Ein Parser für genau ein Quellformat. Getrennte Dateien je Provider (WP4.2) — ein Parser, der
/// zwei Formate kennt, kennt bald beide halb.
/// </summary>
public interface IImportProvider
{
    /// <summary>Der Name, unter dem dieses Format in <see cref="ImportSource.Provider"/> steht.</summary>
    string Name { get; }

    /// <summary>
    /// Wie gut dieses Dokument zu diesem Format passt (0 bis 1). <c>0</c> heißt „nicht meins".
    /// </summary>
    double Recognize(string document);

    ImportPlan Plan(string document, string? originPath);
}

/// <summary>
/// Codebereich des Imports. Reserviert ist <c>BFR-IMP-0001…0999</c>; die Diagnosecodes aus M2 und
/// die Policy-Codes aus M3 bleiben unberührt.
/// <para>
/// Sie stehen gesammelt, damit ein Code nie zweimal vergeben wird — dieselbe Regel wie bei den
/// Diagnosecodes, und sie hat dort bereits eine Kollision zwischen parallel arbeitenden Paketen
/// verhindert.
/// </para>
/// </summary>
public static class ImportReason
{
    // ── Struktur ──────────────────────────────────────────────────────────────────────────────
    public const string NotJson = "BFR-IMP-0001";
    public const string UnknownFormat = "BFR-IMP-0002";
    public const string UnknownField = "BFR-IMP-0003";
    public const string DuplicateServer = "BFR-IMP-0004";
    public const string NameCollision = "BFR-IMP-0005";

    // ── Risiko (verlangt Bestätigung, blockiert nicht) ────────────────────────────────────────

    /// <summary>Das Programm läuft nativ auf dem Host — siehe ADR-0025.</summary>
    public const string HostExecution = "BFR-IMP-0100";

    /// <summary>Kein absoluter Pfad: Was startet, entscheidet die PATH-Variable des Dienstes.</summary>
    public const string PathLookup = "BFR-IMP-0101";

    public const string RelativePath = "BFR-IMP-0102";

    /// <summary><c>npx -y</c>, <c>uvx</c> und Verwandte: lädt beim Start beliebigen Code nach.</summary>
    public const string FetchesCodeAtStart = "BFR-IMP-0103";

    /// <summary>Image ohne Digest — derselbe Name kann morgen etwas anderes sein.</summary>
    public const string UnpinnedImage = "BFR-IMP-0104";

    /// <summary>Ziel im privaten, Loopback- oder Link-Local-Netz (SSRF).</summary>
    public const string PrivateTarget = "BFR-IMP-0105";

    /// <summary>Zugangsdatum im Klartext in der Quelle.</summary>
    public const string PlaintextSecret = "BFR-IMP-0106";

    // ── Abbildung ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Ein Feld, das nur der Quellclient kennt. Erhalten als Befund, nicht still verworfen.</summary>
    public const string ClientOnlyField = "BFR-IMP-0200";

    /// <summary>Die Abbildung verliert etwas. Wird benannt, nicht geglättet.</summary>
    public const string Lossy = "BFR-IMP-0201";

    /// <summary>Ein Wert war bereits maskiert. Er wird NICHT rekonstruiert.</summary>
    public const string MaskedValue = "BFR-IMP-0202";
}
