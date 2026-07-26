using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>WP5.1/5.2-DoD: REST-Roundtrip, Fehler-Mapping, Audit-Parität MCP↔REST, OpenAPI-Sicht, Management.</summary>
public sealed class RestFacadeTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public RestFacadeTests(GatewayFixture gw) => _gw = gw;

    private HttpClient CreateApiClient(string apiKey)
    {
        var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return client;
    }

    [Fact]
    public async Task Audit_endpoint_works_without_paging_parameters()
    {
        // Im Live-Test fiel auf: GET /audit ohne page/pageSize schlug mit 400 fehl, weil die
        // beiden int-Parameter Pflicht waren. Der Default muss ohne Angabe greifen.
        var (_, apiKey) = await _gw.SeedAdminAsync("audit-admin");
        using var client = CreateApiClient(apiKey);

        var response = await client.GetAsync("/api/v1/audit");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "ein simples GET /audit muss ohne Query funktionieren");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("page").GetInt32().Should().Be(1);
        body.GetProperty("pageSize").GetInt32().Should().Be(100);
    }

    /// <summary>
    /// ADR-0015: Die Capability-Sicht ist additiv und wird wirklich bedient — nicht bloss definiert.
    /// Geprüft wird, dass sie dieselben Fähigkeiten wie /tools zeigt, mit stabiler Id, Herkunft des
    /// Schemas und benannter Art, und dass RBAC genauso filtert.
    /// </summary>
    [Fact]
    public async Task Capabilities_endpoint_projects_the_catalog()
    {
        await _gw.AddEchoUpstreamAsync("caps1");
        var (_, apiKey) = await _gw.SeedAdminAsync("cap-admin");
        using var client = CreateApiClient(apiKey);

        var tools = await client.GetFromJsonAsync<JsonElement>("/api/v1/tools");
        var response = await client.GetAsync("/api/v1/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var capabilities = body.GetProperty("capabilities").EnumerateArray().ToList();
        capabilities.Should().NotBeEmpty("die Sicht muss dieselben Fähigkeiten zeigen wie /tools");

        var toolNames = tools.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToHashSet();
        var capabilityNames = capabilities
            .Select(c => c.GetProperty("catalogName").GetString()).ToHashSet();
        capabilityNames.Should().IntersectWith(toolNames);

        var first = capabilities[0];
        first.GetProperty("id").GetString().Should().StartWith("cap_", "stabile, ableitbare Id");
        first.GetProperty("kind").GetString().Should().BeOneOf("Query", "Mutation", "Resource", "Prompt", "Task", "Tool");
        first.GetProperty("execution").GetString().Should().Be("Synchronous");
        first.GetProperty("nativeName").GetString().Should().NotContain("__", "der native Name trägt kein Namespace-Präfix");
        first.GetProperty("inputSchema").GetProperty("provenance").GetString()
            .Should().BeOneOf("Native", "None", "Derived");
    }

    /// <summary>Zweimal abgefragt, dieselbe Id — sonst wäre „stabil" eine Behauptung.</summary>
    [Fact]
    public async Task Capability_ids_are_stable_across_requests()
    {
        await _gw.AddEchoUpstreamAsync("caps2");
        var (_, apiKey) = await _gw.SeedAdminAsync("cap-stable-admin");
        using var client = CreateApiClient(apiKey);

        var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities");

        static IEnumerable<string?> Ids(JsonElement body) => body.GetProperty("capabilities")
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()).Order();
        Ids(second).Should().Equal(Ids(first));
    }

    /// <summary>
    /// Der Punkt, an dem ADR-0015 und ADR-0019 sich treffen: Ein freigabepflichtiger Aufruf ist kein
    /// Fehler mit Prosa, sondern ein <b>Vorgang</b> mit Id — und derselbe Vorgang ist unter
    /// <c>/api/v1/tasks/{id}</c> abrufbar. Vorher hätte ein Agent die Id aus einem deutschen
    /// Meldungstext herauslesen müssen.
    /// </summary>
    [Fact]
    public async Task An_approval_required_capability_call_returns_a_retrievable_task()
    {
        await _gw.AddEchoUpstreamAsync("capappr");
        var (_, apiKey) = await _gw.SeedAdminAsync("cap-approval-admin");
        var policy = _gw.Services.GetRequiredService<McpMcp.Persistence.ApprovalPolicyStore>();
        var tool = new NamespacedToolName("capappr__echo");
        await policy.SetAsync(tool, required: true, TestContext.Current.CancellationToken);
        using var client = CreateApiClient(apiKey);

        try
        {
            // Die Capability-Id aus der Sicht holen, statt sie nachzurechnen — so ruft ein Client auf.
            var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities");
            var capabilityId = listed.GetProperty("capabilities").EnumerateArray()
                .First(c => c.GetProperty("catalogName").GetString() == tool.Value)
                .GetProperty("id").GetString();

            var response = await client.PostAsync(
                $"/api/v1/capabilities/{capabilityId}/invoke",
                new StringContent("""{"message":"bitte freigeben"}""", Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.Accepted,
                "ein Vorgang ist kein Fehler — 202 statt 409 mit Prosa");
            response.Headers.Location!.ToString().Should().StartWith("/api/v1/tasks/");

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("kind").GetString().Should().Be("Task");
            var taskId = body.GetProperty("taskId").GetGuid();

            // Und der Vorgang ist wirklich da — die Id ist keine Behauptung.
            var task = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tasks/{taskId}");
            task.GetProperty("state").GetString().Should().Be("InputRequired");
            task.GetProperty("tool").GetProperty("value").GetString().Should().Be(tool.Value);
        }
        finally
        {
            await policy.SetAsync(tool, required: false, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Fehler tragen einen stabilen Gateway-Code statt eines Meldungstexts, den ein Automat parsen
    /// müsste — samt Aussage, ob ein Wiederholen Aussicht hat.
    /// </summary>
    [Fact]
    public async Task A_capability_error_carries_a_stable_code()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync("cap-error-admin");
        using var client = CreateApiClient(apiKey);

        var response = await client.PostAsync("/api/v1/capabilities/cap_gibtesnicht/invoke", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("gatewayCode").GetString().Should().Be("not-found");
    }

    [Fact]
    public async Task Rest_invoke_roundtrip_works_like_curl()
    {
        await _gw.AddEchoUpstreamAsync("rest1");
        var (_, apiKey) = await _gw.SeedAdminAsync("rest-admin");
        using var client = CreateApiClient(apiKey);

        var tools = await client.GetFromJsonAsync<JsonElement>("/api/v1/tools");
        tools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Contain("rest1__echo");

        var response = await client.PostAsync(
            "/api/v1/tools/rest1__echo/invoke",
            new StringContent("""{"message":"per REST"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("content").GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Be("Echo: per REST", "WP5-DoD: curl-Roundtrip gegen EchoServer-Tool");
    }

    [Fact]
    public async Task Error_mapping_matches_plan()
    {
        await _gw.AddEchoUpstreamAsync("rest2");
        var (_, adminKey) = await _gw.SeedAdminAsync("mapper-admin");
        var (_, restrictedKey) = await _gw.SeedIdentityAsync("mapper-restricted", grants: []);

        using var admin = CreateApiClient(adminKey);
        using var restricted = CreateApiClient(restrictedKey);
        var validBody = new StringContent("""{"message":"x"}""", Encoding.UTF8, "application/json");

        (await restricted.PostAsync("/api/v1/tools/rest2__echo/invoke", validBody))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Denied → 403");
        (await admin.PostAsync("/api/v1/tools/rest2__gibtsnicht/invoke", validBody))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "ToolNotFound → 404");
        (await admin.PostAsync(
            "/api/v1/tools/rest2__echo/invoke",
            new StringContent("""{"falsch":1}""", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "ValidationFailed → 400");
    }

    [Fact]
    public async Task Identical_call_via_mcp_and_rest_yields_identical_audit_semantics()
    {
        await _gw.AddEchoUpstreamAsync("parity1");
        var (identity, apiKey) = await _gw.SeedAdminAsync("parity-agent");

        // ASCII-Payload: der MCP-Pfad escapet Nicht-ASCII bei der Re-Serialisierung (ä),
        // was RequestBytes kosmetisch verschieben würde — hier zählt die Semantik-Parität.
        await using (var mcpClient = await _gw.ConnectClientAsync(apiKey))
        {
            await mcpClient.CallToolAsync(
                "parity1__echo", new Dictionary<string, object?> { ["message"] = "paritaet" });
        }

        using var restClient = CreateApiClient(apiKey);
        await restClient.PostAsync(
            "/api/v1/tools/parity1__echo/invoke",
            new StringContent("""{"message":"paritaet"}""", Encoding.UTF8, "application/json"));

        IReadOnlyList<AuditEvent> events = [];
        await IntegrationSupport.WaitUntilAsync(() =>
        {
            events = _gw.AuditQuery.QueryAsync(
                new AuditFilter(Caller: identity, ToolPrefix: "parity1__echo"), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult().Items;
            return events.Count == 2;
        });

        var viaMcp = events.Single(e => e.Origin == CallOrigin.Mcp);
        var viaRest = events.Single(e => e.Origin == CallOrigin.Rest);
        viaRest.Should().BeEquivalentTo(viaMcp, options => options
                .Excluding(e => e.Origin)
                .Excluding(e => e.Timestamp)
                .Excluding(e => e.Duration)
                .Excluding(e => e.RedactedArguments),
            "WP5-DoD: identische Audit-Semantik — nur Origin/Zeit/Dauer dürfen abweichen (ADR-0008)");
        viaRest.RedactedArguments!.Value.GetRawText().Should().Be(
            viaMcp.RedactedArguments!.Value.GetRawText(),
            "JsonElement hat keine strukturelle Gleichheit — Textvergleich der redigierten Argumente");
    }

    [Fact]
    public async Task OpenApi_document_reflects_only_the_callers_visible_tools()
    {
        await _gw.AddEchoUpstreamAsync("spec1");
        await _gw.AddEchoUpstreamAsync("spec2");
        var (_, restrictedKey) = await _gw.SeedIdentityAsync("spec-restricted",
            [new Grant(new PermissionScope(null, new NamespacedToolName("spec1__echo")), [ToolAction.UseTool])]);

        using var client = CreateApiClient(restrictedKey);
        var doc = await client.GetFromJsonAsync<JsonElement>("/api/v1/openapi.json");

        doc.GetProperty("openapi").GetString().Should().Be("3.1.0");
        var paths = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.Should().Contain("/api/v1/tools/spec1__echo/invoke")
            .And.NotContain("/api/v1/tools/spec2__echo/invoke", "FR-18: Spec ist RBAC-gefiltert pro Key");
    }

    [Fact]
    public async Task Management_requires_global_grant_and_can_add_servers()
    {
        var (_, adminKey) = await _gw.SeedAdminAsync("mgmt-admin");
        var (_, restrictedKey) = await _gw.SeedIdentityAsync("mgmt-restricted", grants: []);

        using var restricted = CreateApiClient(restrictedKey);
        (await restricted.GetAsync("/api/v1/servers")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "ohne Global-Grant kein Management");

        using var admin = CreateApiClient(adminKey);
        var add = await admin.PostAsJsonAsync("/api/v1/servers", new
        {
            slug = "mgmt1",
            displayName = "Per API angelegt",
            kind = "Stdio",
            enabled = true,
            stdio = new { command = TestPaths.Executable("EchoServer"), arguments = Array.Empty<string>() },
        });
        add.StatusCode.Should().Be(HttpStatusCode.Created);

        await IntegrationSupport.WaitUntilAsync(() =>
            _gw.Supervisor.Statuses.Any(s => s.Slug == "mgmt1" && s.State == UpstreamState.Healthy));

        var invoke = await admin.PostAsync(
            "/api/v1/tools/mgmt1__echo/invoke",
            new StringContent("""{"message":"per Management-API angelegt"}""", Encoding.UTF8, "application/json"));
        invoke.StatusCode.Should().Be(HttpStatusCode.OK, "der per API angelegte Server ist sofort nutzbar (FR-06)");

        var duplicate = await admin.PostAsJsonAsync("/api/v1/servers", new
        {
            slug = "mgmt1",
            displayName = "Doppelt",
            kind = "Stdio",
            enabled = true,
            stdio = new { command = "egal", arguments = Array.Empty<string>() },
        });
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Slug-Kollision → verständlicher 400");
    }

    [Fact]
    public async Task Cli_secrets_are_masked_in_configuration_history()
    {
        const string secret = "cli-history-secret-938475";
        var id = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                "clihistory", "CLI history", UpstreamTransportKind.Cli, Enabled: false,
                Cli: new CliTransportOptions(
                    Environment.ProcessPath!,
                    [new CliToolSpec("run")],
                    EnvironmentVariables: new Dictionary<string, string> { ["TOKEN"] = secret })),
            TestContext.Current.CancellationToken);
        var (_, apiKey) = await _gw.SeedAdminAsync("cli-history-admin");
        using var client = CreateApiClient(apiKey);

        var response = await client.GetAsync($"/api/v1/servers/{id.Value}/history");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        raw.Should().NotContain(secret).And.Contain(
            $"\"TOKEN\":\"{UpstreamConfigRedactor.Mask}\"");
    }

    [Fact]
    public async Task Api_reconfigure_preserves_masked_cli_secrets_and_allows_explicit_reset()
    {
        const string secret = "cli-edit-secret-648275";
        var original = new UpstreamServerConfig(
            "cliedit", "CLI edit", UpstreamTransportKind.Cli, Enabled: false,
            Cli: new CliTransportOptions(
                Environment.ProcessPath!,
                [new CliToolSpec("run")],
                EnvironmentVariables: new Dictionary<string, string> { ["TOKEN"] = secret }));
        var id = await _gw.Supervisor.AddAsync(original, TestContext.Current.CancellationToken);
        var (_, apiKey) = await _gw.SeedAdminAsync("cli-edit-admin");
        using var client = CreateApiClient(apiKey);

        var maskedEdit = original with
        {
            DisplayName = "CLI edited",
            Cli = original.Cli! with
            {
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["TOKEN"] = UpstreamConfigRedactor.Mask,
                },
            },
        };
        (await client.PutAsJsonAsync($"/api/v1/servers/{id.Value}", maskedEdit))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var store = _gw.Services.GetRequiredService<IUpstreamConfigStore>();
        var afterMaskedEdit = (await store.GetHistoryAsync(id, TestContext.Current.CancellationToken))
            .OrderByDescending(item => item.Version.Value)
            .First().Config;
        afterMaskedEdit.Cli!.EnvironmentVariables!["TOKEN"].Should().Be(secret);

        var reset = maskedEdit with
        {
            Cli = maskedEdit.Cli! with
            {
                EnvironmentVariables = new Dictionary<string, string>(),
            },
        };
        (await client.PutAsJsonAsync($"/api/v1/servers/{id.Value}", reset))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var afterReset = (await store.GetHistoryAsync(id, TestContext.Current.CancellationToken))
            .OrderByDescending(item => item.Version.Value)
            .First().Config;
        afterReset.Cli!.EnvironmentVariables.Should().BeEmpty();
    }
}
