using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Upstream;
using Xunit;

namespace Faolan.Integration.Tests.Upstreams;

/// <summary>
/// Plan 0005 D6: Die Erneuerungsanforderung des Supervisors gilt dem Token, das die abgewiesene
/// Verbindung vorgelegt hat — nicht dem, das beim nächsten Aufbau zufällig in der Ablage liegt.
/// </summary>
public sealed class OAuthRenewalIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_renewal_marks_the_presented_token_not_a_newer_grant()
    {
        await using var upstream = new ScriptedMcpUpstream();
        await upstream.StartAsync();
        var id = new ServerId(Guid.NewGuid());
        var tokens = new Tokens();

        // Der Aussteller ist absichtlich unerreichbar: Eine Erneuerung, die hier nicht sein dürfte,
        // scheiterte daran sichtbar, statt still zu gelingen.
        const string unreachableIssuer = "http://127.0.0.1:1";
        await tokens.SaveAsync(Token(id, unreachableIssuer), Ct);
        var config = new UpstreamServerConfig(
            "oauth", "OAuth", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                upstream.Endpoint,
                OAuth: new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
                AllowPrivateTargets: true));
        var connector = new StreamableHttpUpstreamConnector(tokens: tokens);

        var first = await connector.ConnectAsync(id, config, Ct);
        await first.DisposeAsync();

        // Inzwischen neu autorisiert; danach meldet der Supervisor die alte Verbindung als abgewiesen.
        var granted = Token(id, unreachableIssuer);
        await tokens.SaveAsync(granted, Ct);
        connector.RequestCredentialRenewal(id);

        var second = await connector.ConnectAsync(id, config, Ct);
        await using var _ = second;

        ((IPresentedCredentials)second).PresentedCredentials.Should().Equal(
            [granted.AccessToken], "das neue Token ist nicht abgewiesen und wird ohne Erneuerung vorgelegt");
        (await tokens.GetAsync(id, Ct))!.AccessToken.Should().Be(granted.AccessToken);
    }

    /// <summary>
    /// E11: Meldet der Supervisor eine ältere Verbindung als abgewiesen, nachdem schon eine neuere mit
    /// frischem Token aufgebaut ist, gilt das frische Token nicht als abgewiesen.
    /// </summary>
    [Fact]
    public async Task A_late_refusal_of_an_older_connection_does_not_mark_the_newer_token()
    {
        await using var upstream = new ScriptedMcpUpstream();
        await upstream.StartAsync();
        var id = new ServerId(Guid.NewGuid());
        var tokens = new Tokens();
        const string unreachableIssuer = "http://127.0.0.1:1";
        var old = Token(id, unreachableIssuer);
        await tokens.SaveAsync(old, Ct);
        var config = new UpstreamServerConfig(
            "oauth", "OAuth", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                upstream.Endpoint,
                OAuth: new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
                AllowPrivateTargets: true));
        var connector = new StreamableHttpUpstreamConnector(tokens: tokens);

        var first = await connector.ConnectAsync(id, config, Ct);
        await first.DisposeAsync();
        var granted = Token(id, unreachableIssuer);
        await tokens.SaveAsync(granted, Ct);
        var second = await connector.ConnectAsync(id, config, Ct);
        await second.DisposeAsync();

        // Erst jetzt kommt die Abweisung der ERSTEN Verbindung an.
        ((IUpstreamCredentialRenewal)connector).RequestCredentialRenewal(id, old.AccessToken);

        var third = await connector.ConnectAsync(id, config, Ct);
        await using var _ = third;
        ((IPresentedCredentials)third).PresentedCredentials.Should().Equal(
            [granted.AccessToken], "abgewiesen war das alte Token, nicht das der neueren Verbindung");
    }

    private static UpstreamOAuthToken Token(ServerId id, string issuer)
        => new(
            id, "at-" + Guid.NewGuid().ToString("N"), "rt-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddHours(1), ["mcp:read"], issuer, DateTimeOffset.UtcNow);

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
                if (!_tokens.TryGetValue(expected.Server, out var current) || current.AccessToken != expected.AccessToken)
                {
                    return Task.FromResult(false);
                }

                _tokens[expected.Server] = replacement;
                return Task.FromResult(true);
            }
        }
    }
}
