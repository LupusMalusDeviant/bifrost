using System.Globalization;
using System.Text.Json;

using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Importing;

namespace Bifrost.Core.Importing;

/// <summary>
/// Die Umsetzung von <see cref="IConfigurationImporter"/>: Formaterkennung, Aufruf des passenden
/// Parsers, zentrale Normalisierung, Risikobeurteilung und Kollisionsprüfung.
/// <para>
/// <b>Sie schreibt nichts.</b> Kein Store, kein Supervisor, keine Datenbank, kein Dateisystem, kein
/// Netz. Was hier herauskommt, ist ein <see cref="ImportPlan"/> — eine Aussage über eine Datei, die
/// sich prüfen und testen lässt, ohne dass sich am Gateway etwas ändert. Wer aus einem Plan
/// Wirklichkeit macht, geht über die bestehenden Stores und den Supervisor; dort sitzen Validierung,
/// Ausführungs-Policy und Audit (WP4.1-DoD, ADR-0025 E4).
/// </para>
/// <para>
/// <b>Die Ausführungs-Policy wird gefragt, nicht nachgebaut.</b> Der Import ist ein Erzeugungsweg,
/// und ADR-0025 E4 nennt ihn ausdrücklich: „Ein Paket bringt eine Konfiguration mit, die niemand
/// eingetippt hat." Was „nativ" heißt, steht in <c>NativeExecution</c>; ob es erlaubt ist, sagt
/// <see cref="IHostExecutionPolicy"/>. Hier steht keine zweite Meinung dazu.
/// </para>
/// </summary>
public sealed class ConfigurationImporter : IConfigurationImporter
{
    /// <summary>Das Format ließ sich nicht bestimmen.</summary>
    public const string UnknownProvider = "unbekannt";

    /// <summary>
    /// Mehrere Parser halten das Dokument für ihres, und keiner deutlich mehr als der andere.
    /// <b>Dann wird nicht der nächstbeste genommen</b> — ein geratenes Format verschiebt den Fehler
    /// nur in die Abbildung, wo er wie ein Datenfehler aussieht.
    /// </summary>
    public const string AmbiguousProvider = "mehrdeutig";

    /// <summary>
    /// Ab welchem Abstand zwischen dem besten und dem zweitbesten Treffer die Erkennung als
    /// eindeutig gilt. Darunter ist es ein Gleichstand, auch wenn die Zahlen verschieden sind.
    /// </summary>
    public const double AmbiguityMargin = 0.1;

    /// <summary>
    /// Unterhalb dieser Sicherheit wird das erkannte Format als <em>geraten</em> gemeldet. Der Plan
    /// entsteht trotzdem — die Alternative wäre, ein knapp erkanntes Dokument gar nicht anzusehen.
    /// </summary>
    public const double WeakRecognition = 0.6;

    private readonly IReadOnlyList<IImportProvider> _providers;

    private readonly IHostExecutionPolicy? _hostExecution;

    /// <param name="providers">
    /// Die bekannten Formate. Die Reihenfolge spielt keine Rolle: Entschieden wird über die
    /// Sicherheit, nicht über die Registrierung — sonst hinge das Ergebnis daran, wer zuerst
    /// verdrahtet wurde.
    /// </param>
    /// <param name="hostExecution">
    /// Die Ausführungs-Policy dieser Instanz (ADR-0025 E1/E4). <c>null</c> heißt nicht „egal",
    /// sondern <see cref="HostExecutionReason.Undetermined"/> — und damit nein.
    /// </param>
    public ConfigurationImporter(
        IEnumerable<IImportProvider> providers, IHostExecutionPolicy? hostExecution)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = [.. providers];
        _hostExecution = hostExecution;

        if (_providers.Count == 0)
        {
            throw new ArgumentException(
                "Ohne einen einzigen Importparser koennte dieser Importer jedes Dokument nur "
                + "ablehnen — und zwar mit der Begruendung 'unbekanntes Format', die dann falsch "
                + "waere.",
                nameof(providers));
        }

        var duplicate = _providers
            .GroupBy(provider => provider.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Der Providername '{duplicate.Key}' ist doppelt vergeben. Er steht im Plan als "
                + "Herkunft; zweimal derselbe Name macht die Herkunftsangabe wertlos.",
                nameof(providers));
        }
    }

    /// <summary>
    /// Der Importer mit den fest eingebauten Parsern: der generische (WP4.1) und die vier
    /// Clientparser (WP4.2).
    /// <para>
    /// <b>Die Reihenfolge sagt nichts.</b> Entschieden wird über die Sicherheit, die jeder Parser
    /// selbst meldet — die Clientparser melden sich nur, wenn im Dokument etwas steht, das
    /// <em>nur</em> ihr Client schreibt, und überstimmen den generischen dann deutlich. Findet sich
    /// nichts Client-Eigenes, bleibt es beim generischen Parser: Das ist der richtige Ausgang und
    /// kein Versäumnis, denn eine schlichte <c>.mcp.json</c> ist bei Claude und Cursor
    /// zeichengleich.
    /// </para>
    /// </summary>
    public static ConfigurationImporter CreateDefault(IHostExecutionPolicy? hostExecution)
        => new(
            [
                new GenericMcpImportProvider(),
                new ClaudeImportProvider(),
                new CursorImportProvider(),
                new VsCodeImportProvider(),
                new CodexImportProvider(),
            ],
            hostExecution);

    /// <summary>Die registrierten Formate, in Registrierungsreihenfolge.</summary>
    public IReadOnlyList<IImportProvider> Providers => _providers;

    /// <inheritdoc/>
    public ImportSource Detect(string document) => Detect(document, null);

    /// <inheritdoc/>
    public ImportPlan Plan(string document, string? originPath = null)
    {
        var text = document ?? string.Empty;

        if (!LooksLikeJson(text, out var parseError))
        {
            return new ImportPlan(
                new ImportSource(UnknownProvider, null, 0, originPath),
                [],
                [LooksLikeToml(text) ? Toml() : NotJson(parseError)]);
        }

        var source = Detect(text, originPath);

        if (string.Equals(source.Provider, UnknownProvider, StringComparison.Ordinal))
        {
            return new ImportPlan(
                source,
                [],
                [
                    new ImportFinding(
                        ImportReason.UnknownFormat,
                        ImportSeverity.Error,
                        "Kein bekannter Parser erkennt dieses Dokument. Bekannt sind: "
                        + string.Join(", ", _providers.Select(p => p.Name)) + ".",
                        null,
                        "Pruefen, ob es sich um eine MCP-Konfiguration handelt — geraten wird hier "
                        + "nicht."),
                ]);
        }

        if (string.Equals(source.Provider, AmbiguousProvider, StringComparison.Ordinal))
        {
            return new ImportPlan(source, [], [Ambiguity(text)]);
        }

        var provider = _providers.First(p =>
            string.Equals(p.Name, source.Provider, StringComparison.Ordinal));
        var raw = provider.Plan(text, originPath);

        var findings = new List<ImportFinding>(raw.Findings);
        if (source.Confidence < WeakRecognition)
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownFormat,
                ImportSeverity.Warning,
                $"Das Format wurde als '{source.Provider}' nur schwach erkannt (Sicherheit "
                + source.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                + "). Der Plan entsteht trotzdem — aber er ist eine Vermutung ueber die Herkunft, "
                + "und das steht hier, statt es zu verschweigen.",
                null,
                "Den Plan Eintrag fuer Eintrag gegen die Quelldatei pruefen."));
        }

        var candidates = Collisions(Complete(raw.Candidates));

        return new ImportPlan(
            new ImportSource(
                provider.Name,
                raw.Source?.SchemaVersion,
                source.Confidence,
                originPath),
            candidates,
            findings);
    }

    /// <summary>
    /// Normalisiert und beurteilt jeden Kandidaten des Parsers.
    /// <para>
    /// <b>Warum zentral und nicht im Parser:</b> Das ist die Stelle, an der die DoD dieses Pakets
    /// eingelöst wird. Ein Parser liefert, was in der Datei stand; was daraus ein Serverentwurf
    /// dieses Gateways wird, entscheidet sich hier — einmal, für alle Formate, samt Frage an die
    /// Ausführungs-Policy. Läge das in den Parsern, wäre jeder neue Parser eine neue Gelegenheit,
    /// die Frage zu vergessen.
    /// </para>
    /// <para>
    /// <b>Der Ort kommt vom Parser.</b> Bis WP4.3 stand hier fest <c>mcpServers/&lt;name&gt;</c> — und
    /// damit zeigte jeder zentrale Befund bei Claudes <c>projects</c>-Karte, bei VS Code
    /// (<c>servers/…</c>, <c>mcp/servers/…</c>) und bei Codex (<c>mcp_servers/…</c>) auf eine Stelle,
    /// die es in der Quelldatei nicht gibt. Ein Ort, der nicht stimmt, ist schlechter als keiner: Er
    /// schickt jemanden an die falsche Zeile. Der Ersatzpfad bleibt für Kandidaten aus fremden Quellen
    /// ohne Angabe — er ist eine Notlösung und heißt hier auch so.
    /// </para>
    /// </summary>
    private List<ImportCandidate> Complete(IReadOnlyList<ImportCandidate> raw)
    {
        var result = new List<ImportCandidate>(raw.Count);

        foreach (var candidate in raw)
        {
            var path = candidate.SourcePath is { Length: > 0 } declared
                ? declared
                : $"mcpServers/{candidate.SourceName}";
            var normalized = ImportNormalization.Normalize(candidate, path);
            var risk = ImportRiskScanner.Scan(path, normalized.Config, _hostExecution);

            result.Add(normalized with
            {
                Findings = [.. normalized.Findings, .. risk.Findings],
                Secrets = [.. candidate.Secrets, .. risk.Secrets],
                SourcePath = path,
            });
        }

        return result;
    }

    /// <summary>
    /// Zwei Quellnamen können auf denselben Slug fallen. Einen davon mit einer angehängten Ziffer zu
    /// retten wäre bequem und falsch: Der Slug ist die Namespacing-Basis der Werkzeugnamen (FR-03),
    /// und welcher der beiden Server künftig <c>github-2</c> heißt, ist keine Entscheidung, die aus
    /// der Reihenfolge in einer fremden Datei folgen darf.
    /// <para>
    /// <b>Der Befund steht an jedem beteiligten Kandidaten, nicht am Plan.</b> Eine Kollision
    /// zwischen zwei Einträgen ist keine Aussage über die achtundzwanzig anderen — sie nimmt die
    /// beiden aus dem Import und lässt den Rest stehen. Beide bleiben gesperrt, auch wenn nur einer
    /// ausgewählt würde: Welcher den Namen behält, entscheidet dieser Typ weiterhin nicht.
    /// </para>
    /// </summary>
    private static List<ImportCandidate> Collisions(List<ImportCandidate> candidates)
    {
        var colliding = candidates
            .GroupBy(candidate => candidate.Config.Slug, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(
                group => group.Key,
                group => $"Die Server {string.Join(", ", group.Select(c => $"'{c.SourceName}'"))} "
                    + $"tragen nach der Normalisierung denselben Slug '{group.Key}'. Welcher davon den "
                    + "Namen behaelt, wird hier nicht entschieden.",
                StringComparer.Ordinal);

        if (colliding.Count == 0)
        {
            return candidates;
        }

        return
        [
            .. candidates.Select(candidate =>
                colliding.TryGetValue(candidate.Config.Slug, out var summary)
                    ? candidate with
                    {
                        Findings =
                        [
                            .. candidate.Findings,
                            new ImportFinding(
                                ImportReason.NameCollision,
                                ImportSeverity.Error,
                                summary,
                                candidate.SourcePath,
                                "Einen der Namen in der Quelldatei aendern und erneut importieren.",
                                ImportFindingScope.Entry),
                        ],
                    }
                    : candidate),
        ];
    }

    private ImportSource Detect(string document, string? originPath)
    {
        var text = document ?? string.Empty;

        var ranked = _providers
            .Select(provider => (provider.Name, Confidence: Clamp(provider.Recognize(text))))
            .Where(entry => entry.Confidence > 0)
            .OrderByDescending(entry => entry.Confidence)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count == 0)
        {
            return new ImportSource(UnknownProvider, null, 0, originPath);
        }

        var best = ranked[0];
        if (ranked.Count > 1 && best.Confidence - ranked[1].Confidence < AmbiguityMargin)
        {
            return new ImportSource(AmbiguousProvider, null, best.Confidence, originPath);
        }

        return new ImportSource(best.Name, null, best.Confidence, originPath);
    }

    private ImportFinding Ambiguity(string document)
    {
        var tied = _providers
            .Select(provider => (provider.Name, Confidence: Clamp(provider.Recognize(document))))
            .Where(entry => entry.Confidence > 0)
            .OrderByDescending(entry => entry.Confidence)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Name
                + " ("
                + entry.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                + ")")
            .ToList();

        return new ImportFinding(
            ImportReason.UnknownFormat,
            ImportSeverity.Error,
            "Mehrere Parser halten dieses Dokument fuer ihres, und keiner deutlich mehr als die "
            + "anderen: " + string.Join(", ", tied) + ". Es wird keiner gewaehlt — ein geratenes "
            + "Format verschiebt den Fehler nur in die Abbildung, wo er wie ein Datenfehler aussieht.",
            null,
            "Das Quellformat ausdruecklich angeben oder die Datei auf die Eigenheiten eines Clients "
            + "reduzieren.");
    }

    /// <summary>
    /// Ein Parser darf sich verrechnen; der Plan darf davon nicht abhängen. Werte außerhalb von
    /// 0 bis 1 werden zurechtgestutzt statt geglaubt.
    /// </summary>
    private static double Clamp(double confidence)
        => double.IsNaN(confidence) ? 0 : Math.Clamp(confidence, 0, 1);

    private static ImportFinding NotJson(string problem)
        => new(
            ImportReason.NotJson,
            ImportSeverity.Error,
            $"Das Dokument ist kein gueltiges JSON: {problem}",
            null,
            "Die Datei in einem Editor mit JSON-Pruefung oeffnen und die genannte Stelle korrigieren.");

    /// <summary>
    /// Der Befund für ein Dokument, das erkennbar TOML ist.
    /// <para>
    /// <b>Das ist die einzige Zugeständnis an TOML, das dieser Weg macht — und zwar bewusst.</b> Die
    /// Alternative wäre gewesen, die Parser <em>vor</em> der JSON-Prüfung zu fragen, ob einer das
    /// Dokument beansprucht. Das hätte an der Sache nichts geändert: Ohne TOML-Leser könnte kein
    /// Parser eine <c>config.toml</c> beanspruchen, und einen TOML-Leser gibt es hier nicht (siehe
    /// docs/import-matrix.md, Abschnitt 8). Gekostet hätte es die Schärfe der Meldung „kaputtes
    /// JSON", denn die entstünde dann erst, wenn sich niemand zuständig fühlt. Also: TOML bleibt
    /// draußen — aber ein Codex-Betreiber erfährt <b>warum</b>, statt in seiner heilen Datei nach
    /// einem Syntaxfehler zu suchen.
    /// </para>
    /// <para>
    /// <b>Kein Wert aus der Quelle im Text.</b> Die Meldung nennt die Form, an der es erkannt wurde,
    /// nicht die Zeile, in der es stand — ein Befundtext ist eine Ausgabe wie jede andere.
    /// </para>
    /// </summary>
    private static ImportFinding Toml()
        => new(
            ImportReason.UnsupportedSourceFormat,
            ImportSeverity.Error,
            "Das Dokument ist kein JSON, sieht aber nach TOML aus (Abschnittsueberschriften in "
            + "eckigen Klammern, Zuweisungen mit '='). Dieser Importweg liest ausschliesslich JSON. "
            + "Die Datei ist also vermutlich in Ordnung — sie kommt hier nur nicht an.",
            null,
            "Die Konfiguration nach JSON umschreiben: Aus [mcp_servers.<name>] wird "
            + "{ \"mcp_servers\": { \"<name>\": { … } } }. Der Codex-Parser liest genau diesen Aufbau.");

    /// <summary>
    /// Sieht dieses Dokument nach TOML aus? <b>Geraten wird nur, nachdem JSON ausgeschlossen ist</b>,
    /// und nur nach der Form: eine Abschnittsüberschrift <c>[…]</c> oder eine Zuweisung
    /// <c>schluessel = wert</c> am Zeilenanfang. Das ist kein TOML-Leser und soll keiner sein — es
    /// entscheidet nur, welche der beiden Meldungen der Betreiber bekommt.
    /// </summary>
    private static bool LooksLikeToml(string document)
    {
        var sections = 0;
        var assignments = 0;

        foreach (var raw in document.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                sections++;
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && line[..separator].TrimEnd()
                    .All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '"'))
            {
                assignments++;
            }
        }

        return sections > 0 || assignments > 0;
    }

    /// <summary>
    /// Ist das überhaupt JSON? Die Frage wird <b>hier</b> beantwortet und nicht in jedem Parser:
    /// „kaputte Datei" ist eine Aussage über das Dokument, nicht über ein Format.
    /// </summary>
    private static bool LooksLikeJson(string document, out string problem)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            problem = "Das Dokument ist leer.";
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(
                document,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32,
                });
            problem = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            problem = exception.Message;
            return false;
        }
    }
}
