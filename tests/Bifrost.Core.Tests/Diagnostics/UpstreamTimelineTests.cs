using System.Text.Json;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Upstreams;
using Bifrost.Core.Execution;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

/// <summary>
/// Die Zeitlinie eines Verbindungsversuchs (WP4.6).
/// <para>
/// Die Fehlerklasse, gegen die dieses Paket antritt, ist eine einzige Zeile: „Verbindung
/// fehlgeschlagen". Sie nennt das Ergebnis und verschweigt jede Zwischenstufe — ein Betreiber weiss
/// danach nicht, ob der Name nicht auflöst, das Ziel abgewiesen wurde, die Anmeldung scheiterte
/// oder die Gegenstelle ein kaputtes Schema liefert. Das sind vier völlig verschiedene Handlungen.
/// </para>
/// <para>
/// Jede Stufe hat deshalb hier ihr eigenes Fixture, und dazu kommt die Regel, ohne die das Ganze
/// wertlos wäre: <b>Die erste scheiternde Stufe beendet die Kette.</b> Ein Bericht, in dem nach der
/// ersten Ursache noch sechs Folgefehler stehen, ist wieder eine Sackgasse, nur länger.
/// </para>
/// </summary>
public class UpstreamTimelineTests
{
    // ───────────────────────── Fixtures je Stufe ─────────────────────────

    /// <summary>Stufe 1 — der Aufbau stimmt nicht. Der Versuch beginnt gar nicht erst.</summary>
    [Fact]
    public async Task Stufe1_Validierung_faengt_eine_kaputte_Konfiguration_ab()
    {
        var tester = new RecordingTester(new UpstreamTestResult(true, 3, null));
        var report = await RunAsync(
            Http() with { Slug = "UNZULÄSSIG" }, tester);

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamValidation);
        tester.Calls.Should().Be(0,
            "was die Aufbauprüfung abweist, würde auch das Anschliessen abweisen — es hat keinen "
            + "Grund, dafür eine Verbindung nach draussen aufzumachen");
    }

    /// <summary>
    /// Stufe 2 — die Ausführungs-Policy (ADR-0025 E4). Ohne Policy heisst „unentschieden" nein, und
    /// das muss als eigene Ursache dastehen und nicht als Formfehler.
    /// </summary>
    [Fact]
    public async Task Stufe2_Policy_verweigert_den_nativen_Start()
    {
        var tester = new RecordingTester(new UpstreamTestResult(true, 1, null));
        var report = await RunAsync(Stdio(), tester, policy: null);

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamPolicy);
        Failure(report).Check.Remediation.Should().NotBeNullOrWhiteSpace();
        tester.Calls.Should().Be(0);
    }

    /// <summary>Stufe 3 — der Name löst nicht auf. Das ist keine „Verbindung fehlgeschlagen".</summary>
    [Fact]
    public async Task Stufe3_Runtime_meldet_einen_Namen_der_nicht_aufloest()
    {
        var report = await RunAsync(
            Http(),
            new RecordingTester(new UpstreamTestResult(true, 1, null)),
            resolution: new FakeResolutionProbe(
                new HostResolution(false, [], "Kein solcher Host ist bekannt.")));

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamRuntime);
        Failure(report).Check.Summary.Should().Contain("example.invalid");
    }

    /// <summary>Stufe 3, nativer Zweig — das zu startende Programm liegt nicht da.</summary>
    [Fact]
    public async Task Stufe3_Runtime_meldet_ein_fehlendes_Programm()
    {
        var report = await RunAsync(
            Stdio(),
            new RecordingTester(new UpstreamTestResult(true, 1, null)),
            policy: HostExecutionPolicy.AllowedByOperator(),
            files: new FakeFileProbe());

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamRuntime);
    }

    /// <summary>
    /// Stufe 4 — Zielschutz. Der Name löst auf, aber nach innen. Ohne diese Prüfung wäre das
    /// Gateway ein Werkzeug, um interne Dienste zu erreichen.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.178.61")]
    [InlineData("::1")]
    public async Task Stufe4_Zielschutz_weist_ein_internes_Ziel_ab(string address)
    {
        var report = await RunAsync(
            Http(),
            new RecordingTester(new UpstreamTestResult(true, 1, null)),
            resolution: new FakeResolutionProbe(new HostResolution(true, [address])));

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamTargetGuard);
        Failure(report).Check.Summary.Should().Contain(address);
        Failure(report).Check.Remediation.Should().Contain("internen Netz");
    }

    /// <summary>
    /// Die Gegenprobe: Ohne sie wäre eine Stufe, die <em>alles</em> abweist, fünfmal grün und
    /// trotzdem kaputt.
    /// </summary>
    [Fact]
    public async Task Stufe4_laesst_ein_oeffentliches_Ziel_durch()
    {
        var report = await RunAsync(
            Http(),
            new RecordingTester(new UpstreamTestResult(true, 2, null)),
            resolution: new FakeResolutionProbe(new HostResolution(true, ["93.184.216.34"])));

        report.Succeeded.Should().BeTrue();
        Outcome(report, UpstreamStage.TargetGuard).Should().Be(UpstreamStageOutcome.Passed);
    }

    /// <summary>Stufe 5 — die Gegenstelle lehnt die Anmeldung ab.</summary>
    [Theory]
    [InlineData("Response status code does not indicate success: 401 (Unauthorized).")]
    [InlineData("invalid_client")]
    [InlineData("AuthKind Bearer verlangt ein Credential.")]
    public async Task Stufe5_Anmeldung_wird_als_eigene_Ursache_gemeldet(string error)
    {
        var report = await RunAsync(
            HttpWithCredentials(),
            new RecordingTester(new UpstreamTestResult(false, 0, error)),
            resolution: PublicTarget);

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamAuth);
        Outcome(report, UpstreamStage.Handshake).Should().Be(UpstreamStageOutcome.NotReached);
        Outcome(report, UpstreamStage.Discovery).Should().Be(UpstreamStageOutcome.NotReached);
    }

    /// <summary>Stufe 6 — Transport oder Protokoll. Die Anmeldung wurde nie vorgelegt.</summary>
    [Theory]
    [InlineData("No connection could be made because the target machine actively refused it.")]
    [InlineData("Zeitüberschreitung nach 15 s.")]
    [InlineData("The SSL connection could not be established.")]
    public async Task Stufe6_Handshake_wird_als_eigene_Ursache_gemeldet(string error)
    {
        var report = await RunAsync(
            Http(),
            new RecordingTester(new UpstreamTestResult(false, 0, error)),
            resolution: PublicTarget);

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamHandshake);
        Outcome(report, UpstreamStage.Discovery).Should().Be(UpstreamStageOutcome.NotReached);
    }

    /// <summary>
    /// Stufe 7 — der Katalog kam an und war unbrauchbar. Das ist ein Fehler der Beschreibung, nicht
    /// der Verbindung, und genau dieser Unterschied entscheidet, wen der Betreiber anruft.
    /// </summary>
    [Theory]
    [InlineData("Dokument ist kein gültiges JSON: unerwartetes Zeichen.")]
    [InlineData("Spec enthält kein 'paths'-Objekt.")]
    [InlineData("Swagger 2.0 wird nicht unterstützt — bitte nach OpenAPI 3.x konvertieren.")]
    public async Task Stufe7_Discovery_wird_als_eigene_Ursache_gemeldet(string error)
    {
        var report = await RunAsync(
            Http(),
            new RecordingTester(new UpstreamTestResult(false, 0, error)),
            resolution: PublicTarget);

        Failure(report).Code.Should().Be(DiagnosticCodes.UpstreamDiscovery);
        Outcome(report, UpstreamStage.Handshake).Should().Be(UpstreamStageOutcome.Passed,
            "wer bis zur Discovery gekommen ist, hat den Handshake hinter sich");
    }

    // ───────────────────────── Die Regel der Kette ─────────────────────────

    /// <summary>
    /// <b>Die erste scheiternde Stufe beendet die Kette.</b> Höchstens eine Stufe ist gescheitert,
    /// und alles danach ist <i>nicht erreicht</i> — nicht „auch kaputt".
    /// </summary>
    [Theory]
    [MemberData(nameof(JedesFixture))]
    public async Task Die_erste_scheiternde_Stufe_beendet_die_Kette(string name)
    {
        var report = await FixtureAsync(name);

        report.Stages.Select(stage => stage.Stage).Should().Equal(UpstreamStages.All,
            "eine Stufe, die im Bericht fehlt, ist die stille Lücke, gegen die das Modell gebaut ist");

        var failed = report.Stages.Where(s => s.Outcome is UpstreamStageOutcome.Failed).ToList();
        failed.Should().HaveCountLessThanOrEqualTo(1,
            $"'{name}': zwei Ursachen sind keine Antwort, sondern eine längere Sackgasse");

        if (failed.Count == 1)
        {
            report.Stages
                .SkipWhile(stage => stage.Stage != failed[0].Stage)
                .Skip(1)
                .Should().OnlyContain(stage => stage.Outcome == UpstreamStageOutcome.NotReached,
                    $"'{name}': über die Stufen nach der Ursache ist nichts bekannt");
        }
    }

    /// <summary>Jede Stufe trägt einen Code, den es in der Codeliste wirklich gibt.</summary>
    [Theory]
    [MemberData(nameof(JedesFixture))]
    public async Task Jede_Stufe_traegt_einen_vergebenen_Code(string name)
    {
        var report = await FixtureAsync(name);

        report.Stages.Select(stage => stage.Code).Should().OnlyHaveUniqueItems();
        report.Stages.Select(stage => stage.Code).Should().BeSubsetOf(DiagnosticCodes.All);
    }

    /// <summary>
    /// Eine gescheiterte Stufe ohne Abhilfe ist eine Sackgasse mit Nummer. Der Code sagt <em>was</em>,
    /// die Abhilfe sagt <em>was jetzt</em> — ohne beides war die alte Meldung auch schon.
    /// </summary>
    [Theory]
    [MemberData(nameof(JedesFixture))]
    public async Task Jede_gescheiterte_Stufe_nennt_eine_konkrete_Abhilfe(string name)
    {
        var report = await FixtureAsync(name);

        foreach (var stage in report.Stages.Where(s => s.Outcome is UpstreamStageOutcome.Failed))
        {
            stage.Check.Remediation.Should().NotBeNullOrWhiteSpace();
            stage.Check.Remediation!.Length.Should().BeGreaterThan(30,
                "'Konfiguration pruefen' ist keine Handlung, sondern eine Umformulierung des Problems");
        }
    }

    /// <summary>Jeder Lauf trägt eine Kennung, mit der man ihn im Serverlog wiederfindet.</summary>
    [Fact]
    public async Task Jeder_Lauf_traegt_eine_eigene_Request_Id()
    {
        var first = await FixtureAsync("handshake");
        var second = await FixtureAsync("handshake");

        first.RequestId.Should().NotBeNullOrWhiteSpace();
        first.RequestId.Should().NotBe(second.RequestId);
        first.Headline().Should().Contain(first.RequestId);
    }

    // ───────────────────────── Derselbe Weg wie die Aktivierung ─────────────────────────

    /// <summary>
    /// Der Verbindungstest geht über den <b>vorhandenen</b> Verbindungstest, der seinerseits
    /// Connector und Discovery aufruft — genau wie der Supervisor beim Anschliessen. Zwei Wege wären
    /// zwei Wahrheiten darüber, ob eine Konfiguration funktioniert, und der Test wäre irgendwann
    /// grün, wo das Anschliessen scheitert.
    /// </summary>
    [Fact]
    public async Task Der_Test_reicht_die_unveraenderte_Konfiguration_an_den_echten_Weg_durch()
    {
        var config = Http();
        var tester = new RecordingTester(new UpstreamTestResult(true, 7, null));

        var report = await RunAsync(config, tester, resolution: PublicTarget);

        tester.Calls.Should().Be(1, "genau ein Versuch, und zwar der echte");
        tester.Seen.Should().BeSameAs(config,
            "eine für den Test zurechtgelegte Konfiguration wäre nicht die, die angeschlossen wird");
        report.Negotiation!.ToolCount.Should().Be(7);
    }

    // ───────────────────────── Redaktion ─────────────────────────

    /// <summary>
    /// Der Bericht ist die Ausgabe, die ein Betreiber im Störungsfall weitergibt — in ein Ticket,
    /// in einen Chat, an den Hersteller. Was hier durchkommt, verlässt das Haus.
    /// <para>
    /// Der Korpus ist nach dem Muster aus <c>Bifrost.Security.Tests/Infrastructure/SecretCorpus.cs</c>
    /// gebaut: erfundene Werte ohne erkennbares Muster, damit der Test die <em>Redaktion</em> prüft
    /// und nicht die Mustererkennung der Guardrail.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Verbindung fehlgeschlagen: Password={0}")]
    [InlineData("Authorization: Bearer {0}")]
    [InlineData("https://benutzer:{0}@ziel.invalid/mcp")]
    [InlineData("client_secret = \"{0}\"")]
    [InlineData("api-key: {0}")]
    [InlineData("Token={0} wurde abgelehnt")]
    public async Task Kein_Zugangsdatum_erreicht_den_Bericht(string template)
    {
        foreach (var secret in Corpus)
        {
            var error = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, secret);
            error.Should().Contain(secret, "sonst prüft der Test nichts");

            var report = await RunAsync(
                HttpWithCredentials(),
                new RecordingTester(new UpstreamTestResult(false, 0, error)),
                resolution: PublicTarget);

            JsonSerializer.Serialize(report).Should().NotContain(secret,
                $"'{template}' ginge so in einen Bericht, den jemand weitergibt");
        }
    }

    /// <summary>
    /// Auch die Angaben, die der Bericht selbst zusammensetzt: Ein Host aus der Konfiguration steht
    /// in den Details, ein Zugangsdatum nie — auch nicht als Teil einer URL.
    /// </summary>
    [Fact]
    public async Task Auch_selbst_gebaute_Details_tragen_kein_Zugangsdatum()
    {
        var config = new UpstreamServerConfig(
            "geheim", "Geheim", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                new Uri($"https://nutzer:{Corpus[0]}@ziel.invalid/mcp"),
                Headers: new Dictionary<string, string> { ["Authorization"] = Corpus[1] },
                AllowPrivateTargets: false));

        var report = await RunAsync(
            config,
            new RecordingTester(new UpstreamTestResult(true, 1, null)),
            resolution: PublicTarget);

        var serialized = JsonSerializer.Serialize(report);
        foreach (var secret in Corpus)
        {
            serialized.Should().NotContain(secret);
        }
    }

    // ───────────────────────── DoD: jede heutige Meldung hat einen Code ─────────────────────────

    /// <summary>
    /// Die Meldungen, die der Verbindungstest <b>heute</b> ausgibt — wörtlich aus
    /// <c>UpstreamConnectionTester</c> und den Konnektoren in <c>Bifrost.Upstream</c>. Jede davon
    /// muss eine Stufe treffen, und zwar über ein bekanntes Muster und nicht über den Rückfall.
    /// <para>
    /// Eine Meldung ohne Code ist eine unerledigte Zeile: Der Betreiber sieht wieder einen Satz und
    /// hat wieder keinen Anker für Runbook, Suche oder Alarmregel.
    /// </para>
    /// </summary>
    public static TheoryData<string, UpstreamStage> HeutigeMeldungen() => new()
    {
        // UpstreamConnectionTester selbst
        { "Kein Connector für Transport OpenApi.", UpstreamStage.Runtime },
        { "Zeitüberschreitung nach 15 s.", UpstreamStage.Handshake },

        // Zielprüfung (RemoteSpecFetcher)
        { "Ziel 'intern' zeigt auf die interne Adresse 10.0.0.5.", UpstreamStage.TargetGuard },
        { "Host 'ziel' ließ sich nicht auflösen: Kein solcher Host.", UpstreamStage.Runtime },
        { "Mehr als 3 Weiterleitungen — abgebrochen.", UpstreamStage.Handshake },
        { "Weiterleitung ohne Ziel.", UpstreamStage.Handshake },
        { "Dokument überschreitet 10 MB.", UpstreamStage.Discovery },

        // Anmeldung (OpenAPI/OpenRPC/OAuth)
        { "Auth-Art gesetzt, aber kein Credential hinterlegt.", UpstreamStage.Auth },
        { "AuthKind Bearer verlangt ein Credential.", UpstreamStage.Auth },
        { "AuthKind Digest wird nicht unterstützt.", UpstreamStage.Auth },
        { "Token-Antwort ohne 'access_token'.", UpstreamStage.Auth },
        { "Metadaten ohne 'token_endpoint'.", UpstreamStage.Auth },

        // Discovery / Schema
        { "Dokument ist kein gültiges JSON: unerwartetes Zeichen.", UpstreamStage.Discovery },
        { "Dokument ist kein JSON-Objekt.", UpstreamStage.Discovery },
        { "Spec-Wurzel muss ein JSON-Objekt sein.", UpstreamStage.Discovery },
        { "Spec enthält kein 'paths'-Objekt.", UpstreamStage.Discovery },
        { "Spec enthält keine importierbaren Operationen.", UpstreamStage.Discovery },
        { "Swagger 2.0 wird nicht unterstützt — bitte nach OpenAPI 3.x konvertieren.", UpstreamStage.Discovery },
        { "Dokument enthält keine 'methods'-Liste.", UpstreamStage.Discovery },
        { "Dokument beschreibt keine Methoden.", UpstreamStage.Discovery },
        { "Methode ohne 'name'.", UpstreamStage.Discovery },
        { "'rpc.discover' lieferte kein Ergebnis.", UpstreamStage.Discovery },
        { "Referenz '#/x' in components zeigt ins Leere.", UpstreamStage.Discovery },
        { "operationId 'lesen' ist mehrfach vergeben.", UpstreamStage.Discovery },

        // WASI-Host
        { "WASI-Host ist nicht bereit (Phase 'hello').", UpstreamStage.Handshake },
        { "WASI-Host wurde beendet.", UpstreamStage.Handshake },
        { "WASI-Host meldet keinen gesunden Zustand.", UpstreamStage.Handshake },
        { "WASI-Host spricht Protokoll '3', erwartet '4'.", UpstreamStage.Handshake },

        // Pfade und Programme (CLI/stdio)
        { "CLI-Pfad '/opt/x' wurde nicht gefunden.", UpstreamStage.Runtime },
        { "Cannot connect to the Docker daemon at unix:///var/run/docker.sock.", UpstreamStage.Runtime },

        // Fremdtexte der Laufzeit, die heute ungefiltert in der Zeile landen
        { "Response status code does not indicate success: 401 (Unauthorized).", UpstreamStage.Auth },
        { "Response status code does not indicate success: 403 (Forbidden).", UpstreamStage.Auth },
        { "Response status code does not indicate success: 404 (Not Found).", UpstreamStage.Handshake },
        { "No such host is known.", UpstreamStage.Runtime },
        { "No connection could be made because the target machine actively refused it.", UpstreamStage.Handshake },
        { "The SSL connection could not be established.", UpstreamStage.Handshake },
        { "The operation was canceled due to a timeout.", UpstreamStage.Handshake },
    };

    [Theory]
    [MemberData(nameof(HeutigeMeldungen))]
    public void Jede_heutige_Fehlermeldung_bekommt_eine_Stufe_und_einen_Code(
        string message, UpstreamStage expected)
    {
        var verdict = UpstreamFailureCatalog.Classify(message, UpstreamTransportKind.StreamableHttp);

        verdict.Confident.Should().BeTrue(
            $"'{message}' ist eine Meldung, die es heute gibt — sie darf nicht im Rückfall landen");
        verdict.Stage.Should().Be(expected, $"'{message}'");
        UpstreamStages.Code(verdict.Stage).Should().BeOneOf(DiagnosticCodes.All);
        verdict.Remediation.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Die Gegenprobe: Ein Satz, den niemand kennt, wird als Rückfall gekennzeichnet — und nicht
    /// mit einer erfundenen Sicherheit einsortiert. Eine geratene Zuordnung, die sich sicher gibt,
    /// schickt jemanden in die falsche Richtung.
    /// </summary>
    [Fact]
    public void Ein_unbekannter_Satz_wird_als_Rueckfall_gekennzeichnet()
    {
        var verdict = UpstreamFailureCatalog.Classify(
            "Der Kater lag auf der Tastatur.", UpstreamTransportKind.Stdio);

        verdict.Confident.Should().BeFalse();
        verdict.Stage.Should().Be(UpstreamStage.Handshake,
            "der Rückfall trifft die früheste der drei Stufen im echten Versuch — die späteren als "
            + "gescheitert zu melden hiesse zu behaupten, die früheren seien durchgelaufen");
    }

    // ───────────────────────── Fixtures ─────────────────────────

    /// <summary>
    /// Erfundene Zugangsdaten. Bewusst ohne erkennbares Muster (kein <c>sk-live-…</c>): Ein Wert mit
    /// Muster würde von der Guardrail gefangen und liesse den Test auch dort grün aussehen, wo gar
    /// keine Redaktion stattfindet.
    /// </summary>
    private static readonly string[] Corpus =
    [
        "KORPUS-updiag-QvXmTsLbNaHkRw",
        "KORPUS-uphead-ZdPcYnEuJgWfTb",
    ];

    private static readonly FakeResolutionProbe PublicTarget =
        new(new HostResolution(true, ["93.184.216.34"]));

    public static TheoryData<string> JedesFixture() =>
    [
        "validierung", "policy", "dns", "programm", "zielschutz",
        "auth", "handshake", "discovery", "erfolg",
    ];

    private static Task<UpstreamDiagnosticReport> FixtureAsync(string name) => name switch
    {
        "validierung" => RunAsync(Http() with { Slug = "UNZULÄSSIG" }, Ok()),
        "policy" => RunAsync(Stdio(), Ok(), policy: null),
        "dns" => RunAsync(Http(), Ok(),
            resolution: new FakeResolutionProbe(new HostResolution(false, [], "unbekannt"))),
        "programm" => RunAsync(Stdio(), Ok(),
            policy: HostExecutionPolicy.AllowedByOperator(), files: new FakeFileProbe()),
        "zielschutz" => RunAsync(Http(), Ok(),
            resolution: new FakeResolutionProbe(new HostResolution(true, ["127.0.0.1"]))),
        "auth" => RunAsync(HttpWithCredentials(),
            new RecordingTester(new UpstreamTestResult(false, 0, "401 Unauthorized")),
            resolution: PublicTarget),
        "handshake" => RunAsync(Http(),
            new RecordingTester(new UpstreamTestResult(false, 0, "connection refused")),
            resolution: PublicTarget),
        "discovery" => RunAsync(Http(),
            new RecordingTester(new UpstreamTestResult(false, 0, "Dokument ist kein gültiges JSON: x")),
            resolution: PublicTarget),
        _ => RunAsync(Http(), Ok(), resolution: PublicTarget),
    };

    private static RecordingTester Ok() => new(new UpstreamTestResult(true, 2, null));

    private static UpstreamServerConfig Http() => new(
        "ziel", "Ziel", UpstreamTransportKind.StreamableHttp, true,
        Http: new HttpTransportOptions(
            new Uri("https://example.invalid/mcp"), AllowPrivateTargets: false));

    private static UpstreamServerConfig HttpWithCredentials() => new(
        "ziel", "Ziel", UpstreamTransportKind.StreamableHttp, true,
        Http: new HttpTransportOptions(
            new Uri("https://example.invalid/mcp"),
            Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer x" },
            AllowPrivateTargets: false));

    private static UpstreamServerConfig Stdio() => new(
        "lokal", "Lokal", UpstreamTransportKind.Stdio, true,
        Stdio: new StdioTransportOptions(
            OperatingSystem.IsWindows() ? @"C:\gibt\es\nicht.exe" : "/gibt/es/nicht", []));

    private static Task<UpstreamDiagnosticReport> RunAsync(
        UpstreamServerConfig config,
        RecordingTester tester,
        Bifrost.Abstractions.Execution.IHostExecutionPolicy? policy = null,
        IHostResolutionProbe? resolution = null,
        FakeFileProbe? files = null)
        => new UpstreamConnectionDiagnostics(
                tester,
                policy,
                resolution ?? PublicTarget,
                files ?? new FakeFileProbe(),
                new FakeProcessProbe())
            .DiagnoseAsync(config, TestContext.Current.CancellationToken);

    private static UpstreamStageResult Failure(UpstreamDiagnosticReport report)
        => report.FirstFailure ?? throw new InvalidOperationException(
            "Der Bericht nennt keine gescheiterte Stufe — dann prüft dieser Test nichts.");

    private static UpstreamStageOutcome Outcome(UpstreamDiagnosticReport report, UpstreamStage stage)
        => report.Stages.Single(result => result.Stage == stage).Outcome;

    private sealed class RecordingTester(UpstreamTestResult result) : IUpstreamConnectionTester
    {
        public int Calls { get; private set; }

        public UpstreamServerConfig? Seen { get; private set; }

        public Task<UpstreamTestResult> TestAsync(UpstreamServerConfig config, CancellationToken ct)
        {
            Calls++;
            Seen = config;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeResolutionProbe(HostResolution resolution) : IHostResolutionProbe
    {
        public Task<HostResolution> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult(resolution);
    }
}
