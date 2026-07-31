using AwesomeAssertions;
using Bifrost.Server;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Der Gateway als OAuth-Resource-Server (MCP-Autorisierung, Stufe 1).
/// <para>
/// Geprüft wird hier die Konfigurationsschicht und die Abschaltbarkeit — die Signaturprüfung selbst
/// liegt in der Bibliothek, und sie gegen einen selbstgebauten Aussteller zu testen würde vor allem
/// beweisen, dass die Bibliothek funktioniert.
/// </para>
/// </summary>
public sealed class OAuthResourceServerTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>
    /// Ohne Issuer bleibt alles wie bisher. Der Standard nennt Autorisierung ausdrücklich optional
    /// — ein Gateway, der ohne Konfiguration plötzlich Token verlangt, wäre ein Bruch.
    /// </summary>
    [Fact]
    public void Without_an_issuer_the_resource_server_stays_off()
        => OAuthResourceServerOptions.FromConfiguration(Config()).Should().BeNull();

    /// <summary>
    /// Ohne eigene kanonische Adresse lässt sich nicht prüfen, ob ein Token <b>für uns</b>
    /// ausgestellt wurde. Das still zu übergehen hiesse, die Audience-Prüfung auszulassen — genau
    /// die Prüfung, die Token-Weitergabe zwischen Diensten verhindert.
    /// </summary>
    [Fact]
    public void An_issuer_without_an_audience_is_refused()
    {
        var act = () => OAuthResourceServerOptions.FromConfiguration(
            Config(("BIFROST_OAUTH_ISSUER", "https://as.example.com")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*kanonische Adresse*");
    }

    [Fact]
    public void The_public_base_url_serves_as_the_audience_when_none_is_given()
    {
        var options = OAuthResourceServerOptions.FromConfiguration(Config(
            ("BIFROST_OAUTH_ISSUER", "https://as.example.com/"),
            ("BIFROST_PUBLIC_BASE_URL", "https://gateway.example.com/")));

        options!.Issuer.Should().Be("https://as.example.com", "der Schrägstrich am Ende fällt weg");
        options.Audience.Should().Be("https://gateway.example.com");
    }

    [Fact]
    public void An_explicit_audience_wins()
    {
        var options = OAuthResourceServerOptions.FromConfiguration(Config(
            ("BIFROST_OAUTH_ISSUER", "https://as.example.com"),
            ("BIFROST_PUBLIC_BASE_URL", "https://intern.example.com"),
            ("BIFROST_OAUTH_AUDIENCE", "https://gateway.example.com/mcp")));

        options!.Audience.Should().Be("https://gateway.example.com/mcp");
    }

    /// <summary>
    /// Die Adresse der Protected Resource Metadata steht in jeder 401-Antwort — daran findet ein
    /// Client den zuständigen Authorization Server, statt ihn zu raten.
    /// </summary>
    [Fact]
    public void The_metadata_url_follows_the_well_known_convention()
    {
        var options = OAuthResourceServerOptions.FromConfiguration(Config(
            ("BIFROST_OAUTH_ISSUER", "https://as.example.com"),
            ("BIFROST_PUBLIC_BASE_URL", "https://gateway.example.com")));

        options!.MetadataUrl.Should()
            .Be("https://gateway.example.com/.well-known/oauth-protected-resource");
    }
}

/// <summary>
/// Der bestehende API-Key-Weg bleibt unberührt, solange kein Issuer konfiguriert ist — und die
/// Fixture konfiguriert keinen.
/// </summary>
public sealed class OAuthResourceServerOffTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public OAuthResourceServerOffTests(GatewayFixture gw) => _gw = gw;

    [Fact]
    public async Task Without_configuration_there_is_no_discovery_document()
    {
        using var client = _gw.CreateDefaultClient();

        var response = await client.GetAsync(
            "/.well-known/oauth-protected-resource", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound,
            "ohne Resource-Server-Konfiguration gibt es nichts zu entdecken");
    }

    /// <summary>
    /// Und die 401-Aufforderung bleibt die alte: ohne Resource-Server-Betrieb kein Verweis auf
    /// Metadaten, die es nicht gibt.
    /// </summary>
    [Fact]
    public async Task The_challenge_stays_plain_when_oauth_is_off()
    {
        using var client = _gw.CreateDefaultClient();

        var response = await client.GetAsync("/api/v1/tools", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().NotContain("resource_metadata");
    }
}
