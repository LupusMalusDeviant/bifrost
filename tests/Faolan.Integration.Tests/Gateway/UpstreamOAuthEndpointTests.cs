using System.Net;
using AwesomeAssertions;
using Faolan.Abstractions;
using Xunit;

namespace Faolan.Integration.Tests.Gateway;

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

    /// <summary>
    /// Plan 0005 F3: Erst das Audit, dann die Wirkung. Weist das Audit ab (Compliance-Modus, voller
    /// Kanal), wird kein Token gespeichert und nichts geweckt — und die Antwort ist ein Fehler.
    /// </summary>
    [Fact]
    public async Task A_refusing_audit_stores_no_token_and_wakes_nothing()
    {
        var (attempt, token, config) = Grant();
        var tokens = new Tokens();
        var observer = new Observer();

        var result = await Server.UpstreamOAuthEndpoints.StoreGrantAsync(
            attempt, token, config.Slug, "admin", _ => Task.FromResult<UpstreamServerConfig?>(config),
            tokens, observer, new RefusingAudit(), TimeProvider.System, TestContext.Current.CancellationToken);

        result.Should().NotBeOfType<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>();
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode.Should().Be(503);
        (await tokens.GetAsync(attempt.Server, TestContext.Current.CancellationToken)).Should().BeNull(
            "ohne Audit-Zeile keine Wirkung");
        observer.Calls.Should().Be(0);
    }

    /// <summary>
    /// Plan 0005 F3: Ist das Token gespeichert, wird der Upstream geweckt — auch wenn die
    /// Nachprüfung danach eine geänderte Anbindung findet und das eigene Token wieder entfernt.
    /// </summary>
    [Fact]
    public async Task A_stored_grant_always_wakes_the_upstream()
    {
        var (attempt, token, config) = Grant();
        var tokens = new Tokens();
        var observer = new Observer();
        var changed = config with
        {
            Http = config.Http! with { Endpoint = new Uri("https://mcp.example.com/anders") },
        };

        var result = await Server.UpstreamOAuthEndpoints.StoreGrantAsync(
            attempt, token, config.Slug, "admin", _ => Task.FromResult<UpstreamServerConfig?>(changed),
            tokens, observer, new RecordingAudit(), TimeProvider.System, TestContext.Current.CancellationToken);

        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
        observer.Calls.Should().Be(1, "gespeichert war das Token, und ein laufender Versuch hat es womöglich gelesen");
        (await tokens.GetAsync(attempt.Server, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>
    /// Plan 0005 F5: Findet die Nachprüfung eine geänderte Anbindung, entfernt der Rückweg nur sein
    /// eigenes Token — nicht eine neuere Autorisierung, die inzwischen gespeichert wurde.
    /// </summary>
    [Fact]
    public async Task The_callback_cleanup_removes_only_its_own_token()
    {
        var (attempt, token, config) = Grant();
        var tokens = new Tokens();
        var changed = config with
        {
            Http = config.Http! with { Endpoint = new Uri("https://mcp.example.com/anders") },
        };
        var newer = Grant().Token with { Server = attempt.Server };

        var result = await Server.UpstreamOAuthEndpoints.StoreGrantAsync(
            attempt, token, config.Slug, "admin",
            async c =>
            {
                // Zwischen Speichern und Nachprüfen: Ein zweiter Vorgang für die neue Anbindung
                // speichert seine Autorisierung.
                await tokens.SaveAsync(newer, c);
                return changed;
            },
            tokens, new Observer(), new RecordingAudit(), TimeProvider.System, TestContext.Current.CancellationToken);

        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
        (await tokens.GetAsync(attempt.Server, TestContext.Current.CancellationToken))!.AccessToken
            .Should().Be(newer.AccessToken, "die neuere Autorisierung bleibt stehen");
    }

    private static (OAuthAuthorizationAttempt Attempt, UpstreamOAuthToken Token, UpstreamServerConfig Config) Grant()
    {
        var server = ServerId.New();
        var endpoint = new Uri("https://mcp.example.com/mcp");
        var config = new UpstreamServerConfig(
            "oauth-f3", "OAuth", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(endpoint, OAuth: new UpstreamOAuthOptions("gateway")));
        var attempt = new OAuthAuthorizationAttempt(
            "state-" + Guid.NewGuid().ToString("N"), server, "v-" + Guid.NewGuid().ToString("N"),
            "https://as.example.com", new Uri("https://as.example.com/token"),
            new Uri("https://gateway.example.com/oauth/upstream/callback"),
            OAuthAuthorizationAttempt.ResourceFor(endpoint), ["mcp:read"], DateTimeOffset.UtcNow.AddMinutes(5))
        {
            ClientId = "gateway",
        };
        var token = new UpstreamOAuthToken(
            server, "at-" + Guid.NewGuid().ToString("N"), "rt-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddHours(1), ["mcp:read"], "https://as.example.com", DateTimeOffset.UtcNow);
        return (attempt, token, config);
    }

    private sealed class RefusingAudit : IAuditSink
    {
        public void Record(AuditEvent evt)
            => throw new Faolan.Persistence.Audit.AuditUnavailableException(
                "Audit-Channel ist voll; Compliance-Modus verweigert weitere Aktionen.");
    }

    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public void Record(AuditEvent evt) => Events.Add(evt);
    }

    private sealed class Observer : IUpstreamCredentialObserver
    {
        public int Calls { get; private set; }

        public void CredentialsChanged(ServerId id) => Calls++;
    }

    private sealed class Tokens : IUpstreamOAuthTokenStore
    {
        private readonly Dictionary<ServerId, UpstreamOAuthToken> _tokens = [];

        public Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct)
        {
            lock (_tokens)
            {
                return Task.FromResult(_tokens.GetValueOrDefault(server));
            }
        }

        public Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct)
        {
            lock (_tokens)
            {
                _tokens[token.Server] = token;
                return Task.CompletedTask;
            }
        }

        public Task RemoveAsync(ServerId server, CancellationToken ct)
        {
            lock (_tokens)
            {
                _tokens.Remove(server);
                return Task.CompletedTask;
            }
        }

        public Task<bool> ReplaceAsync(UpstreamOAuthToken expected, UpstreamOAuthToken replacement, CancellationToken ct)
        {
            lock (_tokens)
            {
                if (!_tokens.TryGetValue(expected.Server, out var current) || current.Revision != expected.Revision)
                {
                    return Task.FromResult(false);
                }

                _tokens[expected.Server] = replacement;
                return Task.FromResult(true);
            }
        }

        public Task<bool> RemoveIfUnchangedAsync(UpstreamOAuthToken expected, CancellationToken ct)
        {
            lock (_tokens)
            {
                return Task.FromResult(
                    _tokens.TryGetValue(expected.Server, out var current)
                    && current.Revision == expected.Revision
                    && _tokens.Remove(expected.Server));
            }
        }
    }
}
