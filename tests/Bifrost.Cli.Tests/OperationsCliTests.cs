using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Cli.Tests;

/// <summary>
/// Die Betriebsbefehle und ihre Exit-Codes (M2-Vertrag §4).
/// <para>
/// Geprüft wird der <b>echte</b> Exit-Code: Die Tests rufen <see cref="GatewayCli.RunAsync"/> auf —
/// genau den Wert, den <c>Program.cs</c> unverändert an das Betriebssystem zurückgibt. Ein Test, der
/// stattdessen <see cref="OperationsCli"/> direkt aufriefe, ließe die Weiche im Dispatcher
/// ungeprüft; und die Weiche ist hier nicht nebensächlich, weil dieselben Zahlen in der übrigen CLI
/// etwas anderes bedeuten.
/// </para>
/// </summary>
public class OperationsCliTests
{
    /// <summary>
    /// Die Tabelle aus dem Vertrag, an einer Stelle festgenagelt. Ein Exit-Code ist das, worauf ein
    /// Skript sich stützt — wer ihn ändert, ändert das Verhalten jeder Automatisierung da draußen.
    /// </summary>
    [Fact]
    public void Exit_codes_match_the_frozen_contract_table()
    {
        OperationsCli.Success.Should().Be(0);
        OperationsCli.UnexpectedError.Should().Be(1);
        OperationsCli.UsageError.Should().Be(2);
        OperationsCli.DiagnosticWarning.Should().Be(3);
        OperationsCli.DiagnosticFailure.Should().Be(4);
        OperationsCli.ArchiveInvalid.Should().Be(5);
        OperationsCli.TargetNotEmpty.Should().Be(6);
    }

    // ── backup create ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backup_create_returns_success_and_never_sends_a_passphrase_from_argv()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"archivePath":"/data/backups/a.zip","sizeBytes":4096,
             "manifest":{"encrypted":false,"sections":"Database, KeyRing, Packages, Config"},
             "hinweis":"UNVERSCHLUESSELTES Vollbackup"}
            """));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["backup", "create", "--out", "/data/backups/a.zip"], output: output);

        exit.Should().Be(OperationsCli.Success);
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/backup");
        handler.Bodies.Single().Should().Contain("\"targetPath\":\"/data/backups/a.zip\"");
        output.ToString().Should().Contain("UNVERSCHLUESSELTES Vollbackup");
    }

    [Fact]
    public async Task Backup_create_without_out_is_a_usage_error()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));

        var exit = await RunAsync(handler, ["backup", "create"]);

        exit.Should().Be(OperationsCli.UsageError);
        handler.Requests.Should().BeEmpty("ein Bedienfehler faellt auf, bevor irgendetwas laeuft");
    }

    /// <summary>
    /// Der Server antwortet mit <c>501</c>, wenn der Vorgang auf dieser Installation nicht geht —
    /// seit ADR-0024 E2 umgesetzt ist, ist das die Lage „pg_dump ist nicht installiert". Die CLI
    /// gibt den Text weiter, statt einen unerwarteten Fehler daraus zu machen.
    /// </summary>
    [Fact]
    public async Task Backup_create_without_pg_dump_says_so_instead_of_pretending()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.NotImplemented,
            """{"error":{"code":"unsupported","message":"PostgreSQL-Sicherungen laufen ueber pg_dump."}}"""));
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["backup", "create", "--out", "/tmp/a.zip"], error: error);

        // Kein Erfolg, kein "unerwarteter Fehler": Die Anfrage ist auf dieser Instanz nicht
        // anwendbar, und der Text sagt warum.
        exit.Should().Be(OperationsCli.UsageError);
        error.ToString().Should().Contain("pg_dump");
    }

    [Fact]
    public async Task A_passphrase_as_an_argument_is_refused_before_any_request()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var error = new StringWriter();

        var exit = await RunAsync(
            handler, ["backup", "create", "--out", "/tmp/a.zip", "--passphrase", "geheim"], error: error);

        exit.Should().Be(OperationsCli.UsageError);
        handler.Requests.Should().BeEmpty();
        error.ToString().Should().Contain("Prozessliste");
    }

    [Fact]
    public async Task A_passphrase_comes_from_the_environment_and_travels_in_the_body()
    {
        var variable = "BIFROST_TEST_PASSPHRASE_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, "korrekt-pferd-batterie");
        try
        {
            var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
                """{"archivePath":"/a.zip","sizeBytes":1,"manifest":{},"hinweis":"ok"}"""));

            var exit = await RunAsync(
                handler, ["backup", "create", "--out", "/a.zip", "--passphrase-env", variable]);

            exit.Should().Be(OperationsCli.Success);
            handler.Bodies.Single().Should().Contain("korrekt-pferd-batterie");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task An_empty_passphrase_variable_is_a_usage_error_not_an_empty_passphrase()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));

        var exit = await RunAsync(
            handler, ["backup", "create", "--out", "/a.zip", "--passphrase-env", "BIFROST_NICHT_GESETZT_XYZ"]);

        exit.Should().Be(OperationsCli.UsageError);
        handler.Requests.Should().BeEmpty();
    }

    // ── backup verify ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backup_verify_reports_an_invalid_archive_with_its_own_exit_code()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"valid":false,"manifest":null,"problems":["Pruefsumme von database/bifrost.db passt nicht."]}"""));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["backup", "verify", "/data/a.zip"], output: output);

        exit.Should().Be(OperationsCli.ArchiveInvalid);
        output.ToString().Should().Contain("Pruefsumme");
    }

    [Fact]
    public async Task Backup_verify_returns_success_for_a_sound_archive()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"valid":true,"manifest":{"formatVersion":1},"problems":[]}"""));

        var exit = await RunAsync(handler, ["backup", "verify", "/data/a.zip"]);

        exit.Should().Be(OperationsCli.Success);
    }

    // ── restore ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_shows_the_plan_and_stops_at_a_non_empty_target_without_replace()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"canApply":false,"manifest":null,"mode":"EmptyTargetOnly","targetIsEmpty":false,
             "blockers":["Die Zielinstanz ist nicht leer."],"warnings":[],"token":"abc"}
            """));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["restore", "/data/a.zip"], output: output);

        exit.Should().Be(OperationsCli.TargetNotEmpty);
        handler.Requests.Should().ContainSingle("ohne anwendbaren Plan wird nichts angewendet")
            .Which.RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/restore/plan");
        output.ToString().Should().Contain("Die Zielinstanz ist nicht leer.");
    }

    [Fact]
    public async Task Restore_with_replace_but_without_confirmation_aborts_with_exit_six()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"canApply":true,"manifest":null,"mode":"Replace","targetIsEmpty":false,
             "blockers":[],"warnings":[],"preBackupPath":"/data/backups/pre-restore.zip","token":"abc"}
            """));
        var error = new StringWriter();

        // Die Bestaetigung wird gelesen — und es steht etwas anderes darin als 'replace'.
        var exit = await RunAsync(
            handler, ["restore", "/data/a.zip", "--replace"], input: new StringReader("ja\n"), error: error);

        exit.Should().Be(OperationsCli.TargetNotEmpty);
        handler.Requests.Should().ContainSingle("es darf nichts angewendet worden sein");
        error.ToString().Should().Contain("ausdrueckliche Bestaetigung");
    }

    [Fact]
    public async Task Restore_is_two_staged_and_carries_the_handle_from_the_plan_into_apply()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/operations/restore/plan" => Json(HttpStatusCode.OK,
                """
                {"canApply":true,"manifest":null,"mode":"Replace","targetIsEmpty":false,
                 "blockers":[],"warnings":[],"preBackupPath":null,"token":"7f3c9a"}
                """),
            _ => Json(HttpStatusCode.OK,
                """
                {"applied":true,"restoredSections":"Database, KeyRing","preBackupPath":null,
                 "notes":["Datenbank ersetzt."]}
                """),
        });

        var exit = await RunAsync(handler, ["restore", "/data/a.zip", "--replace", "--yes"]);

        exit.Should().Be(OperationsCli.Success);
        handler.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().Equal(
            "/api/v1/operations/restore/plan",
            "/api/v1/operations/restore/apply");
        // Der Plan reist als JSON hinaus und kommt als neues Objekt zurueck — anwendbar ist er nur
        // ueber das Handle darin.
        handler.Bodies[1].Should().Contain("\"token\":\"7f3c9a\"");
    }

    // ── doctor ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, OperationsCli.Success)]
    [InlineData(true, false, OperationsCli.DiagnosticWarning)]
    [InlineData(true, true, OperationsCli.DiagnosticFailure)]
    [InlineData(false, true, OperationsCli.DiagnosticFailure)]
    public async Task Doctor_translates_the_report_into_the_contract_exit_code(
        bool warnings, bool failures, int expected)
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, $$"""
            {"scope":"All","startedAt":"2026-07-31T12:00:00Z","durationMs":42,
             "hasWarnings":{{warnings.ToString().ToLowerInvariant()}},
             "hasFailures":{{failures.ToString().ToLowerInvariant()}},
             "checks":[{"code":"BFR-DB-0002","status":"Pass","summary":"Die Datenbank ist erreichbar."}]}
            """));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["doctor"], output: output);

        exit.Should().Be(expected);
        output.ToString().Should().Contain("BFR-DB-0002");
    }

    [Fact]
    public async Task Doctor_passes_the_scope_through_instead_of_filtering_locally()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"scope":"Database","hasWarnings":false,"hasFailures":false,"checks":[]}"""));

        var exit = await RunAsync(handler, ["doctor", "--scope", "database,network"]);

        exit.Should().Be(OperationsCli.Success);
        handler.Requests.Single().RequestUri!.Query.Should().Contain("scope=database%2Cnetwork");
    }

    // ── config ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Config_export_writes_the_payload_locally()
    {
        var target = Path.Combine(Path.GetTempPath(), $"bifrost-export-{Guid.NewGuid():N}.json");
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"formatVersion":1,"productVersion":"0.11.0","createdAt":"2026-07-31T12:00:00Z",
             "containsSecrets":false,"payload":"{\"servers\":[]}"}
            """));
        try
        {
            var exit = await RunAsync(handler, ["config", "export", "--out", target]);

            exit.Should().Be(OperationsCli.Success);
            (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken))
                .Should().Be("""{"servers":[]}""");
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task Config_import_stops_at_conflicts_without_applying_anything()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bifrost-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, "{}", TestContext.Current.CancellationToken);
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"canApply":false,"additions":[],"conflicts":["Rolle 'admin' existiert bereits mit anderem Inhalt."],
             "missingDependencies":[],"unchanged":[],"token":"x"}
            """));
        var output = new StringWriter();
        try
        {
            var exit = await RunAsync(handler, ["config", "import", file], output: output);

            exit.Should().Be(OperationsCli.ArchiveInvalid);
            handler.Requests.Should().ContainSingle()
                .Which.RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/config/import/plan");
            output.ToString().Should().Contain("Rolle 'admin'");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Config_import_applies_after_the_plan()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bifrost-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, "{}", TestContext.Current.CancellationToken);
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/plan", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK,
                    """
                    {"canApply":true,"additions":["Rolle 'reader'"],"conflicts":[],
                     "missingDependencies":[],"unchanged":[],"token":"tok"}
                    """)
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        try
        {
            var exit = await RunAsync(handler, ["config", "import", file]);

            exit.Should().Be(OperationsCli.Success);
            handler.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().Equal(
                "/api/v1/operations/config/import/plan",
                "/api/v1/operations/config/import/apply");
            handler.Bodies[1].Should().Contain("\"token\":\"tok\"");
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ── db unblock ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Db_unblock_reports_how_many_entries_it_removed()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"removed":1,"hinweis":"Der Riegel ist geloest."}"""));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["db", "unblock"], output: output);

        exit.Should().Be(OperationsCli.Success);
        handler.Requests.Single().RequestUri!.AbsolutePath
            .Should().Be("/api/v1/operations/database/unblock");
        output.ToString().Should().Contain("Der Riegel ist geloest.");
    }

    [Fact]
    public async Task An_unreachable_gateway_names_the_offline_way_out_of_BFR_DB_0101()
    {
        var handler = new ThrowingHandler();
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["db", "unblock"], error: error);

        exit.Should().Be(OperationsCli.UnexpectedError);
        // Genau hier ist der Hinweis wichtig: Bei BFR-DB-0101 startet der Gateway nicht, und dann
        // kann dieser Befehl niemanden fragen.
        error.ToString().Should().Contain("--db-unblock");
    }

    [Fact]
    public async Task An_unknown_option_is_named_instead_of_swallowed()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["restore", "/a.zip", "--replaced"], error: error);

        exit.Should().Be(OperationsCli.UsageError);
        error.ToString().Should().Contain("--replaced");
    }

    // ── Helfer ──────────────────────────────────────────────────────────────────────────────────

    // ── support-bundle (WP5.6) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die Vorschau geht auf einen eigenen Endpunkt — und der Befehl ohne <c>--preview</c> erzeugt
    /// wirklich. Ohne diesen Test koennte die Weiche vertauscht sein, und ein Betreiber haette ein
    /// Archiv, wo er eine Vorschau erwartete.
    /// </summary>
    [Fact]
    public async Task Support_bundle_preview_goes_to_the_preview_endpoint()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"entries":[{"path":"version.json","category":"Version","bytes":120}],
             "skippedCategories":[{"category":"Logs","reason":"kein Anwendungslog"}],
             "estimatedBytes":120}
            """));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["support-bundle", "--preview"], output: output);

        exit.Should().Be(OperationsCli.Success);
        handler.Requests.Single().RequestUri!.AbsolutePath
            .Should().Be("/api/v1/operations/support-bundle/preview");
        output.ToString().Should().Contain("version.json");

        // Eine uebersprungene Kategorie steht in der Ausgabe, nicht nur im Manifest.
        output.ToString().Should().Contain("Logs").And.Contain("kein Anwendungslog");
    }

    [Fact]
    public async Task Support_bundle_creates_and_reports_checksum_and_masking()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"archivePath":"/data/support/b.zip","sizeBytes":2048,
             "sha256":"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
             "manifest":{"redaction":{"pass":"central-v1","redactedFields":3}},
             "skipped":[]}
            """));
        var output = new StringWriter();

        var exit = await RunAsync(
            handler, ["support-bundle", "--out", "/data/support/b.zip", "--categories", "version,runtime"],
            output: output);

        exit.Should().Be(OperationsCli.Success);
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/support-bundle");
        handler.Bodies.Single().Should().Contain("\"categories\":[\"version\",\"runtime\"]");
        output.ToString().Should().Contain("abcdef012345").And.Contain("3 Feld(er)");

        // Der Satz, der einem Empfaenger sagt, was NICHT drin ist.
        output.ToString().Should().Contain("KEINE Auditinhalte");
    }

    [Fact]
    public async Task Support_bundle_with_a_non_numeric_log_line_count_is_a_usage_error()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));

        var exit = await RunAsync(handler, ["support-bundle", "--log-lines", "viele"]);

        exit.Should().Be(OperationsCli.UsageError);
        handler.Requests.Should().BeEmpty("ein Bedienfehler faellt vor dem Netzaufruf auf");
    }

    [Fact]
    public void The_usage_text_mentions_the_support_bundle()
    {
        OperationsCli.UsageText.Should().Contain("support-bundle");
    }

    // ── audit (WP6.2) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_export_passes_the_filter_through_instead_of_interpreting_it()
    {
        var handler = new RecordingHandler(_ => Text("{\"sequence\":1}\n{\"sequence\":2}\n"));
        var output = new StringWriter();

        var exit = await RunAsync(
            handler,
            ["audit", "export", "--format", "csv", "--tool", "github__", "--after", "42"],
            output: output);

        exit.Should().Be(OperationsCli.Success);
        var uri = handler.Requests.Single().RequestUri!;
        uri.AbsolutePath.Should().Be("/api/v1/audit/export");
        uri.Query.Should().Contain("format=csv").And.Contain("tool=github__").And.Contain("after=42");
    }

    /// <summary>
    /// Ohne <c>--out</c> gehen die Zeilen auf die Standardausgabe — damit der Export in eine Pipe
    /// passt, ohne vorher irgendwo vollständig zu entstehen.
    /// </summary>
    [Fact]
    public async Task Audit_export_without_a_target_writes_to_stdout()
    {
        var handler = new RecordingHandler(_ => Text("zeile-eins\nzeile-zwei\n"));
        var output = new StringWriter();

        await RunAsync(handler, ["audit", "export"], output: output);

        output.ToString().Should().Contain("zeile-eins").And.Contain("zeile-zwei");
    }

    [Fact]
    public async Task Audit_export_writes_the_file_and_says_how_big_it_got()
    {
        var handler = new RecordingHandler(_ => Text("a\nb\nc\n"));
        var output = new StringWriter();
        var target = Path.Combine(Path.GetTempPath(), $"bifrost-audit-{Guid.NewGuid():N}.jsonl");

        try
        {
            var exit = await RunAsync(handler, ["audit", "export", "--out", target], output: output);

            exit.Should().Be(OperationsCli.Success);
            (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken))
                .Should().Be("a\nb\nc\n");
            output.ToString().Should().Contain("Bytes");
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// Eine intakte Kette ist Erfolg. Der Bericht trägt trotzdem seine Grenzen — ein „kein Bruch
    /// gefunden", das jemand für „nichts fehlt" hält, ist gefährlicher als gar keine Kette.
    /// </summary>
    [Fact]
    public async Task Audit_verify_reports_an_intact_chain_with_its_limits()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, IntactChainJson));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["audit", "verify"], output: output);

        exit.Should().Be(OperationsCli.Success);
        output.ToString().Should().Contain("kein Bruch");
        output.ToString().Should().Contain("Was diese Pruefung NICHT zeigt")
            .And.Contain("uebernommenen Wirt");
    }

    /// <summary>
    /// <b>Ein Bruch ist ein Befund, kein Programmfehler.</b> Er bekommt deshalb den Diagnose-Code
    /// (4) und nicht den für „etwas ging schief" (1) — ein Skript soll beides unterscheiden können.
    /// </summary>
    [Fact]
    public async Task A_broken_chain_ends_with_the_diagnostic_exit_code_and_names_the_line()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, BrokenChainJson));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["audit", "verify"], output: output);

        exit.Should().Be(OperationsCli.DiagnosticFailure);
        output.ToString().Should().Contain("BRUCH ab Zeile 4711").And.Contain("geaendert");
    }

    [Fact]
    public void The_usage_text_mentions_the_audit_commands()
    {
        OperationsCli.UsageText.Should().Contain("audit export").And.Contain("audit verify");
    }

    private const string IntactChainJson =
        """
        {"checked":120,"unchained":3,"segments":[{"chainId":"11111111-1111-1111-1111-111111111111",
          "firstSequence":4,"lastSequence":123,"count":120}],
         "firstBreak":null,
         "limits":["Die Kette ist kein Beweis fuer Vollstaendigkeit.",
                   "Gegen einen uebernommenen Wirt schuetzt sie nicht."]}
        """;

    private const string BrokenChainJson =
        """
        {"checked":40,"unchained":0,"segments":[],
         "firstBreak":{"sequence":4711,"chainId":"11111111-1111-1111-1111-111111111111",
                       "reason":"Der Inhalt passt nicht zu seinem Kettenglied — geaendert."},
         "limits":["Gegen einen uebernommenen Wirt schuetzt sie nicht."]}
        """;

    // ── policy (WP6.1) ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Der Kern des CLI-Adapters.</b> Nicht „3 Rechte hinzugefügt", sondern jede Identität mit
    /// ihrer Wirkungsebene — sonst ist die Ausgabe eine Zahl, über die niemand entscheiden kann.
    /// </summary>
    [Fact]
    public async Task Policy_plan_names_every_identity_that_gains()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, PlanJson));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["policy", "plan", PackFile()], output: output);

        exit.Should().Be(OperationsCli.Success);
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/policy/plan");
        output.ToString().Should().Contain("anna").And.Contain("bernd").And.Contain("alles");
        output.ToString().Should().Contain("team-standard@1.0.0").And.Contain("Official");
    }

    [Fact]
    public async Task Policy_plan_that_is_not_applicable_ends_with_its_own_exit_code()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK, PlanJson.Replace("\"canApply\": true", "\"canApply\": false", StringComparison.Ordinal)));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["policy", "plan", PackFile()], output: output);

        exit.Should().Be(OperationsCli.ArchiveInvalid);
        output.ToString().Should().Contain("Anwendbar: nein");
    }

    /// <summary>
    /// Ohne Bestätigung wird nichts geschrieben — und der Beleg dafür ist, dass die zweite Anfrage
    /// nie hinausgeht. Eine Prüfung nur auf den Exit-Code hätte einen Apply-Aufruf übersehen.
    /// </summary>
    [Fact]
    public async Task Policy_apply_without_confirmation_writes_nothing()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, PlanJson));
        var output = new StringWriter();

        var exit = await RunAsync(
            handler, ["policy", "apply", PackFile()], input: new StringReader("nein"), output: output);

        exit.Should().Be(OperationsCli.TargetNotEmpty);
        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/policy/plan");
    }

    /// <summary>
    /// Die Rückfrage nennt die Zahl der zusätzlichen Berechtigungen. „Wirklich fortfahren?" ohne
    /// diese Zahl ist eine Frage, auf die jeder ja sagt.
    /// </summary>
    [Fact]
    public async Task Policy_apply_asks_with_the_number_of_additional_rights()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, PlanJson));
        var output = new StringWriter();

        await RunAsync(handler, ["policy", "apply", PackFile()], input: new StringReader("nein"), output: output);

        output.ToString().Should().Contain("2 zusaetzliche Berechtigung(en)");
    }

    [Fact]
    public async Task Policy_apply_carries_the_handle_from_the_plan_and_writes_the_way_back()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("plan", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, PlanJson)
                : Json(HttpStatusCode.OK, ApplicationJson));
        var output = new StringWriter();
        var undoFile = Path.Combine(Path.GetTempPath(), $"bifrost-undo-{Guid.NewGuid():N}.json");

        try
        {
            var exit = await RunAsync(
                handler,
                ["policy", "apply", PackFile(), "--yes", "--undo-out", undoFile],
                output: output);

            exit.Should().Be(OperationsCli.Success);
            handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/policy/apply");
            handler.Bodies[1].Should().Contain("handle-1", "ohne das Handle aus dem Plan gibt es keine Uebernahme");

            File.Exists(undoFile).Should().BeTrue();
            (await File.ReadAllTextAsync(undoFile, TestContext.Current.CancellationToken))
                .Should().Contain("replacedRoles", "in der Datei steht der Rueckweg, nicht nur eine Quittung");
            output.ToString().Should().Contain("policy revert");
        }
        finally
        {
            if (File.Exists(undoFile))
            {
                File.Delete(undoFile);
            }
        }
    }

    /// <summary>
    /// <b>Ohne <c>--undo-out</c> ist der Rückweg weg</b> — er steht nur in der Antwort und wird
    /// nirgends aufbewahrt. Da gehört eine Warnung hin und keine Beruhigung.
    /// </summary>
    [Fact]
    public async Task Policy_apply_without_undo_out_says_the_way_back_is_gone()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("plan", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, PlanJson)
                : Json(HttpStatusCode.OK, ApplicationJson));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["policy", "apply", PackFile(), "--yes"], output: output);

        exit.Should().Be(OperationsCli.Success);
        output.ToString().Should().Contain("WARNUNG").And.Contain("Rueckweg");
    }

    [Fact]
    public async Task Policy_revert_sends_the_saved_way_back()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var output = new StringWriter();
        var undoFile = Path.Combine(Path.GetTempPath(), $"bifrost-undo-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(undoFile, ApplicationJson, TestContext.Current.CancellationToken);

        try
        {
            var exit = await RunAsync(handler, ["policy", "revert", undoFile], output: output);

            exit.Should().Be(OperationsCli.Success);
            handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/operations/policy/revert");
            handler.Bodies.Single().Should().Contain("replacedRoles");
            output.ToString().Should().Contain("zurueckgenommen");
        }
        finally
        {
            File.Delete(undoFile);
        }
    }

    [Fact]
    public void The_usage_text_mentions_the_policy_commands()
    {
        OperationsCli.UsageText.Should().Contain("policy plan").And.Contain("--undo-out");
    }

    /// <summary>Ein Pack-Dokument auf der Platte; der Inhalt prüft der Gateway, nicht die CLI.</summary>
    private static string PackFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bifrost-pack-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"manifest":{},"payload":"{}","signatureBase64":"AA=="}""");
        return path;
    }

    private const string PlanJson =
        """
        {"canApply": true,
         "manifest":{"formatVersion":1,"id":"team-standard","version":"1.0.0",
                     "publisherKeyId":"kid-1","description":"Standard","createdAt":"2026-08-02T12:00:00+00:00"},
         "publisherKeyId":"kid-1","trustLevel":"Official","payloadHash":"abc123",
         "rightsDelta":{"added":[{"identity":"anna","scope":"alles","action":"UseTool"},
                                 {"identity":"bernd","scope":"alles","action":"UseTool"}],
                        "removed":[{"identity":"anna","scope":"github__read","action":"UseTool"}]},
         "additions":["Rolle 'pruefer' wird angelegt."],
         "conflicts":[],"unchanged":[],"updates":["Rolle 'entwickler' wird ersetzt."],
         "missingTools":[],"notes":[],"token":"handle-1"}
        """;

    private const string ApplicationJson =
        """
        {"id":"team-standard","version":"1.0.0","payloadHash":"abc123","snapshotBeforeApply":"{}",
         "undo":{"replacedRoles":[{"id":{"value":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"},
                                   "name":"entwickler","grants":[],"rateLimit":null}],
                 "replacedProfiles":[],"createdRoles":[],"createdProfiles":[],
                 "createdUpstreams":[],"createdGuardRules":[],"createdSkills":[],
                 "setApprovals":[],"previousSettings":null}}
        """;

    private static async Task<int> RunAsync(
        HttpMessageHandler handler,
        string[] command,
        TextReader? input = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://gateway.example/") };
        var cli = new GatewayCli(
            client,
            input ?? TextReader.Null,
            output ?? TextWriter.Null,
            error ?? TextWriter.Null,
            jsonOutput: false);
        return await cli.RunAsync(command, TestContext.Current.CancellationToken);
    }

    /// <summary>Eine Antwort, die kein JSON-Dokument ist — der Export liefert Zeilen, kein Objekt.</summary>
    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return responseFactory(request);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Verbindung abgelehnt.");
    }
}
