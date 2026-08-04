using AwesomeAssertions;
using Bifrost.Upstream.OAuth;
using Xunit;

namespace Bifrost.Integration.Tests.OAuth;

/// <summary>
/// WP5.2: der Lauf gegen einen <b>echten externen</b> Authorization Server — kein Container, keine
/// Loopback-Ausnahme, echtes HTTPS.
/// <para>
/// <b>Warum das trotz Keycloak noch gebraucht wird:</b> Ein selbst betriebener Server ist auf
/// unsere Erwartungen konfiguriert. Ein fremder ist es nicht — dort entscheidet der Anbieter über
/// Discovery-Pfade, Zertifikatsketten und darüber, welche Parameter er stillschweigend ignoriert.
/// </para>
/// <para>
/// <b>Der Nachweis steht aus, solange keine Zugangsdaten hinterlegt sind.</b> Dieser Test meldet das
/// als Skip mit Grund; der geplante Lauf in <c>oauth.yml</c> schreibt denselben Satz in die
/// Zusammenfassung. Ein grüner Lauf ohne Konfiguration wäre die Behauptung, es sei geprüft worden.
/// </para>
/// </summary>
public sealed class OAuthSaasSmokeTests
{
    private const string IssuerVariable = "BIFROST_OAUTH_SMOKE_ISSUER";

    [Fact]
    public async Task The_metadata_of_an_external_provider_is_usable()
    {
        var issuer = Environment.GetEnvironmentVariable(IssuerVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(issuer),
            $"{IssuerVariable} ist nicht gesetzt — der Nachweis gegen einen externen Anbieter steht aus.");

        var ct = TestContext.Current.CancellationToken;

        // Ausdruecklich OHNE AllowPrivateTargets: Ein externer Anbieter spricht HTTPS, und genau das
        // soll hier mitgeprueft werden.
        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(issuer!), allowPrivateTargets: false, ct);

        metadata.Issuer.TrimEnd('/').Should().Be(issuer!.TrimEnd('/'));
        metadata.AuthorizationEndpoint.Scheme.Should().Be("https");
        metadata.TokenEndpoint.Scheme.Should().Be("https");
        metadata.CodeChallengeMethodsSupported.Should().Contain(
            "S256", "ohne PKCE-S256 ist der Autorisierungsweg dieses Gateways nicht benutzbar");
    }
}
