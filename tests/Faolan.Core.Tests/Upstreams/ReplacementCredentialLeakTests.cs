using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Core.Execution;
using Faolan.Core.Upstreams;
using Xunit;

namespace Faolan.Core.Tests.Upstreams;

/// <summary>
/// Plan 0005 G5: Zwei Aufrufe halten dieselbe alte Hülle. Der erste läuft auf der Ersatzverbindung B
/// und bekommt einen Fehler, der B's Token zitiert. Bevor sein Aufrufer den Fehler formuliert, hat der
/// zweite die gemeinsame Ersatzverbindung der Hülle schon auf C gewechselt. B's Token darf trotzdem
/// nirgends ankommen — weder in der Ausnahme noch im Grund, der an Agent und Vorgangsablage geht.
/// </summary>
public sealed class ReplacementCredentialLeakTests : IAsyncDisposable
{
    private readonly FakeUpstreamConnector _connector = new();
    private readonly UpstreamSupervisor _supervisor;

    public ReplacementCredentialLeakTests()
    {
        _supervisor = new UpstreamSupervisor(
            [_connector],
            new InMemoryUpstreamConfigStore(),
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromMinutes(5),
                DefaultCallTimeout = TimeSpan.FromSeconds(30),
                DefaultDrainGrace = TimeSpan.FromMilliseconds(200),
                DefaultRestartPolicy = new RestartPolicy(
                    MaxRetries: null, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromSeconds(30)),
                ConnectAttemptTimeout = TimeSpan.FromSeconds(30),
                SessionRebuildTimeout = TimeSpan.FromSeconds(15),
            },
            hostExecution: HostExecutionPolicy.AllowedByOperator());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    [Fact]
    public async Task A_failure_on_a_replacement_does_not_show_its_token_after_the_shell_moved_on()
    {
        var tokenA = "at-" + Guid.NewGuid().ToString("N");
        var tokenB = "at-" + Guid.NewGuid().ToString("N");
        var tokenC = "at-" + Guid.NewGuid().ToString("N");
        _connector.DefaultBehavior = (id, _) => new FakeUpstreamConnection { Id = id, PresentedCredentials = [tokenA] };

        var server = await _supervisor.AddAsync(TestData.StdioConfig("zwei-ersetzungen"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var oldShell = _supervisor.GetConnection(server)!;

        // Die Sitzung von A ist fort; der Neuaufbau liefert B, und B's Upstream zitiert sein Token.
        _connector.Connections[0].CallException = new UpstreamSessionLostException("404 (Test)");
        _connector.DefaultBehavior = (id, _) => new FakeUpstreamConnection
        {
            Id = id,
            PresentedCredentials = [tokenB],
            CallException = new InvalidOperationException($"{{\"error\":\"Token {tokenB} ist unbekannt\"}}"),
        };

        // Erster Aufruf über die alte Hülle: läuft auf B und scheitert. Der Aufrufer hält den Fehler
        // in der Hand, hat ihn aber noch nicht formuliert.
        var first = () => oldShell.CallToolAsync("echo", TestData.EmptySchema(), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var failure = (await first.Should().ThrowAsync<Exception>()).Which;
        failure.Should().NotBeOfType<TimeoutException>("der Aufruf endet an B, nicht an der Testfrist");
        _connector.Connections.Should().HaveCount(2);
        _connector.Connections[1].CallCount.Should().Be(1, "der erste Aufruf lief wirklich auf B");

        // Inzwischen geht auch B's Sitzung verloren, und der Neuaufbau liefert C.
        _connector.DefaultBehavior = (id, _) => new FakeUpstreamConnection { Id = id, PresentedCredentials = [tokenC] };
        _connector.Connections[1].CallException = new UpstreamSessionLostException("404 (Test)");
        await _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);
        _connector.Connections.Should().HaveCount(3);

        // Zweiter Aufruf über DIESELBE alte Hülle: Ihre Ersatzverbindung ist danach C.
        await oldShell.CallToolAsync("echo", TestData.EmptySchema(), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        _connector.Connections[2].CallCount.Should().Be(2, "der zweite Aufruf der alten Hülle lief auf C");

        // Erst jetzt formuliert der erste Aufrufer seinen Fehler — so, wie ToolInvoker es tut.
        var guarded = (GuardedUpstreamConnection)oldShell;
        var reason = guarded.DescribeFailure(failure);

        var prefix = tokenB[..UpstreamConnectionTester.MinimumPrefixLength];
        reason.Should().NotContain(prefix, "B's Token erreicht weder Agent noch Vorgangsablage");
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            current.Message.Should().NotContain(prefix, "auch die Ausnahme selbst trägt B's Token nicht");
        }

        UpstreamFailure.Classify(failure).Should().Be(UpstreamFailureKind.Unreachable);
        UpstreamFailure.TypeChain(failure).Should().Contain(nameof(InvalidOperationException), "die Typnamen bleiben");
    }
}
