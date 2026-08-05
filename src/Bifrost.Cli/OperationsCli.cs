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

    /// <summary>
    /// Auditexport und Kettenprüfung liegen <b>neben der Auditansicht</b> (<c>api/v1/audit</c>) und
    /// nicht unter den Betriebsbefehlen: Es ist dieselbe Sache in einer anderen Ausgabeform, und
    /// eine zweite Adresse für dieselbe Sache lässt jeden raten, welche die richtige ist.
    /// </summary>
    private const string AuditBase = "api/v1/audit";

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
        return args.Count > 0
            && args[0] is "backup" or "restore" or "doctor" or "config" or "db" or "support-bundle"
                or "policy" or "audit";
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
          bifrost audit export [--format jsonl|csv] [--out <pfad>] [--after <n>] [--limit <n>]
                               [--from <zeit>] [--to <zeit>] [--tool <praefix>] [--kind <art>]
                               [--status <status>] [--origin <herkunft>] [--caller <id>]
                               [--server <id>]
          bifrost audit verify [--after <n>] [--limit <n>]
          bifrost policy plan <pack-datei>
          bifrost policy apply <pack-datei> [--yes] [--undo-out <pfad>]
          bifrost policy revert <rueckweg-datei>
          bifrost db unblock
          bifrost support-bundle [--out <pfad>] [--categories version,configuration,diagnostics,
                                 health,logs,migrations,runtime] [--log-lines <n>] [--preview]

        Pfade von backup/restore gelten auf dem Rechner, auf dem der Gateway laeuft.
        Eine Passphrase wird NIE als Argument entgegengenommen — sie stuende in der Prozessliste
        und in der Shell-Historie. Nur Umgebungsvariable oder Eingabe ohne Echo.
        Ein Vollbackup enthaelt den Key-Ring und ist so schuetzenswert wie die Instanz selbst.

        'audit export' streamt: Die Zeilen gehen durch, ohne dass irgendwo die ganze Menge
        entsteht — weder im Gateway noch hier. Ohne --out gehen sie auf die Standardausgabe.
        'audit verify' rechnet die Integritaetskette nach und nennt die ERSTE gebrochene Stelle.
        Was die Kette nicht zeigt, steht in ihrer eigenen Ausgabe.

        'policy apply' zeigt IMMER zuerst, wer danach mehr darf, und fragt nach. --yes ueberspringt
        die Frage, nicht die Anzeige. Der Rueckweg entsteht nur mit --undo-out: Ohne die Datei ist
        die Uebernahme danach nicht mehr zurueckzunehmen.
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
                ["audit", "export", .. var options] => await AuditExportAsync(options, ct),
                ["audit", "verify", .. var options] => await AuditVerifyAsync(options, ct),
                ["policy", "plan", var pack] => await PolicyPlanAsync(pack, ct),
                ["policy", "apply", var pack, .. var options] => await PolicyApplyAsync(pack, options, ct),
                ["policy", "revert", var undoFile] => await PolicyRevertAsync(undoFile, ct),
                ["db", "unblock"] => await UnblockAsync(ct),
                ["support-bundle", .. var options] => await SupportBundleAsync(options, ct),
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

    /// <summary>
    /// Erzeugt ein weitergabefaehiges Bundle — oder zeigt mit <c>--preview</c> erst einmal, was
    /// hineinkaeme. Der Vorschauweg ist kein Komfort: Ein Archiv, dessen Inhalt man erst nach dem
    /// Schreiben sieht, ist bereits geschrieben.
    /// </summary>
    private async Task<int> SupportBundleAsync(IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var preview = parsed.Flag("--preview");
        var categories = parsed.Value("--categories");
        var logLines = parsed.Value("--log-lines");
        var target = parsed.Value("--out");
        parsed.EnsureNoRest();

        if (logLines is not null && !int.TryParse(logLines, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("--log-lines verlangt eine Zahl.");
        }

        var body = new
        {
            categories = categories?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            logTailLines = logLines is null ? (int?)null : int.Parse(logLines, CultureInfo.InvariantCulture),
            outputPath = target,
        };

        var route = preview ? $"{Base}/support-bundle/preview" : $"{Base}/support-bundle";
        var (status, json) = await SendAsync(HttpMethod.Post, route, body, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, json);
        }

        await WriteAsync(json, () => preview ? DescribePreview(json) : DescribeBundle(json));
        return Success;
    }

    private static string DescribePreview(JsonElement json)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Vorschau: {json.GetProperty("entries").GetArrayLength()} Dokument(e), "
            + $"{json.GetProperty("estimatedBytes").GetInt64().ToString("N0", CultureInfo.InvariantCulture)} Bytes roh");

        foreach (var entry in json.GetProperty("entries").EnumerateArray())
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {entry.GetProperty("path").GetString(),-28} {entry.GetProperty("category").GetString()}");
        }

        return text.Append(DescribeSkips(json)).ToString().TrimEnd();
    }

    private static string DescribeBundle(JsonElement json)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Bundle:   {json.GetProperty("archivePath").GetString()}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Groesse:  {json.GetProperty("sizeBytes").GetInt64().ToString("N0", CultureInfo.InvariantCulture)} Bytes");
        text.AppendLine(CultureInfo.InvariantCulture, $"SHA-256:  {json.GetProperty("sha256").GetString()}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Maskiert: {json.GetProperty("manifest").GetProperty("redaction").GetProperty("redactedFields").GetInt32()} Feld(er)");
        text.AppendLine(
            "Enthaelt KEINE Auditinhalte und kein Credentialmaterial — das ist Absicht, nicht Zufall.");
        return text.Append(DescribeSkips(json)).ToString().TrimEnd();
    }

    /// <summary>
    /// Uebersprungene Kategorien stehen in der Ausgabe, nicht nur im Manifest: Ein Bundle ohne Logs
    /// soll nicht aussehen wie eine Installation ohne Vorfaelle.
    /// </summary>
    private static string DescribeSkips(JsonElement json)
    {
        var property = json.TryGetProperty("skipped", out var skipped)
            ? skipped
            : json.TryGetProperty("skippedCategories", out var alternative) ? alternative : default;

        if (property.ValueKind is not JsonValueKind.Array || property.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder("Uebersprungen:");
        text.AppendLine();
        foreach (var skip in property.EnumerateArray())
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {skip.GetProperty("category").GetString()}: {skip.GetProperty("reason").GetString()}");
        }

        return text.ToString();
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
            + Environment.NewLine + Bullets(result, "notes")
            + RestartHint(result));
        return applied ? Success : ArchiveInvalid;
    }

    /// <summary>
    /// <b>Die letzte Zeile, weil sie die wichtigste ist</b> (<c>BFR-F-0047</c>). Sie steht unter den
    /// Notizen und nicht zwischen ihnen: Wer nur das Ende der Ausgabe liest, muss trotzdem erfahren,
    /// dass der Gateway ab jetzt nichts mehr beantwortet.
    /// <para>
    /// Der Exit-Code bleibt <see cref="Success"/> — der Restore <em>ist</em> gelungen, und die
    /// Codetabelle des M2-Vertrags kennt kein „gelungen, aber". Was ein Skript aufhält, das die
    /// Zeile überliest, ist deshalb nicht dieser Text, sondern die <c>503</c> des Gateways beim
    /// nächsten Aufruf. Der Hinweis erklärt sie nur vorher.
    /// </para>
    /// </summary>
    private static string RestartHint(JsonElement result)
        => result.TryGetProperty("restartRequired", out var flag) && flag.ValueKind is JsonValueKind.True
            ? Environment.NewLine
              + Environment.NewLine
              + "NEUSTART ERFORDERLICH. Der wiederhergestellte Stand liegt auf der Platte; dieser"
              + Environment.NewLine
              + "Gateway-Prozess haelt noch die ersetzte Datenbank und den ersetzten Key-Ring offen."
              + Environment.NewLine
              + "Er nimmt ab sofort keine Anfragen mehr an (503) — bis er neu gestartet ist."
              + Environment.NewLine
              + "  Im Container:  docker compose restart bifrost"
            : string.Empty;

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

    // ── Auditexport und Kette (WP6.2) ───────────────────────────────────────────────────────────

    /// <summary>Filteroptionen der CLI auf die Abfrageparameter des Endpunkts.</summary>
    private static readonly (string Option, string Query)[] AuditFilterOptions =
    [
        ("--from", "from"), ("--to", "to"), ("--caller", "caller"), ("--server", "server"),
        ("--tool", "tool"), ("--status", "status"), ("--kind", "kind"), ("--origin", "origin"),
        ("--after", "after"), ("--limit", "limit"),
    ];

    /// <summary>
    /// Exportiert das Auditlog — <b>durchgereicht, nicht gesammelt</b>.
    /// <para>
    /// Die Antwort wird gelesen, sobald die Kopfzeilen da sind, und direkt in die Zieldatei kopiert.
    /// Ein Export, den die CLI erst vollständig in den Arbeitsspeicher holt, hätte die Zusage des
    /// Gateways auf dem letzten Meter wieder aufgegeben — und zwar auf dem Rechner des Betreibers.
    /// </para>
    /// <para>
    /// <b>Werte werden nicht hier ausgelegt.</b> Zeitangaben, Arten und Herkünfte gehen so hinaus,
    /// wie sie eingegeben wurden; über ihre Gültigkeit entscheidet der Gateway. Eine zweite
    /// Auslegung in der CLI wäre eine zweite Wahrheit, und die falsche wäre die hier.
    /// </para>
    /// </summary>
    private async Task<int> AuditExportAsync(IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var format = parsed.Value("--format") ?? "jsonl";
        var outPath = parsed.Value("--out");
        var query = new List<string> { "format=" + Uri.EscapeDataString(format) };

        foreach (var (option, name) in AuditFilterOptions)
        {
            if (parsed.Value(option) is { } value)
            {
                query.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        parsed.EnsureNoRest();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{AuditBase}/export?" + string.Join('&', query));
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            await _error.WriteLineAsync(body);
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? UsageError
                : UnexpectedError;
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        if (outPath is null)
        {
            using var reader = new StreamReader(source);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                await _output.WriteLineAsync(line).ConfigureAwait(false);
            }

            return Success;
        }

        var target = Path.GetFullPath(outPath);
        await using (var file = File.Create(target))
        {
            await source.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        await _output.WriteLineAsync(
            $"Auditexport nach '{target}' geschrieben ({new FileInfo(target).Length:N0} Bytes).");
        return Success;
    }

    /// <summary>
    /// Rechnet die Integritätskette nach. Ein Bruch ist <b>kein</b> Programmfehler, sondern ein
    /// Befund — er bekommt deshalb den Diagnose-Exit-Code und nicht den für „etwas ging schief".
    /// </summary>
    private async Task<int> AuditVerifyAsync(IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var query = new List<string>();
        foreach (var option in (string[])["--after", "--limit"])
        {
            if (parsed.Value(option) is { } value)
            {
                query.Add($"{option[2..]}={Uri.EscapeDataString(value)}");
            }
        }

        parsed.EnsureNoRest();

        var path = $"{AuditBase}/verify" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        var (status, report) = await SendAsync(HttpMethod.Get, path, null, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, report);
        }

        await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(report) : DescribeChain(report));
        return report.GetProperty("firstBreak").ValueKind is JsonValueKind.Null
            ? Success
            : DiagnosticFailure;
    }

    /// <summary>
    /// Der Kettenbericht im Klartext — <b>samt seiner Grenzen</b>. Die stehen nicht am Rand: Ein
    /// „Kette intakt", das jemand für „nichts fehlt" hält, ist gefährlicher als gar keine Kette.
    /// </summary>
    private static string DescribeChain(JsonElement report)
    {
        var text = new StringBuilder();
        var broken = report.GetProperty("firstBreak");

        text.Append("Geprueft: ").Append(report.GetProperty("checked").GetInt64())
            .AppendLine(" verkettete Eintraege");

        if (report.GetProperty("unchained").GetInt64() is > 0 and var unchained)
        {
            text.Append("Ohne Kettenglied: ").Append(unchained)
                .AppendLine(" (aus der Zeit vor der Kette — nicht gedeckt)");
        }

        text.Append("Abschnitte: ").AppendLine(
            report.GetProperty("segments").GetArrayLength().ToString(CultureInfo.InvariantCulture));

        if (broken.ValueKind is JsonValueKind.Null)
        {
            text.AppendLine("Ergebnis: kein Bruch gefunden.");
        }
        else
        {
            text.Append("BRUCH ab Zeile ").Append(broken.GetProperty("sequence").GetInt64())
                .Append(": ").AppendLine(broken.GetProperty("reason").GetString());
        }

        text.AppendLine().AppendLine("Was diese Pruefung NICHT zeigt:");
        text.Append(Bullets(report, "limits"));
        return text.ToString().TrimEnd();
    }

    // ── Policy Packs (WP6.1) ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Zeigt, was ein Pack täte — <b>und wer danach mehr darf</b>. Schreibt nichts.
    /// <para>
    /// Ein Plan, der nicht anwendbar ist, endet mit Code 5, wie beim Konfigurationsimport: Für ein
    /// Skript ist „geprüft und abgelehnt" etwas anderes als „gelaufen".
    /// </para>
    /// </summary>
    private async Task<int> PolicyPlanAsync(string packFile, CancellationToken ct)
    {
        var (status, plan) = await PlanPackAsync(packFile, ct);
        if (status is not HttpStatusCode.OK)
        {
            return await FailAsync(status, plan);
        }

        await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(plan) : DescribePack(plan));
        return plan.GetProperty("canApply").GetBoolean() ? Success : ArchiveInvalid;
    }

    /// <summary>
    /// Übernimmt ein Pack — planen, <b>zeigen</b>, fragen, anwenden.
    /// <para>
    /// <b>Die Anzeige ist nicht überspringbar.</b> <c>--yes</c> überspringt die Rückfrage, nicht den
    /// Rechte-Diff: Eine Übernahme, bei der niemand gesehen hat, wer danach mehr darf, ist genau
    /// die stille Rechteausweitung, gegen die dieses Paket gebaut ist.
    /// </para>
    /// <para>
    /// <b>Der Rückweg entsteht nur mit <c>--undo-out</c>.</b> Er steht in der Antwort der Übernahme
    /// und wird nirgends aufbewahrt; ohne die Datei ist er nach dem Befehl weg. Deshalb steht ohne
    /// <c>--undo-out</c> eine Warnung da und keine Beruhigung.
    /// </para>
    /// </summary>
    private async Task<int> PolicyApplyAsync(
        string packFile, IReadOnlyList<string> options, CancellationToken ct)
    {
        var parsed = new CliOptions(options);
        var confirmed = parsed.Flag("--yes");
        var undoOut = parsed.Value("--undo-out");
        parsed.EnsureNoRest();

        var (planStatus, plan) = await PlanPackAsync(packFile, ct);
        if (planStatus is not HttpStatusCode.OK)
        {
            return await FailAsync(planStatus, plan);
        }

        await _output.WriteLineAsync(_jsonOutput ? JsonSerializer.Serialize(plan) : DescribePack(plan));

        if (!plan.GetProperty("canApply").GetBoolean())
        {
            return ArchiveInvalid;
        }

        if (!confirmed && !await ConfirmPackAsync(plan, ct))
        {
            await _error.WriteLineAsync("Abgebrochen: Es wurde nichts geschrieben.");
            return TargetNotEmpty;
        }

        var (applyStatus, application) = await SendAsync(HttpMethod.Post, $"{Base}/policy/apply", plan, ct);
        if (applyStatus is not HttpStatusCode.OK)
        {
            return await FailAsync(applyStatus, application);
        }

        if (undoOut is { } target)
        {
            await File.WriteAllTextAsync(
                Path.GetFullPath(target), JsonSerializer.Serialize(application, PrettyJson), ct);
        }

        await WriteAsync(application, () =>
            $"Uebernommen: {application.GetProperty("id").GetString()}"
            + $"@{application.GetProperty("version").GetString()}"
            + Environment.NewLine
            + (undoOut is null
                ? "WARNUNG: Ohne --undo-out ist der Rueckweg dieses Vorgangs nicht mehr verfuegbar."
                : $"Rueckweg nach '{Path.GetFullPath(undoOut)}' geschrieben — "
                    + "'bifrost policy revert <datei>' nimmt die Uebernahme zurueck."));
        return Success;
    }

    /// <summary>
    /// Nimmt eine Übernahme zurück — mit der Datei aus <c>--undo-out</c>, nicht mit einem Stand von
    /// gestern: Ein ganzer Stand fasst auch an, was mit dem Pack nie zu tun hatte.
    /// </summary>
    private async Task<int> PolicyRevertAsync(string undoFile, CancellationToken ct)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.GetFullPath(undoFile), ct));

        var (status, result) = await SendAsync(HttpMethod.Post, $"{Base}/policy/revert", body, ct);
        if (status is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            return await FailAsync(status, result);
        }

        await _output.WriteLineAsync("Uebernahme zurueckgenommen.");
        return Success;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PlanPackAsync(
        string packFile, CancellationToken ct)
    {
        var pack = packFile == "-"
            ? await _input.ReadToEndAsync(ct)
            : await File.ReadAllTextAsync(Path.GetFullPath(packFile), ct);

        return await SendAsync(HttpMethod.Post, $"{Base}/policy/plan", new { pack }, ct);
    }

    /// <summary>
    /// Die Rückfrage nennt die <b>Zahl</b> der zusätzlichen Berechtigungen. „Wirklich fortfahren?"
    /// ohne diese Zahl ist eine Frage, auf die jeder ja sagt.
    /// </summary>
    private async Task<bool> ConfirmPackAsync(JsonElement plan, CancellationToken ct)
    {
        var added = CountOf(plan, "rightsDelta", "added");
        await _output.WriteLineAsync(added == 0
            ? "Dieses Pack gibt keiner Identitaet zusaetzliche Rechte. Zum Fortfahren 'uebernehmen' eingeben:"
            : $"Dieses Pack gibt {added} zusaetzliche Berechtigung(en) — sie stehen oben einzeln. "
                + "Zum Fortfahren 'uebernehmen' eingeben:");
        var answer = await _input.ReadLineAsync(ct);
        return string.Equals(answer?.Trim(), "uebernehmen", StringComparison.Ordinal);
    }

    private static int CountOf(JsonElement parent, string property, string child)
        => parent.TryGetProperty(property, out var value)
            && value.TryGetProperty(child, out var array)
            && array.ValueKind is JsonValueKind.Array
                ? array.GetArrayLength()
                : 0;

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

    /// <summary>
    /// Der Plan eines Policy Packs im Klartext.
    /// <para>
    /// <b>Der Rechte-Diff steht oben, nicht unten.</b> Wer eine Ausgabe abschneidet oder nur den
    /// Anfang liest, soll die Rechteausweitung sehen und nicht die Herausgeberbeschreibung. Und
    /// jede zusätzliche Berechtigung steht <b>einzeln</b> da, mit Identität und Wirkungsebene —
    /// „3 Rechte hinzugefügt" ist keine Aussage, über die jemand entscheiden kann.
    /// </para>
    /// </summary>
    private static string DescribePack(JsonElement plan)
    {
        var text = new StringBuilder();
        var manifest = plan.GetProperty("manifest");
        text.Append("Pack: ").Append(manifest.GetProperty("id").GetString())
            .Append('@').AppendLine(manifest.GetProperty("version").GetString());
        text.Append("Herausgeber: ").Append(plan.GetProperty("publisherKeyId").GetString())
            .Append(" (").Append(plan.GetProperty("trustLevel").GetString()).AppendLine(")");

        var added = Rights(plan, "added");
        text.AppendLine(added.Length > 0
            ? "Zusaetzliche Rechte:" + Environment.NewLine + added
            : "Zusaetzliche Rechte: keine.");

        var removed = Rights(plan, "removed");
        if (removed.Length > 0)
        {
            text.AppendLine("Entzogene Rechte:").AppendLine(removed);
        }

        foreach (var (property, label) in ((string, string)[])
                 [("updates", "Wird ersetzt"), ("additions", "Neu"), ("unchanged", "Unveraendert"),
                  ("conflicts", "Konflikte"), ("missingTools", "Hier unbekannte Werkzeuge"),
                  ("notes", "Hinweise")])
        {
            var lines = Bullets(plan, property);
            if (lines.Length > 0)
            {
                text.AppendLine(label + ":").AppendLine(lines);
            }
        }

        text.Append("Anwendbar: ").Append(plan.GetProperty("canApply").GetBoolean() ? "ja" : "nein");
        return text.ToString().TrimEnd();
    }

    /// <summary>Ein Recht je Zeile: <c>identitaet: Wirkung auf Ebene</c>.</summary>
    private static string Rights(JsonElement plan, string property)
        => plan.TryGetProperty("rightsDelta", out var delta)
            && delta.TryGetProperty(property, out var array)
            && array.ValueKind is JsonValueKind.Array
                ? string.Join(
                    Environment.NewLine,
                    array.EnumerateArray().Select(entry =>
                        "  - " + entry.GetProperty("identity").GetString()
                        + ": " + entry.GetProperty("action").GetString()
                        + " auf " + entry.GetProperty("scope").GetString()))
                : string.Empty;

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
