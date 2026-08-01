using System.Globalization;
using System.Text;
using System.Text.Json;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>
/// Der Parser für die MCP-Server der Codex-CLI.
/// <para>
/// <b>Zuerst die unangenehme Wahrheit, weil sie die Grenze dieses Parsers ist:</b> Codex schreibt
/// seine Konfiguration als <b>TOML</b> (<c>~/.codex/config.toml</c> beziehungsweise
/// <c>.codex/config.toml</c>), nicht als JSON. Der Importweg dieses Gateways nimmt JSON entgegen —
/// <c>ConfigurationImporter</c> weist alles andere mit <c>BFR-IMP-0001</c> ab, bevor überhaupt ein
/// Parser gefragt wird. Eine <c>config.toml</c> kommt hier also nicht an, und dieser Parser tut
/// nicht so, als käme sie an: Was er liest, ist die <em>JSON-Umschrift</em> desselben Aufbaus —
/// <c>{ "mcp_servers": { "name": { "command": …, "args": […] } } }</c> —, wie sie beim Umwandeln
/// eines TOML-Dokuments entsteht. Jeder Plan sagt das ausdrücklich (<c>BFR-IMP-0002</c>,
/// Warnung), damit niemand glaubt, er habe seine <c>config.toml</c> importiert.
/// </para>
/// <para>
/// Erkannt wird an <c>mcp_servers</c> in Schlangenschrift — der Name gehört Codex; die anderen drei
/// Clients schreiben <c>mcpServers</c> oder <c>servers</c>.
/// </para>
/// <para>
/// <b>Abgebildet</b> werden <c>command</c>, <c>args</c>, <c>env</c>, <c>cwd</c> und
/// <c>tool_timeout_sec</c> für lokale Server sowie <c>url</c> und <c>http_headers</c> für entfernte.
/// <b>Erhalten als Befund</b> werden <c>startup_timeout_sec</c>, <c>enabled</c> und
/// <c>bearer_token_env_var</c> — Letzteres nennt nur den <em>Namen</em> einer Umgebungsvariablen;
/// dieses Gateway braucht den Wert, und der steht nicht in der Datei.
/// </para>
/// </summary>
public sealed class CodexImportProvider : IImportProvider
{
    /// <summary>Der Name, unter dem dieses Format gemeldet wird.</summary>
    public const string ProviderName = "codex";

    /// <summary>
    /// <c>mcp_servers</c> in Schlangenschrift. Diesen Sammelnamen schreibt von den bekannten
    /// Formaten nur Codex; er ist damit deutlicher als alles, was die drei JSON-Clients gemeinsam
    /// haben.
    /// </summary>
    public const double SnakeCaseConfidence = 0.9;

    private const string McpServers = "mcp_servers";

    /// <summary>
    /// Die Herkunft, so genau wie sie ist: der Aufbau von <c>config.toml</c>, gelesen als JSON.
    /// </summary>
    private const string Schema = "codex/config.toml (als JSON umgeschrieben)";

    /// <summary>Die Felder eines Servereintrags, die dieser Parser abbildet.</summary>
    private static readonly HashSet<string> KnownServerFields = new(StringComparer.Ordinal)
    {
        "command", "args", "env", "cwd", "url", "http_headers", "tool_timeout_sec",
    };

    /// <summary>Felder, die Codex kennt und dieses Gateway nicht abbildet.</summary>
    private static readonly Dictionary<string, string> ClientOnlyServerFields = new(StringComparer.Ordinal)
    {
        ["startup_timeout_sec"] = "wie lange Codex auf den Start des Servers wartet. Dieses Gateway "
            + "fuehrt seine eigenen Startzeiten.",
        ["enabled"] = "Codex' Ein-/Ausschalter. Hier kommt ohnehin jeder importierte Server "
            + "abgeschaltet an; ob er eingeschaltet wird, entscheidet der Betreiber.",
        ["bearer_token_env_var"] = "der NAME einer Umgebungsvariablen, aus der Codex das Bearer-Token "
            + "liest. Der Wert steht nicht in der Datei — hier fehlt er also.",
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
            using var parsed = JsonDocument.Parse(document, ClientConfigReading.ParseOptions);
            return ClientConfigReading.IsObject(parsed.RootElement, McpServers)
                ? SnakeCaseConfidence
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
                            $"Eine Codex-Konfiguration traegt '{McpServers}'; hier steht das nicht."),
                    ]);
            }

            // Die Grenze steht in jedem Plan, nicht nur in der Dokumentation: Wer hier etwas
            // importiert, hat NICHT seine config.toml importiert, sondern deren Umschrift.
            findings.Add(new ImportFinding(
                ImportReason.UnknownFormat,
                ImportSeverity.Warning,
                "Codex schreibt seine Konfiguration als TOML (~/.codex/config.toml). Dieser Importweg "
                + "nimmt JSON entgegen; gelesen wurde also die JSON-Umschrift desselben Aufbaus. Was "
                + "beim Umwandeln verloren ging — Kommentare, Reihenfolge, TOML-eigene Zahlformate — "
                + "sieht dieser Parser nicht.",
                null,
                "Den Plan Eintrag fuer Eintrag gegen die config.toml pruefen."));

            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, McpServers, StringComparison.Ordinal))
                {
                    findings.Add(ClientConfigReading.ClientOnly(
                        property.Name,
                        $"'{property.Name}' ist eine Einstellung der Codex-CLI (Modell, Provider, "
                        + "Sandbox, Freigaben) und wird nicht uebernommen. Erhalten als Befund, damit "
                        + "sie nicht unbemerkt verschwindet."));
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
    /// Der Rückweg — und zwar in <b>TOML</b>, weil das Codex' Format ist. Ein JSON-Ausschnitt wäre
    /// eine Datei, die Codex nicht lädt; er sähe nur so aus, als hätte er geholfen.
    /// <para>
    /// <b>Achtung, kein Kreisschluss:</b> Was hier herauskommt, liest der Importer oben nicht wieder
    /// ein — er nimmt JSON. Das ist die bewusst benannte Asymmetrie dieses Providers.
    /// </para>
    /// </summary>
    [NoHostExecution(
        "Schreibt eine vorhandene Konfiguration als Text im Clientformat. Kein Start, keine "
        + "Persistenz, keine Datei — das Ergebnis ist eine Zeichenkette.")]
    public static ClientExportResult Export(ImportCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<ImportFinding>();
        var config = candidate.Config;
        var toml = new StringBuilder();

        if (ClientExport.Unsupported(config, "Codex") is { } unsupported)
        {
            findings.Add(unsupported);
            return new ClientExportResult(string.Empty, findings);
        }

        toml.Append("[mcp_servers.").Append(Key(config.Slug)).Append(']').Append('\n');

        if (config.Stdio is { } stdio)
        {
            toml.Append("command = ").Append(Text(stdio.Command)).Append('\n');

            if (stdio.Arguments.Count > 0)
            {
                toml.Append("args = [")
                    .Append(string.Join(", ", stdio.Arguments.Select(Text)))
                    .Append(']')
                    .Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(stdio.WorkingDirectory))
            {
                toml.Append("cwd = ").Append(Text(stdio.WorkingDirectory)).Append('\n');
            }

            if (config.CallTimeout is { } timeout)
            {
                toml.Append("tool_timeout_sec = ")
                    .Append(timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            if (stdio.EnvironmentVariables is { Count: > 0 } environment)
            {
                toml.Append('\n').Append("[mcp_servers.").Append(Key(config.Slug)).Append(".env]")
                    .Append('\n');
                foreach (var entry in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    toml.Append(Key(entry.Key)).Append(" = ").Append(Text(entry.Value)).Append('\n');
                }
            }
        }
        else if (config.Http is { } http)
        {
            toml.Append("url = ").Append(Text(http.Endpoint.ToString())).Append('\n');

            if (config.CallTimeout is { } timeout)
            {
                toml.Append("tool_timeout_sec = ")
                    .Append(timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            if (http.Headers is { Count: > 0 } headers)
            {
                toml.Append('\n').Append("[mcp_servers.").Append(Key(config.Slug))
                    .Append(".http_headers]").Append('\n');
                foreach (var entry in headers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    toml.Append(Key(entry.Key)).Append(" = ").Append(Text(entry.Value)).Append('\n');
                }
            }

            if (http.OAuth is not null)
            {
                findings.Add(ClientExport.Drops(
                    config.Slug, "oauth", "Codex",
                    "die OAuth-Anbindung dieses Gateways. Codex kennt nur "
                    + "'bearer_token_env_var' — den Namen einer Umgebungsvariablen, nicht ein "
                    + "ausgehandeltes Token."));
            }
        }

        return new ClientExportResult(toml.ToString(), findings);
    }

    /// <summary>Ein TOML-Schlüssel: nackt, wo er darf, sonst in Anführungszeichen.</summary>
    private static string Key(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? name
            : Text(name);

    /// <summary>
    /// Eine TOML-Zeichenkette. Escaped wird nach der Regel für <c>basic strings</c>; alles
    /// Unbekannte wird als <c>\uXXXX</c> geschrieben, statt es durchzureichen.
    /// </summary>
    private static string Text(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u")
                            .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
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
                $"'{field.Name}' kennt nur Codex und wird nicht uebernommen: {explanation}"));

            if (string.Equals(field.Name, "bearer_token_env_var", StringComparison.Ordinal)
                && field.Value.ValueKind is JsonValueKind.String)
            {
                var variable = field.Value.GetString() ?? string.Empty;
                secrets.Add(new ImportSecret(
                    $"Bearer-Token aus der Umgebungsvariablen '{variable}'",
                    "Codex liest das Token beim Start aus dieser Variablen; die Quelldatei traegt "
                    + "den Wert nicht",
                    false));
                findings.Add(new ImportFinding(
                    ImportReason.MaskedValue,
                    ImportSeverity.Warning,
                    $"Die Autorisierung dieses Servers haengt an der Umgebungsvariablen "
                    + $"'{variable}'. Ihr Wert steht nicht in der Quelle und wird NICHT erraten; "
                    + "ohne ihn ist der Server hier nicht autorisiert.",
                    $"{path}/bearer_token_env_var",
                    "Den Wert hier als Kopfzeile 'Authorization: Bearer …' hinterlegen, bevor der "
                    + "Server eingeschaltet wird."));
            }
        }

        var command = ClientConfigReading.Text(server, "command");
        var url = ClientConfigReading.Text(server, "url");

        if (command is not null && url is not null)
        {
            planFindings.Add(ClientConfigReading.BothTransports(path, "command", "url"));
            planFindings.AddRange(findings);
            return null;
        }

        var timeout = Timeout(server, path, findings);

        ImportCandidate? candidate;
        if (command is not null)
        {
            candidate = Stdio(name, server, command, timeout, path, findings, secrets);
        }
        else if (url is not null)
        {
            candidate = Http(name, server, url, timeout, path, findings, secrets);
        }
        else
        {
            findings.Add(ClientConfigReading.NoTransport(path, "command", "url"));
            candidate = null;
        }

        if (candidate is null)
        {
            planFindings.AddRange(findings);
        }

        return candidate;
    }

    /// <summary>
    /// <c>tool_timeout_sec</c> — das einzige Zeitlimit von Codex, für das es hier eine Entsprechung
    /// gibt. Dass es eine gibt, wird gemeldet: Ein übernommenes Zeitlimit ist eine Verhaltensänderung
    /// gegenüber der Vorgabe dieser Instanz.
    /// </summary>
    private static TimeSpan? Timeout(JsonElement server, string path, List<ImportFinding> findings)
    {
        if (!server.TryGetProperty("tool_timeout_sec", out var value))
        {
            return null;
        }

        if (value.ValueKind is not JsonValueKind.Number || !value.TryGetDouble(out var seconds)
            || seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            findings.Add(new ImportFinding(
                ImportReason.UnknownField,
                ImportSeverity.Warning,
                "'tool_timeout_sec' ist keine brauchbare Sekundenangabe und wird nicht uebernommen.",
                $"{path}/tool_timeout_sec"));
            return null;
        }

        findings.Add(new ImportFinding(
            ImportReason.Lossy,
            ImportSeverity.Info,
            "'tool_timeout_sec' wird als Aufrufzeitlimit dieses Servers uebernommen. Codex' zweites "
            + "Zeitlimit (startup_timeout_sec) hat hier keine Entsprechung.",
            $"{path}/tool_timeout_sec"));

        return TimeSpan.FromSeconds(seconds);
    }

    private static ImportCandidate Stdio(
        string name,
        JsonElement server,
        string command,
        TimeSpan? timeout,
        string path,
        List<ImportFinding> findings,
        List<ImportSecret> secrets)
    {
        var arguments = ClientConfigReading.Arguments(server, path, findings);
        var environment = ClientConfigReading.Map(server, "env", $"{path}/env", findings);
        var workingDirectory = ClientConfigReading.Text(server, "cwd");

        var config = new UpstreamServerConfig(
            name,
            name,
            UpstreamTransportKind.Stdio,
            Enabled: false,
            Stdio: new StdioTransportOptions(
                command,
                arguments,
                environment.Count > 0 ? environment : null,
                workingDirectory),
            CallTimeout: timeout);

        return new ImportCandidate(name, config, findings, secrets);
    }

    private static ImportCandidate? Http(
        string name,
        JsonElement server,
        string url,
        TimeSpan? timeout,
        string path,
        List<ImportFinding> findings,
        List<ImportSecret> secrets)
    {
        var headers = ClientConfigReading.Map(server, "http_headers", $"{path}/http_headers", findings);

        if (ClientConfigReading.Endpoint(url, path, findings) is not { } endpoint)
        {
            return null;
        }

        var config = new UpstreamServerConfig(
            name,
            name,
            UpstreamTransportKind.StreamableHttp,
            Enabled: false,
            Http: new HttpTransportOptions(
                endpoint,
                headers.Count > 0 ? headers : null,
                AllowLegacySse: false),
            CallTimeout: timeout);

        return new ImportCandidate(name, config, findings, secrets);
    }
}
