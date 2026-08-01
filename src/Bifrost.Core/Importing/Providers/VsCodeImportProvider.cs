using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>
/// Der Parser für <c>.vscode/mcp.json</c>, <c>mcp.json</c> auf Benutzerebene und den Block
/// <c>mcp</c> in <c>settings.json</c>.
/// <para>
/// <b>VS Code ist der eigenständigste der vier Formate</b> und deshalb auch der, den man sicher
/// erkennt: Der Sammelname heißt <c>servers</c> statt <c>mcpServers</c>, daneben stehen
/// <c>inputs</c> (die Eingabeaufforderungen, aus denen <c>${input:…}</c> gespeist wird) und
/// <c>sandbox</c> (Datei- und Netzgrenzen je Server).
/// </para>
/// <para>
/// <b>Zwei Dinge gehen hier verloren, und beide sind sicherheitsrelevant — deshalb werden sie
/// benannt, nicht geglättet:</b>
/// </para>
/// <list type="number">
/// <item><c>inputs</c>: Ein <c>${input:api-key}</c> ist <em>kein Wert</em>, sondern eine Frage, die
/// VS Code beim Start stellt. Dieses Gateway fragt niemanden. Was dort erfragt wurde, fehlt hier —
/// und bei <c>"password": true</c> ist es ein Zugangsdatum.</item>
/// <item><c>sandbox</c>: Die Quelle hat diesen Server auf bestimmte Pfade und Domänen beschränkt.
/// Diese Grenze reist nicht mit. Ein Import, der sie stillschweigend fallen lässt, macht aus einem
/// eingehegten Server einen freien.</item>
/// </list>
/// <para>
/// <b>Er liest nichts nach</b> — insbesondere kein <c>envFile</c> und keine Datei aus
/// <c>sandbox</c>.
/// </para>
/// </summary>
public sealed partial class VsCodeImportProvider : IImportProvider
{
    /// <summary>Der Name, unter dem dieses Format gemeldet wird.</summary>
    public const string ProviderName = "vscode";

    /// <summary>
    /// <c>servers</c> zusammen mit einem Merkmal, das nur VS Code schreibt: <c>inputs</c>,
    /// <c>sandbox</c>, <c>envFile</c>, <c>dev</c>, <c>sandboxEnabled</c> oder <c>${input:…}</c>.
    /// </summary>
    public const double ServersConfidence = 0.9;

    /// <summary>
    /// Der Block <c>mcp</c> in einer <c>settings.json</c> — dort steht <c>servers</c> eine Ebene
    /// tiefer. Etwas höher als <see cref="ServersConfidence"/>, weil diese Schachtelung von den vier
    /// Clients nur VS Code hat.
    /// </summary>
    public const double SettingsConfidence = 0.92;

    private const string Servers = "servers";

    private const string Inputs = "inputs";

    private const string Sandbox = "sandbox";

    private const string SettingsBlock = "mcp";

    private const string FileSchema = "vscode/mcp.json";

    private const string SettingsSchema = "vscode/settings.json#mcp";

    /// <summary>Die Felder eines Servereintrags, die dieser Parser abbildet.</summary>
    private static readonly HashSet<string> KnownServerFields = new(StringComparer.Ordinal)
    {
        "type", "command", "args", "cwd", "env", "url", "headers",
    };

    /// <summary>Felder, die VS Code kennt und dieses Gateway nicht abbildet.</summary>
    private static readonly Dictionary<string, string> ClientOnlyServerFields = new(StringComparer.Ordinal)
    {
        ["envFile"] = "ein Verweis auf eine Datei mit Umgebungsvariablen. Diese Datei wird NICHT "
            + "gelesen — der Import liest nichts nach. Die Werte fehlen hier also.",
        ["dev"] = "der Entwicklungsmodus (watch/debug). Er beschreibt, wie VS Code den Server beim "
            + "Programmieren neu startet.",
        ["sandboxEnabled"] = "der Schalter fuer VS Codes Sandbox. Die dort gesetzten Datei- und "
            + "Netzgrenzen gelten hier NICHT — der Server laeuft ohne sie, wenn er eingeschaltet wird.",
        ["oauth"] = "VS Codes OAuth-Angaben. Dieses Gateway fuehrt seine eigene OAuth-Anbindung; "
            + "uebernommen wird sie nicht.",
        ["gallery"] = "die Herkunft aus VS Codes Serverkatalog.",
        ["version"] = "die Versionsangabe aus VS Codes Serverkatalog.",
    };

    /// <summary>Ein Verweis auf eine Eingabeaufforderung: <c>${input:api-key}</c>.</summary>
    [GeneratedRegex(@"\$\{input:([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex InputReference();

    /// <summary>VS Codes übrige Ersetzungen — Arbeitsbereich, Umgebung, Pfadtrenner.</summary>
    [GeneratedRegex(
        @"\$\{(env:[A-Za-z_][A-Za-z0-9_]*|userHome|workspaceFolder|workspaceFolderBasename"
        + @"|pathSeparator|/)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceExpansion();

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

            if (ClientConfigReading.IsObject(root, SettingsBlock)
                && ClientConfigReading.IsObject(root.GetProperty(SettingsBlock), Servers))
            {
                return SettingsConfidence;
            }

            return ClientConfigReading.IsObject(root, Servers) && HasVsCodeTrait(root, document)
                ? ServersConfidence
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
            var scope = root;
            var prefix = string.Empty;
            schema = FileSchema;

            if (root.ValueKind is JsonValueKind.Object
                && ClientConfigReading.IsObject(root, SettingsBlock)
                && ClientConfigReading.IsObject(root.GetProperty(SettingsBlock), Servers))
            {
                scope = root.GetProperty(SettingsBlock);
                prefix = SettingsBlock + "/";
                schema = SettingsSchema;
            }

            if (!ClientConfigReading.IsObject(scope, Servers))
            {
                return new ImportPlan(
                    new ImportSource(ProviderName, null, Recognize(text), originPath),
                    [],
                    [
                        ClientConfigReading.WrongShape(
                            $"Eine VS-Code-Konfiguration traegt '{Servers}' als Objekt; hier steht das "
                            + "nicht."),
                    ]);
            }

            var inputs = ReadInputs(scope, prefix, findings);
            ReadRoot(scope, prefix, findings);

            foreach (var server in ClientConfigReading.Servers(
                scope.GetProperty(Servers), prefix + Servers, findings))
            {
                var candidate = ReadServer(server.Name, server.Value, server.Path, inputs, findings);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
        }

        return new ImportPlan(
            new ImportSource(ProviderName, schema, Recognize(text), originPath),
            candidates,
            findings);
    }

    /// <summary>
    /// Der Rückweg in eine <c>.vscode/mcp.json</c>. VS Code ist das einzige der vier Formate mit
    /// einem dokumentierten <c>cwd</c> — hier geht deshalb am wenigsten verloren.
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

        if (ClientExport.Unsupported(config, "VS Code") is { } unsupported)
        {
            findings.Add(unsupported);
        }
        else if (config.Stdio is { } stdio)
        {
            entry["type"] = "stdio";
            entry["command"] = stdio.Command;
            if (ClientExport.List(stdio.Arguments) is { } args)
            {
                entry["args"] = args;
            }

            if (!string.IsNullOrWhiteSpace(stdio.WorkingDirectory))
            {
                entry["cwd"] = stdio.WorkingDirectory;
            }

            if (ClientExport.Map(stdio.EnvironmentVariables) is { } environment)
            {
                entry["env"] = environment;
            }
        }
        else if (config.Http is { } http)
        {
            entry["type"] = "http";
            entry["url"] = http.Endpoint.ToString();
            if (ClientExport.Map(http.Headers) is { } headers)
            {
                entry["headers"] = headers;
            }

            if (http.OAuth is not null)
            {
                findings.Add(ClientExport.Drops(
                    config.Slug, "oauth", "VS Code",
                    "die OAuth-Anbindung dieses Gateways; VS Code handelt seine Autorisierung selbst "
                    + "aus."));
            }
        }

        var wrapper = new JsonObject { [Servers] = new JsonObject { [config.Slug] = entry } };
        return new ClientExportResult(wrapper.ToJsonString(ClientExport.Pretty), findings);
    }

    /// <summary>
    /// Ein Merkmal, das nur VS Code schreibt. Ohne eines davon ist ein Dokument mit <c>servers</c>
    /// nicht von einer generischen Konfiguration zu unterscheiden — und dann meldet sich dieser
    /// Parser nicht.
    /// </summary>
    private static bool HasVsCodeTrait(JsonElement root, string document)
    {
        if (ClientConfigReading.IsArray(root, Inputs) || ClientConfigReading.IsObject(root, Sandbox))
        {
            return true;
        }

        if (InputReference().IsMatch(document))
        {
            return true;
        }

        return root.GetProperty(Servers).EnumerateObject().Any(server =>
            ClientConfigReading.Has(server.Value, "envFile")
            || ClientConfigReading.Has(server.Value, "dev")
            || ClientConfigReading.Has(server.Value, "sandboxEnabled")
            || ClientConfigReading.Has(server.Value, "gallery"));
    }

    /// <summary>
    /// Die Eingabeaufforderungen. Zurück kommt, welche Kennung ein Zugangsdatum erfragt — der Wert
    /// selbst steht nirgends, VS Code fragt ihn beim Start ab.
    /// </summary>
    private static Dictionary<string, bool> ReadInputs(
        JsonElement scope, string prefix, List<ImportFinding> findings)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!ClientConfigReading.IsArray(scope, Inputs))
        {
            return result;
        }

        foreach (var input in scope.GetProperty(Inputs).EnumerateArray())
        {
            if (input.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            var id = ClientConfigReading.Text(input, "id");
            if (id is null)
            {
                continue;
            }

            var isPassword = input.TryGetProperty("password", out var password)
                && password.ValueKind is JsonValueKind.True;
            var type = ClientConfigReading.Text(input, "type");
            result[id] = isPassword
                || string.Equals(type, "promptPassword", StringComparison.Ordinal);
        }

        findings.Add(ClientConfigReading.ClientOnly(
            prefix + Inputs,
            $"Die Quelle beschreibt {result.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            + "Eingabeaufforderung(en). VS Code fragt diese Werte beim Start ab; dieses Gateway fragt "
            + "niemanden — was dort erfragt wurde, muss hier als Wert hinterlegt werden.",
            "Die betroffenen Werte vor dem Einschalten des Servers eintragen."));

        return result;
    }

    /// <summary>Was neben den Servern steht.</summary>
    private static void ReadRoot(JsonElement scope, string prefix, List<ImportFinding> findings)
    {
        foreach (var property in scope.EnumerateObject())
        {
            if (property.Name is Servers or Inputs)
            {
                continue;
            }

            if (string.Equals(property.Name, Sandbox, StringComparison.Ordinal))
            {
                findings.Add(new ImportFinding(
                    ImportReason.ClientOnlyField,
                    ImportSeverity.Risk,
                    "Die Quelle beschraenkt ihre Server mit 'sandbox' auf bestimmte Pfade und "
                    + "Domaenen. Diese Grenze reist nicht mit: Hier laufen die Server ohne sie. Aus "
                    + "einem eingehegten Server wird durch diesen Import ein freier — das ist eine "
                    + "Entscheidung, die der Betreiber treffen muss, keine, die der Import "
                    + "wegnimmt.",
                    prefix + Sandbox,
                    "Vor dem Einschalten pruefen, ob dieser Server hier eine Isolation braucht "
                    + "(Container statt nativer Start)."));
                continue;
            }

            findings.Add(ClientConfigReading.Unknown(prefix + property.Name, property.Name));
        }
    }

    private static ImportCandidate? ReadServer(
        string name,
        JsonElement server,
        string path,
        Dictionary<string, bool> inputs,
        List<ImportFinding> planFindings)
    {
        var findings = new List<ImportFinding>();
        var secrets = new List<ImportSecret>();

        foreach (var field in server.EnumerateObject())
        {
            if (KnownServerFields.Contains(field.Name))
            {
                continue;
            }

            findings.Add(ClientOnlyServerFields.TryGetValue(field.Name, out var explanation)
                ? ClientConfigReading.ClientOnly(
                    $"{path}/{field.Name}",
                    $"'{field.Name}' kennt nur VS Code und wird nicht uebernommen: {explanation}")
                : ClientConfigReading.Unknown($"{path}/{field.Name}", field.Name));

            if (string.Equals(field.Name, "envFile", StringComparison.Ordinal))
            {
                secrets.Add(new ImportSecret(
                    $"Umgebungsdatei aus '{path}/envFile'",
                    "VS Code laedt aus dieser Datei Umgebungsvariablen; dieser Import oeffnet sie "
                    + "ausdruecklich nicht",
                    false));
            }
        }

        var command = ClientConfigReading.Text(server, "command");
        var url = ClientConfigReading.Text(server, "url");
        var declared = ClientConfigReading.Text(server, "type")?.Trim().ToLowerInvariant();

        if (command is not null && url is not null)
        {
            planFindings.Add(ClientConfigReading.BothTransports(path, "command", "url"));
            planFindings.AddRange(findings);
            return null;
        }

        var references = new List<string>();
        ImportCandidate? candidate;
        if (command is not null)
        {
            candidate = Stdio(name, server, command, path, findings, references);
        }
        else if (url is not null)
        {
            candidate = Http(name, server, url, declared, path, findings, references);
        }
        else
        {
            findings.Add(ClientConfigReading.NoTransport(path, "command", "url"));
            candidate = null;
        }

        ReportReferences(references, inputs, path, findings, secrets);

        if (candidate is null)
        {
            planFindings.AddRange(findings);
            return null;
        }

        return candidate with { Findings = findings, Secrets = secrets };
    }

    /// <summary>
    /// Die gefundenen Ersetzungen. <c>${input:…}</c> mit <c>password: true</c> ist ein Zugangsdatum,
    /// das die Quelle <b>nicht</b> mitbringt — es wird als solches gemeldet und nicht erraten.
    /// </summary>
    private static void ReportReferences(
        List<string> references,
        Dictionary<string, bool> inputs,
        string path,
        List<ImportFinding> findings,
        List<ImportSecret> secrets)
    {
        foreach (var reference in references)
        {
            var separator = reference.IndexOf('=', StringComparison.Ordinal);
            var where = reference[..separator];
            var value = reference[(separator + 1)..];

            var match = InputReference().Match(value);
            if (!match.Success)
            {
                findings.Add(new ImportFinding(
                    ImportReason.Lossy,
                    ImportSeverity.Warning,
                    "Der Wert benutzt eine Ersetzung von VS Code (${workspaceFolder}, ${env:…}, "
                    + "${userHome}). Sie zeigt auf die Umgebung des Editors; dieses Gateway hat diese "
                    + "Umgebung nicht und ersetzt ausdruecklich nichts.",
                    where,
                    "Den Wert vor dem Einschalten durch den hier gueltigen ersetzen."));
                continue;
            }

            var id = match.Groups[1].Value;
            var isPassword = inputs.TryGetValue(id, out var secret) && secret;

            findings.Add(new ImportFinding(
                ImportReason.MaskedValue,
                ImportSeverity.Warning,
                $"Der Wert verweist auf die Eingabeaufforderung '{id}'. VS Code fragt sie beim Start "
                + "ab; die Quelle traegt den Wert also gar nicht. Er wird NICHT erraten"
                + (isPassword ? " — laut Quelle ist es ein Zugangsdatum." : "."),
                where,
                "Den Wert auf dieser Instanz eintragen, bevor der Server eingeschaltet wird."));

            if (isPassword)
            {
                secrets.Add(new ImportSecret(
                    $"Eingabeaufforderung '{id}' (verwendet in {where})",
                    "die Quelle markiert diese Eingabe als Zugangsdatum (password); ein Wert steht "
                    + "nicht in der Datei",
                    false));
            }
        }
    }

    private static ImportCandidate Stdio(
        string name,
        JsonElement server,
        string command,
        string path,
        List<ImportFinding> findings,
        List<string> references)
    {
        var arguments = ClientConfigReading.Arguments(server, path, findings);
        var environment = ClientConfigReading.Map(server, "env", $"{path}/env", findings);
        var workingDirectory = ClientConfigReading.Text(server, "cwd");

        Note(command, $"{path}/command", references);
        for (var index = 0; index < arguments.Count; index++)
        {
            Note(arguments[index], $"{path}/args[{index}]", references);
        }

        foreach (var entry in environment)
        {
            Note(entry.Value, $"{path}/env/{entry.Key}", references);
        }

        if (workingDirectory is not null)
        {
            Note(workingDirectory, $"{path}/cwd", references);
        }

        var config = new UpstreamServerConfig(
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
        string? declared,
        string path,
        List<ImportFinding> findings,
        List<string> references)
    {
        var headers = ClientConfigReading.Map(server, "headers", $"{path}/headers", findings);

        Note(url, $"{path}/url", references);
        foreach (var entry in headers)
        {
            Note(entry.Value, $"{path}/headers/{entry.Key}", references);
        }

        if (ClientConfigReading.Endpoint(url, path, findings) is not { } endpoint)
        {
            return null;
        }

        var legacySse = string.Equals(declared, "sse", StringComparison.Ordinal);
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

    /// <summary>
    /// Merkt sich eine Fundstelle mit Ersetzung als <c>pfad=wert</c>, ohne sie aufzulösen.
    /// </summary>
    private static void Note(string value, string path, List<string> references)
    {
        if (InputReference().IsMatch(value) || WorkspaceExpansion().IsMatch(value))
        {
            references.Add($"{path}={value}");
        }
    }
}
