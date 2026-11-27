using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Core.Execution;
using Faolan.Core.Upstreams;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Faolan.Core.Tests.Upstreams;

/// <summary>
/// Plan 0005 F9: Eine ersetzte Hülle reicht ihren Aufruf nur innerhalb derselben Konfiguration an
/// die aktuelle Verbindung weiter. Ein Neuaufbau der Sitzung behält die Konfiguration; ein
/// Umkonfigurieren (anderes Ziel, andere Zugangsdaten) ersetzt sie — dorthin geht ein Aufruf, der
/// vorher gefasst wurde, nicht.
/// </summary>
public sealed class StaleHandleTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeUpstreamConnector _connector = new();
    private readonly UpstreamSupervisor _supervisor;

    public StaleHandleTests()
    {
        _supervisor = new UpstreamSupervisor(
            [_connector],
            new InMemoryUpstreamConfigStore(_time),
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromSeconds(1),
                HealthyResetWindow = TimeSpan.FromSeconds(60),
                DefaultCallTimeout = TimeSpan.FromSeconds(30),
                DefaultDrainGrace = TimeSpan.FromSeconds(5),
                DefaultRestartPolicy = RestartPolicy.Default,
                AuthenticationRetryInterval = TimeSpan.FromMinutes(15),
                ConnectAttemptTimeout = TimeSpan.FromSeconds(60),
                SessionRebuildTimeout = TimeSpan.FromSeconds(15),
                JitterSource = () => 0d,
            },
            _time,
            NullLogger<UpstreamSupervisor>.Instance,
            new Invocation.FakeAuditSink(),
            hostExecution: HostExecutionPolicy.AllowedByOperator());
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_handle_from_before_a_reconfiguration_does_not_reach_the_new_upstream()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("umgezogen"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var stale = _supervisor.GetConnection(server)!;

        // Ein anderes Ziel: Der Aufruf war für den alten Upstream gedacht.
        await _supervisor.ReconfigureAsync(
            server,
            TestData.StdioConfig("umgezogen") with { Stdio = new StdioTransportOptions("anderes-ziel", []) },
            Ct);
        await TestData.WaitUntilAsync(
            () => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy && _connector.Connections.Count == 2);

        var act = () => stale.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        (await act.Should().ThrowAsync<UpstreamConnectionLostException>())
            .Which.Message.Should().Contain("umkonfiguriert").And.Contain("nicht gesendet");
        _connector.Connections[0].CallCount.Should().Be(0, "die alte Verbindung ist abgebaut");
        _connector.Connections[1].CallCount.Should().Be(0, "der neue Upstream bekommt keinen Aufruf, der dem alten galt");
    }

    [Fact]
    public async Task A_handle_from_before_a_session_rebuild_still_runs_on_the_new_session()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("neue-sitzung"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var stale = _supervisor.GetConnection(server)!;
        var first = _connector.Connections[0];

        first.PingException = new UpstreamSessionLostException("404 auf die Sitzung (Test)");
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetConnection(server) is { } current
                && !ReferenceEquals(current, stale)
                && _supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromMilliseconds(500), timeoutMs: 15000,
            because: "das Lebenszeichen bemerkt den Verlust, die Sitzung wird neu aufgebaut");

        var result = await stale.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        result.GetProperty("tool").GetString().Should().Be("echo");
        first.CallCount.Should().Be(0, "über die tote Sitzung geht nichts mehr hinaus");
        _connector.Connections[1].CallCount.Should().Be(1, "derselbe Upstream mit neuer Sitzung führt den Aufruf aus");
    }
}
