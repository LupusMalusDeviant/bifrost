using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Plan 0003, M2/WP2.4 (Risiko R6): Ein WASI-Upstream muss durch dieselbe Governance-Pipeline
/// laufen wie jeder andere — RBAC, Guardrail, Approval, Audit — und darf keinen Seitenweg am
/// <c>IToolInvoker</c> vorbei öffnen. Genau das prüfen diese Tests am echten Gateway-Host.
/// <para>
/// Hinter dem Connector steht hier der Stub-Host: Der Prüfgegenstand ist der Governance-Pfad, und
/// der ist unabhängig davon, was hinter der IPC-Leitung rechnet. Dass ein <b>echter</b>, signierter
/// Component über dieselbe Pipeline läuft, belegt <see cref="WasiRealHostGovernanceTests"/> gegen
/// das gebaute Rust-Binary; die Wire-Kompatibilität selbst belegen die
/// <c>WasiRealHostCompatibilityTests</c> (WP6.2). Keiner der drei Tests trägt den Nachweis allein.
/// </para>
/// </summary>
public sealed class WasiUpstreamE2ETests : IClassFixture<GatewayFixture>, IAsyncLifetime
{
    /// <summary>Kanonischer Beispielschlüssel aus der AWS-Dokumentation — kein echtes Secret.</summary>
    private const string FakeAwsKey = "AKIAIOSFODNN7EXAMPLE";

    private const string Slug = "wasi1";
    private const string DoubleTool = Slug + "__double";
    private const string LeakTool = Slug + "__leak";

    private readonly GatewayFixture _gw;
    private string _componentPath = string.Empty;
    private string _signaturePath = string.Empty;

    public WasiUpstreamE2ETests(GatewayFixture gw) => _gw = gw;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        // Der Stub-Host verifiziert nicht — hier zählen nur die Pfade, nicht der Inhalt.
        _componentPath = Path.Combine(Path.GetTempPath(), $"mcpmcp-e2e-{Guid.NewGuid():N}.wasm");
        _signaturePath = Path.ChangeExtension(_componentPath, ".sig");
        await File.WriteAllBytesAsync(_componentPath, [0x00, 0x61, 0x73, 0x6D], ct);
        await File.WriteAllBytesAsync(_signaturePath, new byte[64], ct);

        // Die Fixture ist klassenweit — der Upstream wird nur beim ersten Test angelegt.
        // Ab WP4 kommt das Vertrauen aus dem Trust-Store, nicht aus der Konfiguration: Ohne
        // gepinnten Schlüssel käme der Upstream gar nicht erst hoch.
        await _gw.Services.GetRequiredService<PublisherTrustStore>()
            .PinAsync(Convert.ToBase64String(new byte[32]), "e2e-stub", ct);

        if (_gw.Supervisor.Statuses.Any(status => status.Slug == Slug))
        {
            return;
        }

        var id = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                Slug, "WASI-Component", UpstreamTransportKind.Wasi, Enabled: true,
                Wasi: new WasiTransportOptions(
                    TestPaths.Executable("WasiHostStub"),
                    _componentPath,
                    _signaturePath,
                    PinnedPublishers: [],
                    Grants: new WasiCapabilityGrants(Environment: ["MCPMCP_SPIKE"]))),
            ct);
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "der WASI-Upstream muss wie jeder andere Healthy werden");
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(_componentPath);
        File.Delete(_signaturePath);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Wasi_tools_appear_in_the_catalog_and_answer_over_mcp_and_rest()
    {
        // Ohne gepinntes Profil liefert der Lazy-Modus nur die Meta-Tools (ADR-0003) — das Pinning
        // ist hier der Weg, das WASI-Tool mit vollem Schema in tools/list zu sehen.
        var profile = new ToolProfile(
            ProfileId.New(), "wasi-pinned", [new NamespacedToolName(DoubleTool)], LazyToolsEnabled: true);
        var (_, apiKey) = await _gw.SeedAdminAsync("wasi-admin", profile);

        await using var mcp = await _gw.ConnectClientAsync(apiKey);
        var tools = await mcp.ListToolsAsync();
        var overMcp = await mcp.CallToolAsync(
            DoubleTool, new Dictionary<string, object?> { ["value"] = 21 });

        using var rest = _gw.CreateDefaultClient();
        rest.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        var restTools = await rest.GetFromJsonAsync<JsonElement>("/api/v1/tools", TestContext.Current.CancellationToken);
        var overRest = await rest.PostAsync(
            $"/api/v1/tools/{DoubleTool}/invoke",
            new StringContent("""{"value":21}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).Should().Contain(DoubleTool,
            "ein WASI-Export ist im Katalog von anderen Upstreams ununterscheidbar (FR-04)");
        overMcp.IsError.Should().NotBe(true);
        overMcp.Content.OfType<TextContentBlock>().Single().Text.Should().Be("42");

        restTools.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).Should().Contain(DoubleTool);
        overRest.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            await overRest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rbac_denies_a_wasi_call_before_it_reaches_the_host_and_audits_it()
    {
        var (identity, _) = await _gw.SeedIdentityAsync("wasi-verboten", grants: []);

        var result = await InvokeAsync(identity, DoubleTool, """{"value":21}""");

        result.Status.Should().Be(InvocationStatus.Denied,
            "RBAC greift vor dem Transport — der WASI-Host bekommt den Aufruf nie zu sehen");
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.AuditQuery.QueryAsync(
                new AuditFilter(Caller: identity, Status: InvocationStatus.Denied),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult().TotalCount >= 1,
            because: "auch der Deny eines WASI-Tools steht im Audit (FR-22)");
    }

    [Fact]
    public async Task The_inbound_guardrail_holds_back_a_secret_the_component_printed()
    {
        var (identity, _) = await _gw.SeedAdminAsync("wasi-guardrail");

        // Ein Guest darf alles auf stdout schreiben; genau dort greift die eingehende Prüfung.
        var result = await InvokeAsync(identity, LeakTool, """{"value":1}""");

        result.Status.Should().Be(InvocationStatus.GuardBlocked,
            "der Guardrail prüft auch Ergebnisse von WASI-Tools (ADR-0011)");
        result.Content.Should().BeNull("das Ergebnis wird zurückgehalten, nicht durchgereicht");
        result.ErrorMessage.Should().NotContain(FakeAwsKey, "die Meldung darf das Secret nicht wiederholen");
    }

    [Fact]
    public async Task The_generated_schema_is_enforced_before_the_call_leaves_the_gateway()
    {
        var (identity, _) = await _gw.SeedAdminAsync("wasi-schema");

        // Das Schema kommt aus der typisierten Discovery (WP6.1) und ist strikt — ein unbekanntes
        // Feld fällt hier auf und nicht erst im Guest.
        var result = await InvokeAsync(identity, DoubleTool, $$"""{"value":1,"note":"{{FakeAwsKey}}"}""");

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
    }

    [Fact]
    public async Task An_approval_requirement_holds_a_wasi_call_back()
    {
        var policy = _gw.Services.GetRequiredService<ApprovalPolicyStore>();
        var (identity, _) = await _gw.SeedAdminAsync("wasi-approval");
        await policy.SetAsync(new NamespacedToolName(DoubleTool), required: true, TestContext.Current.CancellationToken);

        try
        {
            var result = await InvokeAsync(identity, DoubleTool, """{"value":21}""");

            result.Status.Should().Be(InvocationStatus.ApprovalRequired,
                "Freigabepflicht gilt auch für WASI-Tools (FR-32, ADR-0012)");
            result.Content.Should().BeNull("ohne Freigabe darf kein Ergebnis entstehen");
        }
        finally
        {
            await policy.SetAsync(new NamespacedToolName(DoubleTool), required: false, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_successful_wasi_call_lands_in_the_audit_log()
    {
        var (identity, _) = await _gw.SeedAdminAsync("wasi-audit");

        var result = await InvokeAsync(identity, DoubleTool, """{"value":4}""");

        result.Status.Should().Be(InvocationStatus.Success);
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.AuditQuery.QueryAsync(
                new AuditFilter(Caller: identity, Status: InvocationStatus.Success, ToolPrefix: Slug + "__"),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult().TotalCount >= 1,
            because: "jeder erfolgreiche WASI-Aufruf ist auditiert (FR-22)");
    }

    [Fact]
    public async Task A_removed_wasi_upstream_leaves_no_callable_tool_behind()
    {
        // Der Host ist ein Kindprozess — ein Entfernen darf weder Tool noch Prozess zurücklassen.
        var id = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                "wasi-temp", "WASI (temporär)", UpstreamTransportKind.Wasi, Enabled: true,
                Wasi: new WasiTransportOptions(
                    TestPaths.Executable("WasiHostStub"), _componentPath, _signaturePath,
                    PinnedPublishers: [])),
            TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(id)?.State == UpstreamState.Healthy);
        var (identity, _) = await _gw.SeedAdminAsync("wasi-remover");

        await _gw.Supervisor.RemoveAsync(id, DrainPolicy.Immediate, TestContext.Current.CancellationToken);

        var result = await InvokeAsync(identity, "wasi-temp__double", """{"value":1}""");
        result.Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    private Task<ToolInvocationResult> InvokeAsync(IdentityId caller, string tool, string argumentsJson)
        => _gw.Invoker.InvokeAsync(
            new ToolInvocationRequest(
                caller, CallOrigin.Mcp, new NamespacedToolName(tool),
                JsonSerializer.Deserialize<JsonElement>(argumentsJson), TimeoutOverride: null),
            TestContext.Current.CancellationToken);
}
