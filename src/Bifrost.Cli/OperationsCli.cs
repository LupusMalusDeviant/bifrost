using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Bifrost.Cli;

/// <summary>
/// Die Betriebsbefehle: Sicherung, Wiederherstellung, Diagnose, Konfiguration und der Ausweg aus
/// <c>BFR-DB-0101</c> (M2, WP2.7).
/// <para>
/// <b>Adapter, keine zweite Fachlogik.</b> Was ein gültiges Archiv ist, wann ein Restore anwendbar
/// ist und wie ein Diagnosebefund zu bewerten ist, entscheiden die Dienste hinter der REST-Fassade.
/// Diese Klasse übersetzt Kommandozeile in Anfragen und Antworten in Text und <b>Exit-Codes</b> —
/// und der Exit-Code ist die einzige Regel, die ihr gehört.
/// </para>
/// <para>
/// <b>Pfade gelten auf dem Rechner, auf dem der Gateway läuft.</b> Ein Archiv ist schnell mehrere
/// Gigabyte groß und enthält den Key-Ring; es durch die HTTP-Fassade zu schleusen hieße, es
/// zusätzlich durch jeden Proxy und jeden Zwischenspeicher auf dem Weg zu tragen. Ausnahme ist der
/// Konfigurationsexport: Der ist eine JSON-Nutzlast und wird deshalb <i>lokal</i> geschrieben.
/// </para>
/// </summary>
public sealed class OperationsCli
{
    // ── Exit-Codes: M2-Vertrag §4, Tabelle „Exit-Codes (CLI, für alle Operations-Befehle gleich)".
    //
    // Bewusst hier noch einmal als Konstanten und nicht als Verweis auf
    // Bifrost.Abstractions.Operations.OperationsExitCode: Dieses Programm ist der oeffentliche
    // HTTP-Client und haelt keine Projektreferenz auf die Serverwelt (siehe Bifrost.Cli.csproj).
    // Die Werte sind das oeffentliche Verhalten der CLI; OperationsExitCodeTests haelt sie an die
    // Vertragstabelle.
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int UsageError = 2;
    public const int DiagnosticWarning = 3;
    public const int DiagnosticFailure = 4;
    public const int ArchiveInvalid = 5;
    public const int TargetNotEmpty = 6;

    private const string Base = "api/v1/operations";

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

    public OperationsCli(
        HttpClient client, TextReader input, TextWriter output, TextWriter error, bool jsonOutput)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _input = input;
        _output = output;
        _error = error;
        _jsonOutput = jsonOutput;
    }

    /// <summary>Gehört dieser Befehl hierher? Die Betriebsbefehle haben eigene Exit-Codes.</summary>
    public static bool Handles(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && args[0] is "backup" or "restore" or "doctor" or "config" or "db";
    }

    public const string UsageText =
        """
        Betrieb (Exit-Codes: 0 ok · 1 Fehler · 2 Bedienfehler · 3 Warnung · 4 Diagnosefehler
                              · 5 Archiv ungültig · 6 Ziel nicht leer / nicht bestätigt):

          bifrost backup create --out <pfad> [--sections database,keyring,packages,config]
                                             [--passphrase-env <VAR> | --passphrase-prompt]
          bifrost backup verify <archiv> [--passphrase-env <VAR> | --passphrase-prompt]
          bifrost restore <archiv> [--replace] [--yes] [--passphrase-env <VAR> | --passphrase-prompt]
          bifrost doctor [--scope configuration,database,keyring,network,runtime,upstreams]
          bifrost config export [--include-secrets] [--out <pfad>] [--passphrase-env <VAR>]
          bifrost config import <datei> [--dry-run] [--passphrase-env <VAR>]
          bifrost db unblock

        Pfade von backup/restore gelten auf dem Rechner, auf dem der Gateway laeuft.
        Eine Passphrase wird NIE als Argument entgegengenommen — sie stuende in der Prozessliste
        und in der Shell-Historie. Nur Umgebungsvariable oder Eingabe ohne Echo.
        Ein Vollbackup enthaelt den Key-Ring und ist so schuetzenswert wie die Instanz selbst.
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            var command = args.ToArray();
            return command switch
            {
                ["backup", "create", .. var options] => await BackupCreateAsync(options, ct),
                ["backup", "verify", var archive, .. var options]
                    => await BackupVerifyAsync(archive, options, ct),
                ["restore", var archive, .. var options] => await RestoreAsync(archive, options, ct),
                ["doctor", .. var options] => await DoctorAsync(options, ct),
                ["config", "export", .. var options] => await ConfigExportAsync(options, ct),
                ["config", "import", var file, .. var options]
                    => await ConfigImportAsync(file, options, ct),
                ["db", "unblock"] => await UnblockAsync(ct),
                _ => await UsageAsync("Unbekannter Betriebsbefehl."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _error.WriteLineAsync("Abgebrochen.");
            return UnexpectedError;
        }
        catch (ArgumentException exception)
        {
            await _error.WriteLineAsync(exception.Message);
            return UsageError;
        }
        catch (HttpRequestException exception)
        {
            await _error.WriteLineAsync(
                $"Gateway nicht erreichbar: {exception.Message}");
            await _error.WriteLineAsync(
                "Hinweis: Steht der Start wegen BFR-DB-0101 (offener Migrationseintrag), laeuft kein "
                + "Gateway, den man fragen koennte. Dann im Serverprozess 'bifrost-server --db-unblock' "
                + "verwenden — siehe docs/operations.md.");
            return UnexpectedError;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            await _error.WriteLineAsync(exception.Message);
            return UnexpectedError;
        }
    }

    // ── Sicherung ───────────────────────────────────────────────────────────────────────────────

    private async Task<int> BackupCreateAsync(IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var target = parsed.Value("--out")
            ?? throw new ArgumentException("backup create verlangt --out <pfad>.");
        var sections = parsed.Value("--sections");
        var passphrase = await ReadPassphraseAsync(parsed, ct);
        parsed.EnsureNoRest();

        var body = new
        {
            targetPath = target,
            sections = sections is null
                ? null
                : sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            passphrase,
        };

        var (status, json) = await SendAsync(HttpMethod.Post, $"{Base}/backup", body, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, json);
        }

        await WriteAsync(json, () =>
            $"Archiv: {json.GetProperty("archivePath").GetString()}"
            + Environment.NewLine
            + $"Groesse: {json.GetProperty("sizeBytes").GetInt64().ToString("N0", CultureInfo.InvariantCulture)} Bytes"
            + Environment.NewLine
            + json.GetProperty("hinweis").GetString());
        return Success;
    }

    private async Task<int> BackupVerifyAsync(
        string archive, IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var passphrase = await ReadPassphraseAsync(parsed, ct);
        parsed.EnsureNoRest();

        var (status, json) = await SendAsync(
            HttpMethod.Post, $"{Base}/backup/verify", new { archivePath = archive, passphrase }, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, json);
        }

        var valid = json.GetProperty("valid").GetBoolean();
        await WriteAsync(json, () => valid
            ? "Das Archiv ist gueltig." + Environment.NewLine + Describe(json, "manifest")
            : "Das Archiv ist NICHT gueltig:" + Environment.NewLine + Bullets(json, "problems"));
        return valid ? Success : ArchiveInvalid;
    }

    // ── Wiederherstellung ───────────────────────────────────────────────────────────────────────

    private async Task<int> RestoreAsync(
        string archive, IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var replace = parsed.Flag("--replace");
        var confirmed = parsed.Flag("--yes");
        var passphrase = await ReadPassphraseAsync(parsed, ct);
        parsed.EnsureNoRest();

        // Erst planen. Ein Restore, der beim Schreiben merkt, dass er nicht passt, hat bereits
        // geschrieben — deshalb ist die Vorpruefung Pflicht und kein Komfort.
        var (planStatus, plan) = await SendAsync(
            HttpMethod.Post,
            $"{Base}/restore/plan",
            new { archivePath = archive, mode = replace ? "Replace" : "EmptyTargetOnly", passphrase },
            ct);
        if (planStatus is not HttpStatusCode.OK)
        {
            return await FailAsync(planStatus, plan);
        }

        await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(plan) : DescribePlan(plan));

        if (!plan.GetProperty("canApply").GetBoolean())
        {
            // Ein nicht leeres Ziel ohne --replace ist der eine Blocker mit eigenem Code (6); alles
            // andere heisst „so nicht anwendbar" (5).
            return !plan.GetProperty("targetIsEmpty").GetBoolean() && !replace
                ? TargetNotEmpty
                : ArchiveInvalid;
        }

        if (replace && !confirmed && !await ConfirmReplaceAsync(ct))
        {
            await _error.WriteLineAsync(
                "Abgebrochen: --replace ueberschreibt die bestehende Instanz und verlangt eine "
                + "ausdrueckliche Bestaetigung (Eingabe 'replace' oder --yes).");
            return TargetNotEmpty;
        }

        var (applyStatus, result) = await SendAsync(HttpMethod.Post, $"{Base}/restore/apply", plan, ct);
        if (applyStatus is not HttpStatusCode.OK)
        {
            return await FailAsync(applyStatus, result);
        }

        var applied = result.GetProperty("applied").GetBoolean();
        await WriteAsync(result, () => (applied
                ? $"Wiederhergestellt: {result.GetProperty("restoredSections").GetString()}"
                : "Nicht angewendet.")
            + Environment.NewLine + Bullets(result, "notes"));
        return applied ? Success : ArchiveInvalid;
    }

    private async Task<bool> ConfirmReplaceAsync(CancellationToken ct)
    {
        await _output.WriteLineAsync(
            "Dieser Vorgang UEBERSCHREIBT die bestehende Instanz. Zum Fortfahren 'replace' eingeben:");
        var answer = await _input.ReadLineAsync(ct);
        return string.Equals(answer?.Trim(), "replace", StringComparison.Ordinal);
    }

    // ── Diagnose ────────────────────────────────────────────────────────────────────────────────

    private async Task<int> DoctorAsync(IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var scope = parsed.Value("--scope");
        parsed.EnsureNoRest();

        var path = scope is null ? $"{Base}/doctor" : $"{Base}/doctor?scope={Uri.EscapeDataString(scope)}";
        var (status, json) = await SendAsync(HttpMethod.Get, path, null, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, json);
        }

        await WriteAsync(json, () => DescribeReport(json));

        // Die Bewertung kommt aus dem Bericht; hier wird sie nur in einen Exit-Code uebersetzt.
        // Ein uebersprungener Check ist neutral — er steht sichtbar mit Begruendung im Bericht.
        if (json.GetProperty("hasFailures").GetBoolean())
        {
            return DiagnosticFailure;
        }

        return json.GetProperty("hasWarnings").GetBoolean() ? DiagnosticWarning : Success;
    }

    // ── Konfiguration ───────────────────────────────────────────────────────────────────────────

    private async Task<int> ConfigExportAsync(IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var includeSecrets = parsed.Flag("--include-secrets");
        var outPath = parsed.Value("--out");
        var passphrase = await ReadPassphraseAsync(parsed, ct);
        parsed.EnsureNoRest();

        var (status, json) = await SendAsync(
            HttpMethod.Post, $"{Base}/config/export", new { includeSecrets, passphrase }, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, json);
        }

        var payload = json.GetProperty("payload").GetString() ?? string.Empty;
        if (outPath is null)
        {
            await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(json) : payload);
            return Success;
        }

        // Die Nutzlast ist JSON und reist ohnehin durch die Antwort — sie wird deshalb LOKAL
        // geschrieben, anders als ein Archiv.
        await File.WriteAllTextAsync(Path.GetFullPath(outPath), payload, ct);
        await WriteAsync(json, () =>
            $"Export nach '{Path.GetFullPath(outPath)}' geschrieben."
            + (json.GetProperty("containsSecrets").GetBoolean()
                ? Environment.NewLine + "ACHTUNG: Der Export enthaelt Zugangsdaten (verschluesselt)."
                : Environment.NewLine + "Der Export enthaelt keine Zugangsdaten, sondern Referenzen."));
        return Success;
    }

    private async Task<int> ConfigImportAsync(
        string file, IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var dryRun = parsed.Flag("--dry-run");
        var passphrase = await ReadPassphraseAsync(parsed, ct);
        parsed.EnsureNoRest();

        var payload = file == "-"
            ? await _input.ReadToEndAsync(ct)
            : await File.ReadAllTextAsync(Path.GetFullPath(file), ct);

        var (planStatus, plan) = await SendAsync(
            HttpMethod.Post, $"{Base}/config/import/plan", new { payload, passphrase }, ct);
        if (planStatus is not HttpStatusCode.OK)
        {
            return await FailAsync(planStatus, plan);
        }

        await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(plan) : DescribeImport(plan));

        if (!plan.GetProperty("canApply").GetBoolean())
        {
            return ArchiveInvalid;
        }

        if (dryRun)
        {
            await _output.WriteLineAsync("--dry-run: es wurde nichts geschrieben.");
            return Success;
        }

        var (applyStatus, applyBody) = await SendAsync(
            HttpMethod.Post, $"{Base}/config/import/apply", plan, ct);
        if (applyStatus is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            return await FailAsync(applyStatus, applyBody);
        }

        await _output.WriteLineAsync("Import angewendet.");
        return Success;
    }

    // ── Recovery ────────────────────────────────────────────────────────────────────────────────

    private async Task<int> UnblockAsync(CancellationToken ct)
    {
        var (status, json) = await SendAsync(HttpMethod.Post, $"{Base}/database/unblock", new { }, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, json);
        }

        await WriteAsync(json, () =>
            $"Entfernte Journaleintraege: {json.GetProperty("removed").GetInt32()}"
            + Environment.NewLine + json.GetProperty("hinweis").GetString());
        return Success;
    }

    // ── Ausgabe ─────────────────────────────────────────────────────────────────────────────────

    private static string DescribePlan(JsonElement plan)
    {
        var text = new StringBuilder();
        text.Append("Ziel ist leer: ")
            .AppendLine(plan.GetProperty("targetIsEmpty").GetBoolean() ? "ja" : "nein");
        text.Append("Modus: ").AppendLine(plan.GetProperty("mode").GetString());
        text.AppendLine(Describe(plan, "manifest"));
        var blockers = Bullets(plan, "blockers");
        if (blockers.Length > 0)
        {
            text.AppendLine("Blocker:").AppendLine(blockers);
        }

        var warnings = Bullets(plan, "warnings");
        if (warnings.Length > 0)
        {
            text.AppendLine("Warnungen:").AppendLine(warnings);
        }

        if (plan.TryGetProperty("preBackupPath", out var preBackup)
            && preBackup.ValueKind is JsonValueKind.String)
        {
            text.Append("Sicherung des Altzustands entsteht unter: ").AppendLine(preBackup.GetString());
        }

        return text.ToString().TrimEnd();
    }

    private static string DescribeImport(JsonElement plan)
    {
        var text = new StringBuilder();
        foreach (var (property, label) in ((string, string)[])
                 [("additions", "Neu"), ("unchanged", "Unveraendert"), ("conflicts", "Konflikte"),
                  ("missingDependencies", "Fehlende Abhaengigkeiten")])
        {
            var lines = Bullets(plan, property);
            if (lines.Length > 0)
            {
                text.AppendLine(label + ":").AppendLine(lines);
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string DescribeReport(JsonElement report)
    {
        var text = new StringBuilder();
        foreach (var check in report.GetProperty("checks").EnumerateArray())
        {
            text.Append(check.GetProperty("status").GetString()?.ToUpperInvariant().PadRight(8))
                .Append(check.GetProperty("code").GetString())
                .Append("  ")
                .AppendLine(check.GetProperty("summary").GetString());
            if (check.TryGetProperty("remediation", out var remediation)
                && remediation.ValueKind is JsonValueKind.String)
            {
                text.Append("          -> ").AppendLine(remediation.GetString());
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Describe(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null
            ? JsonSerializer.Serialize(value, PrettyJson)
            : string.Empty;

    private static string Bullets(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var array) && array.ValueKind is JsonValueKind.Array
            ? string.Join(
                Environment.NewLine,
                array.EnumerateArray().Select(item => "  - " + item.GetString()))
            : string.Empty;

    private async Task WriteAsync(JsonElement json, Func<string> human)
        => await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(json) : human());

    private async Task<int> UsageAsync(string message)
    {
        await _error.WriteLineAsync(message);
        await _error.WriteLineAsync(UsageText);
        return UsageError;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(
                body is JsonElement element
                    ? JsonSerializer.Serialize(element)
                    : JsonSerializer.Serialize(body, RequestJson),
                Encoding.UTF8,
                "application/json");
        }

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
    /// Der Exit-Code einer abgelehnten Anfrage. Er haengt an der <b>Kennung im Rumpf</b>, nicht am
    /// HTTP-Status: 400 kann „Archiv kaputt" (5) oder „Argument fehlt" (2) heissen, und das sind
    /// zwei verschiedene Aussagen.
    /// </summary>
    private async Task<int> FailAsync(HttpStatusCode status, JsonElement body)
    {
        var code = body.ValueKind is JsonValueKind.Object
            && body.TryGetProperty("error", out var error)
            && error.ValueKind is JsonValueKind.Object
            && error.TryGetProperty("code", out var codeValue)
                ? codeValue.GetString()
                : null;
        var message = body.ValueKind is JsonValueKind.Object
            && body.TryGetProperty("error", out var withMessage)
            && withMessage.ValueKind is JsonValueKind.Object
            && withMessage.TryGetProperty("message", out var messageValue)
                ? messageValue.GetString()
                : body.ValueKind is JsonValueKind.Undefined
                    ? $"Gateway-Fehler {(int)status} ({status})."
                    : JsonSerializer.Serialize(body, PrettyJson);

        await _error.WriteLineAsync(message);

        return code switch
        {
            "usage" => UsageError,
            "archive-invalid" => ArchiveInvalid,
            "target-not-empty" => TargetNotEmpty,
            // Der Dienst kann es auf DIESER Instanz nicht — heute vor allem: pg_dump/pg_restore
            // fehlen (ADR-0024 E2). Kein Serverfehler, sondern eine Anfrage, die hier nicht
            // anwendbar ist; der Text der Antwort sagt, was fehlt und wo man es herbekommt.
            "unsupported" => UsageError,
            _ => status switch
            {
                // Fehlender oder zu schwacher Token — das ist eine Bedienung, keine Stoerung.
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => UsageError,
                _ => UnexpectedError,
            },
        };
    }

    // ── Passphrase ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Eine Passphrase kommt aus einer Umgebungsvariablen oder aus einer Eingabe ohne Echo —
    /// <b>nie</b> aus einem Argument: Argumente stehen in der Prozessliste (<c>ps</c>) und in der
    /// Shell-Historie, und beides ueberlebt den Befehl.
    /// </summary>
    private async Task<string?> ReadPassphraseAsync(CliOptions options, CancellationToken ct)
    {
        if (options.Value("--passphrase") is not null)
        {
            throw new ArgumentException(
                "--passphrase gibt es nicht und wird es nicht geben: Ein Argument steht in der "
                + "Prozessliste und in der Shell-Historie. --passphrase-env <VAR> oder "
                + "--passphrase-prompt verwenden.");
        }

        if (options.Value("--passphrase-env") is { } variable)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrEmpty(value)
                ? throw new ArgumentException($"Die Umgebungsvariable '{variable}' ist leer oder nicht gesetzt.")
                : value;
        }

        if (!options.Flag("--passphrase-prompt"))
        {
            return null;
        }

        await _output.WriteLineAsync("Passphrase (Eingabe bleibt unsichtbar):");
        return Console.IsInputRedirected
            ? (await _input.ReadLineAsync(ct))?.Trim()
            : ReadWithoutEcho();
    }

    private static string ReadWithoutEcho()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Length--;
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }

}
