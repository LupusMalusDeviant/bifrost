using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Die Verwaltung der festgehaltenen Tool-Definitionen an der REST-Fassade. Die Durchsetzung selbst
/// — Zurückhalten, Annehmen, Zurückkehren — ist im Supervisor getestet
/// (<c>ToolDefinitionPinScreeningTests</c>); hier geht es nur darum, wer die Liste sehen und
/// entscheiden darf.
/// </summary>
public sealed class ToolDefinitionPinTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public ToolDefinitionPinTests(GatewayFixture gw) => _gw = gw;

    /// <summary>
    /// Eine geänderte Tool-Definition anzunehmen ist dieselbe Entscheidung wie „diesem Server
    /// vertraue ich" — also Adminsache.
    /// </summary>
    [Fact]
    public async Task Pin_management_is_admin_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedIdentityAsync($"pin-ohne-grant-{Guid.NewGuid():N}", grants: []);
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var response = await client.GetAsync("/api/v1/tool-definitions", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_can_read_the_pin_list()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedAdminAsync($"pin-admin-{Guid.NewGuid():N}");
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/tool-definitions", ct);

        listed.GetProperty("pins").ValueKind.Should().Be(JsonValueKind.Array);
    }

    /// <summary>Ein Pin, den es nicht gibt, ist ein 404 — kein stiller Erfolg.</summary>
    [Fact]
    public async Task Accepting_an_unknown_pin_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedAdminAsync($"pin-admin2-{Guid.NewGuid():N}");
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var response = await client.PostAsync(
            $"/api/v1/tool-definitions/{Guid.NewGuid()}/gibtesnicht/accept", null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
