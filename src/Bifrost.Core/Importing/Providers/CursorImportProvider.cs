using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>
/// Der Parser für <c>~/.cursor/mcp.json</c> und <c>.cursor/mcp.json</c>.
/// <para>
/// <b>Cursors Datei ist im Regelfall zeichengleich mit der generischen:</b> ein Objekt
/// <c>mcpServers</c>, darin <c>command</c>/<c>args</c>/<c>env</c> oder <c>url</c>/<c>headers</c>.
/// Dieser Parser meldet sich deshalb nur, wenn etwas im Dokument steht, das Cursor von den anderen
/// dreien unterscheidet:
/// </para>
/// <list type="bullet">
/// <item>der Block <c>auth</c> mit <c>CLIENT_ID</c>/<c>CLIENT_SECRET</c> — VS Code nennt das
/// <c>oauth</c>, Claude und Codex kennen es nicht;</item>
/// <item>Cursors Ersetzungsschreibweise <c>${env:NAME}</c>, <c>${userHome}</c>,
/// <c>${workspaceFolder}</c>, <c>${pathSeparator}</c>, <c>${/}</c> — dieselbe Schreibweise benutzt
/// VS Code, aber unter dem Sammelnamen <c>servers</c>, nicht <c>mcpServers</c>;</item>
/// <item><c>envFile</c> neben <c>mcpServers</c> — bei VS Code steht dasselbe Feld unter
/// <c>servers</c>.</item>
/// </list>
/// <para>
/// Findet er nichts davon, meldet er <c>0</c>. Dann übernimmt der generische Parser, und die
/// Herkunftsangabe lautet „MCP" statt „Cursor" — was belegt ist, und nicht mehr.
/// </para>
/// <para>
/// <b>Er liest nichts nach.</b> Insbesondere wird ein <c>envFile</c> <em>nicht</em> geöffnet: Der
/// Import beschreibt, was in der Datei stand. Ein Gateway, das den in einer fremden Konfiguration
/// genannten Pfad selbst ausliest, wäre ein Weg, beliebige Dateien des Rechners zu lesen.
/// </para>
/// </summary>
public sealed partial class CursorImportProvider : IImportProvider
{
    /// <summary>Der Name, unter dem dieses Format gemeldet wird.</summary>
    public const string ProviderName = "cursor";

    /// <summary>Der <c>auth</c>-Block. Von den vier Clients schreibt ihn nur Cursor so.</summary>
    public const double AuthConfidence = 0.9;

    /// <summary>
    /// Cursors Ersetzungsschreibweise unter <c>mcpServers</c>. Etwas schwächer als
    /// <see cref="AuthConfidence"/>, weil VS Code dieselbe Schreibweise benutzt — nur eben unter
    /// einem anderen Sammelnamen.
    /// </summary>
    public const double DialectConfidence = 0.85;

    /// <summary><c>envFile</c> unter <c>mcpServers</c>.</summary>
    public const double EnvFileConfidence = 0.8;

    private const string McpServers = "mcpServers";

    /// <summary>Die belegte Erscheinungsform — dieselbe Datei auf Benutzer- und Projektebene.</summary>
    private const string Schema = "cursor/mcp.json";

    /// <summary>Die Felder eines Servereintrags, die dieser Parser abbildet.</summary>
    private static readonly HashSet<string> KnownServerFields = new(StringComparer.Ordinal)
    {
        "command", "args", "env", "type", "url", "headers",
    };

    /// <summary>
    /// Felder, die Cursor kennt und dieses Gateway nicht abbildet. Sie werden <b>als Befund
    /// erhalten</b> — was Cursor „disabled" nennt, ist eine Aussage über den Betrieb, und die
    /// verschwindet nicht dadurch, dass das Feld hier keinen Platz hat.
    /// </summary>
    private static readonly Dictionary<string, string> ClientOnlyServerFields = new(StringComparer.Ordinal)
    {
        ["disabled"] = "der Ein-/Ausschalter von Cursor. Hier kommt ohnehin jeder importierte Server "
            + "abgeschaltet an; ob er eingeschaltet wird, entscheidet der Betreiber.",
        ["envFile"] = "ein Verweis auf eine Datei mit Umgebungsvariablen. Diese Datei wird NICHT "
            + "gelesen — der Import beschreibt, was in der Konfiguration stand, und liest nichts "
            + "nach. Die Werte fehlen hier also.",
        ["auth"] = "Cursors OAuth-Angaben (CLIENT_ID, CLIENT_SECRET, scopes). Dieses Gateway fuehrt "
            + "seine eigene OAuth-Anbindung; uebernommen wird sie nicht.",
        ["cwd"] = "ein Arbeitsverzeichnis. Es gehoert nicht zum dokumentierten Cursor-Schema; wenn "
            + "Cursor es ignoriert hat, liefe der Server hier woanders als dort.",
    };

    /// <summary>
    /// Cursors Ersetzungen. <c>${env:NAME}</c> und die Arbeitsbereichsangaben zeigen auf die
    /// Umgebung des <em>Clients</em> — auf dieser Instanz gibt es sie nicht.
    /// </summary>
    [GeneratedRegex(
        @"\$\{(env:[A-Za-z_][A-Za-z0-9_]*|userHome|workspaceFolder|workspaceFolderBasename"
        + @"|pathSeparator|/)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex CursorExpansion();

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
            if (!ClientConfigReading.IsObject(root, McpServers))
            {
                return 0;
            }

            var servers = root.GetProperty(McpServers);
            if (servers.EnumerateObject().Any(server =>
                ClientConfigReading.IsObject(server.Value, "auth")))
            {
                return AuthConfidence;
            }

            if (CursorExpansion().IsMatch(document))
            {
                return DialectConfidence;
            }

            return servers.EnumerateObject().Any(server =>
                ClientConfigReading.Has(server.Value, "envFile"))
                ? EnvFileConfidence
                : 0;
        }
        catch (JsonException)
        {
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
        var source = new ImportSource(ProviderName, Schema, Recognize(text), originPath);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(text, ClientConfigReading.ParseOptions);
        }
        catch (JsonException exception)
        {
            return ClientConfigReading.Broken(ProviderName, originPath, exception);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (!ClientConfigReading.IsObject(root, McpServers))
            {
                return new ImportPlan(
                    source with { SchemaVersion = null },
                    [],
                    [
                        ClientConfigReading.WrongShape(
                            $"Eine Cursor-Konfiguration traegt '{McpServers}' als Objekt; hier steht "
                            + "das nicht."),
                    ]);
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, McpServers, StringComparison.Ordinal))
                {
                    findings.Add(ClientConfigReading.Unknown(property.Name, property.Name));
                }
            }

            foreach (var server in ClientConfigReading.Servers(
                root.GetProperty(McpServers), McpServers, findings))
            {
                var candidate = ReadServer(server.Name, server.Value, server.Path, findings);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
        }

        return new ImportPlan(source, candidates, findings);
    }

    /// <summary>
    /// Der Rückweg in eine <c>mcp.json</c> von Cursor. Verlustfrei für ein lokales Programm ohne
    /// Arbeitsverzeichnis und für einen HTTP-Server mit Kopfzeilen.
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

        if (ClientExport.Unsupported(config, "Cursor") is { } unsupported)
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
                    config.Slug, "workingDirectory", "Cursor",
                    "das Arbeitsverzeichnis; es gehoert nicht zum dokumentierten Cursor-Schema."));
            }
        }
        else if (config.Http is { } http)
        {
            entry["url"] = http.Endpoint.ToString();
            if (ClientExport.Map(http.Headers) is { } headers)
            {
                entry["headers"] = headers;
            }

            if (http.OAuth is not null)
            {
                findings.Add(ClientExport.Drops(
                    config.Slug, "oauth", "Cursor",
                    "die OAuth-Anbindung dieses Gateways. Cursors 'auth' verlangt CLIENT_ID und "
                    + "CLIENT_SECRET des Clients — das sind andere Angaben, keine Uebersetzung."));
            }
        }

        var wrapper = new JsonObject { [McpServers] = new JsonObject { [config.Slug] = entry } };
        return new ClientExportResult(wrapper.ToJsonString(ClientExport.Pretty), findings);
    }

    private static ImportCandidate? ReadServer(
        string name, JsonElement server, string path, List<ImportFinding> planFindings)
    {
        var findings = new List<ImportFinding>();
        var secrets = new List<ImportSecret>();

        foreach (var field in server.EnumerateObject())
        {
            if (KnownServerFields.Contains(field.Name))
            {
                continue;
            }

            if (!ClientOnlyServerFields.TryGetValue(field.Name, out var explanation))
            {
                findings.Add(ClientConfigReading.Unknown($"{path}/{field.Name}", field.Name));
                continue;
            }

            findings.Add(ClientConfigReading.ClientOnly(
                $"{path}/{field.Name}",
                $"'{field.Name}' kennt nur Cursor und wird nicht uebernommen: {explanation}"));

            switch (field.Name)
            {
                case "envFile":
                    // Der Pfad steht in der Quelle; gelesen wird er nicht. Gemeldet wird, DASS dort
                    // Werte liegen — sonst sähe der Import vollständig aus, obwohl die halbe
                    // Umgebung fehlt.
                    secrets.Add(new ImportSecret(
                        $"Umgebungsdatei aus '{path}/envFile'",
                        "Cursor laedt aus dieser Datei Umgebungsvariablen; dieser Import oeffnet sie "
                        + "ausdruecklich nicht",
                        false));
                    break;

                case "auth":
                    ReadAuth(field.Value, $"{path}/auth", findings, secrets);
                    break;

                default:
                    break;
            }
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
        ImportCandidate? candidate;
        if (command is not null)
        {
            candidate = Stdio(name, server, command, path, findings, secrets, expansions);
        }
        else if (url is not null)
        {
            candidate = Http(name, server, url, declared, path, findings, secrets, expansions);
        }
        else
        {
            candidate = NoTransport(path, findings);
        }

        if (expansions.Count > 0)
        {
            findings.Add(new ImportFinding(
                ImportReason.Lossy,
                ImportSeverity.Warning,
                "Die Quelle benutzt Cursors Ersetzungen (${env:…}, ${userHome}, ${workspaceFolder}, "
                + "${pathSeparator}) an diesen Stellen: " + string.Join(", ", expansions)
                + ". Sie zeigen auf die Umgebung des Clients; dieses Gateway hat diese Umgebung "
                + "nicht und ersetzt ausdruecklich nichts.",
                path,
                "Die betroffenen Werte vor dem Einschalten des Servers durch die hier gueltigen "
                + "ersetzen."));
        }

        if (candidate is null)
        {
            planFindings.AddRange(findings);
        }

        return candidate;
    }

    private static ImportCandidate? NoTransport(string path, List<ImportFinding> findings)
    {
        findings.Add(ClientConfigReading.NoTransport(path, "command", "url"));
        return null;
    }

    /// <summary>
    /// Cursors <c>auth</c>-Block. Das Interessante daran ist <c>CLIENT_SECRET</c>: Es ist ein
    /// Zugangsdatum an einer Stelle, die dieses Gateway <b>nicht</b> übernimmt — die zentrale
    /// Risikoprüfung sähe es also nie. Genau deshalb wird es hier eingeordnet.
    /// </summary>
    private static void ReadAuth(
        JsonElement auth, string path, List<ImportFinding> findings, List<ImportSecret> secrets)
    {
        if (auth.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        foreach (var field in auth.EnumerateObject())
        {
            if (field.Value.ValueKind is not JsonValueKind.String)
            {
                continue;
            }

            var verdict = ImportSecretDetection.InspectEnvironment(field.Name, field.Value.GetString());
            if (!verdict.IsSecret)
            {
                continue;
            }

            var where = $"Cursor-OAuth-Feld '{field.Name}'";
            secrets.Add(new ImportSecret(where, verdict.Looked, verdict.ValuePresent));

            if (verdict.Masked)
            {
                findings.Add(new ImportFinding(
                    ImportReason.MaskedValue,
                    ImportSeverity.Warning,
                    $"{where}: Der Wert ist maskiert oder eine Verweisform. Er wird NICHT "
                    + "rekonstruiert.",
                    $"{path}/{field.Name}",
                    "Das Zugangsdatum auf der Zielinstanz nachtragen, falls dieser Server OAuth "
                    + "braucht."));
                continue;
            }

            if (verdict.ValuePresent)
            {
                findings.Add(new ImportFinding(
                    ImportReason.PlaintextSecret,
                    ImportSeverity.Risk,
                    $"{where}: Die Quelle traegt das Zugangsdatum im Klartext ({verdict.Looked}). Es "
                    + "wird nicht uebernommen, weil dieses Gateway Cursors OAuth-Angaben nicht "
                    + "fuehrt — es steht aber in der Quelldatei.",
                    $"{path}/{field.Name}",
                    "Die Quelldatei als kompromittiert behandeln: Wer sie gelesen hat, hat das "
                    + "Zugangsdatum."));
            }
        }
    }

    private static ImportCandidate Stdio(
        string name,
        JsonElement server,
        string command,
        string path,
        List<ImportFinding> findings,
        List<ImportSecret> secrets,
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
            name,
            name,
            UpstreamTransportKind.Stdio,
            Enabled: false,
            Stdio: new StdioTransportOptions(
                command,
                arguments,
                environment.Count > 0 ? environment : null));

        return new ImportCandidate(name, config, findings, secrets);
    }

    private static ImportCandidate? Http(
        string name,
        JsonElement server,
        string url,
        string? declared,
        string path,
        List<ImportFinding> findings,
        List<ImportSecret> secrets,
        List<string> expansions)
    {
        var headers = ClientConfigReading.Map(server, "headers", $"{path}/headers", findings);

        Note(url, $"{path}/url", expansions);
        foreach (var entry in headers)
        {
            Note(entry.Value, $"{path}/headers/{entry.Key}", expansions);
        }

        if (ClientConfigReading.Endpoint(url, path, findings) is not { } endpoint)
        {
            return null;
        }

        // Cursor leitet den Transport aus der Adresse ab und schreibt 'type' nicht zwingend. Ein
        // ausdrückliches 'sse' ist trotzdem eine Aussage, und die wird nicht verschluckt.
        var legacySse = string.Equals(declared, "sse", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(declared, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Warning,
                "Der Eintrag nennt den Typ 'stdio', traegt aber nur eine Adresse und kein Kommando. "
                + "Uebernommen wird ein HTTP-Upstream — abgeleitet aus den vorhandenen Feldern, nicht "
                + "aus dem Typ.",
                $"{path}/type",
                "Den Typ in der Quelldatei korrigieren."));
        }

        if (legacySse)
        {
            findings.Add(new ImportFinding(
                ImportReason.Lossy,
                ImportSeverity.Warning,
                "Die Quelle nennt den abgeloesten HTTP+SSE-Transport. Uebernommen wird Streamable "
                + "HTTP mit erlaubtem Rueckfall auf SSE.",
                $"{path}/type",
                "Beim Anbieter nachsehen, ob es einen Streamable-HTTP-Endpunkt gibt."));
        }

        var environment = ClientConfigReading.Map(server, "env", $"{path}/env", findings);
        if (environment.Count > 0)
        {
            findings.Add(ClientConfigReading.ClientOnly(
                $"{path}/env",
                "Der Eintrag traegt 'env' an einem entfernten Server. Ein HTTP-Upstream startet hier "
                + "kein Programm, also gibt es keine Umgebung, in die diese Werte gehoeren.",
                "Falls die Werte zur Autorisierung dienen, gehoeren sie als Kopfzeile in 'headers'."));
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

        return new ImportCandidate(name, config, findings, secrets);
    }

    private static void Note(string value, string path, List<string> expansions)
    {
        if (CursorExpansion().IsMatch(value))
        {
            expansions.Add(path);
        }
    }
}
