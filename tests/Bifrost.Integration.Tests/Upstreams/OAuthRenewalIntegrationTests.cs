using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream;
using Xunit;

namespace Bifrost.Integration.Tests.Upstreams;

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
