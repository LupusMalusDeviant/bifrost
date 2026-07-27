using System.Net;
using System.Web;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Upstream.OAuth;
using Xunit;

namespace McpMcp.Upstream.Tests;

/// <summary>
/// Der Autorisierungsablauf gegen einen Upstream (MCP-Autorisierung, OAuth 2.1 + RFC 8707 + RFC 9207).
/// <para>
/// Geprüft wird vor allem, wo der Standard ein <b>Abbrechen</b> verlangt — die Stellen, an denen ein
/// stiller Rückfall bequem wäre und die Sicherheit auflöste.
/// </para>
/// </summary>
public sealed class OAuthFlowTests
{
    private static readonly AuthorizationServerMetadata Metadata = new(
        "https://as.example.com",
        new Uri("https://as.example.com/authorize"),
        new Uri("https://as.example.com/token"),
        ["S256"],
        ["mcp:read"],
        IssuerParameterSupported: true);

    private static readonly UpstreamOAuthOptions Options = new("gateway-client");

    private static (Uri Url, OAuthAuthorizationAttempt Attempt) Begin(
        IReadOnlyList<string>? scopes = null)
        => OAuthFlow.Begin(
            new ServerId(Guid.NewGuid()), Metadata, Options,
            new Uri("https://gateway.example.com/oauth/callback"),
            "https://upstream.example.com/mcp",
            scopes ?? ["mcp:read"],
            DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Der <c>resource</c>-Parameter bindet das Token an genau diesen Upstream. Der Standard
    /// verlangt ihn, <em>unabhängig</em> davon, ob der Server ihn unterstützt — ohne ihn wäre ein
    /// Token anderswo einlösbar.
    /// </summary>
    [Fact]
    public void The_authorization_url_carries_the_resource_indicator()
    {
        var (url, attempt) = Begin();

        var query = HttpUtility.ParseQueryString(url.Query);
        query["resource"].Should().Be("https://upstream.example.com/mcp");
        attempt.Resource.Should().Be("https://upstream.example.com/mcp");
    }

    /// <summary>PKCE ist Pflicht, und zwar mit S256 — nicht mit <c>plain</c>.</summary>
    [Fact]
    public void The_authorization_url_uses_pkce_with_s256()
    {
        var (url, attempt) = Begin();

        var query = HttpUtility.ParseQueryString(url.Query);
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().NotBeNullOrEmpty();
        query["code_challenge"].Should().NotBe(attempt.CodeVerifier,
            "die Challenge ist der Hash des Verifiers, nicht der Verifier selbst");
        attempt.CodeVerifier.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// State und Verifier müssen bei jedem Vorgang neu sein. Ein wiederverwendeter State macht die
    /// CSRF-Bindung wertlos.
    /// </summary>
    [Fact]
    public void Every_attempt_gets_fresh_state_and_verifier()
    {
        var (_, first) = Begin();
        var (_, second) = Begin();

        second.State.Should().NotBe(first.State);
        second.CodeVerifier.Should().NotBe(first.CodeVerifier);
    }

    /// <summary>
    /// Der beim Start notierte Issuer ist der Bezugspunkt der Antwortprüfung (RFC 9207). Ohne ihn
    /// ließe sich die Antwort eines anderen Authorization Servers unterschieben.
    /// </summary>
    [Fact]
    public void A_response_from_a_different_issuer_is_refused()
    {
        var (_, attempt) = Begin();

        var act = () => OAuthFlow.EnsureIssuerMatches(attempt, "https://boese.example.com");

        act.Should().Throw<OAuthDiscoveryException>().WithMessage("*erwartet war*");
    }

    /// <summary>
    /// Verglichen wird ohne Normalisierung: Der Standard verbietet, abschließende Schrägstriche
    /// oder Groß-/Kleinschreibung anzugleichen — genau darin kann der Unterschied stecken.
    /// </summary>
    [Theory]
    [InlineData("https://as.example.com/")]
    [InlineData("https://AS.example.com")]
    public void The_issuer_comparison_does_not_normalise(string issuer)
    {
        var (_, attempt) = Begin();

        var act = () => OAuthFlow.EnsureIssuerMatches(attempt, issuer);

        act.Should().Throw<OAuthDiscoveryException>();
    }

    [Fact]
    public void The_matching_issuer_passes()
    {
        var (_, attempt) = Begin();

        var act = () => OAuthFlow.EnsureIssuerMatches(attempt, "https://as.example.com");

        act.Should().NotThrow();
    }

    /// <summary>
    /// Ein Authorization Server, der S256 nicht ausweist, wird abgelehnt. Der Standard sagt
    /// ausdrücklich, dass der Client dann <b>nicht</b> fortfahren darf.
    /// </summary>
    [Fact]
    public async Task An_authorization_server_without_pkce_is_refused()
    {
        using var server = new MetadataServer(issuer => $$"""
            {
              "issuer": "{{issuer}}",
              "authorization_endpoint": "{{issuer}}/authorize",
              "token_endpoint": "{{issuer}}/token",
              "code_challenge_methods_supported": ["plain"]
            }
            """);

        var act = async () => await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            server.Issuer, allowPrivateTargets: true, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OAuthDiscoveryException>()).WithMessage("*S256*");
    }

    /// <summary>
    /// Nennen die Metadaten einen anderen Issuer als den angefragten, beschreiben sie einen anderen
    /// Server — und die spätere Issuer-Prüfung liefe gegen einen Wert, den die Gegenseite
    /// vorgegeben hat.
    /// </summary>
    [Fact]
    public async Task Metadata_naming_a_foreign_issuer_is_refused()
    {
        using var server = new MetadataServer(_ => """
            {
              "issuer": "https://jemand-anderes.example.com",
              "authorization_endpoint": "https://jemand-anderes.example.com/authorize",
              "token_endpoint": "https://jemand-anderes.example.com/token",
              "code_challenge_methods_supported": ["S256"]
            }
            """);

        var act = async () => await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            server.Issuer, allowPrivateTargets: true, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OAuthDiscoveryException>()).WithMessage("*angefragt war*");
    }

    /// <summary>
    /// Die entscheidende Verteidigung dieses Features: Die Adressen der Discovery kommen vom
    /// <b>Upstream</b>. Zeigt er auf einen internen Dienst, wird nicht abgerufen — derselbe
    /// SSRF-Weg wie bei den Schemaimporten.
    /// </summary>
    [Fact]
    public async Task An_authorization_server_on_an_internal_address_is_refused()
    {
        var act = async () => await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri("https://169.254.169.254"), allowPrivateTargets: false,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OAuthDiscoveryException>())
            .WithMessage("*interne Adresse*");
    }

    /// <summary>Ohne HTTPS wird nicht autorisiert — ein Token über Klartext ist keins.</summary>
    [Fact]
    public async Task A_plain_http_authorization_server_is_refused()
    {
        var act = async () => await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri("http://as.example.com"), allowPrivateTargets: false,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OAuthDiscoveryException>()).WithMessage("*kein HTTPS*");
    }

    /// <summary>
    /// Die <c>resource_metadata</c>-Adresse kommt aus der <c>WWW-Authenticate</c>-Aufforderung.
    /// Fehlt sie, wird kein Well-Known-Pfad geraten — das hieße, eine fremde Adresse zu erfinden.
    /// </summary>
    [Fact]
    public void The_resource_metadata_url_is_read_from_the_challenge()
    {
        using var withHeader = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        withHeader.Headers.TryAddWithoutValidation(
            "WWW-Authenticate",
            "Bearer resource_metadata=\"https://upstream.example.com/.well-known/oauth-protected-resource\", scope=\"mcp:read mcp:write\"");

        OAuthDiscovery.ReadResourceMetadataUrl(withHeader).Should()
            .Be(new Uri("https://upstream.example.com/.well-known/oauth-protected-resource"));
        OAuthDiscovery.ReadChallengedScopes(withHeader).Should().Equal("mcp:read", "mcp:write");

        using var without = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        OAuthDiscovery.ReadResourceMetadataUrl(without).Should().BeNull();
    }

    /// <summary>Ein winziger HTTPS-freier Metadaten-Server auf Loopback für die Discovery-Tests.</summary>
    private sealed class MetadataServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public MetadataServer(Func<string, string> body)
        {
            var port = FreePort();
            Issuer = new Uri($"http://127.0.0.1:{port}");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (HttpListenerException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    var payload = System.Text.Encoding.UTF8.GetBytes(body(Issuer.ToString().TrimEnd('/')));
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(payload);
                    context.Response.Close();
                }
            });
        }

        public Uri Issuer { get; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
