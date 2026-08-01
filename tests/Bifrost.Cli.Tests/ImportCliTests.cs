using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Xunit;

namespace Bifrost.Cli.Tests;

/// <summary>
/// Die Importbefehle der CLI (M4, WP4.3).
/// <para>
/// Geprüft wird über <see cref="GatewayCli.RunAsync"/> — also der echte Exit-Code, den
/// <c>Program.cs</c> unverändert an das Betriebssystem zurückgibt, und zugleich die Weiche im
/// Dispatcher. Ein Test, der <see cref="ImportCli"/> direkt aufriefe, ließe sie ungeprüft.
/// </para>
/// </summary>
public class ImportCliTests : IDisposable
{
    private readonly string _datei = Path.Combine(
        Path.GetTempPath(), $"wp43-cli-{Guid.NewGuid():N}.json");

    public ImportCliTests()
        => File.WriteAllText(
            _datei,
            """{"mcpServers":{"github":{"command":"npx","args":["-y","@beispiel/server"]}}}""");

    public void Dispose()
    {
        File.Delete(_datei);
        GC.SuppressFinalize(this);
    }

    private const string Vorschau =
        """
        {"source":{"provider":"mcp","schemaVersion":null,"confidence":0.6,"originPath":null},
         "candidates":[{"sourceName":"github","slug":"github","displayName":"GitHub",
           "kind":"Stdio","enabled":false,
           "transport":{"kind":"Stdio","program":"npx","argumentCount":2,
             "environmentNames":["GITHUB_TOKEN"]},
           "findings":[],"secrets":[]}],
         "findings":[],"canApply":true,"requiresConfirmation":[],
         "token":"handle-1","expiresAt":"2026-08-01T12:00:00+00:00"}
        """;

    /// <summary>
    /// <b>Die tragende Zusage der CLI:</b> Die Datei wird hier gelesen und im Rumpf übertragen. Ein
    /// Pfad geht höchstens als Herkunftsangabe mit — ein Endpunkt, der einen Pfad entgegennimmt und
    /// ihn serverseitig öffnet, wäre ein Werkzeug zum Auslesen fremder Dateien.
    /// </summary>
    [Fact]
    public async Task Preview_sends_the_file_content_and_never_only_its_path()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, Vorschau));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["import", "preview", _datei], output: output);

        exit.Should().Be(GatewayCli.Success);
        var request = handler.Requests.Single();
        request.RequestUri!.AbsolutePath.Should().Be("/api/v1/import/preview");
        handler.Bodies.Single().Should().Contain("mcpServers")
            .And.Contain("npx", "der INHALT reist, nicht der Pfad");
        handler.Bodies.Single().Should().NotContain(_datei);
        output.ToString().Should().Contain("github").And.Contain("npx");
    }

    [Fact]
    public async Task An_origin_path_travels_as_a_query_label()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, Vorschau));

        var exit = await RunAsync(
            handler, ["import", "preview", _datei, "--origin", "/heim/nutzer/.config/mcp.json"]);

        exit.Should().Be(GatewayCli.Success);
        handler.Requests.Single().RequestUri!.Query
            .Should().Contain("originPath=").And.Contain("mcp.json");
    }

    [Fact]
    public async Task An_unknown_option_is_a_usage_error_before_anything_runs()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, Vorschau));
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["import", "preview", _datei, "--gibts-nicht"], error: error);

        exit.Should().Be(GatewayCli.UsageError);
        handler.Requests.Should().BeEmpty("ein Bedienfehler faellt auf, bevor irgendetwas laeuft");
        error.ToString().Should().Contain("--gibts-nicht");
    }

    /// <summary>Ein nicht anwendbarer Plan endet vor der Übernahme — und ohne zweite Anfrage.</summary>
    [Fact]
    public async Task An_unusable_plan_stops_before_the_commit()
    {
        var nichtAnwendbar = Vorschau
            .Replace("\"canApply\":true", "\"canApply\":false", StringComparison.Ordinal);
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, nichtAnwendbar));

        var exit = await RunAsync(handler, ["import", "apply", _datei]);

        exit.Should().Be(GatewayCli.GatewayError);
        handler.Requests.Should().ContainSingle(
            "nach einem nicht anwendbaren Plan gibt es nichts mehr anzufragen");
    }

    /// <summary><c>--dry-run</c> merkt vor und übernimmt nicht.</summary>
    [Fact]
    public async Task Dry_run_previews_and_writes_nothing()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, Vorschau));
        var output = new StringWriter();

        var exit = await RunAsync(handler, ["import", "apply", _datei, "--dry-run"], output: output);

        exit.Should().Be(GatewayCli.Success);
        handler.Requests.Should().ContainSingle();
        output.ToString().Should().Contain("--dry-run");
    }

    /// <summary>
    /// Der ganze Weg: Vorschau, dann Übernahme mit Handle, Auswahl und Isolationsentscheidung.
    /// </summary>
    [Fact]
    public async Task Apply_carries_the_handle_the_selection_and_the_isolation_decision()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK,
                    """{"imported":[{"id":"11111111-1111-1111-1111-111111111111","slug":"github"}],"count":1}""")
                : Json(HttpStatusCode.OK, Vorschau));
        var output = new StringWriter();

        var exit = await RunAsync(
            handler,
            ["import", "apply", _datei, "--only", "github", "--isolation", "container",
             "--image", "ghcr.io/beispiel/server:1", "--confirm-risks"],
            output: output);

        exit.Should().Be(GatewayCli.Success);
        handler.Requests.Should().HaveCount(2);

        using var commit = JsonDocument.Parse(handler.Bodies[1]);
        commit.RootElement.GetProperty("token").GetString().Should().Be("handle-1");
        commit.RootElement.GetProperty("confirmRisks").GetBoolean().Should().BeTrue();
        var server = commit.RootElement.GetProperty("servers").EnumerateArray().Single();
        server.GetProperty("sourceName").GetString().Should().Be("github");
        server.GetProperty("isolation").GetString().Should().Be("Container",
            "die Kommandozeile schreibt klein, der Vertrag gross — die Umschrift gehoert in die CLI");
        server.GetProperty("containerImage").GetString().Should().Be("ghcr.io/beispiel/server:1");

        output.ToString().Should().Contain("Uebernommen: 1 Server").And.Contain("github");
    }

    /// <summary>
    /// Eine fehlende Bestätigung ist ein eigener Exit-Code — sie sagt „bestaetige das", nicht „das
    /// ging schief". Ein Skript soll den Unterschied sehen können.
    /// </summary>
    [Fact]
    public async Task A_missing_confirmation_gets_its_own_exit_code()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Conflict,
                    """{"error":{"code":"confirmation-required","message":"BFR-IMP-0103 laedt Code nach."}}""")
                : Json(HttpStatusCode.OK, Vorschau));
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["import", "apply", _datei], error: error);

        exit.Should().Be(GatewayCli.ApprovalRequired);
        error.ToString().Should().Contain("BFR-IMP-0103");
    }

    /// <summary>Ein verbrauchtes oder unbekanntes Handle ist keine Bedienfrage, sondern ein Konflikt.</summary>
    [Fact]
    public async Task An_unknown_handle_is_reported_as_a_gateway_conflict()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Conflict,
                    """{"error":{"code":"handle-unknown","message":"Das Handle ist verbraucht."}}""")
                : Json(HttpStatusCode.OK, Vorschau));
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["import", "apply", _datei], error: error);

        exit.Should().Be(GatewayCli.GatewayError);
        error.ToString().Should().Contain("verbraucht");
    }

    /// <summary>
    /// <c>--probe</c> testet vor der Übernahme — und eine gescheiterte Probe verhindert sie.
    /// </summary>
    [Fact]
    public async Task A_failed_probe_stops_the_commit()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/probe", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK,
                    """{"sourceName":"github","slug":"github","success":false,"toolCount":0,"error":"Programm nicht gefunden"}""")
                : Json(HttpStatusCode.OK, Vorschau));
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["import", "apply", _datei, "--probe"], error: error);

        exit.Should().Be(GatewayCli.GatewayError);
        handler.Requests.Should().HaveCount(2, "nach einer gescheiterten Probe wird nicht uebernommen");
        handler.Requests[1].RequestUri!.AbsolutePath.Should().EndWith("/probe");
        error.ToString().Should().Contain("Probe nicht bestanden");
    }

    [Fact]
    public async Task Too_large_a_document_is_reported_as_a_usage_error()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.RequestEntityTooLarge,
            """{"error":{"code":"too-large","message":"Das Dokument ist groesser als 1024 KiB."}}"""));
        var error = new StringWriter();

        var exit = await RunAsync(handler, ["import", "preview", _datei], error: error);

        exit.Should().Be(GatewayCli.UsageError);
        error.ToString().Should().Contain("1024 KiB");
    }

    [Fact]
    public void The_usage_text_names_the_import_commands()
    {
        GatewayCli.UsageText.Should().Contain("import preview")
            .And.Contain("import apply")
            .And.Contain("Herkunftsangabe");
    }

    // ── Helfer ──────────────────────────────────────────────────────────────────────────────────

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
}
