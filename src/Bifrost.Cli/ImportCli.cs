using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Bifrost.Cli;

/// <summary>
/// Die Importbefehle (M4, WP4.3): eine fremde MCP-Konfiguration ansehen, proben und übernehmen.
///
/// <para>
/// <b>Adapter, keine zweite Fachlogik.</b> Was ein bekanntes Format ist, was ein Risiko ist und ob
/// ein Plan anwendbar ist, entscheidet der Dienst hinter der Fassade. Diese Klasse liest die Datei,
/// zeigt das Ergebnis und übersetzt es in einen Exit-Code — mehr nicht. Insbesondere beurteilt sie
/// nichts selbst: Ein zweites Urteil hier wäre eines, das irgendwann vom ersten abweicht.
/// </para>
///
/// <para>
/// <b>Die Datei wird HIER gelesen, nicht dort.</b> Der Dienst bekommt den Inhalt im Rumpf. Ein
/// Endpunkt, der einen Pfad entgegennimmt und ihn serverseitig öffnet, wäre ein Werkzeug zum
/// Auslesen fremder Dateien — der Pfad reist deshalb höchstens als <em>Herkunftsangabe</em> mit,
/// damit ein Befund seine Fundstelle nennen kann.
/// </para>
///
/// <para>
/// <b>Zweistufig, wie Restore.</b> <c>preview</c> merkt beim Dienst einen Plan vor und gibt ein
/// Handle zurück; <c>apply</c> macht beides in einem Befehl, zeigt aber dazwischen, was passieren
/// wird. Der Plan selbst enthält nie Werte — er liegt beim Dienst, und nur das Handle reist.
/// </para>
/// </summary>
public sealed class ImportCli
{
    private const string Base = "api/v1/import";

    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _jsonOutput;

    public ImportCli(
        HttpClient client, TextReader input, TextWriter output, TextWriter error, bool jsonOutput)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _input = input;
        _output = output;
        _error = error;
        _jsonOutput = jsonOutput;
    }

    public const string UsageText =
        """
          bifrost import preview <datei|-> [--origin <pfad>]
          bifrost import apply   <datei|-> [--only <name,name>] [--isolation host|container]
                                           [--image <image>] [--confirm-risks] [--probe]
                                           [--dry-run] [--origin <pfad>]

        Die Datei wird LOKAL gelesen und im Rumpf uebertragen; --origin ist nur eine
        Herkunftsangabe fuer die Befunde und wird vom Gateway nie geoeffnet.
        Die Vorschau traegt keine Zugangsdaten: von wertetragenden Feldern reisen Namen und
        Anzahlen, nie Inhalte. Die Werte bleiben beim Dienst, hinter einem Handle.
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);

        var command = args.ToArray();
        return command switch
        {
            ["preview", var file, .. var options] => await PreviewAsync(file, options, ct),
            ["apply", var file, .. var options] => await ApplyAsync(file, options, ct),
            _ => await UsageAsync(),
        };
    }

    // ── Vorschau ────────────────────────────────────────────────────────────────────────────────

    private async Task<int> PreviewAsync(
        string file, IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var origin = parsed.Value("--origin");
        parsed.EnsureNoRest();

        var (status, preview) = await RequestPreviewAsync(file, origin, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, preview);
        }

        await WriteAsync(preview, () => Describe(preview));
        return preview.GetProperty("canApply").GetBoolean()
            ? GatewayCli.Success
            : GatewayCli.GatewayError;
    }

    // ── Übernahme ───────────────────────────────────────────────────────────────────────────────

    private async Task<int> ApplyAsync(
        string file, IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var origin = parsed.Value("--origin");
        var only = parsed.Value("--only");
        var isolation = parsed.Value("--isolation");
        var image = parsed.Value("--image");
        var confirmRisks = parsed.Flag("--confirm-risks");
        var probe = parsed.Flag("--probe");
        var dryRun = parsed.Flag("--dry-run");
        parsed.EnsureNoRest();

        var (status, preview) = await RequestPreviewAsync(file, origin, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, preview);
        }

        await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(preview) : Describe(preview));

        if (!preview.GetProperty("canApply").GetBoolean())
        {
            await _error.WriteLineAsync(
                "Der Plan ist nicht anwendbar. Es wurde nichts angelegt.");
            return GatewayCli.GatewayError;
        }

        var selected = Selection(preview, only, isolation, image);
        if (selected.Count == 0)
        {
            await _error.WriteLineAsync(only is null
                ? "Kein Server dieser Quelle ist anwendbar. Es wurde nichts angelegt."
                : $"--only '{only}' trifft keinen Server aus dieser Quelle.");
            return GatewayCli.UsageError;
        }

        var token = preview.GetProperty("token").GetString();

        if (probe)
        {
            var probeExit = await ProbeAsync(token, selected, ct);
            if (probeExit != GatewayCli.Success)
            {
                return probeExit;
            }
        }

        if (dryRun)
        {
            await _output.WriteLineAsync(
                "--dry-run: es wurde nichts angelegt. Das Handle verfaellt von selbst.");
            return GatewayCli.Success;
        }

        var (commitStatus, result) = await SendAsync(
            $"{Base}/commit",
            new
            {
                token,
                confirmRisks,
                servers = selected,
            },
            ct);
        if (commitStatus is not HttpStatusCode.OK)
        {
            return await FailAsync(commitStatus, result);
        }

        await WriteAsync(result, () =>
            $"Uebernommen: {result.GetProperty("count").GetInt32()} Server"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                result.GetProperty("imported").EnumerateArray()
                    .Select(item => $"  - {item.GetProperty("slug").GetString()} ({item.GetProperty("id").GetString()})"))
            + Skipped(result));
        return GatewayCli.Success;
    }

    private async Task<int> ProbeAsync(
        string? token, IReadOnlyList<SelectedServer> selected, CancellationToken ct)
    {
        var failures = 0;
        foreach (var server in selected)
        {
            var (status, body) = await SendAsync(
                $"{Base}/probe", new { token, sourceName = server.SourceName }, ct);
            if (status is not HttpStatusCode.OK)
            {
                return await FailAsync(status, body);
            }

            var ok = body.GetProperty("success").GetBoolean();
            if (!ok)
            {
                failures++;
            }

            await _output.WriteLineAsync(
                $"  Probe {body.GetProperty("slug").GetString()}: "
                + (ok
                    ? $"erreichbar, {body.GetProperty("toolCount").GetInt32()} Werkzeuge"
                    : "FEHLGESCHLAGEN — " + Text(body, "error")));
        }

        if (failures > 0)
        {
            await _error.WriteLineAsync(
                $"{failures} von {selected.Count} Servern haben die Probe nicht bestanden. Es wurde "
                + "nichts angelegt.");
            return GatewayCli.GatewayError;
        }

        return GatewayCli.Success;
    }

    // ── Auswahl ─────────────────────────────────────────────────────────────────────────────────

    private static List<SelectedServer> Selection(
        JsonElement preview, string? only, string? isolation, string? image)
    {
        var wanted = only is null
            ? null
            : only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

        return [.. preview.GetProperty("candidates").EnumerateArray()
            .Select(candidate => new
            {
                SourceName = candidate.GetProperty("sourceName").GetString()!,
                Slug = candidate.GetProperty("slug").GetString()!,
                CanApply = Applicable(candidate),
            })
            // --only nimmt beides an: den Namen aus der Quelle und den Slug, unter dem der Server
            // hier heissen wird. Wer eine Vorschau vor sich hat, liest den einen oder den anderen —
            // ihn zu zwingen, den richtigen zu erraten, waere Schikane.
            //
            // Ohne --only werden die gesperrten Eintraege hier bereits ausgelassen: Der Dienst
            // wuerde eine AUSDRUECKLICHE Wahl eines gesperrten Servers abweisen, und diese Wahl hat
            // hier niemand getroffen. Mit --only bleiben sie drin — dann soll die Absage kommen,
            // statt dass die CLI den benannten Server stillschweigend uebergeht.
            .Where(entry => wanted is null
                ? entry.CanApply
                : wanted.Contains(entry.SourceName) || wanted.Contains(entry.Slug))
            .Select(entry => new SelectedServer(entry.SourceName, Normalize(isolation), image))];
    }

    /// <summary>
    /// <c>host</c> und <c>container</c> von der Kommandozeile in die Schreibweise des Vertrags. Der
    /// Dienst nimmt beides an; die Umschrift steht hier, damit die Anfrage aussieht wie jede andere.
    /// </summary>
    private static string? Normalize(string? isolation) => isolation?.ToLowerInvariant() switch
    {
        "host" => "Host",
        "container" => "Container",
        null => null,
        _ => isolation,
    };

    private sealed record SelectedServer(string SourceName, string? Isolation, string? ContainerImage);

    // ── Ausgabe ─────────────────────────────────────────────────────────────────────────────────

    private static string Describe(JsonElement preview)
    {
        var text = new StringBuilder();
        var source = preview.GetProperty("source");
        text.Append("Format: ").Append(source.GetProperty("provider").GetString())
            .Append(" (Sicherheit ")
            .Append(source.GetProperty("confidence").GetDouble().ToString("0.00", CultureInfo.InvariantCulture))
            .AppendLine(")");

        foreach (var candidate in preview.GetProperty("candidates").EnumerateArray())
        {
            var transport = candidate.GetProperty("transport");
            text.Append(Environment.NewLine)
                .Append("  ").Append(candidate.GetProperty("sourceName").GetString())
                .Append("  ->  ").Append(candidate.GetProperty("slug").GetString())
                .Append("  [").Append(transport.GetProperty("kind").GetString()).Append(']')
                // Die Zeile, an der ein Betreiber den Teilimport sieht: Was hier gesperrt ist, wird
                // nicht angelegt; die uebrigen Eintraege derselben Datei schon.
                .AppendLine(Applicable(candidate) ? string.Empty : "  — NICHT ANWENDBAR");

            foreach (var (property, label) in ((string, string)[])
                     [("program", "Programm"), ("endpoint", "Ziel"), ("specLocation", "Spezifikation"),
                      ("containerImage", "Image"), ("isolationMode", "Isolation")])
            {
                if (Text(transport, property) is { Length: > 0 } value)
                {
                    text.Append("      ").Append(label).Append(": ").AppendLine(value);
                }
            }

            if (transport.TryGetProperty("argumentCount", out var arguments)
                && arguments.GetInt32() > 0)
            {
                text.Append("      Argumente: ").Append(arguments.GetInt32())
                    .AppendLine(" (Werte bleiben beim Dienst)");
            }

            foreach (var (property, label) in ((string, string)[])
                     [("environmentNames", "Umgebung"), ("headerNames", "Header"), ("secretNames", "Secrets")])
            {
                if (transport.TryGetProperty(property, out var names)
                    && names.ValueKind is JsonValueKind.Array
                    && names.GetArrayLength() > 0)
                {
                    text.Append("      ").Append(label).Append(": ")
                        .AppendLine(string.Join(", ", names.EnumerateArray().Select(n => n.GetString())));
                }
            }

            AppendFindings(text, candidate, "findings", "      ");
            AppendSecrets(text, candidate);
        }

        AppendFindings(text, preview, "findings", "  ");

        var candidates = preview.GetProperty("candidates").EnumerateArray().ToList();
        var applicable = candidates.Count(Applicable);

        text.Append(Environment.NewLine)
            .AppendLine(preview.GetProperty("canApply").GetBoolean()
                ? applicable == candidates.Count
                    ? $"Anwendbar: alle {candidates.Count} Server."
                    : $"Teilweise anwendbar: {applicable} von {candidates.Count} Servern. Die "
                        + "uebrigen sind oben als NICHT ANWENDBAR gekennzeichnet und werden "
                        + "uebergangen."
                : "NICHT anwendbar — siehe die Befunde der Stufe Error.");

        var confirmations = preview.GetProperty("requiresConfirmation");
        if (confirmations.GetArrayLength() > 0)
        {
            text.AppendLine(
                "Verlangt eine ausdrueckliche Bestaetigung (--confirm-risks):");
            foreach (var finding in confirmations.EnumerateArray())
            {
                text.Append("  ! ").Append(finding.GetProperty("code").GetString()).Append("  ")
                    .AppendLine(finding.GetProperty("summary").GetString());
            }
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendFindings(
        StringBuilder text, JsonElement parent, string property, string indent)
    {
        if (!parent.TryGetProperty(property, out var findings)
            || findings.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var finding in findings.EnumerateArray())
        {
            text.Append(indent)
                .Append(finding.GetProperty("severity").GetString()?.ToUpperInvariant().PadRight(8))
                .Append(finding.GetProperty("code").GetString()).Append("  ")
                .AppendLine(finding.GetProperty("summary").GetString());
        }
    }

    private static void AppendSecrets(StringBuilder text, JsonElement candidate)
    {
        if (!candidate.TryGetProperty("secrets", out var secrets)
            || secrets.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var secret in secrets.EnumerateArray())
        {
            // Ort und Begruendung, nie der Wert — so kommt es aus dem Dienst, und so bleibt es.
            text.Append("      Zugangsdatum: ").Append(secret.GetProperty("location").GetString())
                .Append(" (").Append(secret.GetProperty("looked").GetString()).Append("), Wert in der Quelle: ")
                .AppendLine(secret.GetProperty("valuePresent").GetBoolean() ? "ja" : "nein");
        }
    }

    /// <summary>
    /// Was der Dienst übergangen hat, samt Grund. <b>Wird immer ausgegeben, wenn es etwas gibt:</b>
    /// Ein Teilimport, dessen Differenz nur in der Vorschau stand, sieht am Ende aus wie ein
    /// vollständiger.
    /// </summary>
    private static string Skipped(JsonElement result)
    {
        if (!result.TryGetProperty("skipped", out var skipped)
            || skipped.ValueKind is not JsonValueKind.Array
            || skipped.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        return Environment.NewLine
            + "Uebergangen (nicht anwendbar):"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                skipped.EnumerateArray().Select(item =>
                    $"  ! {item.GetProperty("sourceName").GetString()}  "
                    + string.Join(
                        ", ",
                        item.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()))));
    }

    /// <summary>
    /// Ob dieser Kandidat angelegt würde. Fehlt die Angabe, gilt „ja" — ein älterer Dienst kennt sie
    /// nicht, und die CLI erfindet dafür keine Sperre.
    /// </summary>
    private static bool Applicable(JsonElement candidate)
        => !candidate.TryGetProperty("canApply", out var value)
            || value.ValueKind is not JsonValueKind.False;

    private static string? Text(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task WriteAsync(JsonElement json, Func<string> human)
        => await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(json) : human());

    private async Task<int> UsageAsync()
    {
        await _error.WriteLineAsync(UsageText);
        return GatewayCli.UsageError;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, JsonElement Body)> RequestPreviewAsync(
        string file, string? origin, CancellationToken ct)
    {
        var document = file == "-"
            ? await _input.ReadToEndAsync(ct)
            : await File.ReadAllTextAsync(Path.GetFullPath(file), ct);

        var path = origin is null
            ? $"{Base}/preview"
            : $"{Base}/preview?originPath={Uri.EscapeDataString(origin)}";

        // Bewusst text/plain: Ob das Dokument gueltiges JSON ist, ist genau die Frage, die der
        // Dienst beantwortet. Sie hier vorwegzunehmen verschoebe den Fehler in den Transport, wo er
        // wie ein Netzproblem aussieht.
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(document, Encoding.UTF8, "text/plain"),
        };
        return await SendAsync(request, ct);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, RequestJson), Encoding.UTF8, "application/json"),
        };
        return await SendAsync(request, ct);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _client.SendAsync(request, ct);
        if (response.Content.Headers.ContentLength == 0
            || response.StatusCode is HttpStatusCode.NoContent)
        {
            return (response.StatusCode, default);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return (response.StatusCode, await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct));
    }

    /// <summary>
    /// Der Exit-Code einer abgelehnten Anfrage. Er hängt an der Kennung im Rumpf, nicht am
    /// HTTP-Status: <c>409</c> heißt „bestätige das" oder „das Handle ist weg", und das sind zwei
    /// verschiedene nächste Handgriffe.
    /// </summary>
    private async Task<int> FailAsync(HttpStatusCode status, JsonElement body)
    {
        var error = body.ValueKind is JsonValueKind.Object
            && body.TryGetProperty("error", out var value)
            && value.ValueKind is JsonValueKind.Object
                ? value
                : default;

        var code = error.ValueKind is JsonValueKind.Object ? Text(error, "code") : null;
        var message = error.ValueKind is JsonValueKind.Object
            ? Text(error, "message")
            : body.ValueKind is JsonValueKind.Undefined
                ? $"Gateway-Fehler {(int)status} ({status})."
                : JsonSerializer.Serialize(body, PrettyJson);

        await _error.WriteLineAsync(message);

        return code switch
        {
            "usage" or "content-type" or "too-large" => GatewayCli.UsageError,
            "confirmation-required" => GatewayCli.ApprovalRequired,
            "handle-unknown" or "conflict" => GatewayCli.GatewayError,
            "document-invalid" => GatewayCli.GatewayError,
            _ => status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GatewayCli.AuthorizationError,
                HttpStatusCode.NotFound => GatewayCli.NotFound,
                HttpStatusCode.TooManyRequests => GatewayCli.GatewayError,
                _ => GatewayCli.GatewayError,
            },
        };
    }
}
