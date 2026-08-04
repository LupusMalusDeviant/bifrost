using System.Web;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.OAuth;
using Xunit;

namespace Bifrost.Integration.Tests.OAuth;

/// <summary>
/// WP5.2: Der Upstream-OAuth-Weg gegen eine echte Implementierung — Discovery, Autorisierungscode
/// mit PKCE, Einlösung, Erneuerung.
/// <para>
/// <b>Die DoD dieses Pakets steht und fällt mit diesen Tests:</b> Die Zusage „OAuth unterstützt"
/// war bis hierher an Verträge und Nachbauten gebunden, nicht an einen vollständigen Flow gegen
/// einen fremden Authorization Server.
/// </para>
/// </summary>
[Collection(KeycloakTests.Name)]
public sealed class UpstreamOAuthAgainstKeycloakTests
{
    private static readonly Uri RedirectUri = new("http://127.0.0.1:8099/oauth/callback");

    private const string Resource = "https://upstream.example.invalid/mcp";

    private readonly KeycloakFixture _keycloak;

    public UpstreamOAuthAgainstKeycloakTests(KeycloakFixture keycloak) => _keycloak = keycloak;

    /// <summary>
    /// <c>AllowPrivateTargets</c> ist hier gesetzt, weil der Authorization Server auf Loopback
    /// steht — genau der Fall, für den das Produkt die Ausnahme vorsieht.
    /// </summary>
    private static UpstreamOAuthOptions Options => new(
        KeycloakFixture.ClientId,
        KeycloakFixture.ClientSecret,
        Scopes: ["openid", "profile"],
        AllowPrivateTargets: true);

    [Fact]
    public async Task Discovery_finds_the_metadata_of_a_real_authorization_server()
    {
        Assert.SkipWhen(_keycloak.SkipReason is not null, _keycloak.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(_keycloak.Issuer), allowPrivateTargets: true, ct);

        metadata.Issuer.Should().Be(_keycloak.Issuer);
        metadata.AuthorizationEndpoint.ToString().Should().Contain("/protocol/openid-connect/auth");
        metadata.TokenEndpoint.ToString().Should().Contain("/protocol/openid-connect/token");
    }

    /// <summary>
    /// Der vollständige Weg: Adresse bauen, im „Browser" anmelden, Code einlösen. Danach liegt ein
    /// Token vor, das von einem fremden Server ausgestellt wurde.
    /// </summary>
    [Fact]
    public async Task A_full_authorization_code_flow_with_pkce_yields_a_usable_token()
    {
        Assert.SkipWhen(_keycloak.SkipReason is not null, _keycloak.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var (token, _, authorizationUrl) = await AuthorizeAndRedeemAsync(ct);

        token.AccessToken.Should().NotBeNullOrWhiteSpace();
        token.Issuer.Should().Be(_keycloak.Issuer);
        token.ExpiresAt.Should().NotBeNull();
        token.ExpiresAt!.Value.Should().BeAfter(DateTimeOffset.UtcNow);
        token.RefreshToken.Should().NotBeNullOrWhiteSpace(
            "ohne Refresh-Token muesste ein Betreiber die Verbindung staendig neu herstellen");

        // RFC 8707: Der Resource-Indicator geht mit — er bindet das Token an genau diesen Upstream.
        var query = HttpUtility.ParseQueryString(authorizationUrl.Query);
        query["resource"].Should().Be(Resource);
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// <b>Die Gegenprobe zu PKCE.</b> Derselbe Code, aber mit einem anderen <c>code_verifier</c> —
    /// der Authorization Server muss ablehnen. Ohne diesen Fall belegt der Test oben nur, dass
    /// irgendein Parameter mitgeschickt wurde, nicht dass er geprüft wird.
    /// </summary>
    [Fact]
    public async Task A_code_redeemed_with_the_wrong_verifier_is_refused()
    {
        Assert.SkipWhen(_keycloak.SkipReason is not null, _keycloak.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(_keycloak.Issuer), allowPrivateTargets: true, ct);
        var (url, attempt) = OAuthFlow.Begin(
            ServerId.New(), metadata, Options, RedirectUri, Resource, ["openid"], DateTimeOffset.UtcNow);

        var callback = await KeycloakFixture.AuthorizeAsync(url, ct);
        var code = HttpUtility.ParseQueryString(callback.Query)["code"];
        code.Should().NotBeNullOrWhiteSpace();

        var tampered = attempt with { CodeVerifier = "ein-anderer-verifier-der-nicht-passt-0123456789" };

        var redeem = async () => await OAuthFlow.RedeemAsync(
            tampered, code!, Options, DateTimeOffset.UtcNow, ct);

        await redeem.Should().ThrowAsync<OAuthDiscoveryException>(
            "PKCE ist nur dann ein Schutz, wenn der Server den Verifier wirklich prueft");
    }

    [Fact]
    public async Task A_token_can_be_refreshed_and_the_refresh_token_rotates()
    {
        Assert.SkipWhen(_keycloak.SkipReason is not null, _keycloak.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var (token, attempt, _) = await AuthorizeAndRedeemAsync(ct);

        var refreshed = await OAuthFlow.RefreshAsync(
            token, attempt.TokenEndpoint, Options, Resource, DateTimeOffset.UtcNow, ct);

        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();
        refreshed.Issuer.Should().Be(_keycloak.Issuer);
        refreshed.RefreshToken.Should().NotBeNullOrWhiteSpace();
        refreshed.ExpiresAt.Should().NotBeNull();
        refreshed.RefreshToken.Should().NotBe(token.RefreshToken, "ein rotierendes Token ist ein neues");

        // **Befund aus dem Audit:** Bis hierher hiess der Test "rotates" und prueft nur, dass ein
        // neues Token zurueckkommt. Rotation heisst aber, dass das ALTE danach wertlos ist — sonst
        // liegt nach jeder Erneuerung ein weiterer gueltiger Schluessel herum. Das Realm hat
        // 'revokeRefreshToken' gesetzt; hier wird nachgesehen, ob es auch wirkt.
        var reuseOld = async () => await OAuthFlow.RefreshAsync(
            token, attempt.TokenEndpoint, Options, Resource, DateTimeOffset.UtcNow, ct);

        await reuseOld.Should().ThrowAsync<OAuthDiscoveryException>(
            "das verbrauchte Refresh-Token darf nicht ein zweites Mal gelten");
    }

    /// <summary>
    /// SEP-2352: Ein Antwort-Issuer, der nicht zum begonnenen Vorgang passt, entwertet ihn. Der Fall
    /// ist der Kern des Mix-Up-Schutzes — ein untergeschobener Authorization Server.
    /// </summary>
    [Fact]
    public async Task A_callback_from_another_issuer_is_refused()
    {
        Assert.SkipWhen(_keycloak.SkipReason is not null, _keycloak.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(_keycloak.Issuer), allowPrivateTargets: true, ct);
        var (_, attempt) = OAuthFlow.Begin(
            ServerId.New(), metadata, Options, RedirectUri, Resource, ["openid"], DateTimeOffset.UtcNow);

        var wrongIssuer = () => OAuthFlow.EnsureIssuerMatches(attempt, "https://fremder.example.invalid/realms/x");

        wrongIssuer.Should().Throw<OAuthDiscoveryException>();

        // Und die Gegenprobe: Der echte Issuer geht durch.
        var rightIssuer = () => OAuthFlow.EnsureIssuerMatches(attempt, _keycloak.Issuer);
        rightIssuer.Should().NotThrow();
    }

    /// <summary>
    /// Die HTTPS-Pflicht gilt weiterhin — die Loopback-Ausnahme ist an <c>AllowPrivateTargets</c>
    /// gebunden. Ohne diesen Test wäre nicht belegt, dass die Ausnahme eine Ausnahme ist.
    /// </summary>
    [Fact]
    public async Task Plain_http_discovery_is_refused_when_private_targets_are_not_allowed()
    {
        Assert.SkipWhen(_keycloak.SkipReason is not null, _keycloak.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var discover = async () => await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(_keycloak.Issuer), allowPrivateTargets: false, ct);

        (await discover.Should().ThrowAsync<OAuthDiscoveryException>()).Which
            .Message.Should().Contain("HTTPS");
    }

    private async Task<(UpstreamOAuthToken Token, OAuthAuthorizationAttempt Attempt, Uri AuthorizationUrl)>
        AuthorizeAndRedeemAsync(CancellationToken ct)
    {
        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(_keycloak.Issuer), allowPrivateTargets: true, ct);

        var (url, attempt) = OAuthFlow.Begin(
            ServerId.New(), metadata, Options, RedirectUri, Resource, ["openid", "profile"],
            DateTimeOffset.UtcNow);

        var callback = await KeycloakFixture.AuthorizeAsync(url, ct);
        var query = HttpUtility.ParseQueryString(callback.Query);

        query["error"].Should().BeNull($"die Anmeldung darf nicht scheitern: {callback}");
        OAuthFlow.EnsureIssuerMatches(attempt, query["iss"]);

        var token = await OAuthFlow.RedeemAsync(attempt, query["code"]!, Options, DateTimeOffset.UtcNow, ct);
        return (token, attempt, url);
    }
}
