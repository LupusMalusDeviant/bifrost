using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Vertrag „Selbsttätige Wiederverbindung", Abschnitt 5.3: die vier neuen Felder in der
/// Serverliste und <c>POST /api/v1/servers/{id}/connect</c> — gegen den echten Gateway-Host.
/// </summary>
public sealed class ServerReconnectApiTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public ServerReconnectApiTests(GatewayFixture gw) => _gw = gw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient CreateApiClient(string apiKey)
    {
        var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return client;
    }

    private static async Task<JsonElement> ServerAsync(HttpClient client, ServerId id)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/servers", Ct);
        return body.GetProperty("servers").EnumerateArray()
            .Single(server => server.GetProperty("id").GetGuid() == id.Value);
    }

    /// <summary>Ein Upstream, bei dem niemand zuhört: Der Port ist frei und bleibt es.</summary>
    private static UpstreamServerConfig Unreachable(string slug, int port, bool enabled = true)
        => new(
            slug, $"Nicht erreichbar ({slug})", UpstreamTransportKind.StreamableHttp, enabled,
            Http: new HttpTransportOptions(new Uri($"http://127.0.0.1:{port}/mcp")));

    [Fact]
    public async Task A_failed_upstream_shows_its_reconnect_state_and_can_be_connected_now()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync("reconnect-admin");
        using var client = CreateApiClient(apiKey);
        var port = FreePort();
        var id = await _gw.Supervisor.AddAsync(Unreachable("weg", port), Ct);
        try
        {
            // Bis die Frist zum nächsten Versuch läuft — erst dann kann „Jetzt verbinden" etwas anstoßen.
            await IntegrationSupport.WaitUntilAsync(
                () => _gw.Supervisor.GetStatus(id)?.Reconnect is { NextAttemptAt: not null },
                because: "ein Upstream, bei dem niemand zuhört, geht auf Failed und plant den nächsten Versuch");

            var listed = await ServerAsync(client, id);
            listed.GetProperty("state").GetString().Should().Be("Failed");
            listed.GetProperty("failureKind").GetString().Should().Be("Unreachable");
            listed.GetProperty("reconnectAttempt").GetInt32().Should().BeGreaterThanOrEqualTo(1);
            listed.GetProperty("reconnectExhausted").GetBoolean().Should().BeFalse(
                "ohne ausdrücklich gesetzte Grenze wird weiter versucht");
            listed.GetProperty("nextAttemptAt").GetDateTimeOffset().Should().BeAfter(
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
            listed.GetProperty("lastError").GetString().Should().Contain($"(127.0.0.1:{port})");

            var attemptsBefore = _gw.Supervisor.GetStatus(id)!.Reconnect!.Attempt;
            using var response = await client.PostAsync($"/api/v1/servers/{id.Value}/connect", content: null, Ct);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted, "der Versuch ist angestoßen, nicht abgewartet");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("triggered").GetBoolean().Should().BeTrue();
            body.GetProperty("state").GetString().Should().Be("Failed");

            await IntegrationSupport.WaitUntilAsync(
                () => _gw.Supervisor.GetStatus(id)?.Reconnect is { NextAttemptAt: not null } info
                    && info.Attempt > attemptsBefore,
                because: "der angestoßene Versuch findet sofort statt und zählt mit");

            await IntegrationSupport.WaitUntilAsync(
                // Verwaltungsereignisse tragen ihren Gegenstand im Feld Tool — wie server-added.
                async () => (await _gw.AuditQuery.QueryAsync(
                        new AuditFilter(Server: id, Kind: AuditEventKind.ConfigChanged), Ct))
                    .Items.Any(e => e.Tool == "server-connect-requested" && e.Origin == CallOrigin.Rest),
                because: "wer den Knopf drückt, steht im Audit");
        }
        finally
        {
            await _gw.Supervisor.RemoveAsync(id, DrainPolicy.Immediate, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_connected_upstream_has_no_reconnect_state_and_nothing_to_trigger()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync("reconnect-admin-2");
        using var client = CreateApiClient(apiKey);
        var id = await _gw.AddEchoUpstreamAsync("verbunden");

        var listed = await ServerAsync(client, id);
        listed.GetProperty("failureKind").ValueKind.Should().Be(JsonValueKind.Null);
        listed.GetProperty("reconnectAttempt").GetInt32().Should().Be(0);
        listed.GetProperty("nextAttemptAt").ValueKind.Should().Be(JsonValueKind.Null);
        listed.GetProperty("reconnectExhausted").GetBoolean().Should().BeFalse();

        using var response = await client.PostAsync($"/api/v1/servers/{id.Value}/connect", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("triggered").GetBoolean().Should().BeFalse("verbunden heißt: nichts zu tun");
        body.GetProperty("state").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task Connect_answers_404_for_an_unknown_and_409_for_a_disabled_upstream()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync("reconnect-admin-3");
        using var client = CreateApiClient(apiKey);

        using var unknown = await client.PostAsync($"/api/v1/servers/{Guid.NewGuid()}/connect", content: null, Ct);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var disabled = await _gw.Supervisor.AddAsync(Unreachable("aus", FreePort(), enabled: false), Ct);
        using var conflict = await client.PostAsync($"/api/v1/servers/{disabled.Value}/connect", content: null, Ct);

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict, "deaktiviert bleibt aus — auch über die API");
        (await conflict.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("error").GetString().Should().Contain("deaktiviert");
        _gw.Supervisor.GetStatus(disabled)!.State.Should().Be(UpstreamState.Stopped);
    }

    [Fact]
    public async Task Connect_is_a_management_action()
    {
        var (_, restrictedKey) = await _gw.SeedIdentityAsync("reconnect-restricted", grants: []);
        using var restricted = CreateApiClient(restrictedKey);

        using var response = await restricted.PostAsync(
            $"/api/v1/servers/{Guid.NewGuid()}/connect", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "ohne Global-Grant kein Management");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
