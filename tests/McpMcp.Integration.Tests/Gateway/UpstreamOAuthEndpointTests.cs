using System.Net;
using AwesomeAssertions;
using McpMcp.Abstractions;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Die Endpunkte der Upstream-Autorisierung liegen hinter der UI-Anmeldung, nicht hinter einem
/// Agenten-Schlüssel: Der Rückweg aus dem Browser trägt kein Bearer-Token.
/// <para>
/// Genau deshalb muss geprüft sein, dass sie ohne Anmeldung <b>nicht</b> erreichbar sind — ein
/// offener Callback nähme Autorisierungscodes von jedem entgegen.
/// </para>
/// </summary>
public sealed class UpstreamOAuthEndpointTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public UpstreamOAuthEndpointTests(GatewayFixture gw) => _gw = gw;

    [Theory]
    [InlineData("/oauth/upstream/00000000-0000-0000-0000-000000000001/connect")]
    [InlineData("/oauth/upstream/callback?code=x&state=y")]
    public async Task The_endpoints_are_not_reachable_without_a_ui_login(string path)
    {
        using var client = _gw.CreateUiClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized],
            "ohne Anmeldung f\u00fchrt der Weg zur Anmeldemaske, nicht in den Vorgang");
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Ein Callback ohne bekannten Vorgang wird abgewiesen. Der State ist die Klammer um den
    /// Vorgang — ohne ihn w\u00e4re jede Antwort annehmbar, und ein untergeschobener Code liefe durch.
    /// </summary>
    [Fact]
    public async Task A_callback_with_an_unknown_state_is_refused()
    {
        var name = $"oauth-admin-{Guid.NewGuid():N}";
        await _gw.UiUsers.CreateAsync(
            name, "passwort123", UiRole.Admin, TestContext.Current.CancellationToken);
        using var client = await _gw.LoginUiAsync(name, "passwort123");

        var response = await client.GetAsync(
            "/oauth/upstream/callback?code=abc&state=gibtesnicht",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
