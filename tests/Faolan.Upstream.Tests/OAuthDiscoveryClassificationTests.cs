using System.Net;
using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Upstream.OAuth;
using Xunit;

namespace Faolan.Upstream.Tests;

/// <summary>
/// Plan 0005 G3 (Vertrag N40): Die OAuth-Erkennung ordnet ihre Fehler der richtigen Wiederholungs-
/// regel zu. Ein Name, der sich nicht auflösen lässt, ist eine Unerreichbarkeit (kurze Frist), keine
/// abgelehnte Anmeldung (15 min, „neu autorisieren"). Ein Unsinnsdokument auf einem Well-known-Pfad
/// ist wie „nicht gefunden": Der nächste Pfad wird versucht.
/// </summary>
public sealed class OAuthDiscoveryClassificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Ein reservierter, nie auflösbarer Name (RFC 2606) — je Lauf ein anderer.</summary>
    private static Uri UnresolvableIssuer() => new($"https://bifrost-{Guid.NewGuid():N}.invalid");

    /// <summary>
    /// G3a: Mit aktiver Prüfung privater Ziele (Standard) scheiterte die Auflösung des Issuers an
    /// der Zielprüfung — als OAuthDiscoveryException ohne Status und ohne innere Ausnahme, also als
    /// abgelehnte Anmeldung. Gleiche Ursache ohne Zielprüfung war schon immer eine Unerreichbarkeit.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_issuer_arrives_as_unreachable()
    {
        var id = new ServerId(Guid.NewGuid());
        var tokens = new OAuthTokenEndpointFailureTests.MemoryTokens();
        await tokens.SaveAsync(ExpiredToken(id, UnresolvableIssuer()), Ct);
        var config = Config(new Uri("http://127.0.0.1:1/mcp"), allowPrivateOAuthTargets: false);
        var connector = new StreamableHttpUpstreamConnector(tokens: tokens);

        var act = async () => await connector.ConnectAsync(id, config, Ct).WaitAsync(TimeSpan.FromMinutes(1), Ct);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>(
            "ein unauflösbarer Name wird von selbst wieder auflösbar — kurze Frist, kein „neu autorisieren“")).Which;
        Chain(thrown).Should().NotContain(e => e is UpstreamAuthenticationException);
    }

    /// <summary>G3a, Token-Endpunkt: derselbe Netzfehler, dieselbe Einordnung.</summary>
    [Fact]
    public async Task An_unresolvable_token_endpoint_is_transient()
    {
        var issuer = UnresolvableIssuer();
        var token = ExpiredToken(new ServerId(Guid.NewGuid()), issuer);

        var act = async () => await OAuthFlow.RefreshAsync(
            token, new Uri(issuer, "/token"), new UpstreamOAuthOptions("gateway"),
            "https://example.org/mcp", DateTimeOffset.UtcNow, Ct).WaitAsync(TimeSpan.FromMinutes(1), Ct);

        var thrown = (await act.Should().ThrowAsync<OAuthDiscoveryException>()).Which;
        OAuthDiscovery.IsTransient(thrown).Should().BeTrue("die Auflösung scheiterte, niemand hat abgelehnt");
    }

    /// <summary>
    /// G3b: Liefert der erste Well-known-Pfad 200 mit Unsinn, versucht die Erkennung die übrigen.
    /// Vorher warf das Lesen eine InvalidOperationException, die die Kandidatenschleife nicht fing.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("""{"issuer": 42}""")]
    [InlineData("42")]
    public async Task Nonsense_on_the_first_path_falls_through_to_the_next(string nonsense)
    {
        using var server = new MetadataServer(firstPathBody: nonsense, otherPathsValid: true);
        var id = new ServerId(Guid.NewGuid());
        var tokens = new OAuthTokenEndpointFailureTests.MemoryTokens();
        var expired = ExpiredToken(id, server.Issuer);
        await tokens.SaveAsync(expired, Ct);
        var connector = new StreamableHttpUpstreamConnector(tokens: tokens);

        var act = async () => await connector.ConnectAsync(id, Config(new Uri(server.Issuer, "/mcp")), Ct)
            .WaitAsync(TimeSpan.FromMinutes(1), Ct);

        // Der Upstream selbst antwortet mit 503 — bis dahin muss die Erneuerung gelungen sein.
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        Chain(thrown).Should().NotContain(e => e is UpstreamAuthenticationException || e is InvalidOperationException);
        (await tokens.GetAsync(id, Ct))!.AccessToken.Should().NotBe(expired.AccessToken, "die Erneuerung ist gelungen");
        server.FallbackRequests.Should().BeGreaterThan(0, "der zweite Pfad wurde gefragt");
        server.McpRequests.Should().BeGreaterThan(0, "das erneuerte Token ging an den Upstream");
    }

    /// <summary>
    /// G3b, N40: Liefert kein Pfad ein brauchbares Dokument, ist das dauerhaft — eine abgelehnte
    /// Anmeldung wie „überall 404", keine Unerreichbarkeit im Sekundentakt.
    /// </summary>
    [Fact]
    public async Task Nonsense_on_every_path_arrives_as_a_rejected_login()
    {
        using var server = new MetadataServer(firstPathBody: "[]", otherPathsValid: false);
        var id = new ServerId(Guid.NewGuid());
        var tokens = new OAuthTokenEndpointFailureTests.MemoryTokens();
        await tokens.SaveAsync(ExpiredToken(id, server.Issuer), Ct);
        var connector = new StreamableHttpUpstreamConnector(tokens: tokens);

        var act = async () => await connector.ConnectAsync(id, Config(new Uri(server.Issuer, "/mcp")), Ct)
            .WaitAsync(TimeSpan.FromMinutes(1), Ct);

        await act.Should().ThrowAsync<UpstreamAuthenticationException>();
        server.McpRequests.Should().Be(0);
    }

    /// <summary>
    /// N47 in der Fassung nach H8: Liefert ein Pfad Unsinn und sind die übrigen gestört (503), ist
    /// das eine Unerreichbarkeit — der gestörte Pfad entscheidet, nicht der Unsinn.
    /// </summary>
    [Fact]
    public async Task Nonsense_beside_a_disturbed_path_arrives_as_unreachable()
    {
        using var server = new MetadataServer(firstPathBody: "[]", otherPathsValid: false, HttpStatusCode.ServiceUnavailable);
        var id = new ServerId(Guid.NewGuid());
        var tokens = new OAuthTokenEndpointFailureTests.MemoryTokens();
        await tokens.SaveAsync(ExpiredToken(id, server.Issuer), Ct);
        var connector = new StreamableHttpUpstreamConnector(tokens: tokens);

        var act = async () => await connector.ConnectAsync(id, Config(new Uri(server.Issuer, "/mcp")), Ct)
            .WaitAsync(TimeSpan.FromMinutes(1), Ct);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        Chain(thrown).Should().NotContain(e => e is UpstreamAuthenticationException);
        Chain(thrown).OfType<HttpRequestException>().Should().Contain(e => e.StatusCode == HttpStatusCode.ServiceUnavailable);
        server.McpRequests.Should().Be(0);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static UpstreamServerConfig Config(Uri endpoint, bool allowPrivateOAuthTargets = true)
        => new(
            "oauth", "OAuth", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                endpoint,
                OAuth: new UpstreamOAuthOptions("gateway", AllowPrivateTargets: allowPrivateOAuthTargets),
                AllowPrivateTargets: true));

    private static UpstreamOAuthToken ExpiredToken(ServerId id, Uri issuer)
        => new(
            id, "at-" + Guid.NewGuid().ToString("N"), "rt-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddMinutes(-5), ["mcp:read"], issuer.ToString().TrimEnd('/'),
            DateTimeOffset.UtcNow.AddHours(-1));

    /// <summary>
    /// Ein Authorization Server, dessen erster Well-known-Pfad (RFC 8414) Unsinn liefert; die
    /// OIDC-Pfade liefern gültige Metadaten oder ebenfalls Unsinn. Token-Endpunkt gibt ein neues
    /// Token aus, der MCP-Endpunkt antwortet 503.
    /// </summary>
    private sealed class MetadataServer : IDisposable
    {
        private readonly HttpListener _listener = null!;
        private int _mcpRequests;
        private int _fallbackRequests;

        public MetadataServer(string firstPathBody, bool otherPathsValid, HttpStatusCode otherPathsStatus = HttpStatusCode.OK)
        {
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Issuer = new Uri($"http://127.0.0.1:{port}");
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    listener.Close();
                }
            }

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    var issuer = Issuer.ToString().TrimEnd('/');
                    var path = context.Request.Url!.AbsolutePath;
                    if (path.StartsWith("/.well-known/oauth-authorization-server", StringComparison.Ordinal))
                    {
                        Write(context, HttpStatusCode.OK, firstPathBody);
                    }
                    else if (path.StartsWith("/.well-known/", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _fallbackRequests);
                        Write(context, otherPathsStatus, otherPathsValid
                            ? $$"""
                                {
                                  "issuer": "{{issuer}}",
                                  "authorization_endpoint": "{{issuer}}/authorize",
                                  "token_endpoint": "{{issuer}}/token",
                                  "code_challenge_methods_supported": ["S256"]
                                }
                                """
                            : """{"issuer": ["kein", "string"]}""");
                    }
                    else if (path.StartsWith("/mcp", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _mcpRequests);
                        Write(context, HttpStatusCode.ServiceUnavailable, "{}");
                    }
                    else
                    {
                        Write(context, HttpStatusCode.OK,
                            $$"""{"access_token":"at-{{Guid.NewGuid():N}}","refresh_token":"rt-{{Guid.NewGuid():N}}","expires_in":3600}""");
                    }
                }
            });
        }

        public Uri Issuer { get; } = null!;

        public int McpRequests => Volatile.Read(ref _mcpRequests);

        public int FallbackRequests => Volatile.Read(ref _fallbackRequests);

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }

        private static void Write(HttpListenerContext context, HttpStatusCode status, string body)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(payload);
            context.Response.Close();
        }
    }
}
