using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Der Gateway unter der Spec-Revision <c>2026-07-28</c> (SEP-2567): ohne Sitzung, ohne
/// <c>Mcp-Session-Id</c>, jede Anfrage steht fuer sich.
/// <para>
/// <b>Was diese Tests festhalten sollen,</b> ist nicht „das SDK kann das" — sondern dass <em>beide</em>
/// Welten an derselben Adresse funktionieren: ein Client auf dem neuen Stand und einer, der bei
/// <c>2025-11-25</c> stehengeblieben ist. Ein Gateway, der nur die eine Haelfte bedient, ist fuer
/// die andere ein Ausfall — und zwar einer, den niemand sieht, bis der erste Agent nicht mehr
/// hochkommt.
/// </para>
/// </summary>
public sealed class StatelessProtocolTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public StatelessProtocolTests(GatewayFixture gw) => _gw = gw;

    private ApprovalPolicyStore Policy => _gw.Services.GetRequiredService<ApprovalPolicyStore>();

    private IApprovalStore Approvals => _gw.Services.GetRequiredService<IApprovalStore>();

    /// <summary>
    /// Die Vorgabe ist der neue Stand. Ohne diesen Test verschiebt ein SDK-Wechsel die ausgehandelte
    /// Revision still — und „still" ist bei einer Protokollversion die schlechteste Eigenschaft.
    /// </summary>
    [Fact]
    public async Task A_current_client_negotiates_the_july_2026_revision()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync($"stateless-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);

        client.NegotiatedProtocolVersion.Should().Be("2026-07-28");
        client.SessionId.Should().BeNull(
            "die Revision hat Mcp-Session-Id ersatzlos gestrichen — ein Wert hier waere ein Rueckfall");
    }

    /// <summary>
    /// <b>Die eigentliche Zusage dieser Umstellung.</b> Ein Client, der bei <c>2025-11-25</c>
    /// stehengeblieben ist, muss an derselben Adresse weiterarbeiten koennen — sonst ist der
    /// Umstieg kein Umstieg, sondern eine Abschaltung.
    /// </summary>
    [Fact]
    public async Task A_client_pinned_to_the_previous_revision_still_works()
    {
        var slug = $"old{Guid.NewGuid():N}"[..10];
        await _gw.AddEchoUpstreamAsync(slug);
        var (_, apiKey) = await _gw.SeedAdminAsync($"downlevel-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(
            apiKey, options: new McpClientOptions { ProtocolVersion = "2025-11-25" });

        client.NegotiatedProtocolVersion.Should().Be("2025-11-25");

        // Die Liste zeigt ohne Profil nur die Meta-Werkzeuge (ADR-0003, Lazy Discovery) — der
        // Beweis, dass ein alter Client arbeiten kann, ist der Aufruf selbst.
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        tools.Should().Contain(t => t.Name == "search_tools");

        var result = await client.CallToolAsync(
            $"{slug}__echo", new Dictionary<string, object?> { ["message"] = "von gestern" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("Echo: von gestern");
    }

    /// <summary>
    /// Ohne Sitzung gibt es kein <c>tools/list_changed</c> mehr — der Cache-Hinweis ist der Ersatz
    /// (SEP-2549). Und er muss <c>private</c> sein: Unsere Listen sind je Identitaet gefiltert.
    /// Waere der Hinweis <c>public</c> (der Standardwert, wenn das Feld fehlt), duerfte ein
    /// gemeinsamer Zwischenspeicher die Werkzeugliste der einen Identitaet an die naechste
    /// ausliefern — eine Rechteweitergabe durch einen Cache.
    /// </summary>
    [Fact]
    public async Task The_tool_list_carries_a_private_cache_hint()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync($"cachehint-{Guid.NewGuid():N}");
        using var http = _gw.CreateDefaultClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var raw = await PostListToolsAsync(http);

        raw.TryGetProperty("result", out var result).Should().BeTrue($"unerwartete Antwort: {raw}");
        result.GetProperty("cacheScope").GetString().Should().Be("private");
        result.GetProperty("ttlMs").GetInt32().Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Die Freigabe-Rueckfrage im Moment des Aufrufs — der Pfad, der ohne Sitzung eigentlich
    /// unmoeglich waere. Er funktioniert trotzdem, weil die Revision ihn ersetzt hat: Der Aufruf
    /// endet mit <c>input_required</c>, der Client zeigt das Formular und <b>wiederholt</b> den
    /// Aufruf mit der Antwort (MRTR).
    /// <para>
    /// Der Test prueft die Zustimmung <em>und</em> das Log: Ein Aufruf, der in der Warteschlange
    /// landet, sieht von aussen gleich aus, egal ob niemand gefragt wurde oder ob jemand Nein
    /// gesagt hat.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_approval_is_obtained_without_a_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = $"mr{Guid.NewGuid():N}"[..10];
        await _gw.AddEchoUpstreamAsync(slug);
        var tool = new NamespacedToolName($"{slug}__echo");
        await Policy.SetAsync(tool, required: true, ct);
        try
        {
            var (_, apiKey) = await _gw.SeedAdminAsync($"mrtr-{Guid.NewGuid():N}");
            await using var client = await _gw.ConnectClientAsync(apiKey, (_, _) =>
                ValueTask.FromResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["approve"] = JsonSerializer.SerializeToElement(true),
                    },
                }));

            var result = await client.CallToolAsync(
                tool.Value, new Dictionary<string, object?> { ["message"] = "ohne Sitzung" },
                cancellationToken: ct);

            result.IsError.Should().NotBe(true,
                "die Zustimmung kam an — Absagen im Log: "
                + string.Join(" / ", _gw.Log.From("ApprovalElicitation")));
            (await Approvals.ListAsync(null, ct)).Single(r => r.Tool == tool)
                .State.Should().Be(ApprovalState.Consumed);
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// <b>Der Preis der Umstellung, festgehalten statt verschwiegen.</b> Auf der Revision
    /// 2026-07-28 gibt es kein Merkmal mehr, an dem ein Server erkennen koennte, ob ein Client ein
    /// Formular <em>anzeigen</em> kann — die Elicitation-Faehigkeit ist in MRTR aufgegangen und
    /// wird nicht mehr gemeldet (nachgemessen: auch dann nicht, wenn ein Client sie am Draht
    /// ausdruecklich deklariert). Der Gateway fragt deshalb jeden, der MRTR spricht.
    /// <para>
    /// Wer dann kein Formular anzeigen kann, bekommt eine Ausnahme statt der
    /// Warteschlangen-Meldung. <b>Der Vorgang geht dabei nicht verloren</b> — genau das prueft
    /// dieser Test: Die Freigabe steht danach in der Warteschlange und laesst sich in der
    /// Oberflaeche entscheiden. Ein aergerlicher Fehler ist verkraftbar; ein verlorener Aufruf
    /// waere es nicht.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_client_that_cannot_show_a_form_still_leaves_the_request_in_the_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = $"nf{Guid.NewGuid():N}"[..10];
        await _gw.AddEchoUpstreamAsync(slug);
        var tool = new NamespacedToolName($"{slug}__echo");
        await Policy.SetAsync(tool, required: true, ct);
        try
        {
            var (_, apiKey) = await _gw.SeedAdminAsync($"noform-{Guid.NewGuid():N}");
            await using var client = await _gw.ConnectClientAsync(apiKey);

            var call = async () => await client.CallToolAsync(
                tool.Value, new Dictionary<string, object?> { ["message"] = "ohne Formular" },
                cancellationToken: ct);

            (await call.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*ElicitationHandler*",
                    "das SDK sagt dem Client deutlich, was ihm fehlt");
            (await Approvals.ListAsync(ApprovalState.Pending, ct))
                .Should().Contain(r => r.Tool == tool,
                    "der Vorgang bleibt entscheidbar — die Rueckfrage ist gescheitert, nicht der Auftrag");
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// Roher JSON-RPC-Aufruf: Die Cache-Felder stehen im Protokoll, und ob wir sie senden, laesst
    /// sich nur am Draht pruefen — ein deserialisiertes Ergebnis wuerde einen fehlenden Wert als
    /// Standardwert zurueckgeben und den Fehler verstecken.
    /// </summary>
    private static async Task<JsonElement> PostListToolsAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                // Die Revision verlangt Protokollversion UND Client-Faehigkeiten in JEDER Anfrage —
                // es gibt keinen Handshake mehr, in dem beides einmal ausgetauscht wuerde. Genau
                // das ist der Kern von SEP-2567: Die Anfrage beschreibt sich selbst.
                """
                {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{
                  "io.modelcontextprotocol/protocolVersion":"2026-07-28",
                  "io.modelcontextprotocol/clientCapabilities":{},
                  "io.modelcontextprotocol/clientInfo":{"name":"raw-wire-test","version":"1.0"}}}}
                """,
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Accept.Add(new("text/event-stream"));
        // Bewusst Zeichenketten statt SDK-Konstanten: Geprueft wird das Format am Draht. Eine
        // Konstante wuerde mit dem SDK mitwandern und die Pruefung damit gegen sich selbst fuehren.
        request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Streamable HTTP darf mit SSE antworten; dann steht das JSON hinter 'data: '.
        var payload = body.Contains("data:", StringComparison.Ordinal)
            ? string.Concat(body.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l["data:".Length..].Trim()))
            : body;

        return JsonSerializer.Deserialize<JsonElement>(payload);
    }
}
