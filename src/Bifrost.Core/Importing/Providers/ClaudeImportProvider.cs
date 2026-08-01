using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>
/// Der Parser für die Konfigurationen von Claude Code und Claude Desktop.
/// <para>
/// <b>Drei belegte Erscheinungsformen:</b>
/// </para>
/// <list type="bullet">
/// <item><c>.mcp.json</c> im Projekt (Claude Code): <c>{ "mcpServers": { … } }</c> mit
/// <c>command</c>/<c>args</c>/<c>env</c> oder <c>type</c>/<c>url</c>/<c>headers</c>.</item>
/// <item><c>~/.claude.json</c> (Claude Code, Benutzerebene): eine Karte <c>projects</c>, unter jedem
/// Projektpfad wieder ein <c>mcpServers</c>.</item>
/// <item><c>claude_desktop_config.json</c> (Claude Desktop): <c>mcpServers</c> neben
/// Anwendungseinstellungen wie <c>globalShortcut</c>.</item>
/// </list>
/// <para>
/// <b>Warum dieser Parser sich bei einer schlichten <c>.mcp.json</c> ausdrücklich NICHT meldet:</b>
/// Diese Datei ist zeichengleich mit dem, was Cursor und jeder generische Client schreiben. Ein
/// Parser, der sie trotzdem für seine hält, gewinnt nichts und verliert die Ehrlichkeit der
/// Herkunftsangabe — er behauptete „aus Claude", wo nur „MCP" belegt ist. Gemeldet wird deshalb erst,
/// wenn etwas im Dokument steht, das <b>nur</b> Claude schreibt: die Einstellungsschlüssel von Claude
/// Code, die <c>projects</c>-Karte, oder die Ersetzungsform <c>${VAR:-vorgabe}</c>, die Claude Code
/// als einziger der vier Clients kennt. Sonst übernimmt der generische Parser — und das ist der
/// richtige Ausgang, nicht ein Versäumnis.
/// </para>
/// <para>
/// <b>Er liest nichts nach.</b> Kein Dateisystem, kein Netz, keine Auflösung von
/// <c>${VAR}</c>-Ersetzungen. Ein Quellpfad ist eine Angabe über die Herkunft, kein Leseauftrag: Was
/// dieser Parser über <c>originPath</c> erfährt, landet unverändert in der Herkunftsangabe des Plans
/// und wird nirgends geöffnet.
/// </para>
/// </summary>
public sealed partial class ClaudeImportProvider : IImportProvider
{
    /// <summary>Der Name, unter dem dieses Format gemeldet wird.</summary>
    public const string ProviderName = "claude";

    /// <summary>
    /// Ein Einstellungsschlüssel von Claude Code oder die <c>projects</c>-Karte aus
    /// <c>~/.claude.json</c>. Diese Namen schreibt kein anderer Client.
    /// </summary>
    public const double SettingsConfidence = 0.95;

    /// <summary>
    /// <c>mcpServers</c> plus die Ersetzungsform <c>${VAR:-vorgabe}</c>. Claude Code ist der einzige
    /// der vier Clients mit dieser Schreibweise (Cursor und VS Code benutzen <c>${env:NAME}</c>).
    /// Schwächer als ein Einstellungsschlüssel, weil sie in einer von Hand gepflegten Datei auch
    /// versehentlich stehen kann.
    /// </summary>
    public const double DialectConfidence = 0.8;

    /// <summary>Der Sammelname der Server, in allen drei Erscheinungsformen gleich.</summary>
    private const string McpServers = "mcpServers";

    /// <summary>Die Karte der Projektkonfigurationen aus <c>~/.claude.json</c>.</summary>
    private const string Projects = "projects";

    /// <summary>Die Namen der belegten Erscheinungsformen — sie stehen im Plan als Schemaangabe.</summary>
    private const string ProjectSchema = "claude-code/.mcp.json";

    private const string UserSchema = "claude-code/~/.claude.json";

    private const string DesktopSchema = "claude-desktop/claude_desktop_config.json";

    /// <summary>
    /// Schlüssel, die Claude Code und Claude Desktop auf oberster Ebene schreiben. Sie gehören dem
    /// Quellclient und werden <b>als Befund erhalten</b>, nicht still verworfen: Was dort steht, sind
    /// Freigaben, Hooks und Modellwahl — Dinge, die hier eine Entsprechung haben können.
    /// </summary>
    private static readonly HashSet<string> ClientOnlyRoot = new(StringComparer.Ordinal)
    {
        "globalShortcut", "enabledMcpjsonServers", "disabledMcpjsonServers",
        "enableAllProjectMcpServers", "permissions", "hooks", "model", "env", "apiKeyHelper",
        "statusLine", "outputStyle", "includeCoAuthoredBy", "cleanupPeriodDays", "autoUpdates",
        "forceLoginMethod", "theme", "mcpContextUris", "sandbox",
    };

    /// <summary>Die Felder eines Servereintrags, die dieser Parser abbildet.</summary>
    private static readonly HashSet<string> KnownServerFields = new(StringComparer.Ordinal)
    {
        "command", "args", "env", "type", "url", "headers",
    };

    /// <summary>
    /// Die Ersetzungsform mit Vorgabewert. Sie ist der Dialektnachweis: <c>${VAR:-vorgabe}</c>
    /// schreibt von den vier Clients nur Claude Code.
    /// </summary>
    [GeneratedRegex(@"\$\{[A-Za-z_][A-Za-z0-9_]*:-[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultedExpansion();

    /// <summary>Jede Ersetzung, mit und ohne Vorgabewert.</summary>
    [GeneratedRegex(@"\$\{[A-Za-z_][A-Za-z0-9_]*(:-[^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex AnyExpansion();

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
            using var parsed = JsonDocument.Parse(document, ClientConfigReading.ParseOptions);
            var root = parsed.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return 0;
            }

            if (HasProjectServers(root) || root.EnumerateObject().Any(IsClaudeSetting))
            {
                return SettingsConfidence;
            }

            return ClientConfigReading.IsObject(root, McpServers) && DefaultedExpansion().IsMatch(document)
                ? DialectConfidence
                : 0;
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
        var text = document ?? string.Empty;
        var findings = new List<ImportFinding>();
        var candidates = new List<ImportCandidate>();

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(text, ClientConfigReading.ParseOptions);
        }
        catch (JsonException exception)
        {
            return ClientConfigReading.Broken(ProviderName, originPath, exception);
        }

        string schema;
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return new ImportPlan(
                    new ImportSource(ProviderName, null, Recognize(text), originPath),
                    [],
                    [
                        ClientConfigReading.WrongShape(
                            $"Auf oberster Ebene steht {ClientConfigReading.Describe(root.ValueKind)} "
                            + $"statt eines Objekts mit '{McpServers}'."),
                    ]);
            }

            schema = Schema(root);
            var containers = Containers(root, findings);

            foreach (var (container, path) in containers)
            {
                foreach (var server in ClientConfigReading.Servers(container, path, findings))
                {
                    var candidate = ReadServer(server.Name, server.Value, server.Path, findings);
                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            if (containers.Count == 0)
            {
                findings.Add(ClientConfigReading.WrongShape(
                    $"Die Datei sieht nach Claude aus, enthaelt aber kein '{McpServers}' als Objekt — "
                    + "weder auf oberster Ebene noch unter einem Projekt."));
            }

            ReadRoot(root, findings);
        }

        return new ImportPlan(
            new ImportSource(ProviderName, schema, Recognize(text), originPath),
            candidates,
            findings);
    }

    /// <summary>
    /// Der Rückweg in eine <c>.mcp.json</c>. Verlustfrei für ein lokales Programm ohne
    /// Arbeitsverzeichnis und für einen HTTP-Server mit Kopfzeilen; alles andere wird benannt.
    /// </summary>
    [NoHostExecution(
        "Schreibt eine vorhandene Konfiguration als Text im Clientformat. Kein Start, keine "
        + "Persistenz, keine Datei — das Ergebnis ist eine Zeichenkette.")]
    public static ClientExportResult Export(ImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<ImportFinding>();
        var config = candidate.Config;
        var entry = new JsonObject();

        if (ClientExport.Unsupported(config, "Claude") is { } unsupported)
        {
            findings.Add(unsupported);
        }
        else if (config.Stdio is { } stdio)
        {
            entry["command"] = stdio.Command;
            if (ClientExport.List(stdio.Arguments) is { } args)
            {
                entry["args"] = args;
            }

            if (ClientExport.Map(stdio.EnvironmentVariables) is { } environment)
            {
                entry["env"] = environment;
            }

            if (!string.IsNullOrWhiteSpace(stdio.WorkingDirectory))
            {
                findings.Add(ClientExport.Drops(
                    config.Slug, "workingDirectory", "Claude",
                    "das Arbeitsverzeichnis; Claude startet den Server im Verzeichnis des Clients."));
            }
        }
        else if (config.Http is { } http)
        {
            // Bewusst immer 'http': Ob dieses Gateway auf den abgeloesten SSE-Transport
            // zurueckfaellt, ist eine Eigenschaft SEINER Verbindung und keine Angabe ueber den
            // Server. 'sse' hierher zu schreiben hiesse, Claude den alten Transport vorzuschreiben.
            entry["type"] = "http";
            entry["url"] = http.Endpoint.ToString();
            if (ClientExport.Map(http.Headers) is { } headers)
            {
                entry["headers"] = headers;
            }

            if (http.OAuth is not null)
            {
                findings.Add(ClientExport.Drops(
                    config.Slug, "oauth", "Claude",
                    "die OAuth-Anbindung dieses Gateways; Claude handelt seine Autorisierung selbst aus."));
            }
        }

        var wrapper = new JsonObject { [McpServers] = new JsonObject { [config.Slug] = entry } };
        return new ClientExportResult(wrapper.ToJsonString(ClientExport.Pretty), findings);
    }

    /// <summary>Welche der drei Erscheinungsformen liegt vor?</summary>
    private static string Schema(JsonElement root)
    {
        if (HasProjectServers(root))
        {
            return UserSchema;
        }

        return root.EnumerateObject().Any(property =>
            string.Equals(property.Name, "globalShortcut", StringComparison.Ordinal))
            ? DesktopSchema
            : ProjectSchema;
    }

    private static bool HasProjectServers(JsonElement root)
        => ClientConfigReading.IsObject(root, Projects)
            && root.GetProperty(Projects).EnumerateObject()
                .Any(project => ClientConfigReading.IsObject(project.Value, McpServers));

    private static bool IsClaudeSetting(JsonProperty property)
        => property.Name is "enabledMcpjsonServers" or "disabledMcpjsonServers"
            or "enableAllProjectMcpServers";

    /// <summary>
    /// Die Serverblöcke: der auf oberster Ebene und je einer pro Projekt. Beide Formen kommen in
    /// derselben Datei vor — <c>~/.claude.json</c> trägt eine Benutzerebene <b>und</b> Projekte.
    /// </summary>
    private static List<(JsonElement Container, string Path)> Containers(
        JsonElement root, List<ImportFinding> findings)
    {
        var result = new List<(JsonElement, string)>();

        if (ClientConfigReading.IsObject(root, McpServers))
        {
            result.Add((root.GetProperty(McpServers), McpServers));
        }

        if (!ClientConfigReading.IsObject(root, Projects))
        {
            return result;
        }

        var withoutServers = 0;
        foreach (var project in root.GetProperty(Projects).EnumerateObject())
        {
            if (!ClientConfigReading.IsObject(project.Value, McpServers))
            {
                withoutServers++;
                continue;
            }

            result.Add((
                project.Value.GetProperty(McpServers),
                $"{Projects}/{project.Name}/{McpServers}"));

            // Was sonst noch unter einem Projekt steht — Verlauf, Freigaben, Modellwahl — gehört
            // dem Quellclient. Ein Import, der es stillschweigend fallen lässt, erzeugt eine
            // Konfiguration, die anders ist als die Quelle.
            findings.Add(ClientConfigReading.ClientOnly(
                $"{Projects}/{project.Name}",
                $"Der Projekteintrag '{project.Name}' traegt neben den Servern die Projektangaben von "
                + "Claude Code (Freigaben, Verlauf, Modellwahl). Uebernommen werden nur die Server.",
                "Freigaben und Modellwahl haben hier eigene Orte (Profile, Richtlinien) und muessen "
                + "dort gesetzt werden."));
        }

        if (withoutServers > 0)
        {
            findings.Add(ClientConfigReading.ClientOnly(
                Projects,
                $"{withoutServers.ToString(CultureInfo.InvariantCulture)} weitere Projekteintraege "
                + "tragen keine Server und werden nicht weiter angesehen.",
                "Nichts zu tun — der Hinweis steht hier, damit die Zahl der Server nachvollziehbar "
                + "bleibt."));
        }

        return result;
    }

    /// <summary>Was auf oberster Ebene neben den Servern steht.</summary>
    private static void ReadRoot(JsonElement root, List<ImportFinding> findings)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, McpServers, StringComparison.Ordinal)
                || string.Equals(property.Name, Projects, StringComparison.Ordinal))
            {
                continue;
            }

            if (property.Name is "enabledMcpjsonServers" or "disabledMcpjsonServers"
                or "enableAllProjectMcpServers")
            {
                findings.Add(ClientConfigReading.ClientOnly(
                    property.Name,
                    $"'{property.Name}' sagt, welche Server in Claude Code freigegeben oder "
                    + "abgeschaltet waren. Dieses Gateway kennt diese Liste nicht — jeder importierte "
                    + "Server kommt abgeschaltet an und wird einzeln eingeschaltet.",
                    "Vor dem Einschalten abgleichen, welche Server in der Quelle wirklich aktiv waren."));
                continue;
            }

            findings.Add(ClientOnlyRoot.Contains(property.Name)
                ? ClientConfigReading.ClientOnly(
                    property.Name,
                    $"'{property.Name}' ist eine Einstellung des Quellclients und wird nicht "
                    + "uebernommen. Erhalten als Befund, damit sie nicht unbemerkt verschwindet.")
                : ClientConfigReading.Unknown(property.Name, property.Name));
        }
    }

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

            findings.Add(string.Equals(field.Name, "cwd", StringComparison.Ordinal)
                ? ClientConfigReading.ClientOnly(
                    $"{path}/cwd",
                    "'cwd' gehoert nicht zum dokumentierten Claude-Schema (command, args, env "
                    + "beziehungsweise type, url, headers). Uebernommen wird es deshalb nicht — wenn "
                    + "Claude es ignoriert hat, lief der Server dort woanders als er hier liefe.",
                    "Falls der Server ein Arbeitsverzeichnis braucht, es hier ausdruecklich setzen.")
                : ClientConfigReading.Unknown($"{path}/{field.Name}", field.Name));
        }

        var command = ClientConfigReading.Text(server, "command");
        var url = ClientConfigReading.Text(server, "url");
        var declared = ClientConfigReading.Text(server, "type")?.Trim();

        if (command is not null && url is not null)
        {
            planFindings.Add(ClientConfigReading.BothTransports(path, "command", "url"));
            planFindings.AddRange(findings);
            return null;
        }

        var expansions = new List<string>();
        var candidate = declared switch
        {
            "stdio" or null when command is not null => Stdio(name, server, command, path, findings, expansions),
            "http" or "sse" or "https" when url is not null =>
                Http(name, server, url, string.Equals(declared, "sse", StringComparison.Ordinal), path, findings, expansions),
            null when url is not null => Http(name, server, url, false, path, findings, expansions),
            _ => Mismatch(declared, command, url, path, findings),
        };

        if (expansions.Count > 0)
        {
            findings.Add(new ImportFinding(
                ImportReason.Lossy,
                ImportSeverity.Warning,
                "Die Quelle benutzt die Ersetzung ${VAR} beziehungsweise ${VAR:-vorgabe} von Claude "
                + "Code an diesen Stellen: " + string.Join(", ", expansions) + ". Dieses Gateway "
                + "ersetzt nichts — die Werte stehen hier woertlich so, wie sie in der Datei "
                + "standen.",
                path,
                "Die betroffenen Werte vor dem Einschalten des Servers eintragen."));
        }

        if (candidate is null)
        {
            // Ohne Kandidat gibt es keinen Ort für die Befunde — sie wandern an den Plan. Sie hier
            // zu verlieren wäre der stille Fehler, den dieses Paket abschaffen soll.
            planFindings.AddRange(findings);
        }

        return candidate;
    }

    /// <summary>Ein Eintrag, dessen <c>type</c> nicht zu den vorhandenen Feldern passt.</summary>
    private static ImportCandidate? Mismatch(
        string? declared, string? command, string? url, string path, List<ImportFinding> findings)
    {
        if (declared is null)
        {
            findings.Add(ClientConfigReading.NoTransport(path, "command", "url"));
            return null;
        }

        findings.Add(new ImportFinding(
            ImportReason.UnknownField,
            ImportSeverity.Error,
            command is null && url is null
                ? $"Der Typ '{declared}' steht ohne 'command' und ohne 'url' da."
                : $"Der Typ '{declared}' passt nicht zu den vorhandenen Feldern. Claude kennt 'stdio', "
                    + "'http' und 'sse'.",
            $"{path}/type",
            "Den Eintrag in der Quelldatei vervollstaendigen oder den Typ korrigieren.",
            ImportFindingScope.Entry));
        return null;
    }

    private static ImportCandidate Stdio(
        string name,
        JsonElement server,
        string command,
        string path,
        List<ImportFinding> findings,
        List<string> expansions)
    {
        var arguments = ClientConfigReading.Arguments(server, path, findings);
        var environment = ClientConfigReading.Map(server, "env", $"{path}/env", findings);

        Note(command, $"{path}/command", expansions);
        for (var index = 0; index < arguments.Count; index++)
        {
            Note(arguments[index], $"{path}/args[{index}]", expansions);
        }

        foreach (var entry in environment)
        {
            Note(entry.Value, $"{path}/env/{entry.Key}", expansions);
        }

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
                environment.Count > 0 ? environment : null));

        return new ImportCandidate(name, config, findings, [], path);
    }

    private static ImportCandidate? Http(
        string name,
        JsonElement server,
        string url,
        bool legacySse,
        string path,
        List<ImportFinding> findings,
        List<string> expansions)
    {
        var headers = ClientConfigReading.Map(server, "headers", $"{path}/headers", findings);

        Note(url, $"{path}/url", expansions);
        foreach (var entry in headers)
        {
            Note(entry.Value, $"{path}/headers/{entry.Key}", expansions);
        }

        // Eine Adresse, die erst nach einer Ersetzung eine Adresse ist, wird abgewiesen statt halb
        // angelegt: Eine halbe Adresse liefe durch jede Pruefung und scheiterte erst am Netz — mit
        // einer Meldung, die nach einem Netzproblem aussieht.
        if (AnyExpansion().IsMatch(url) && !Uri.TryCreate(url.Trim(), UriKind.Absolute, out _))
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Error,
                "Die Adresse besteht zum Teil aus einer Ersetzung (${VAR} beziehungsweise "
                + "${VAR:-vorgabe}) und ist deshalb keine Adresse. Dieses Gateway ersetzt nichts — "
                + "aufgeloest wird sie hier also nie.",
                $"{path}/url",
                "Die Adresse ausgeschrieben eintragen.",
                ImportFindingScope.Entry));
            return null;
        }

        if (ClientConfigReading.Endpoint(url, path, findings) is not { } endpoint)
        {
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

        var config = new UpstreamServerConfig(
            name,
            name,
            UpstreamTransportKind.StreamableHttp,
            Enabled: false,
            Http: new HttpTransportOptions(
                endpoint,
                headers.Count > 0 ? headers : null,
                AllowLegacySse: legacySse));

        return new ImportCandidate(name, config, findings, [], path);
    }

    /// <summary>Merkt sich eine Fundstelle mit Ersetzung, ohne sie aufzulösen.</summary>
    private static void Note(string value, string path, List<string> expansions)
    {
        if (AnyExpansion().IsMatch(value))
        {
            expansions.Add(path);
        }
    }
}
