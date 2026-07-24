using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Plan 0003, M2: der wörtliche Meilenstein — ein <b>signiertes</b> WASI-Component erscheint als
/// Upstream und wird durch die <b>volle</b> Governance-Pipeline aufgerufen. Anders als
/// <see cref="WasiUpstreamE2ETests"/> steht hier das echte Rust-Binary hinter der IPC-Leitung: Der
/// Host verifiziert die Signatur gegen den gepinnten Publisher, setzt die Grants durch und führt
/// echtes WebAssembly aus. Ohne diesen Test wäre „signiert" im Meilenstein eine Behauptung, die
/// nur der Stub trägt.
/// <para>
/// Übersprungen, wenn kein Host-Binary gefunden wird (siehe <see cref="WasiHostPaths"/>); im
/// Rust-CI-Job erzwingt <c>MCPMCP_REQUIRE_WASI_HOST=1</c> den Lauf.
/// </para>
/// </summary>
public sealed class WasiRealHostGovernanceTests : IClassFixture<GatewayFixture>
{
    private const string Slug = "wasirt";

    /// <summary>Der Guest importiert wasi:cli/environment — ohne diesen Grant startet er nicht.</summary>
    private static readonly string[] EnvironmentGrant = ["MCPMCP_SPIKE"];

    private readonly GatewayFixture _gw;

    public WasiRealHostGovernanceTests(GatewayFixture gw) => _gw = gw;

    [Fact]
    public async Task A_signed_component_runs_through_the_full_pipeline_and_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await AddSignedUpstreamAsync(ct);
        var (allowed, _) = await _gw.SeedAdminAsync("wasi-rt-admin");
        var (denied, _) = await _gw.SeedIdentityAsync("wasi-rt-verboten", grants: []);

        var success = await InvokeAsync(allowed, tool, ct);
        var refused = await InvokeAsync(denied, tool, ct);

        // Erfolgsfall: echtes WebAssembly lief, das Ergebnis kam durch den Invoker zurück.
        success.Status.Should().Be(InvocationStatus.Success, success.ErrorMessage);
        success.Content!.Value.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("mcpmcp-guest-ok");

        // Kein Bypass (Plan-Risiko R6): RBAC greift auch auf dem WASI-Pfad, vor dem Host.
        refused.Status.Should().Be(InvocationStatus.Denied);

        await IntegrationSupport.WaitUntilAsync(
            () => _gw.AuditQuery.QueryAsync(
                new AuditFilter(Caller: allowed, Status: InvocationStatus.Success, ToolPrefix: Slug + "__"), ct)
                .GetAwaiter().GetResult().TotalCount >= 1,
            because: "der Aufruf eines signierten Components steht im Audit (FR-22)");
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.AuditQuery.QueryAsync(
                new AuditFilter(Caller: denied, Status: InvocationStatus.Denied), ct)
                .GetAwaiter().GetResult().TotalCount >= 1,
            because: "der Deny ebenso");
    }

    /// <summary>Hängt das signierte Fixture-Component als Upstream ein und liefert seinen Tool-Namen.</summary>
    private async Task<NamespacedToolName> AddSignedUpstreamAsync(CancellationToken ct)
    {
        var host = WasiHostPaths.RequireHost();
        var publisher = (await File.ReadAllTextAsync(WasiHostPaths.PublisherPath, ct)).Trim();

        // WP4: Der Publisher muss im Trust-Store stehen — die Konfiguration ist keine
        // Vertrauensquelle mehr.
        await _gw.Services.GetRequiredService<PublisherTrustStore>()
            .PinAsync(publisher, "fixture-publisher", ct);

        var id = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                Slug, "Signiertes WASI-Component", UpstreamTransportKind.Wasi, Enabled: true,
                Wasi: new WasiTransportOptions(
                    host,
                    WasiHostPaths.ComponentPath,
                    WasiHostPaths.SignaturePath,
                    PinnedPublishers: [],
                    Grants: new WasiCapabilityGrants(Environment: EnvironmentGrant))),
            ct);
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: $"der signierte Upstream muss Healthy werden (Fehler: {_gw.Supervisor.GetStatus(id)?.LastError})");

        // Ein einziger, normalisierter Katalogeintrag für den Kommando-Einstiegspunkt (WP6.1).
        var inventory = _gw.Supervisor.GetInventory(id);
        var command = inventory!.Tools.Single();
        command.Name.Should().Be("wasi_cli_run");
        return NamespacedToolName.Create(Slug, command.Name);
    }

    private Task<ToolInvocationResult> InvokeAsync(IdentityId caller, NamespacedToolName tool, CancellationToken ct)
        => _gw.Invoker.InvokeAsync(
            new ToolInvocationRequest(
                caller, CallOrigin.Mcp, tool,
                JsonSerializer.Deserialize<JsonElement>("{}"), TimeoutOverride: null),
            ct);
}
