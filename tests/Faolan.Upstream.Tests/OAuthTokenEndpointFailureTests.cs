using System.Net;
using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Upstream.OAuth;
using Xunit;

namespace Faolan.Upstream.Tests;

/// <summary>
/// L5 (Plan 0005): Ein gestörter Token-Endpunkt ist keine abgelehnte Anmeldung. Ein 5xx oder 429
/// beim Erneuern des Tokens muss als „nicht erreichbar" ankommen — sonst wartet der Upstream die
/// 15 min einer abgelehnten Anmeldung ab, obwohl der Endpunkt Sekunden später wieder antwortet.
/// </summary>
public sealed class OAuthTokenEndpointFailureTests
{
    /// <summary>Die Obergrenze einer Token-Laufzeit (N53): zehn Jahre in Sekunden.</summary>
    private const long TenYears = 10L * 365 * 24 * 60 * 60;

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
    /// abgewiesene Token noch einmal vorzulegen. Ist der Token-Endpunkt gestört, bleibt die
    /// Anforderung stehen (Codex H3): Auch der nächste Aufbau legt das abgewiesene Token nicht vor.
    /// </summary>
    [Fact]
    public async Task A_requested_renewal_survives_a_disturbed_token_endpoint()
    {
        using var server = new AuthorizationServer(HttpStatusCode.ServiceUnavailable);
        var (connector, config, id) = await ArrangeAsync(server, expired: false);
        var ct = TestContext.Current.CancellationToken;

        connector.RequestCredentialRenewal(id);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var renewing = async () => await connector.ConnectAsync(id, config, ct);
            (await renewing.Should().ThrowAsync<HttpRequestException>())
                .WithMessage("*Token-Endpunkt*", "das abgewiesene Token wird nicht noch einmal vorgelegt");
        }

        server.McpRequests.Should().Be(0, "kein Aufbau hat das abgewiesene Token an den Upstream geschickt");
    }

    /// <summary>
    /// Ist inzwischen ein anderes Token gespeichert (neu autorisiert), ist die Anforderung erledigt:
    /// Das neue Token ist nicht abgewiesen und wird ohne Erneuerung vorgelegt.
    /// </summary>
    [Fact]
    public async Task A_new_grant_settles_a_pending_renewal()
    {
        using var server = new AuthorizationServer(HttpStatusCode.ServiceUnavailable);
        var (connector, config, id, tokens) = await ArrangeWithStoreAsync(server, expired: false);
        var ct = TestContext.Current.CancellationToken;

        connector.RequestCredentialRenewal(id);
        var renewing = async () => await connector.ConnectAsync(id, config, ct);
        await renewing.Should().ThrowAsync<HttpRequestException>();

        var fresh = ExpiredToken(id, server.Issuer) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ObtainedAt = DateTimeOffset.UtcNow,
        };
        await tokens.SaveAsync(fresh, ct);

        var connecting = async () => await connector.ConnectAsync(id, config, ct);
        (await connecting.Should().ThrowAsync<Exception>())
            .Which.Message.Should().NotContain("Token-Endpunkt", "das neue Token wird nicht erneuert");
        server.McpRequests.Should().BeGreaterThan(0, "das neue Token ging an den Upstream");
    }

    /// <summary>
    /// Codex M3: Wird während einer Erneuerung getrennt, schreibt die Erneuerung das Token nicht
    /// zurück — nach dem Trennen bleibt der Upstream ohne Token.
    /// </summary>
    [Fact]
    public async Task A_disconnect_during_a_refresh_is_not_undone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new AuthorizationServer(HttpStatusCode.OK);
        var (connector, config, id, tokens) = await ArrangeWithStoreAsync(server, expired: true);
        server.OnTokenRequest = () => tokens.RemoveAsync(id, ct).GetAwaiter().GetResult();

        var connecting = async () => await connector.ConnectAsync(id, config, ct);

        await connecting.Should().ThrowAsync<UpstreamAuthenticationException>();
        (await tokens.GetAsync(id, ct)).Should().BeNull("das Trennen hat Vorrang vor der späten Erneuerung");
    }

    /// <summary>
    /// Codex M3: Wird während einer Erneuerung neu autorisiert, bleibt die neue Autorisierung stehen.
    /// </summary>
    [Fact]
    public async Task A_new_grant_during_a_refresh_is_not_overwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new AuthorizationServer(HttpStatusCode.OK);
        var (connector, config, id, tokens) = await ArrangeWithStoreAsync(server, expired: true);
        var fresh = ExpiredToken(id, server.Issuer) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ObtainedAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        server.OnTokenRequest = () => tokens.SaveAsync(fresh, ct).GetAwaiter().GetResult();

        var connecting = async () => await connector.ConnectAsync(id, config, ct);

        await connecting.Should().ThrowAsync<Exception>();
        (await tokens.GetAsync(id, ct))!.AccessToken.Should().Be(fresh.AccessToken);
    }

    /// <summary>
    /// Codex H1: Die Meldung einer abgelehnten Token-Anfrage nennt Status und bekannten Fehlercode,
    /// nie den Antwortkörper — ein spiegelnder Server gäbe sonst Code, Verifier oder Secret wieder.
    /// </summary>
    [Fact]
    public async Task A_refused_token_request_does_not_repeat_the_response_body()
    {
        using var server = new AuthorizationServer(HttpStatusCode.BadRequest);
        var token = ExpiredToken(new ServerId(Guid.NewGuid()), server.Issuer);

        var act = async () => await OAuthFlow.RefreshAsync(
            token, new Uri(server.Issuer, "/token"), new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
            "http://127.0.0.1/mcp", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<OAuthDiscoveryException>()).Which;
        thrown.Message.Should().NotContain("geheim-aus-dem-token-endpunkt");
        thrown.Message.Should().Contain("temporarily_unavailable", "ein bekannter Fehlercode darf stehen");
    }

    /// <summary>
    /// Plan 0005 D9: Nennt die Erneuerung kein neues Refresh-Token (RFC 6749 §6 erlaubt das), gilt
    /// das alte weiter — sonst ließe sich das Token nur ein einziges Mal erneuern.
    /// </summary>
    [Fact]
    public async Task A_refresh_without_a_new_refresh_token_keeps_the_old_one()
    {
        using var server = new AuthorizationServer(HttpStatusCode.OK) { OmitRefreshToken = true };
        var token = ExpiredToken(new ServerId(Guid.NewGuid()), server.Issuer);

        var refreshed = await OAuthFlow.RefreshAsync(
            token, new Uri(server.Issuer, "/token"), new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
            "http://127.0.0.1/mcp", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        refreshed.AccessToken.Should().NotBe(token.AccessToken);
        refreshed.RefreshToken.Should().Be(token.RefreshToken);
    }

    /// <summary>
    /// Plan 0005 F12: Weist der Authorization Server schon den Abruf seiner Metadaten mit 401/403
    /// ab, ist das eine abgelehnte Anmeldung — keine Unerreichbarkeit im Sekundentakt.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refused_metadata_arrives_as_a_rejected_login(HttpStatusCode status)
    {
        using var server = new AuthorizationServer(HttpStatusCode.OK) { MetadataStatus = status };
        var (connector, config, id) = await ArrangeAsync(server);

        var act = async () => await connector.ConnectAsync(id, config, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UpstreamAuthenticationException>();
    }

    /// <summary>
    /// Plan 0005 J7: Ein 401 des Token-Endpunkts mit ungültigem Text im <c>error</c>-Feld (einzelnes
    /// Surrogat) bleibt eine abgelehnte Anmeldung. Vorher warf das Lesen des Fehlercodes, bevor der
    /// Status festgehalten war, und die Erneuerung galt als „nicht erreichbar".
    /// </summary>
    [Fact]
    public async Task A_refusal_with_an_undecodable_error_code_stays_a_rejected_login()
    {
        using var server = new AuthorizationServer(HttpStatusCode.Unauthorized) { ErrorBody = """{"error":"\uD800"}""" };
        var (connector, config, id) = await ArrangeAsync(server);

        var act = async () => await connector.ConnectAsync(id, config, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UpstreamAuthenticationException>();
        server.McpRequests.Should().Be(0);
    }

    /// <summary>
    /// Plan 0005 F12: Gestörte Metadaten (5xx, 429) sind wie ein gestörter Token-Endpunkt eine
    /// Unerreichbarkeit mit Status — sie werden bald wieder versucht.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Disturbed_metadata_arrives_as_unreachable(HttpStatusCode status)
    {
        using var server = new AuthorizationServer(HttpStatusCode.OK) { MetadataStatus = status };
        var (connector, config, id) = await ArrangeAsync(server);

        var act = async () => await connector.ConnectAsync(id, config, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
    }

    /// <summary>
    /// Plan 0005 F12: Fehlen die Metadaten dauerhaft (404 auf allen Wegen), ist das ebenfalls eine
    /// Sache der Anmeldung — von selbst wird es nicht besser.
    /// </summary>
    [Fact]
    public async Task Missing_metadata_arrives_as_a_rejected_login()
    {
        using var server = new AuthorizationServer(HttpStatusCode.OK) { MetadataStatus = HttpStatusCode.NotFound };
        var (connector, config, id) = await ArrangeAsync(server);

        var act = async () => await connector.ConnectAsync(id, config, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UpstreamAuthenticationException>();
    }

    /// <summary>
    /// Plan 0005 F13: Ein Aufbau, der sein Token gelesen hat, BEVOR ein neueres gespeichert und als
    /// abgewiesen gemeldet wurde, darf den Vermerk für das neuere nicht löschen — sonst legte der
    /// nächste Aufbau das abgewiesene Token ohne Erneuerung vor.
    /// </summary>
    [Fact]
    public async Task A_stale_build_does_not_clear_the_renewal_of_a_newer_token()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new AuthorizationServer(HttpStatusCode.ServiceUnavailable);
        var (connector, config, id, tokens) = await ArrangeWithStoreAsync(server, expired: false);
        var newer = ExpiredToken(id, server.Issuer) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ObtainedAt = DateTimeOffset.UtcNow,
        };

        // Während der erste Aufbau liest: Ein neueres Token wird gespeichert, mit ihm verbunden und
        // abgewiesen. Der Aufbau hält danach noch das ältere in der Hand.
        tokens.OnGet = () =>
        {
            tokens.OnGet = null;
            var stale = tokens.Peek(id);
            tokens.SaveAsync(newer, ct).GetAwaiter().GetResult();
            ((IUpstreamCredentialRenewal)connector).RequestCredentialRenewal(id, newer.AccessToken);
            return stale;
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var connecting = async () => await connector.ConnectAsync(id, config, ct);
            await connecting.Should().ThrowAsync<Exception>();
        }

        server.McpRequests.Should().Be(0, "das abgewiesene neuere Token ging nicht ohne Erneuerung an den Upstream");
    }

    /// <summary>
    /// Plan 0005 F5: Ein Token, das für einen anderen Endpunkt oder eine andere Client-ID ausgestellt
    /// wurde, geht nicht an den Upstream — auch nicht in der Lücke, bevor das Umkonfigurieren es löscht.
    /// </summary>
    [Theory]
    [InlineData("/alt", "gateway")]
    [InlineData("/mcp", "anderer-client")]
    public async Task A_token_of_another_binding_is_not_presented(string path, string clientId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new AuthorizationServer(HttpStatusCode.OK);
        var (connector, config, id, tokens) = await ArrangeWithStoreAsync(server, expired: false);
        var bound = ExpiredToken(id, server.Issuer) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Resource = OAuthAuthorizationAttempt.ResourceFor(new Uri(server.Issuer, path)),
            ClientId = clientId,
        };
        await tokens.SaveAsync(bound, ct);

        var connecting = async () => await connector.ConnectAsync(id, config, ct);

        (await connecting.Should().ThrowAsync<UpstreamAuthenticationException>())
            .WithMessage("*anderen OAuth-Anbindung*");
        server.McpRequests.Should().Be(0, "das Token der alten Anbindung ging nicht an den Upstream");
    }

    /// <summary>
    /// Plan 0005 F5: Ein Token aus der Zeit vor der Bindung (Bestand nach dem Update) wird
    /// vorgelegt, und seine erste Erneuerung schreibt die Bindung der aktuellen Anbindung nach.
    /// </summary>
    [Fact]
    public async Task A_token_from_before_the_binding_is_used_and_bound_by_its_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new AuthorizationServer(HttpStatusCode.OK);
        var (connector, config, id, tokens) = await ArrangeWithStoreAsync(server, expired: true);
        (await tokens.GetAsync(id, ct))!.Resource.Should().BeNull("so liegt ein Bestandstoken vor");

        var connecting = async () => await connector.ConnectAsync(id, config, ct);
        await connecting.Should().ThrowAsync<Exception>("der Upstream selbst antwortet hier mit 503");

        var stored = (await tokens.GetAsync(id, ct))!;
        stored.Resource.Should().Be(OAuthAuthorizationAttempt.ResourceFor(config.Http!.Endpoint));
        stored.ClientId.Should().Be("gateway");
        server.McpRequests.Should().BeGreaterThan(0, "das erneuerte Token ging an den Upstream");
    }

    /// <summary>
    /// Plan 0005 H7: Eine Token-Antwort mit HTTP 200 in falscher Form ist eine abgelehnte Anmeldung
    /// wie ungültiges JSON — nicht „nicht erreichbar" mit kurzer Frist. Vorher warf das Lesen eine
    /// InvalidOperationException, die am Connector vorbeilief.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("""{"access_token":1,"expires_in":3600}""")]
    [InlineData("""{"access_token":"ACCESS","scope":["a"]}""")]
    [InlineData("""{"access_token":"ACCESS","refresh_token":{}}""")]
    [InlineData("""{"access_token":"ACCESS","expires_in":"bald"}""")]
    [InlineData("""{"access_token":"\uD800"}""")]
    [InlineData("""{"access_token":"ACCESS","scope":"\uDC00 a"}""")]
    [InlineData("""{"access_token":"ACCESS","refresh_token":"\uD800"}""")]
    public async Task A_malformed_token_response_arrives_as_a_rejected_login(string body)
    {
        var access = "at-" + Guid.NewGuid().ToString("N");
        using var server = new AuthorizationServer(HttpStatusCode.OK) { TokenBody = () => body.Replace("ACCESS", access) };
        var (connector, config, id) = await ArrangeAsync(server);

        var act = async () => await connector.ConnectAsync(id, config, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<UpstreamAuthenticationException>())
            .Which.Message.Should().NotContain(access);
        server.McpRequests.Should().Be(0, "ohne brauchbares Token geht nichts an den Upstream");
    }

    /// <summary>
    /// H7, I8: Manche Anbieter senden die Laufzeit als Text; der wird gelesen, nicht verworfen. Eine
    /// Kommazahl wird abgerundet, alles über zehn Jahre (als Zahl wie als Text) auf zehn Jahre begrenzt.
    /// </summary>
    [Theory]
    [InlineData("\"3600\"", 3600L)]
    [InlineData("3600.7", 3600L)]
    [InlineData("99999999999999", TenYears)]
    [InlineData("\"99999999999999999999999\"", TenYears)]
    [InlineData("99999999999999999999999", TenYears)]
    [InlineData("1e400", TenYears)]
    [InlineData("-5", 0L)]
    public async Task The_lifetime_is_read_from_text_fractions_and_huge_numbers(string expiresIn, long expectedSeconds)
    {
        using var server = new AuthorizationServer(HttpStatusCode.OK)
        {
            TokenBody = () => $$"""{"access_token":"at-{{Guid.NewGuid():N}}","expires_in":{{expiresIn}}}""",
        };
        var token = ExpiredToken(new ServerId(Guid.NewGuid()), server.Issuer);
        var now = DateTimeOffset.UtcNow;

        var refreshed = await OAuthFlow.RefreshAsync(
            token, new Uri(server.Issuer, "/token"), new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
            "http://127.0.0.1/mcp", now, TestContext.Current.CancellationToken);

        refreshed.ExpiresAt.Should().Be(now + TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData("invalid_grant", "invalid_grant")]
    [InlineData("""{"error":"invalid_client","error_description":"x"}""", "invalid_client")]
    [InlineData("""{"error":"Bearer geheim"}""", null)]
    [InlineData("geheim", null)]
    [InlineData("""{"error":"\uD800"}""", null)]
    public void Only_known_error_codes_pass(string input, string? expected)
        => OAuthFlow.KnownErrorCode(input).Should().Be(expected);

    private static async Task<(StreamableHttpUpstreamConnector Connector, UpstreamServerConfig Config, ServerId Id)> ArrangeAsync(
        AuthorizationServer server, bool expired = true)
    {
        var (connector, config, id, _) = await ArrangeWithStoreAsync(server, expired);
        return (connector, config, id);
    }

    private static async Task<(StreamableHttpUpstreamConnector Connector, UpstreamServerConfig Config, ServerId Id, MemoryTokens Tokens)>
        ArrangeWithStoreAsync(AuthorizationServer server, bool expired)
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
        return (new StreamableHttpUpstreamConnector(tokens: tokens), config, id, tokens);
    }

    private static UpstreamOAuthToken ExpiredToken(ServerId id, Uri issuer)
        => new(
            id, "at-" + Guid.NewGuid().ToString("N"), "rt-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddMinutes(-5), ["mcp:read"], issuer.ToString().TrimEnd('/'),
            DateTimeOffset.UtcNow.AddHours(-1));

    internal sealed class MemoryTokens : IUpstreamOAuthTokenStore
    {
        private readonly Dictionary<ServerId, UpstreamOAuthToken> _tokens = [];

        /// <summary>Ersetzt einen Lesevorgang — für das, was zwischen Lesen und Prüfen geschieht.</summary>
        public Func<UpstreamOAuthToken?>? OnGet { get; set; }

        public UpstreamOAuthToken? Peek(ServerId server) => _tokens.GetValueOrDefault(server);

        public Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct)
            => Task.FromResult(OnGet is { } hook ? hook() : _tokens.GetValueOrDefault(server));

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

        public Task<bool> ReplaceAsync(UpstreamOAuthToken expected, UpstreamOAuthToken replacement, CancellationToken ct)
        {
            lock (_tokens)
            {
                if (!_tokens.TryGetValue(expected.Server, out var current)
                    || current.AccessToken != expected.AccessToken || current.ObtainedAt != expected.ObtainedAt)
                {
                    return Task.FromResult(false);
                }

                _tokens[expected.Server] = replacement;
                return Task.FromResult(true);
            }
        }
    }

    /// <summary>Metadaten unter dem Well-Known-Pfad, ein Token-Endpunkt mit festem Fehlerstatus.</summary>
    private sealed class AuthorizationServer : IDisposable
    {
        private readonly LoopbackHttpServer _server;

        public AuthorizationServer(HttpStatusCode tokenStatus)
        {
            _server = new LoopbackHttpServer(context =>
            {
                var issuer = Issuer.ToString().TrimEnd('/');
                if (context.Request.Url!.AbsolutePath.StartsWith("/.well-known/", StringComparison.Ordinal)
                    && MetadataStatus != HttpStatusCode.OK)
                {
                    Write(context, MetadataStatus, "{}");
                }
                else if (context.Request.Url!.AbsolutePath.StartsWith("/.well-known/", StringComparison.Ordinal))
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
                else if (context.Request.Url.AbsolutePath.StartsWith("/mcp", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _mcpRequests);
                    Write(context, HttpStatusCode.ServiceUnavailable, "{}");
                }
                else if (tokenStatus == HttpStatusCode.OK)
                {
                    OnTokenRequest?.Invoke();
                    Write(context, HttpStatusCode.OK, TokenBody is { } body ? body() : OmitRefreshToken
                        ? $$"""{"access_token":"at-{{Guid.NewGuid():N}}","expires_in":3600}"""
                        : $$"""{"access_token":"at-{{Guid.NewGuid():N}}","refresh_token":"rt-{{Guid.NewGuid():N}}","expires_in":3600}""");
                }
                else
                {
                    Write(context, tokenStatus, ErrorBody ?? """{"error":"temporarily_unavailable","error_description":"geheim-aus-dem-token-endpunkt"}""");
                }

                return Task.CompletedTask;
            });
        }

        public Uri Issuer => _server.Address;

        private int _mcpRequests;

        /// <summary>Anfragen an den MCP-Endpunkt — jede trug ein Token an den Upstream.</summary>
        public int McpRequests => Volatile.Read(ref _mcpRequests);

        /// <summary>Läuft, während der Token-Endpunkt antwortet — die Lücke, in der andere schreiben.</summary>
        public Action? OnTokenRequest { get; set; }

        /// <summary>Der Status, mit dem jeder Metadaten-Abruf beantwortet wird; <c>OK</c> liefert sie aus.</summary>
        public HttpStatusCode MetadataStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>Antwortet ohne neues Refresh-Token — RFC 6749 §6 erlaubt das.</summary>
        public bool OmitRefreshToken { get; set; }

        /// <summary>Ersetzt den Körper einer erfolgreichen Token-Antwort (HTTP 200).</summary>
        public Func<string>? TokenBody { get; init; }

        /// <summary>Wenn gesetzt, der Fehlerkörper des Token-Endpunkts bei einem Status ungleich 200.</summary>
        public string? ErrorBody { get; init; }

        public void Dispose() => _server.Dispose();

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
