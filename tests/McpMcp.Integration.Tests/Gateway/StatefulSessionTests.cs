using AwesomeAssertions;
using McpMcp.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Der Gateway im <b>Sitzungsbetrieb</b> (<c>MCPMCP_MCP_STATELESS=0</c>) — der Rückfallweg für
/// Betreiber, deren Clients alle auf dem vorigen Stand stehen und die
/// <c>tools/list_changed</c> brauchen.
/// </summary>
public sealed class StatefulGatewayFixture : GatewayFixture
{
    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings =>
        [new("MCPMCP_MCP_STATELESS", "0")];
}

/// <summary>
/// Was im Sitzungsbetrieb gilt — und was er kostet.
/// <para>
/// Beide Tests zusammen sind die Begründung dafür, dass der stateless Betrieb die Vorgabe ist: Der
/// Sitzungsbetrieb kann etwas mehr (Benachrichtigungen), aber er <b>zwingt jeden</b> Client auf den
/// alten Stand zurück — auch den, der längst weiter ist.
/// </para>
/// </summary>
public sealed class StatefulSessionTests : IClassFixture<StatefulGatewayFixture>
{
    private readonly StatefulGatewayFixture _gw;

    public StatefulSessionTests(StatefulGatewayFixture gw) => _gw = gw;

    /// <summary>
    /// FR-07 im Sitzungsbetrieb: Ein Server, der während einer laufenden Sitzung dazukommt, meldet
    /// sich von selbst. Das ist der Grund, warum es diesen Modus noch gibt.
    /// </summary>
    [Fact]
    public async Task A_running_session_is_notified_when_the_catalog_changes()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync("stateful-hotswap");
        await using var client = await _gw.ConnectClientAsync(apiKey);

        var notifications = 0;
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                Interlocked.Increment(ref notifications);
                return default;
            });

        var id = await _gw.AddEchoUpstreamAsync("swapstateful");
        await IntegrationSupport.WaitUntilAsync(
            () => Volatile.Read(ref notifications) > 0,
            because: "tools/list_changed muss die laufende Sitzung erreichen (FR-07)");

        var result = await client.CallToolAsync(
            "swapstateful__echo", new Dictionary<string, object?> { ["message"] = "ohne Reconnect" });
        result.IsError.Should().NotBe(true);

        var before = Volatile.Read(ref notifications);
        await _gw.Supervisor.RemoveAsync(id, DrainPolicy.Immediate, TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(() => Volatile.Read(ref notifications) > before);
    }

    /// <summary>
    /// <b>Der Preis, ausgesprochen.</b> Ein Client auf dem Stand 2026-07-28 wird in diesem Modus
    /// vom SDK mit <c>-32022 UnsupportedProtocolVersion</c> abgewiesen — und handelt daraufhin
    /// selbst den vorigen Stand aus. Er läuft also, aber er spricht nicht mehr das, was er könnte.
    /// <para>
    /// Dieser Test steht hier, damit niemand den Schalter für kostenlos hält: Wer ihn umlegt,
    /// schaltet den ganzen Gateway auf die alte Revision zurück, für alle.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_current_client_is_downgraded_to_the_previous_revision()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync("stateful-downgrade");

        await using var client = await _gw.ConnectClientAsync(apiKey);

        client.NegotiatedProtocolVersion.Should().Be("2025-11-25",
            "im Sitzungsbetrieb weist das SDK 2026-07-28 ab und der Client faellt zurueck");
        client.SessionId.Should().NotBeNull("dieser Modus existiert genau wegen der Sitzung");
    }
}
