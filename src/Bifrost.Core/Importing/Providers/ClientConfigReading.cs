using System.Globalization;
using System.Text.Json;

using Bifrost.Abstractions.Importing;

namespace Bifrost.Core.Importing;

/// <summary>
/// Die gemeinsamen Lesehilfen der vier Clientparser (WP4.2).
/// <para>
/// <b>Warum es diese Datei gibt, obwohl je Provider eine eigene Datei gilt:</b> „Ein Parser, der zwei
/// Formate kennt, kennt bald beide halb" ist eine Aussage über die <em>Formatkenntnis</em> — über die
/// Feldnamen, die Transportwörter und die clientexklusiven Eigenheiten. Die liegen je Client in
/// seiner eigenen Datei und werden hier nicht angefasst. Was hier steht, ist die Mechanik darunter:
/// Wie liest man eine Liste von Argumenten, wie ein Objekt aus Zeichenketten, wie beschreibt man
/// einen JSON-Typ auf Deutsch. Diese vier Mal zu kopieren hieße, vier Gelegenheiten zu schaffen, die
/// Meldung bei einem Zahl-statt-Text-Argument unterschiedlich zu formulieren.
/// </para>
/// <para>
/// <b>Der Namensraum ist mit Absicht <c>Bifrost.Core.Importing</c> und nicht <c>…Importing.Providers</c>:</b>
/// Die Architekturprüfung der Importzone (<c>ImportingWriteFreedomTests</c>) holt ihre Typen über
/// genau diesen Namensraum. Eine Datei in einem Unter-Namensraum läge außerhalb der Typ- und
/// IL-Prüfung und wäre nur noch von der Textsuche abgedeckt.
/// </para>
/// <para>
/// <b>Hier wird nichts gelesen, was nicht im Dokument steht.</b> Kein Dateisystem, kein Netz, keine
/// Auflösung von Umgebungsvariablen — ein Quellpfad ist eine Angabe über die Herkunft und kein
/// Leseauftrag.
/// </para>
/// </summary>
internal static class ClientConfigReading
{
    /// <summary>Wie tief ein Importdokument geschachtelt sein darf, bevor es abgewiesen wird.</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Fremde Konfigurationsdateien tragen Kommentare (VS Code schreibt jsonc, Cursor duldet es) und
    /// nachlaufende Kommata. Sie abzulehnen hieße, an einer Formalie zu scheitern, die kein Client
    /// durchsetzt.
    /// </summary>
    public static JsonDocumentOptions ParseOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = MaxDepth,
    };

    /// <summary>Trägt <paramref name="element"/> ein Feld dieses Namens, das ein Objekt ist?</summary>
    public static bool IsObject(JsonElement element, string field)
        => element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind is JsonValueKind.Object;

    /// <summary>Trägt <paramref name="element"/> ein Feld dieses Namens, das eine Liste ist?</summary>
    public static bool IsArray(JsonElement element, string field)
        => element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind is JsonValueKind.Array;

    /// <summary>Trägt <paramref name="element"/> überhaupt ein Feld dieses Namens?</summary>
    public static bool Has(JsonElement element, string field)
        => element.ValueKind is JsonValueKind.Object && element.TryGetProperty(field, out _);

    /// <summary>Eine nicht leere Zeichenkette — oder <c>null</c>.</summary>
    public static string? Text(JsonElement element, string field)
        => element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
            && value.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

    /// <summary>
    /// Die Argumentliste. Zahlen und Wahrheitswerte werden zu Text — eindeutig, aber eine
    /// Umwandlung, und die wird benannt.
    /// </summary>
    public static List<string> Arguments(JsonElement server, string path, List<ImportFinding> findings)
    {
        if (!server.TryGetProperty("args", out var args))
        {
            return [];
        }

        if (args.ValueKind is not JsonValueKind.Array)
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Warning,
                $"'args' ist {Describe(args.ValueKind)} statt einer Liste und wird nicht uebernommen.",
                $"{path}/args"));
            return [];
        }

        var result = new List<string>();
        var index = 0;
        foreach (var argument in args.EnumerateArray())
        {
            var position = (index++).ToString(CultureInfo.InvariantCulture);
            switch (argument.ValueKind)
            {
                case JsonValueKind.String:
                    result.Add(argument.GetString() ?? string.Empty);
                    break;

                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    result.Add(argument.GetRawText());
                    findings.Add(new ImportFinding(
                        ImportReason.Lossy,
                        ImportSeverity.Info,
                        $"Das Argument an Position {position} steht in der Quelle nicht als Text und "
                        + "wird als Text uebergeben.",
                        $"{path}/args[{position}]"));
                    break;

                default:
                    findings.Add(new ImportFinding(
                        ImportReason.Lossy,
                        ImportSeverity.Warning,
                        $"Das Argument an Position {position} ist {Describe(argument.ValueKind)} und "
                        + "wird nicht uebernommen. Die Reihenfolge der uebrigen Argumente verschiebt "
                        + "sich dadurch.",
                        $"{path}/args[{position}]",
                        "Den Aufruf nach dem Import gegen die Quelldatei pruefen."));
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Ein Objekt aus Zeichenketten — <c>env</c>, <c>headers</c> und <c>http_headers</c> haben
    /// dieselbe Form. <c>null</c> als Wert ist bei VS Code ausdrücklich zulässig und heißt „Variable
    /// aus der Umgebung des Clients übernehmen"; hier gibt es diese Umgebung nicht, also wird es
    /// gemeldet statt geraten.
    /// </summary>
    public static Dictionary<string, string> Map(
        JsonElement server, string field, string path, List<ImportFinding> findings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!server.TryGetProperty(field, out var map))
        {
            return result;
        }

        if (map.ValueKind is not JsonValueKind.Object)
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Warning,
                $"'{field}' ist {Describe(map.ValueKind)} statt eines Objekts und wird nicht "
                + "uebernommen.",
                path));
            return result;
        }

        foreach (var entry in map.EnumerateObject())
        {
            switch (entry.Value.ValueKind)
            {
                case JsonValueKind.String:
                    result[entry.Name] = entry.Value.GetString() ?? string.Empty;
                    break;

                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    result[entry.Name] = entry.Value.GetRawText();
                    findings.Add(new ImportFinding(
                        ImportReason.Lossy,
                        ImportSeverity.Info,
                        $"'{entry.Name}' steht in der Quelle nicht als Text und wird als Text "
                        + "uebernommen.",
                        $"{path}/{entry.Name}"));
                    break;

                case JsonValueKind.Null:
                    findings.Add(new ImportFinding(
                        ImportReason.Lossy,
                        ImportSeverity.Warning,
                        $"'{entry.Name}' steht in der Quelle als null. Der Quellclient setzt dafuer "
                        + "seinen eigenen Wert ein; dieses Gateway hat diese Umgebung nicht und "
                        + "uebernimmt den Eintrag nicht.",
                        $"{path}/{entry.Name}",
                        "Den Wert auf der Zielinstanz ausdruecklich eintragen."));
                    break;

                default:
                    findings.Add(new ImportFinding(
                        ImportReason.Lossy,
                        ImportSeverity.Warning,
                        $"'{entry.Name}' ist {Describe(entry.Value.ValueKind)} und wird nicht "
                        + "uebernommen.",
                        $"{path}/{entry.Name}"));
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Die Zieladresse eines HTTP-Servers. Liefert <c>null</c> samt Befund, wenn daraus keine
    /// absolute http- oder https-Adresse wird.
    /// </summary>
    public static Uri? Endpoint(string url, string path, List<ImportFinding> findings)
    {
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https")
        {
            return endpoint;
        }

        // Die Adresse steht NICHT im Text — dieselbe Regel wie im generischen Parser, von dem
        // diese Zeile stammt. Eine ungueltige URL traegt oft ein '?token=...', und ein Befundtext
        // ist eine Ausgabe wie jede andere. Der Pfad sagt, wo der Wert steht.
        findings.Add(new ImportFinding(
            ImportReason.UnknownField,
            ImportSeverity.Error,
            "Die Adresse ist keine absolute http- oder https-Adresse.",
            $"{path}/url",
            "Die vollstaendige Adresse einschliesslich Schema eintragen."));
        return null;
    }

    /// <summary>Der Befund für ein Feld, das nur der Quellclient kennt.</summary>
    public static ImportFinding ClientOnly(string path, string summary, string? remediation = null)
        => new(
            ImportReason.ClientOnlyField,
            ImportSeverity.Warning,
            summary,
            path,
            remediation ?? "Pruefen, ob es hier eine Entsprechung gibt — und sie von Hand setzen.");

    /// <summary>Der Befund für ein Feld, das dieser Parser nicht kennt.</summary>
    public static ImportFinding Unknown(string path, string field)
        => new(
            ImportReason.UnknownField,
            ImportSeverity.Warning,
            $"'{field}' ist diesem Parser unbekannt und wird nicht uebernommen.",
            path);

    /// <summary>Der Plan, der aus einem kaputten Dokument entsteht: einer, der nichts behauptet.</summary>
    public static ImportPlan Broken(string provider, string? originPath, JsonException exception)
        => new(
            new ImportSource(provider, null, 0, originPath),
            [],
            [
                new ImportFinding(
                    ImportReason.NotJson,
                    ImportSeverity.Error,
                    $"Das Dokument ist kein gueltiges JSON: {exception.Message}",
                    string.IsNullOrEmpty(exception.Path) ? null : exception.Path,
                    "Die Datei in einem Editor mit JSON-Pruefung oeffnen und die genannte Stelle "
                    + "korrigieren."),
            ]);

    /// <summary>Der Befund für ein Dokument, dessen oberste Ebene nicht passt.</summary>
    public static ImportFinding WrongShape(string summary)
        => new(
            ImportReason.UnknownFormat,
            ImportSeverity.Error,
            summary,
            null,
            "Pruefen, ob die Datei wirklich eine MCP-Konfiguration dieses Clients ist — geraten wird "
            + "hier nicht.");

    /// <summary>
    /// Der Befund für einen Server, aus dem sich kein Transport ergibt.
    /// </summary>
    public static ImportFinding NoTransport(string path, string commandField, string urlField)
        => new(
            ImportReason.UnknownField,
            ImportSeverity.Error,
            $"Der Eintrag traegt weder '{commandField}' noch '{urlField}' — es ist nicht erkennbar, "
            + "was gestartet oder aufgerufen werden soll.",
            path,
            "Den Eintrag in der Quelldatei vervollstaendigen.");

    /// <summary>
    /// Der Befund für einen Eintrag, der lokal <b>und</b> entfernt zugleich sein will. Aufgelöst
    /// wird das nicht: Die Quelle sagt damit nicht, was gemeint ist.
    /// </summary>
    public static ImportFinding BothTransports(string path, string commandField, string urlField)
        => new(
            ImportReason.UnknownField,
            ImportSeverity.Error,
            $"Der Eintrag traegt '{commandField}' und '{urlField}' zugleich. Ob ein lokales Programm "
            + "oder ein entfernter Dienst gemeint ist, sagt die Quelle damit nicht.",
            path,
            "Das ueberfluessige Feld in der Quelldatei entfernen.");

    /// <summary>Ein JSON-Typ auf Deutsch — für Meldungen, die ein Mensch liest.</summary>
    public static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "eine Liste",
        JsonValueKind.String => "eine Zeichenkette",
        JsonValueKind.Number => "eine Zahl",
        JsonValueKind.True or JsonValueKind.False => "ein Wahrheitswert",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "ein Objekt",
        _ => "etwas Unbekanntes",
    };

    /// <summary>
    /// Läuft über die Servereinträge eines Containers und meldet doppelte Namen sowie Einträge, die
    /// gar keine Serverobjekte sind. Ein JSON-Objekt darf denselben Schlüssel zweimal tragen; welcher
    /// Eintrag dann gilt, hängt vom JSON-Leser ab — das wird hier nicht entschieden.
    /// </summary>
    public static IEnumerable<(string Name, JsonElement Value, string Path)> Servers(
        JsonElement container, string basePath, List<ImportFinding> findings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var server in container.EnumerateObject())
        {
            var path = $"{basePath}/{server.Name}";

            if (!seen.Add(server.Name))
            {
                findings.Add(new ImportFinding(
                    ImportReason.DuplicateServer,
                    ImportSeverity.Error,
                    $"Der Server '{server.Name}' steht mehrfach in der Quelle. Welcher Eintrag gilt, "
                    + "haengt vom JSON-Leser ab — das wird hier nicht entschieden.",
                    path,
                    "Den doppelten Eintrag in der Quelldatei entfernen und erneut importieren."));
                continue;
            }

            if (server.Value.ValueKind is not JsonValueKind.Object)
            {
                findings.Add(new ImportFinding(
                    ImportReason.UnknownField,
                    ImportSeverity.Error,
                    $"Der Eintrag '{server.Name}' ist {Describe(server.Value.ValueKind)} statt eines "
                    + "Serverobjekts.",
                    path));
                continue;
            }

            yield return (server.Name, server.Value, path);
        }
    }
}
