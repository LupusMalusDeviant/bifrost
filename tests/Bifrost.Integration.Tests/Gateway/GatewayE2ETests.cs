using System.Diagnostics;
using AwesomeAssertions;
using Bifrost.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>WP4-DoD: echter SDK-Client gegen den echten Gateway-Host (Programm-Komposition, EchoServer-Upstreams).</summary>
public sealed class GatewayE2ETests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public GatewayE2ETests(GatewayFixture gw) => _gw = gw;

    [Fact]
    public async Task Client_connects_lists_profile_tools_and_calls_through_gateway()
    {
        await _gw.AddEchoUpstreamAsync("list1");
        var profile = new ToolProfile(ProfileId.New(), "pinned",
            [new NamespacedToolName("list1__echo")], LazyToolsEnabled: true);
        var (_, apiKey) = await _gw.SeedAdminAsync("lister", profile);

        await using var client = await _gw.ConnectClientAsync(apiKey);
        var tools = await client.ListToolsAsync();

        tools.Select(t => t.Name).Should().Contain("list1__echo", "gepinnte Tools erscheinen mit vollem Schema")
            .And.Contain(["search_tools", "describe_tool", "invoke_tool"], "Lazy-Modus liefert die Meta-Tools");

        var result = await client.CallToolAsync(
            "list1__echo", new Dictionary<string, object?> { ["message"] = "durch den Gateway" });

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("Echo: durch den Gateway");
    }

    [Fact]
    public async Task Negotiated_protocol_version_meets_the_required_spec_revision()
    {
        // NFR-09: Die Protokollversion kommt vollständig aus dem SDK — ohne Test verschiebt ein
        // SDK-Upgrade sie still.
        //
        // Die Untergrenze ist der Stand, den wir TATSÄCHLICH sprechen, nicht der historisch einmal
        // zugesagte: Sie stand fünf Monate lang auf 2025-06-18, während das SDK längst 2025-11-25
        // aushandelte. Eine Untergrenze weit unter dem Ist prüft nichts — ein Rückschritt des SDK
        // wäre glatt durchgegangen. Steigt die Version, schlägt dieser Test an und will bewusst
        // nachgezogen werden; das ist der Zweck — zuletzt am 2026-07-31 beim Umstieg auf SDK 2.0.
        const string RequiredRevision = "2026-07-28";

        var (_, apiKey) = await _gw.SeedAdminAsync("protocol");

        await using var client = await _gw.ConnectClientAsync(apiKey);

        var negotiated = client.NegotiatedProtocolVersion;
        negotiated.Should().NotBeNullOrWhiteSpace();

        // Die Revisionen sind datumssortiert benannt, ein Stringvergleich reicht darum aus.
        string.CompareOrdinal(negotiated, RequiredRevision).Should().BeGreaterThanOrEqualTo(0,
            $"NFR-09 verlangt {RequiredRevision} oder neuer, ausgehandelt wurde '{negotiated}'");
    }

    [Fact]
    public async Task Meta_tools_search_describe_invoke_work_end_to_end()
    {
        await _gw.AddEchoUpstreamAsync("lazy1");
        var (_, apiKey) = await _gw.SeedAdminAsync("lazy-agent");

        await using var client = await _gw.ConnectClientAsync(apiKey);

        var search = await client.CallToolAsync(
            "search_tools", new Dictionary<string, object?> { ["query"] = "echo message" });
        search.IsError.Should().NotBe(true);
        search.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("lazy1__echo");

        var describe = await client.CallToolAsync(
            "describe_tool", new Dictionary<string, object?> { ["name"] = "lazy1__echo" });
        describe.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("inputSchema");

        var invoke = await client.CallToolAsync(
            "invoke_tool", new Dictionary<string, object?>
            {
                ["name"] = "lazy1__echo",
                ["arguments"] = new Dictionary<string, object?> { ["message"] = "lazy!" },
            });
        invoke.IsError.Should().NotBe(true);
        invoke.Content.OfType<TextContentBlock>().Single().Text.Should().Be("Echo: lazy!");
    }

    /// <summary>
    /// PRD-Abnahmekriterium 3: Ein Server, der während einer laufenden Verbindung dazukommt, ist
    /// ohne Neuverbinden nutzbar — und einer, der verschwindet, liefert einen sauberen Fehler statt
    /// eines Protokollbruchs.
    /// <para>
    /// <b>Die Benachrichtigung steht nicht mehr hier drin.</b> Sie ist seit der Revision 2026-07-28
    /// keine Eigenschaft dieses Aufrufs mehr, sondern eine des Betriebsmodus: Ohne Sitzung gibt es
    /// keine unaufgeforderte Server-zu-Client-Nachricht, an ihre Stelle tritt die Cache-Frist auf
    /// der Werkzeugliste. Geprüft wird sie weiterhin — in
    /// <see cref="StatefulSessionTests"/> dort, wo es sie noch gibt, und als Cache-Hinweis in
    /// <see cref="StatelessProtocolTests"/>. Was dieser Test zusagt, ist die <em>Funktion</em>, und
    /// die gilt in beiden Betriebsarten unverändert.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Hot_swap_makes_a_new_tool_callable_without_reconnect()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync("hotswapper");
        await using var client = await _gw.ConnectClientAsync(apiKey);

        var id = await _gw.AddEchoUpstreamAsync("swap1");

        var result = await client.CallToolAsync(
            "swap1__echo", new Dictionary<string, object?> { ["message"] = "ohne Reconnect" });
        result.IsError.Should().NotBe(true, "das neue Tool ist ohne Reconnect nutzbar (WP4-DoD)");

        // Teil 2: Server entfernen → sauberer Fehler, Gateway bleibt stabil
        await _gw.Supervisor.RemoveAsync(id, DrainPolicy.Immediate, TestContext.Current.CancellationToken);

        var afterRemove = await client.CallToolAsync(
            "swap1__echo", new Dictionary<string, object?> { ["message"] = "weg?" });
        afterRemove.IsError.Should().Be(true, "entferntes Tool liefert sauberen Tool-Error, keinen Protokollbruch");
        afterRemove.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("ToolNotFound");
    }

    [Fact]
    public async Task Rbac_denial_returns_clean_tool_error_and_lands_in_audit_log()
    {
        await _gw.AddEchoUpstreamAsync("denied1");
        var (identity, apiKey) = await _gw.SeedIdentityAsync("verboten", grants: []);

        await using var client = await _gw.ConnectClientAsync(apiKey);
        var result = await client.CallToolAsync(
            "denied1__echo", new Dictionary<string, object?> { ["message"] = "darf nicht" });

        result.IsError.Should().Be(true, "RBAC-Deny als sauberer Tool-Error (WP4-DoD)");
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("Denied");

        await IntegrationSupport.WaitUntilAsync(
            () => _gw.AuditQuery.QueryAsync(
                new AuditFilter(Caller: identity, Status: InvocationStatus.Denied), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult().TotalCount >= 1,
            because: "der Deny muss als Audit-Zeile persistiert werden (WP4-DoD, FR-22)");
    }

    [Fact]
    public async Task Requests_without_valid_api_key_get_401()
    {
        using var anonymous = _gw.CreateDefaultClient();
        var noKey = await anonymous.PostAsync("/mcp", new StringContent("{}"));
        noKey.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);

        using var wrongKey = _gw.CreateDefaultClient();
        wrongKey.DefaultRequestHeaders.Authorization = new("Bearer", "mcpk_falsch");
        var invalid = await wrongKey.PostAsync("/mcp", new StringContent("{}"));
        invalid.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoints_answer_without_auth()
    {
        using var client = _gw.CreateDefaultClient();

        (await client.GetAsync("/healthz")).IsSuccessStatusCode.Should().BeTrue();
        (await client.GetAsync("/readyz")).IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Gateway_overhead_p95_stays_under_50ms_for_100_parallel_calls()
    {
        await _gw.AddEchoUpstreamAsync("bench1");
        var (_, apiKey) = await _gw.SeedAdminAsync("bencher");

        // 4 Sessions × 25 parallele Calls = 100 In-Flight (NFR-01/02-Nachweis im Kleinen)
        var clients = new List<ModelContextProtocol.Client.McpClient>();
        for (var i = 0; i < 4; i++)
        {
            clients.Add(await _gw.ConnectClientAsync(apiKey));
        }

        try
        {
            foreach (var client in clients)
            {
                for (var i = 0; i < 5; i++)
                {
                    await client.CallToolAsync("bench1__echo", new Dictionary<string, object?> { ["message"] = "warmup" });
                }
            }

            var durations = new System.Collections.Concurrent.ConcurrentBag<double>();
            var tasks = new List<Task>();
            foreach (var client in clients)
            {
                for (var i = 0; i < 25; i++)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var sw = Stopwatch.StartNew();
                        var result = await client.CallToolAsync(
                            "bench1__echo", new Dictionary<string, object?> { ["message"] = "bench" });
                        sw.Stop();
                        result.IsError.Should().NotBe(true);
                        durations.Add(sw.Elapsed.TotalMilliseconds);
                    }));
                }
            }

            await Task.WhenAll(tasks);

            var sorted = durations.Order().ToArray();
            var p95 = sorted[(int)(sorted.Length * 0.95)];

            // Funktionale Zusage — gilt überall: 100 parallele Calls über 4 Sessions, keiner scheitert.
            durations.Should().HaveCount(100);

            // Latenz-Zusage nur dort, wo wir die Hardware kennen. Auf geteilten CI-Runnern misst diese
            // Schranke die Auslastung des Runners, nicht den Gateway (beobachtet: p95 > 2 s bei sonst
            // einstelligen Millisekunden). Der belastbare NFR-01-Nachweis läuft als eigener Benchmark
            // auf Referenz-Hardware: PerformanceBenchmarkTests + docs/acceptance/performance.md.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            {
                p95.Should().BeLessThan(50, "NFR-01: Gateway-Overhead p95 ≤ 50 ms bei 100 parallelen Calls");
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }
}
