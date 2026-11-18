using System.Net;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.OAuth;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// L5 (Plan 0005): Ein gestörter Token-Endpunkt ist keine abgelehnte Anmeldung. Ein 5xx oder 429
/// beim Erneuern des Tokens muss als „nicht erreichbar" ankommen — sonst wartet der Upstream die
/// 15 min einer abgelehnten Anmeldung ab, obwohl der Endpunkt Sekunden später wieder antwortet.
/// </summary>
public sealed class OAuthTokenEndpointFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_disturbed_token_endpoint_arrives_as_unreachable(HttpStatusCode status)
    {
        using var server = new AuthorizationServer(status);
        var (connector, config, id) = await ArrangeAsync(server);

        var act = async () => await connector.ConnectAsync(id, config, TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.StatusCode.Should().Be(status);
        thrown.Message.Should().NotContain("geheim-aus-dem-token-endpunkt", "der Antwortkörper bleibt draußen");
    }

    [Fact]
    public async Task A_refusing_token_endpoint_still_arrives_as_a_rejected_login()
    {
        using var server = new AuthorizationServer(HttpStatusCode.BadRequest);
        var (connector, config, id) = await ArrangeAsync(server);

        var act = async () => await connector.ConnectAsync(id, config, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UpstreamAuthenticationException>();
    }

    [Fact]
    public async Task The_token_request_carries_the_status_of_its_refusal()
    {
        using var server = new AuthorizationServer(HttpStatusCode.BadGateway);
        var token = ExpiredToken(new ServerId(Guid.NewGuid()), server.Issuer);

        var act = async () => await OAuthFlow.RefreshAsync(
            token, new Uri(server.Issuer, "/token"), new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
            "http://127.0.0.1/mcp", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OAuthDiscoveryException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    /// <summary>
    /// V1: Weist der Upstream ein Token ab, das laut Ablaufzeit noch gälte, verlangt der Supervisor
    /// die Erneuerung — und der nächste Verbindungsaufbau geht zum Token-Endpunkt, statt das
    /// abgewiesene Token noch einmal vorzulegen. Einmal: danach gilt wieder die Ablaufzeit.
    /// </summary>
    [Fact]
    public async Task A_requested_renewal_refreshes_a_token_that_has_not_expired_yet()
    {
        using var server = new AuthorizationServer(HttpStatusCode.ServiceUnavailable);
        var (connector, config, id) = await ArrangeAsync(server, expired: false);
        var ct = TestContext.Current.CancellationToken;

        connector.RequestCredentialRenewal(id);
        var renewing = async () => await connector.ConnectAsync(id, config, ct);
        (await renewing.Should().ThrowAsync<HttpRequestException>())
            .WithMessage("*Token-Endpunkt*", "erneuert wurde, obwohl das Token noch gälte");

        var again = async () => await connector.ConnectAsync(id, config, ct);
        (await again.Should().ThrowAsync<Exception>())
            .Which.Message.Should().NotContain("Token-Endpunkt", "die Anforderung gilt für einen Aufbau, nicht für immer");
    }

    private static async Task<(StreamableHttpUpstreamConnector Connector, UpstreamServerConfig Config, ServerId Id)> ArrangeAsync(
        AuthorizationServer server, bool expired = true)
    {
        var id = new ServerId(Guid.NewGuid());
        var tokens = new MemoryTokens();
        var token = ExpiredToken(id, server.Issuer);
        await tokens.SaveAsync(
            expired ? token : token with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            TestContext.Current.CancellationToken);
        var config = new UpstreamServerConfig(
            "oauth", "OAuth", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                new Uri(server.Issuer, "/mcp"),
                OAuth: new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
                AllowPrivateTargets: true));
        return (new StreamableHttpUpstreamConnector(tokens: tokens), config, id);
    }

    private static UpstreamOAuthToken ExpiredToken(ServerId id, Uri issuer)
        => new(
            id, "at-" + Guid.NewGuid().ToString("N"), "rt-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddMinutes(-5), ["mcp:read"], issuer.ToString().TrimEnd('/'),
            DateTimeOffset.UtcNow.AddHours(-1));

    private sealed class MemoryTokens : IUpstreamOAuthTokenStore
    {
        private readonly Dictionary<ServerId, UpstreamOAuthToken> _tokens = [];

        public Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct)
            => Task.FromResult(_tokens.GetValueOrDefault(server));

        public Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct)
        {
            _tokens[token.Server] = token;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(ServerId server, CancellationToken ct)
        {
            _tokens.Remove(server);
            return Task.CompletedTask;
        }
    }

    /// <summary>Metadaten unter dem Well-Known-Pfad, ein Token-Endpunkt mit festem Fehlerstatus.</summary>
    private sealed class AuthorizationServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public AuthorizationServer(HttpStatusCode tokenStatus)
        {
            // Den Port vergibt das Betriebssystem erst beim Binden; ein vorher erfragter freier
            // Port kann bis dahin belegt sein. Darum: versuchen, bis einer frei bleibt.
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    _listener.Start();
                    Issuer = new Uri($"http://127.0.0.1:{port}");
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
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
                    if (context.Request.Url!.AbsolutePath.StartsWith("/.well-known/", StringComparison.Ordinal))
                    {
                        Write(context, HttpStatusCode.OK, $$"""
                            {
                              "issuer": "{{issuer}}",
                              "authorization_endpoint": "{{issuer}}/authorize",
                              "token_endpoint": "{{issuer}}/token",
                              "code_challenge_methods_supported": ["S256"]
                            }
                            """);
                    }
                    else
                    {
                        Write(context, tokenStatus, """{"error":"temporarily_unavailable","error_description":"geheim-aus-dem-token-endpunkt"}""");
                    }
                }
            });
        }

        public Uri Issuer { get; } = null!;

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
