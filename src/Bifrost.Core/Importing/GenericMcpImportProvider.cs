using System.Globalization;
using System.Text.Json;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>
/// Der Parser für die verbreitetste Schreibweise einer MCP-Konfiguration: ein Objekt
/// <c>mcpServers</c> (ersatzweise <c>servers</c>), darin je ein benannter Server mit
/// <c>command</c>/<c>args</c>/<c>env</c> oder <c>url</c>/<c>headers</c>.
/// <para>
/// <b>Dies ist die Referenzumsetzung, nicht der Providerparser für einen bestimmten Client.</b> Die
/// Parser für Claude, Cursor, VS Code und Codex kommen in WP4.2 in eigenen Dateien — ein Parser, der
/// zwei Formate kennt, kennt bald beide halb. Dieser hier kennt genau das, was alle vier gemeinsam
/// haben, und erkennt sich deshalb bewusst nur mit mittlerer Sicherheit: Wer mehr weiß, soll ihn
/// überstimmen.
/// </para>
/// <para>
/// <b>Er schreibt nichts und liest nichts nach.</b> Kein Dateisystem, kein Netz, keine Auflösung von
/// Umgebungsvariablen. Was er liefert, ist eine Beschreibung dessen, was in der Datei stand — die
/// Normalisierung, die Risikobeurteilung und die Frage an die Ausführungs-Policy folgen zentral im
/// <see cref="ConfigurationImporter"/>.
/// </para>
/// </summary>
public sealed class GenericMcpImportProvider : IImportProvider
{
    /// <summary>Der Name, unter dem dieses Format gemeldet wird.</summary>
    public const string ProviderName = "mcp";

    /// <summary>
    /// Wie sicher sich dieser Parser bei <c>mcpServers</c> ist. Bewusst mittelmäßig: Das Feld steht
    /// in fast jeder Client-Konfiguration, es sagt also, dass es MCP ist — nicht, welcher Client.
    /// </summary>
    public const double McpServersConfidence = 0.6;

    /// <summary>
    /// <c>servers</c> ohne <c>mcpServers</c>. Schwächer, weil das Wort für sich genommen in
    /// beliebigen Konfigurationsdateien vorkommt.
    /// </summary>
    public const double ServersConfidence = 0.5;

    private const string McpServers = "mcpServers";

    private const string Servers = "servers";

    /// <summary>Wie tief ein Importdokument geschachtelt sein darf, bevor es abgewiesen wird.</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Felder auf Serverebene, die nur der Quellclient kennt. Sie werden <b>als Befund erhalten</b>
    /// statt still verworfen — was ein Client „autoApprove" nennt, ist hier eine Freigaberegel, und
    /// die verschwindet nicht dadurch, dass das Feld unbekannt ist.
    /// </summary>
    private static readonly HashSet<string> ClientOnlyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "disabled", "enabled", "autoApprove", "alwaysAllow", "description", "icon", "timeout",
        "initTimeout", "trust", "gallery", "version", "dev", "note", "tools", "roots", "sampling",
    };

    private static readonly HashSet<string> KnownServerFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "args", "env", "cwd", "workingDirectory", "type", "transport", "url",
        "serverUrl", "endpoint", "headers",
    };

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        // Fremde Konfigurationsdateien tragen Kommentare (VS Code schreibt jsonc) und
        // nachlaufende Kommata. Sie abzulehnen hiesse, an einer Formalie zu scheitern, die kein
        // Client durchsetzt.
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = MaxDepth,
    };

    /// <inheritdoc/>
    public string Name => ProviderName;

    /// <inheritdoc/>
    public double Recognize(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            return 0;
        }

        try
        {
            using var parsed = JsonDocument.Parse(document, ParseOptions);
            if (parsed.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return 0;
            }

            if (IsObject(parsed.RootElement, McpServers))
            {
                return McpServersConfidence;
            }

            return IsObject(parsed.RootElement, Servers) ? ServersConfidence : 0;
        }
        catch (JsonException)
        {
            // „Nicht meins" ist die richtige Antwort auf ein kaputtes Dokument. Dass es kaputt ist,
            // meldet der Importer mit BFR-IMP-0001 — hier waere es eine zweite Stimme zur selben
            // Sache.
            return 0;
        }
    }

    /// <inheritdoc/>
    [NoHostExecution(
        "Ein Parser erzeugt einen Plan, er startet nichts. Die Frage, ob etwas nativ laufen darf, "
        + "stellt ImportRiskScanner an die Policy; angewendet wird ein Plan ueber die Stores, und "
        + "dort sitzt der Torposten. Die erzeugte Konfiguration ist ausserdem Enabled = false.")]
    public ImportPlan Plan(string document, string? originPath)
    {
        var findings = new List<ImportFinding>();
        var candidates = new List<ImportCandidate>();
        var source = new ImportSource(ProviderName, null, Recognize(document ?? string.Empty), originPath);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(document ?? string.Empty, ParseOptions);
        }
        catch (JsonException exception)
        {
            return new ImportPlan(
                source with { Confidence = 0 },
                [],
                [
                    new ImportFinding(
                        ImportReason.NotJson,
                        ImportSeverity.Error,
                        $"Das Dokument ist kein gueltiges JSON: {exception.Message}",
                        Location(exception),
                        "Die Datei in einem Editor mit JSON-Pruefung oeffnen und die genannte Stelle "
                        + "korrigieren."),
                ]);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                findings.Add(new ImportFinding(
                    ImportReason.UnknownFormat,
                    ImportSeverity.Error,
                    $"Auf oberster Ebene steht {Describe(root.ValueKind)} statt eines Objekts mit "
                    + $"'{McpServers}'.",
                    null,
                    "Pruefen, ob die Datei wirklich eine MCP-Konfiguration ist — geraten wird hier nicht."));
                return new ImportPlan(source, [], findings);
            }

            var container = FindContainer(root, findings);
            if (container is not { } servers)
            {
                return new ImportPlan(source, [], findings);
            }

            ReadServers(servers.Element, servers.Path, candidates, findings);
        }

        return new ImportPlan(source, candidates, findings);
    }

    /// <summary>
    /// Sucht das Objekt mit den Servern und meldet, was sonst noch auf oberster Ebene steht.
    /// </summary>
    private static (JsonElement Element, string Path)? FindContainer(
        JsonElement root, List<ImportFinding> findings)
    {
        (JsonElement Element, string Path)? container = null;

        if (IsObject(root, McpServers))
        {
            container = (root.GetProperty(McpServers), McpServers);
        }
        else if (IsObject(root, Servers))
        {
            container = (root.GetProperty(Servers), Servers);
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, container?.Path, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(property.Name, "inputs", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new ImportFinding(
                    ImportReason.ClientOnlyField,
                    ImportSeverity.Warning,
                    "Das Feld 'inputs' beschreibt Eingabeaufforderungen des Quellclients. Dieses "
                    + "Gateway fragt niemanden beim Start — was dort erfragt wurde, muss hier als "
                    + "Wert hinterlegt werden.",
                    property.Name,
                    "Die betroffenen Werte vor dem Einschalten des Servers eintragen."));
                continue;
            }

            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Warning,
                $"Das Feld '{property.Name}' auf oberster Ebene ist diesem Parser unbekannt und wird "
                + "nicht uebernommen.",
                property.Name));
        }

        if (container is null)
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownFormat,
                ImportSeverity.Error,
                $"Das Dokument enthaelt weder '{McpServers}' noch '{Servers}' als Objekt.",
                null,
                "Pruefen, ob die Datei wirklich eine MCP-Konfiguration ist — geraten wird hier nicht."));
        }

        return container;
    }

    private static void ReadServers(
        JsonElement servers,
        string basePath,
        List<ImportCandidate> candidates,
        List<ImportFinding> findings)
    {
        // Bewusst ueber JsonElement statt ueber ein Dictionary: Ein JSON-Objekt darf denselben
        // Schluessel zweimal tragen, und ein Dictionary haette den ersten stillschweigend verloren
        // oder waere geworfen. Beides waere eine Antwort auf eine Frage, die der Betreiber
        // beantworten muss.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var server in servers.EnumerateObject())
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

            var candidate = ReadServer(server.Name, server.Value, path, findings);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Liest einen einzelnen Server. Liefert <c>null</c>, wenn sich daraus kein Transport ergibt —
    /// der zugehörige Befund steht dann in <paramref name="planFindings"/>, weil ohne Kandidat kein
    /// Ort für ihn bliebe.
    /// </summary>
    private static ImportCandidate? ReadServer(
        string name, JsonElement server, string path, List<ImportFinding> planFindings)
    {
        var findings = new List<ImportFinding>();

        foreach (var field in server.EnumerateObject())
        {
            if (KnownServerFields.Contains(field.Name))
            {
                continue;
            }

            findings.Add(ClientOnlyFields.Contains(field.Name)
                ? new ImportFinding(
                    ImportReason.ClientOnlyField,
                    ImportSeverity.Warning,
                    $"'{field.Name}' kennt nur der Quellclient und wird nicht uebernommen. Erhalten "
                    + "als Befund, damit die Einstellung nicht unbemerkt verschwindet.",
                    $"{path}/{field.Name}",
                    "Pruefen, ob es hier eine Entsprechung gibt (Freigaben, Zeitlimits, Beschreibung).")
                : new ImportFinding(
                    ImportReason.UnknownField,
                    ImportSeverity.Warning,
                    $"'{field.Name}' ist diesem Parser unbekannt und wird nicht uebernommen.",
                    $"{path}/{field.Name}"));
        }

        var command = Text(server, "command");
        var url = Text(server, "url") ?? Text(server, "serverUrl") ?? Text(server, "endpoint");
        var declaredType = (Text(server, "type") ?? Text(server, "transport"))?.Trim().ToLowerInvariant();

        var kind = ResolveKind(declaredType, command is not null, url is not null, path, findings);
        var candidate = kind switch
        {
            UpstreamTransportKind.Stdio => Stdio(name, server, command!, path, findings),
            UpstreamTransportKind.StreamableHttp => Http(
                name,
                server,
                url!,
                string.Equals(declaredType, "sse", StringComparison.Ordinal),
                path,
                findings),
            _ => null,
        };

        // Entsteht kein Kandidat, gibt es keinen Ort für die Befunde — sie wandern an den Plan.
        // Sie hier zu verlieren wäre der stille Fehler, den dieses Paket abschaffen soll: Der
        // Eintrag fehlte, und niemand erführe warum.
        if (candidate is null)
        {
            planFindings.AddRange(findings);
        }

        return candidate;
    }

    /// <summary>
    /// Welcher Transport ist gemeint? Bei Widerspruch oder Leere wird ein Befund erzeugt und
    /// <b>nicht</b> der wahrscheinlichere Fall genommen.
    /// </summary>
    private static UpstreamTransportKind? ResolveKind(
        string? declaredType, bool hasCommand, bool hasUrl, string path, List<ImportFinding> findings)
    {
        if (hasCommand && hasUrl)
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Error,
                "Der Eintrag traegt 'command' und 'url' zugleich. Ob ein lokales Programm oder ein "
                + "entfernter Dienst gemeint ist, sagt die Quelle damit nicht.",
                path,
                "Das ueberfluessige Feld in der Quelldatei entfernen."));
            return null;
        }

        switch (declaredType)
        {
            case null or "":
                break;

            case "stdio" or "local":
                if (!hasCommand)
                {
                    findings.Add(Missing(path, declaredType, "command"));
                    return null;
                }

                return UpstreamTransportKind.Stdio;

            case "http" or "https" or "sse" or "streamable-http" or "streamablehttp" or "streamable_http"
                or "remote":
                if (!hasUrl)
                {
                    findings.Add(Missing(path, declaredType, "url"));
                    return null;
                }

                return UpstreamTransportKind.StreamableHttp;

            default:
                findings.Add(new ImportFinding(
                    ImportReason.UnknownField,
                    ImportSeverity.Warning,
                    $"Der Transporttyp '{declaredType}' ist unbekannt; abgeleitet wird er aus den "
                    + "vorhandenen Feldern.",
                    $"{path}/type"));
                break;
        }

        if (hasCommand)
        {
            return UpstreamTransportKind.Stdio;
        }

        if (hasUrl)
        {
            return UpstreamTransportKind.StreamableHttp;
        }

        findings.Add(new ImportFinding(
            ImportReason.UnknownField,
            ImportSeverity.Error,
            "Der Eintrag traegt weder 'command' noch 'url' — es ist nicht erkennbar, was gestartet "
            + "oder aufgerufen werden soll.",
            path,
            "Den Eintrag in der Quelldatei vervollstaendigen."));
        return null;
    }

    private static ImportFinding Missing(string path, string declaredType, string field)
        => new(
            ImportReason.UnknownField,
            ImportSeverity.Error,
            $"Der Typ '{declaredType}' verlangt das Feld '{field}', das hier fehlt.",
            path,
            $"'{field}' in der Quelldatei ergaenzen.");

    private static ImportCandidate Stdio(
        string name, JsonElement server, string command, string path, List<ImportFinding> findings)
    {
        var arguments = ReadArguments(server, path, findings);
        var environment = ReadMap(server, "env", $"{path}/env", findings);
        var workingDirectory = Text(server, "cwd") ?? Text(server, "workingDirectory");

        var config = new UpstreamServerConfig(
            // Slug und Anzeigename setzt die Normalisierung; hier steht der Rohname, damit ein
            // unvollstaendiger Zwischenstand nicht wie ein fertiger aussieht.
            name,
            name,
            UpstreamTransportKind.Stdio,
            Enabled: false,
            Stdio: new StdioTransportOptions(
                command,
                arguments,
                environment.Count > 0 ? environment : null,
                workingDirectory));

        return new ImportCandidate(name, config, findings, []);
    }

    private static ImportCandidate? Http(
        string name,
        JsonElement server,
        string url,
        bool legacySse,
        string path,
        List<ImportFinding> findings)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            // Die Adresse selbst steht NICHT im Text. Eine ungueltige URL traegt haeufig genau das,
            // was sie ungueltig macht — und oft auch ein '?token=...'. Der Pfad unten sagt, WO der
            // Wert steht; wer ihn sehen will, sieht in seine eigene Datei.
            //
            // Gefunden von WP4.3 beim Bau der API: Dieser Befund gehoert zum Plan und nicht zu
            // einem Kandidaten, der Scrubber der API-Vorschau kommt also nicht heran. Ein
            // Befundtext ist eine Ausgabe wie jede andere.
            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Error,
                "Die Adresse ist keine absolute http- oder https-Adresse.",
                $"{path}/url",
                "Die vollstaendige Adresse einschliesslich Schema eintragen."));
            return null;
        }

        if (legacySse)
        {
            findings.Add(new ImportFinding(
                ImportReason.Lossy,
                ImportSeverity.Warning,
                "Die Quelle nennt den abgeloesten HTTP+SSE-Transport. Uebernommen wird Streamable "
                + "HTTP mit erlaubtem Rueckfall auf SSE — die Verbindung kommt damit zustande, aber "
                + "der Rueckfall faellt weg, sobald SSE aus dem Standard geht.",
                $"{path}/type",
                "Beim Anbieter nachsehen, ob es einen Streamable-HTTP-Endpunkt gibt."));
        }

        var headers = ReadMap(server, "headers", $"{path}/headers", findings);

        var config = new UpstreamServerConfig(
            name,
            name,
            UpstreamTransportKind.StreamableHttp,
            Enabled: false,
            Http: new HttpTransportOptions(
                endpoint,
                headers.Count > 0 ? headers : null,
                AllowLegacySse: legacySse));

        return new ImportCandidate(name, config, findings, []);
    }

    private static List<string> ReadArguments(
        JsonElement server, string path, List<ImportFinding> findings)
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
            var position = index++;
            switch (argument.ValueKind)
            {
                case JsonValueKind.String:
                    result.Add(argument.GetString() ?? string.Empty);
                    break;

                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    // Ein Argument ist auf der Kommandozeile immer Text. Die Umwandlung ist
                    // eindeutig, aber sie ist eine Umwandlung — und die wird benannt.
                    result.Add(argument.GetRawText());
                    findings.Add(new ImportFinding(
                        ImportReason.Lossy,
                        ImportSeverity.Info,
                        $"Das Argument an Position {position.ToString(CultureInfo.InvariantCulture)} "
                        + "steht in der Quelle nicht als Text und wird als Text uebergeben.",
                        $"{path}/args[{position.ToString(CultureInfo.InvariantCulture)}]"));
                    break;

                default:
                    findings.Add(new ImportFinding(
                        ImportReason.Lossy,
                        ImportSeverity.Warning,
                        $"Das Argument an Position {position.ToString(CultureInfo.InvariantCulture)} "
                        + $"ist {Describe(argument.ValueKind)} und wird nicht uebernommen. Die "
                        + "Reihenfolge der uebrigen Argumente verschiebt sich dadurch.",
                        $"{path}/args[{position.ToString(CultureInfo.InvariantCulture)}]",
                        "Den Aufruf nach dem Import gegen die Quelldatei pruefen."));
                    break;
            }
        }

        return result;
    }

    /// <summary>Ein Objekt aus Zeichenketten — <c>env</c> und <c>headers</c> haben dieselbe Form.</summary>
    private static Dictionary<string, string> ReadMap(
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

    private static string? Text(JsonElement element, string field)
        => element.TryGetProperty(field, out var value)
            && value.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

    private static bool IsObject(JsonElement element, string field)
        => element.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.Object;

    private static string Describe(JsonValueKind kind) => kind switch
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
    /// Die Fundstelle eines JSON-Fehlers als Pfadangabe. Zeile und Position stehen bereits in der
    /// Meldung; der Pfad sagt, <em>welcher</em> Server betroffen ist.
    /// </summary>
    private static string? Location(JsonException exception)
        => string.IsNullOrEmpty(exception.Path) ? null : exception.Path;
}
