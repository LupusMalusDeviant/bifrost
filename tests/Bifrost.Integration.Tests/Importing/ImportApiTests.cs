using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Security.Tests.Infrastructure;
using Bifrost.Server.Importing;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Bifrost.Integration.Tests.Importing;

/// <summary>
/// Die Import-API am laufenden Gateway (WP4.3): Vorschau mit Handle, Übernahme in einem Stück,
/// Eingangsgrenzen.
/// </summary>
public class ImportApiTests : IClassFixture<ImportGatewayFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ImportGatewayFixture _gateway;

    public ImportApiTests(ImportGatewayFixture gateway) => _gateway = gateway;

    /// <summary>Die Audit-Eintraege dieses Pakets, aus dem laufenden Dienst.</summary>
    private async Task<IReadOnlyList<AuditEvent>> EintraegeAsync(CancellationToken ct)
    {
        var seite = await _gateway.AuditQuery
            .QueryAsync(new AuditFilter(ToolPrefix: "import-", PageSize: 200), ct);
        return [.. seite.Items.Where(item => item.Tool is not null)];
    }

    // ── Vorschau ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Der Nachweis am echten Endpunkt: Was durch die Leitung geht, trägt keinen Wert aus der
    /// Quelle — auch kein Bruchstück. Die Werte bleiben beim Dienst, hinter dem Handle.
    /// </summary>
    [Fact]
    public async Task The_preview_response_carries_the_handle_and_no_value_from_the_source()
    {
        var client = await AdminAsync("wp43-vorschau");

        var document = $$"""
            {
              "mcpServers": {
                "github": {
                  "command": "/usr/local/bin/mcp-github",
                  "args": ["--api-key", "{{SecretCorpus.ToolArgument}}"],
                  "env": { "GITHUB_TOKEN": "{{SecretCorpus.StdioEnv}}" }
                },
                "remote": {
                  "url": "https://api.example/mcp?token={{SecretCorpus.OAuthToken}}",
                  "headers": { "Authorization": "Bearer {{SecretCorpus.HttpHeader}}" }
                }
              }
            }
            """;

        var (status, body) = await PreviewAsync(client, document);
        status.Should().Be(HttpStatusCode.OK);

        SecretCorpus.FirstLeakIn(body).Should().BeNull(
            "die Vorschau ist eine Positivliste, keine Maskierung. Antwort:\n" + body);

        using var parsed = JsonDocument.Parse(body);
        var root = parsed.RootElement;
        root.GetProperty("token").GetString().Should().NotBeNullOrWhiteSpace(
            "ohne Handle gaebe es keinen Weg von der Vorschau zur Uebernahme, der die Werte "
            + "nicht durch die Leitung schickt");
        root.GetProperty("candidates").GetArrayLength().Should().Be(2);

        // Die Auskunft ist trotzdem da: Namen und Anzahlen statt Werte.
        var github = root.GetProperty("candidates").EnumerateArray()
            .Single(c => c.GetProperty("sourceName").GetString() == "github")
            .GetProperty("transport");
        github.GetProperty("program").GetString().Should().Be("/usr/local/bin/mcp-github");
        github.GetProperty("argumentCount").GetInt32().Should().Be(2);
        github.GetProperty("environmentNames").EnumerateArray()
            .Select(n => n.GetString()).Should().Contain("GITHUB_TOKEN");

        var remote = root.GetProperty("candidates").EnumerateArray()
            .Single(c => c.GetProperty("sourceName").GetString() == "remote")
            .GetProperty("transport");
        remote.GetProperty("endpoint").GetString().Should().Be("https://api.example/mcp");
        remote.GetProperty("endpointCarriedQuery").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Ein Quellpfad ist eine Herkunftsangabe. Der Dienst öffnet ihn nicht — sonst wäre dieser
    /// Endpunkt ein Werkzeug zum Auslesen fremder Dateien.
    /// </summary>
    [Fact]
    public async Task The_origin_path_is_a_label_and_never_opened()
    {
        var client = await AdminAsync("wp43-herkunft");
        var unlesbar = Path.Combine(Path.GetTempPath(), $"gibt-es-nicht-{Guid.NewGuid():N}.json");

        using var response = await client.PostAsync(
            ImportEndpoints.ApiBase + "/preview?originPath=" + Uri.EscapeDataString(unlesbar),
            new StringContent(
                """{"mcpServers":{"echo":{"command":"/usr/bin/echo"}}}""",
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // Haette der Dienst den Pfad gelesen, waere die Antwort ein Fehler ueber eine fehlende
        // Datei — und der Endpunkt haette gerade bewiesen, dass er Dateien oeffnet.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("source").GetProperty("originPath").GetString()
            .Should().Be(unlesbar, "die Angabe reist mit, damit ein Befund seine Fundstelle nennt");
        parsed.RootElement.GetProperty("candidates").GetArrayLength().Should().Be(1);
    }

    // ── Eingangsgrenzen ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_wrong_content_type_is_refused_before_anything_is_parsed()
    {
        var client = await AdminAsync("wp43-inhaltstyp");

        using var response = await client.PostAsync(
            ImportEndpoints.ApiBase + "/preview",
            new StringContent("<xml/>", Encoding.UTF8, "application/xml"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ImportErrors.ContentType);
    }

    [Fact]
    public async Task A_document_beyond_the_limit_is_refused_instead_of_read()
    {
        var client = await AdminAsync("wp43-groesse");
        var zuGross = "{\"mcpServers\":{},\"fuellung\":\""
            + new string('x', ImportRequestLimits.MaxDocumentBytes)
            + "\"}";

        using var response = await client.PostAsync(
            ImportEndpoints.ApiBase + "/preview",
            new StringContent(zuGross, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task An_empty_body_says_that_a_path_is_not_a_read_order()
    {
        var client = await AdminAsync("wp43-leer");

        using var response = await client.PostAsync(
            ImportEndpoints.ApiBase + "/preview",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("Leseauftrag");
    }

    /// <summary>
    /// Der Zähler greift bedingungslos — auch für eine Identität mit Global-Grant, die nach RBAC
    /// unbegrenzt wäre. Ein Import ist teuer, unabhängig davon, was in einer Rolle steht.
    /// </summary>
    [Fact]
    public async Task The_rate_limit_applies_even_to_an_identity_without_a_role_limit()
    {
        var client = await AdminAsync("wp43-zaehler");
        var document = """{"mcpServers":{"echo":{"command":"/usr/bin/echo"}}}""";

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < ImportRateLimiter.PermitsPerWindow + 2; i++)
        {
            var (status, _) = await PreviewAsync(client, document);
            codes.Add(status);
        }

        codes.Should().Contain(HttpStatusCode.TooManyRequests,
            $"nach {ImportRateLimiter.PermitsPerWindow} Anfragen im Fenster ist Schluss");
        codes.Take(ImportRateLimiter.PermitsPerWindow).Should().AllBeEquivalentTo(HttpStatusCode.OK,
            "die erlaubten Anfragen davor muessen durchgehen — sonst waere der Zaehler zu eng");
    }

    // ── Handle ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Das Handle gilt <b>einmal</b>, und ein erfundenes wird abgewiesen statt geraten — dieselbe
    /// Zusage wie beim Restore-Handle (M2-Vertrag, Nachtrag).
    /// </summary>
    [Fact]
    public async Task A_handle_is_single_use_and_an_invented_one_is_refused()
    {
        var client = await AdminAsync("wp43-handle");

        var (_, body) = await PreviewAsync(
            client,
            """
            {"mcpServers":{"handle-fall":{"command":"/usr/bin/echo","args":["hallo"]}}}
            """);
        using var parsed = JsonDocument.Parse(body);
        var token = parsed.RootElement.GetProperty("token").GetString();

        var erfunden = await CommitAsync(client, "gibt-es-nicht", confirmRisks: true);
        erfunden.Status.Should().Be(HttpStatusCode.Conflict);
        erfunden.Body.Should().Contain(ImportErrors.HandleUnknown);

        var erste = await CommitAsync(client, token, confirmRisks: true, isolation: "Host");
        erste.Status.Should().Be(HttpStatusCode.OK, erste.Body);

        var zweite = await CommitAsync(client, token, confirmRisks: true, isolation: "Host");
        zweite.Status.Should().Be(HttpStatusCode.Conflict,
            "ein zweites Anwenden desselben Handles legte dieselben Server ein zweites Mal an");
        zweite.Body.Should().Contain(ImportErrors.HandleUnknown);

        await RemoveAsync("handle-fall");
    }

    /// <summary>Ein fremdes Handle taugt nicht — und es lässt sich auch nicht durch Vorlegen abräumen.</summary>
    [Fact]
    public async Task A_handle_belongs_to_the_identity_that_created_it()
    {
        var einer = await AdminAsync("wp43-eigentuemer-a");
        var anderer = await AdminAsync("wp43-eigentuemer-b");

        var (_, body) = await PreviewAsync(
            einer, """{"mcpServers":{"fremd":{"command":"/usr/bin/echo"}}}""");
        using var parsed = JsonDocument.Parse(body);
        var token = parsed.RootElement.GetProperty("token").GetString();

        var fremd = await CommitAsync(anderer, token, confirmRisks: true, isolation: "Host");
        fremd.Status.Should().Be(HttpStatusCode.Conflict);

        // Und das Handle lebt danach weiter: Ein fremder Versuch darf es nicht entwerten, sonst
        // koennte jeder jedem den Vorgang abschiessen.
        var eigen = await CommitAsync(einer, token, confirmRisks: true, isolation: "Host");
        eigen.Status.Should().Be(HttpStatusCode.OK, eigen.Body);

        await RemoveAsync("fremd");
    }

    // ── Übernahme ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ein Befund der Stufe <c>Risk</c> blockiert nicht — aber er wird auch nicht wegentschieden.
    /// Ohne Bestätigung passiert nichts.
    /// </summary>
    [Fact]
    public async Task A_risk_finding_needs_an_explicit_confirmation()
    {
        var client = await AdminAsync("wp43-risiko");

        // 'npx -y' laedt beim Start beliebigen Code nach (BFR-IMP-0103) und ist damit genau der
        // Fall, den ADR-0025 sichtbar machen will.
        var (_, body) = await PreviewAsync(
            client,
            """
            {"mcpServers":{"npx-fall":{"command":"npx","args":["-y","@beispiel/server"]}}}
            """);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("requiresConfirmation").GetArrayLength()
            .Should().BeGreaterThan(0);
        var token = parsed.RootElement.GetProperty("token").GetString();

        var ohne = await CommitAsync(client, token, confirmRisks: false, isolation: "Host");
        ohne.Status.Should().Be(HttpStatusCode.Conflict);
        ohne.Body.Should().Contain(ImportErrors.ConfirmationRequired);

        _gateway.Supervisor.Statuses.Should().NotContain(s => s.Slug == "npx-fall",
            "eine fehlende Bestaetigung darf nichts anlegen");
    }

    /// <summary>
    /// Ein Server, der ein fremdes Programm startet, braucht eine ausdrückliche Isolationsangabe
    /// (ADR-0025 E2/E5). Der Import ist ein Erzeugungsweg — er rät sie nicht.
    /// </summary>
    [Fact]
    public async Task A_native_upstream_without_an_isolation_decision_is_refused()
    {
        var client = await AdminAsync("wp43-isolation");

        var (_, body) = await PreviewAsync(
            client, """{"mcpServers":{"ohne-isolation":{"command":"/usr/bin/echo"}}}""");
        using var parsed = JsonDocument.Parse(body);
        var token = parsed.RootElement.GetProperty("token").GetString();

        var ohne = await CommitAsync(client, token, confirmRisks: true);

        ohne.Status.Should().Be(HttpStatusCode.Conflict);
        ohne.Body.Should().Contain("Isolation");
        _gateway.Supervisor.Statuses.Should().NotContain(s => s.Slug == "ohne-isolation");
    }

    /// <summary>
    /// <b>Atomar oder gar nicht.</b> Der zweite Server der Quelle kollidiert mit dem Bestand; danach
    /// steht auch der erste nicht da.
    /// <para>
    /// Geprüft wird der Zustand, nicht die Absicht: Ein halb übernommener Import sieht aus wie ein
    /// gelungener, bis jemand die Liste zählt.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_commit_that_collides_with_the_existing_stock_changes_nothing()
    {
        var client = await AdminAsync("wp43-atomar");

        // Erst einen Server anlegen, mit dem die zweite Uebernahme kollidiert.
        var (_, ersterLauf) = await PreviewAsync(
            client, """{"mcpServers":{"belegt":{"command":"/usr/bin/echo"}}}""");
        using var ersterPlan = JsonDocument.Parse(ersterLauf);
        var ersteUebernahme = await CommitAsync(
            client, ersterPlan.RootElement.GetProperty("token").GetString(),
            confirmRisks: true, isolation: "Host");
        ersteUebernahme.Status.Should().Be(HttpStatusCode.OK, ersteUebernahme.Body);

        // Jetzt eine Quelle mit einem neuen UND dem belegten Namen.
        var (_, zweiterLauf) = await PreviewAsync(
            client,
            """
            {"mcpServers":{
               "frisch":{"command":"/usr/bin/echo"},
               "belegt":{"command":"/usr/bin/echo"}}}
            """);
        using var zweiterPlan = JsonDocument.Parse(zweiterLauf);
        var zweiteUebernahme = await CommitAsync(
            client, zweiterPlan.RootElement.GetProperty("token").GetString(),
            confirmRisks: true, isolation: "Host");

        zweiteUebernahme.Status.Should().Be(HttpStatusCode.Conflict);
        zweiteUebernahme.Body.Should().Contain("belegt");
        _gateway.Supervisor.Statuses.Should().NotContain(s => s.Slug == "frisch",
            "der Kollisionsbefund faellt VOR der ersten Aenderung — deshalb steht auch der "
            + "unproblematische Server nicht da");

        await RemoveAsync("belegt");
    }

    /// <summary>Eine Auswahl übernimmt genau die genannten Server und keinen weiteren.</summary>
    [Fact]
    public async Task A_selection_imports_exactly_what_it_names()
    {
        var client = await AdminAsync("wp43-auswahl");

        var (_, body) = await PreviewAsync(
            client,
            """
            {"mcpServers":{
               "gewollt":{"command":"/usr/bin/echo"},
               "ungewollt":{"command":"/usr/bin/echo"}}}
            """);
        using var parsed = JsonDocument.Parse(body);

        var result = await CommitAsync(
            client,
            parsed.RootElement.GetProperty("token").GetString(),
            confirmRisks: true,
            isolation: "Host",
            only: "gewollt");

        result.Status.Should().Be(HttpStatusCode.OK, result.Body);
        result.Body.Should().Contain("gewollt").And.NotContain("ungewollt");
        _gateway.Supervisor.Statuses.Should().Contain(s => s.Slug == "gewollt");
        _gateway.Supervisor.Statuses.Should().NotContain(s => s.Slug == "ungewollt");

        await RemoveAsync("gewollt");
    }

    /// <summary>Ein Dokument, das der Importer nicht anwenden kann, legt nichts an.</summary>
    [Fact]
    public async Task An_unusable_document_is_reported_and_never_applied()
    {
        var client = await AdminAsync("wp43-kaputt");

        var (status, body) = await PreviewAsync(client, "{ das ist kein json");
        status.Should().Be(HttpStatusCode.OK, "die Beurteilung IST die Antwort auf diese Frage");

        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("canApply").GetBoolean().Should().BeFalse();

        var commit = await CommitAsync(
            client, parsed.RootElement.GetProperty("token").GetString(), confirmRisks: true);
        commit.Status.Should().Be(HttpStatusCode.BadRequest);
        commit.Body.Should().Contain(ImportErrors.DocumentInvalid);
    }

    // ── Audit ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Alles wird auditiert — <b>ohne</b> Secretwerte. Ein Audit-Eintrag, der die Quelle zitiert,
    /// verlagert das Geheimnis nur von der Antwort in die Datenbank.
    /// </summary>
    [Fact]
    public async Task Every_import_step_is_audited_without_a_single_value()
    {
        var client = await AdminAsync("wp43-audit");

        var (_, body) = await PreviewAsync(
            client,
            "{\"mcpServers\":{\"audit-fall\":{\"command\":\"/usr/bin/echo\","
            + "\"env\":{\"TOKEN\":\"" + SecretCorpus.StdioEnv + "\"}}}}");
        using var parsed = JsonDocument.Parse(body);
        var commit = await CommitAsync(
            client, parsed.RootElement.GetProperty("token").GetString(),
            confirmRisks: true, isolation: "Host");
        commit.Status.Should().Be(HttpStatusCode.OK, commit.Body);

        // Der Audit-Pfad schreibt gepuffert. Gewartet wird auf den Eintrag, nicht auf eine Uhr.
        var ct = TestContext.Current.CancellationToken;
        await IntegrationSupport.WaitUntilAsync(
            async () => (await EintraegeAsync(ct)).Any(item => item.Tool!.StartsWith("import-added", StringComparison.Ordinal)),
            because: "ein Import ohne Spur im Audit ist ein Import, den niemand nachvollziehen kann");

        var eintraege = await EintraegeAsync(ct);
        eintraege.Select(item => item.Tool).Should().Contain(t => t!.Contains("import-preview", StringComparison.Ordinal));
        eintraege.Select(item => item.Tool).Should().Contain(t => t!.Contains("import-added", StringComparison.Ordinal));

        SecretCorpus.FirstLeakIn(JsonSerializer.Serialize(eintraege, Json)).Should().BeNull(
            "das Audit haelt fest, WAS geschah — nicht, mit welchem Zugangsdatum");

        await RemoveAsync("audit-fall");
    }

    // ── Helfer ──────────────────────────────────────────────────────────────────────────────────

    private async Task<HttpClient> AdminAsync(string name)
    {
        var (_, apiKey) = await _gateway.SeedAdminAsync(name);
        var client = _gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body)> PreviewAsync(
        HttpClient client, string document)
    {
        using var response = await client.PostAsync(
            ImportEndpoints.ApiBase + "/preview",
            new StringContent(document, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(HttpStatusCode Status, string Body)> CommitAsync(
        HttpClient client,
        string? token,
        bool confirmRisks,
        string? isolation = null,
        string? only = null)
    {
        object body = new
        {
            token,
            confirmRisks,
            // Die Isolationsangabe gilt fuer den ganzen Vorgang; die Auswahl nennt nur Namen.
            isolation,
            servers = only is null ? null : new[] { new { sourceName = only } },
        };

        using var response = await client.PostAsync(
            ImportEndpoints.ApiBase + "/commit",
            new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task RemoveAsync(string slug)
    {
        var status = _gateway.Supervisor.Statuses.FirstOrDefault(s => s.Slug == slug);
        if (status is not null)
        {
            await _gateway.Supervisor.RemoveAsync(
                status.Id, DrainPolicy.Immediate, TestContext.Current.CancellationToken);
        }
    }
}
