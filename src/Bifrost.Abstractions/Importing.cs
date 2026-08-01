namespace Bifrost.Abstractions.Importing;

/// <summary>
/// Verträge des providerneutralen Imports (M4, WP4.1). Eine fremde MCP-Konfiguration wird
/// <b>analysiert, bevor sie irgendwo landet</b> — der Kern dieses Meilensteins.
/// <para>
/// Diese Datei wird vom Lead gelegt. Die Einfrierung der ersten Welle ist aufgehoben; jede Änderung
/// wird an Ort und Stelle begründet, statt sie stillschweigend zu erweitern.
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

/// <summary>
/// Wen ein Befund betrifft: das ganze Dokument oder genau einen Eintrag darin.
/// <para>
/// <b>Warum das im Vertrag steht und nicht am Pfad abgelesen wird.</b> Ohne diese Angabe müsste ein
/// planweiter Befund an seinem fehlenden Pfad erkannt werden — und ein neuer Befund, der versehentlich
/// einen Pfad trägt, wäre stillschweigend zu einem Einzelbefund verharmlost. Die Vorgabe ist deshalb
/// <see cref="Document"/>: Ein Befund gilt für alles, solange nicht ausdrücklich dabeisteht, dass er
/// nur eine Stelle betrifft. Fail-closed in die Richtung, in der ein Irrtum nur zu viel blockiert.
/// </para>
/// </summary>
public enum ImportFindingScope
{
    /// <summary>
    /// Betrifft das ganze Dokument — kaputtes JSON, unbekanntes Format, mehrdeutige Erkennung. Ein
    /// solcher Befund der Stufe <see cref="ImportSeverity.Error"/> hält den <b>ganzen</b> Plan an.
    /// </summary>
    Document,

    /// <summary>
    /// Betrifft genau den Eintrag unter <see cref="ImportFinding.Path"/>. Ein solcher Befund der
    /// Stufe <see cref="ImportSeverity.Error"/> nimmt <b>diesen einen</b> Kandidaten aus dem Import;
    /// die übrigen bleiben anwendbar.
    /// </summary>
    Entry,
}

/// <param name="Code">
/// Stabil und maschinenlesbar (<c>BFR-IMP-0007</c>). Stabil heißt: Der Code überlebt
/// Umformulierungen des Textes.
/// </param>
/// <param name="Path">
/// Wo im Quelldokument — etwa <c>mcpServers/github/args[2]</c>. Ohne Ort ist ein Befund über eine
/// Datei mit dreißig Servern eine Suchaufgabe. <b>Der Ort muss in der Quelle wirklich existieren:</b>
/// Ein Pfad, der auf eine Stelle zeigt, die es in der Datei nicht gibt, ist schlechter als keiner —
/// er schickt jemanden an die falsche Zeile.
/// </param>
/// <param name="Remediation">Die nächste Handlung, wenn es eine gibt.</param>
/// <param name="Scope">
/// Ob der Befund das ganze Dokument oder nur den Eintrag unter <see cref="Path"/> betrifft. Vorgabe
/// ist <see cref="ImportFindingScope.Document"/> — siehe die Begründung dort.
/// </param>
public sealed record ImportFinding(
    string Code,
    ImportSeverity Severity,
    string Summary,
    string? Path = null,
    string? Remediation = null,
    ImportFindingScope Scope = ImportFindingScope.Document);

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
/// <param name="SourcePath">
/// Wo dieser Eintrag in der Quelldatei wirklich steht — <c>mcpServers/github</c> bei einer schlichten
/// <c>.mcp.json</c>, aber <c>projects/&lt;projekt&gt;/mcpServers/github</c> in Claudes
/// <c>projects</c>-Karte, <c>servers/github</c> und <c>mcp/servers/github</c> bei VS Code,
/// <c>mcp_servers/github</c> bei Codex.
/// <para>
/// <b>Warum der Parser das mitliefern muss:</b> Die zentralen Befunde (Normalisierung, Risiko) sind
/// die Mehrheit aller Befunde, und sie entstehen erst <em>nach</em> dem Parser. Ohne diese Angabe
/// müsste die Mitte den Ort raten — und ein geratener Ort zeigt bei drei der fünf Formate auf eine
/// Stelle, die es in der Quelldatei nicht gibt.
/// </para>
/// <para>
/// <c>null</c> heißt „unbekannt", nicht „mcpServers": Die Mitte setzt dann einen Ersatzpfad und der
/// bleibt eine Notlösung, keine Zusicherung.
/// </para>
/// </param>
public sealed record ImportCandidate(
    string SourceName,
    UpstreamServerConfig Config,
    IReadOnlyList<ImportFinding> Findings,
    IReadOnlyList<ImportSecret> Secrets,
    string? SourcePath = null)
{
    /// <summary>
    /// Ob dieser Eintrag <b>für sich</b> stimmig ist. Der Kern des Teilimports: Ein Kandidat ohne
    /// eigenen Fehler bleibt anwendbar, auch wenn ein anderer Eintrag derselben Datei einen trägt.
    /// Eine Datei mit dreißig Servern an einem kaputten Eintrag scheitern zu lassen, ist genau die
    /// Einschränkung, die einen geführten Erstaufbau unbrauchbar macht.
    /// <para>
    /// <b>Diese Angabe allein entscheidet nichts</b> — sie kennt nur die eigenen Befunde. Ein
    /// planweiter Fehler und ein Befund, der über seinen Pfad diesen Eintrag meint, stehen beide
    /// woanders. Maßgeblich ist <see cref="ImportPlan.IsApplicable"/>.
    /// </para>
    /// </summary>
    public bool CanApply => !Findings.Any(f => f.Severity is ImportSeverity.Error);
}

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
    /// Die Fehler, die den <b>ganzen</b> Plan anhalten: kaputtes JSON, unbekanntes oder mehrdeutiges
    /// Format. Ein Befund gehört nur dann hierher, wenn er ausdrücklich
    /// <see cref="ImportFindingScope.Document"/> trägt — und das ist die Vorgabe.
    /// </summary>
    public IReadOnlyList<ImportFinding> BlockingFindings =>
        [.. Findings.Where(f =>
            f.Severity is ImportSeverity.Error && f.Scope is ImportFindingScope.Document)];

    /// <summary>
    /// Ob dieser Eintrag angelegt werden kann. <b>Die maßgebliche Auskunft</b> — sie fasst drei
    /// Dinge zusammen, die einzeln unvollständig wären:
    /// <list type="number">
    /// <item>kein planweiter Fehler, denn der betrifft auch den unauffälligen Eintrag;</item>
    /// <item>kein eigener Fehler des Kandidaten (<see cref="ImportCandidate.CanApply"/>);</item>
    /// <item>kein Fehler auf Planebene, der über seinen Pfad genau <em>diesen</em> Eintrag meint.</item>
    /// </list>
    /// <para>
    /// Der dritte Punkt ist der unauffällige: Ein Parser sammelt seine Befunde, während er läuft, und
    /// weiß bei einem doppelten Servernamen noch gar nicht, welchen Kandidaten das trifft — der ist
    /// da schon abgeliefert. Die Zuordnung über den Pfad holt das hier nach, statt die Parser um eine
    /// Buchführung zu erweitern, die vier Mal richtig sein müsste.
    /// </para>
    /// </summary>
    public bool IsApplicable(ImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return BlockingFindings.Count == 0
            && candidate.CanApply
            && !Findings.Any(f =>
                f.Severity is ImportSeverity.Error
                && f.Scope is ImportFindingScope.Entry
                && Concerns(candidate, f));
    }

    /// <summary>Die Kandidaten, die tatsächlich angelegt werden können.</summary>
    public IReadOnlyList<ImportCandidate> ApplicableCandidates =>
        [.. Candidates.Where(IsApplicable)];

    /// <summary>
    /// Die Kandidaten, die aus dem Import herausfallen. Ein Betreiber soll sehen, was nicht geht —
    /// eine Vorschau, die einen Eintrag einfach weglässt, ist eine Vorschau, die lügt.
    /// </summary>
    public IReadOnlyList<ImportCandidate> BlockedCandidates =>
        [.. Candidates.Where(candidate => !IsApplicable(candidate))];

    /// <summary>
    /// Die Fehler, die genau diesen Eintrag aus dem Import nehmen — seine eigenen und die vom Plan,
    /// die ihn meinen. Für die Frage „warum geht der nicht?", die ohne Antwort eine Suchaufgabe ist.
    /// </summary>
    public IReadOnlyList<ImportFinding> BlockersFor(ImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return
        [
            .. candidate.Findings.Where(f => f.Severity is ImportSeverity.Error),
            .. Findings.Where(f =>
                f.Severity is ImportSeverity.Error
                && f.Scope is ImportFindingScope.Entry
                && Concerns(candidate, f)),
        ];
    }

    /// <summary>
    /// Meint dieser Befund diesen Eintrag? Verglichen wird der Ort: <c>mcpServers/github</c> und
    /// alles darunter (<c>mcpServers/github/url</c>) gehört zu <c>github</c>,
    /// <c>mcpServers/github-2</c> nicht.
    /// </summary>
    private static bool Concerns(ImportCandidate candidate, ImportFinding finding)
        => candidate.SourcePath is { Length: > 0 } source
            && finding.Path is { Length: > 0 } path
            && (string.Equals(path, source, StringComparison.Ordinal)
                || path.StartsWith(source + "/", StringComparison.Ordinal));

    /// <summary>
    /// Anwendbar, sobald <b>mindestens ein</b> Kandidat anwendbar ist und kein planweiter Fehler
    /// dazwischensteht.
    /// <para>
    /// <b>Die Bedeutung hat sich mit dem Teilimport verschoben</b> und das ist Absicht: Früher hieß
    /// <c>true</c> „alles geht", jetzt heißt es „etwas geht". Was genau, sagt
    /// <see cref="ApplicableCandidates"/>; was nicht, sagt <see cref="BlockedCandidates"/>. Ein
    /// Aufrufer, der weiterhin „alles oder nichts" braucht, vergleicht die beiden Anzahlen — er
    /// bekommt die Auskunft also weiterhin, muss sie aber aussprechen.
    /// </para>
    /// <see cref="ImportSeverity.Risk"/> blockiert <b>nicht</b> — es verlangt eine Bestätigung, und
    /// die trifft der Betreiber, nicht dieser Typ.
    /// </summary>
    public bool CanApply => ApplicableCandidates.Count > 0;

    /// <summary>Die Befunde, die eine ausdrückliche Bestätigung verlangen.</summary>
    public IReadOnlyList<ImportFinding> RequiresConfirmation =>
        Findings.Concat(Candidates.SelectMany(c => c.Findings))
            .Where(f => f.Severity is ImportSeverity.Risk)
            .ToArray();

    /// <summary>
    /// Die Bestätigungen, die <b>für diese Auswahl</b> nötig sind: die planweiten Risikobefunde und
    /// die der ausgewählten Kandidaten.
    /// <para>
    /// <b>Warum nicht einfach <see cref="RequiresConfirmation"/>:</b> Wer drei von dreißig Servern
    /// übernimmt, soll die Risiken dieser drei bestätigen — nicht die der siebenundzwanzig, die er
    /// gerade nicht anlegt. Eine Bestätigung, die pauschal für alles gilt, wird zur Formalie, und
    /// eine Formalie liest niemand.
    /// </para>
    /// <para>
    /// Einträge mit <see cref="ImportFindingScope.Entry"/> auf Planebene bleiben draußen: Sie
    /// betreffen Stellen, aus denen gar kein Kandidat wurde, und was nicht angelegt wird, muss auch
    /// niemand bestätigen.
    /// </para>
    /// </summary>
    public IReadOnlyList<ImportFinding> ConfirmationsFor(IEnumerable<ImportCandidate> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return
        [
            .. Findings.Where(f =>
                f.Severity is ImportSeverity.Risk && f.Scope is ImportFindingScope.Document),
            .. selection.SelectMany(c => c.Findings)
                .Where(f => f.Severity is ImportSeverity.Risk),
        ];
    }
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

    /// <summary>
    /// Das Dokument ist erkennbar eine Konfiguration in einem Format, das dieser Weg nicht liest —
    /// heute genau ein Fall: TOML. Eigener Code neben <see cref="NotJson"/>, weil es ein anderer
    /// Sachverhalt und eine andere Handlung ist: „Die Datei ist kaputt" verlangt eine Korrektur,
    /// „die Datei ist TOML" verlangt eine Umschrift. Beides unter <c>BFR-IMP-0001</c> zu führen
    /// hieße, den Codex-Betreiber in seiner Datei nach einem Syntaxfehler suchen zu lassen, den es
    /// nicht gibt.
    /// </summary>
    public const string UnsupportedSourceFormat = "BFR-IMP-0006";

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
