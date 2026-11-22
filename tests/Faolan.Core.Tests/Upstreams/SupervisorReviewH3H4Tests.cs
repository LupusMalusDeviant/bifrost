using System.Text.Json;
using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Core.Execution;
using Faolan.Core.Upstreams;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Faolan.Core.Tests.Upstreams;

/// <summary>
/// Befunde H3 und H4 aus dem Review des Supervisors (Plan 0005): Verkehr zum Upstream im Drain
/// (Lebenszeichen, Katalogabfragen) und neue Zugangsdaten, während eine verlorene Verbindung noch
/// geschlossen wird.
/// </summary>
public sealed class SupervisorReviewH3H4Tests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(1);

    private static readonly RestartPolicy Unlimited =
        new(MaxRetries: null, InitialBackoff: TimeSpan.FromSeconds(5), BackoffMultiplier: 2.0, MaxBackoff: TimeSpan.FromMinutes(5));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero));
    private readonly Invocation.FakeAuditSink _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement NoArguments => JsonSerializer.SerializeToElement(new { });

    private UpstreamSupervisor Create(FakeUpstreamConnector connector)
        => new(
            [connector],
            new InMemoryUpstreamConfigStore(_time),
            new SupervisorOptions
            {
                HealthCheckInterval = HealthCheckInterval,
                HealthyResetWindow = TimeSpan.FromSeconds(60),
                CatalogPollInterval = TimeSpan.FromSeconds(3),
                DefaultCallTimeout = TimeSpan.FromMinutes(10),
                DefaultDrainGrace = TimeSpan.FromMinutes(5),
                DegradedPingTolerance = 1,
                DefaultRestartPolicy = Unlimited,
                AuthenticationRetryInterval = TimeSpan.FromMinutes(15),
                ConnectAttemptTimeout = TimeSpan.FromSeconds(60),
                SessionRebuildTimeout = TimeSpan.FromSeconds(15),
                JitterSource = () => 0d,
            },
            _time,
            audit: _audit,
            hostExecution: HostExecutionPolicy.AllowedByOperator());

    /// <summary>
    /// H4: Ausdrücklich gesetzte Grenze <c>maxRetries = 0</c>. Das Lebenszeichen überschreitet die
    /// Toleranz, und das Schließen der verlorenen Verbindung hängt. Genau dann wird neu autorisiert —
    /// der Zustand steht noch auf Degraded, und es wartet noch niemand. Das neue Token muss trotzdem
    /// einen Versuch auslösen (3.3, E13); sonst bliebe der Upstream an der Grenze stehen.
    /// </summary>
    [Fact]
    public async Task H4_new_credentials_while_a_lost_connection_is_closed_trigger_one_attempt_at_the_retry_limit()
    {
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector);
        var limit = new RestartPolicy(MaxRetries: 0, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5));
        var server = await supervisor.AddAsync(TestData.StdioConfig("h4-schliessen", restart: limit), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        var lost = connector.Connections[0];
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lost.DisposeHang = closing.Task;
        lost.FailPing = true;
        try
        {
            await TestData.WaitUntilAdvancingAsync(
                _time, () => lost.DisposeCount == 1,
                because: "die verlorene Verbindung muss im Schließen hängen");
            supervisor.GetStatus(server)!.State.Should().Be(
                UpstreamState.Degraded, "verbucht ist der Verlust erst nach dem Schließen");

            supervisor.CredentialsChanged(server);
        }
        finally
        {
            closing.TrySetResult();
        }

        await TestData.WaitUntilAsync(
            () => connector.ConnectCalls == 2 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            because: "das neue Token löst trotz erreichter Grenze einen Versuch aus");
        supervisor.GetStatus(server)!.Reconnect.Should().BeNull();
    }

    /// <summary>
    /// H3: Ein zugelassener Aufruf läuft noch, der Upstream wird mit Gnadenfrist abgeschaltet. Im Drain
    /// geht kein Lebenszeichen mehr hinaus (N45) — ein 404 darauf beendete sonst den Client samt dem
    /// Aufruf, auf den der Drain wartet.
    /// </summary>
    [Fact]
    public async Task H3_no_ping_is_sent_during_the_drain()
    {
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector);
        var server = await supervisor.AddAsync(TestData.StdioConfig("h3-ping"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var upstream = connector.Connections[0];

        var (read, release) = StartBlockingRead(supervisor, server, upstream);
        var disable = supervisor.SetEnabledAsync(server, false, Ct);
        try
        {
            await WaitForDrainAsync(supervisor, server);

            // Die Fake-Uhr stand seit dem Verbinden still: Kein Lebenszeichen ist gerade unterwegs.
            var pings = upstream.PingCalls;
            await AdvanceAsync(HealthCheckInterval * 6);

            upstream.PingCalls.Should().Be(pings, "im Drain geht kein Lebenszeichen mehr hinaus");
            upstream.DisposeCount.Should().Be(0, "die Verbindung, auf deren Aufruf der Drain wartet, bleibt offen");

            release();
            await read.WaitAsync(Bound, Ct);
        }
        finally
        {
            await FinishAsync(release, disable);
        }

        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
    }

    /// <summary>
    /// H3: Eine turnusmäßige Katalogabfrage steckt in <c>tools/list</c>, eine ausdrückliche wartet auf
    /// die Sperre der Neuabfrage. Dann beginnt das Stoppen. Wer die Sperre erst im Drain bekommt,
    /// sendet nichts mehr — auch keine weitere turnusmäßige Abfrage.
    /// </summary>
    [Fact]
    public async Task H3_a_rediscovery_that_gets_the_lock_during_the_drain_sends_nothing()
    {
        FakeUpstreamConnection? upstream = null;
        var connector = new FakeUpstreamConnector
        {
            DefaultBehavior = (id, _) => upstream = new FakeUpstreamConnection { Id = id, PushesCatalogChanges = false },
        };
        await using var supervisor = Create(connector);
        var server = await supervisor.AddAsync(TestData.StdioConfig("h3-katalog"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        var discovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream!.DiscoverHook = () => discovering.Task;
        var (read, release) = StartBlockingRead(supervisor, server, upstream);
        var disable = Task.CompletedTask;
        try
        {
            var discoveries = upstream.DiscoverCalls;
            await TestData.WaitUntilAdvancingAsync(
                _time, () => upstream.DiscoverCalls == discoveries + 1,
                because: "die turnusmäßige Abfrage muss in tools/list stecken");

            // Die Sperre hält die turnusmäßige Abfrage: Die ausdrückliche wartet, sobald sie zurückkehrt.
            var rediscover = supervisor.RediscoverAsync(server, Ct);
            rediscover.IsCompleted.Should().BeFalse("die ausdrückliche Abfrage wartet auf die Sperre");

            disable = supervisor.SetEnabledAsync(server, false, Ct);
            await WaitForDrainAsync(supervisor, server);

            discovering.SetResult();
            await rediscover.WaitAsync(Bound, Ct);
            upstream.DiscoverCalls.Should().Be(discoveries + 1, "wer die Sperre im Drain bekommt, sendet nichts mehr");

            await AdvanceAsync(TimeSpan.FromSeconds(10));
            upstream.DiscoverCalls.Should().Be(discoveries + 1, "im Drain ruht die turnusmäßige Abfrage");

            release();
            await read.WaitAsync(Bound, Ct);
        }
        finally
        {
            discovering.TrySetResult();
            await FinishAsync(release, disable);
        }

        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
    }

    /// <summary>
    /// Plan 0005 I3: Eine ausdrückliche Neuabfrage steckt in <c>tools/list</c>, als das Stoppen
    /// beginnt. Danach schickt die Verbindung keine Folgeanfrage (<c>resources/list</c>,
    /// <c>prompts/list</c>) mehr, und die Neuabfrage endet ohne Fehler wie eine im Drain (N49).
    /// </summary>
    [Fact]
    public async Task I3_a_catalog_query_under_way_when_the_drain_begins_sends_no_follow_up()
    {
        FakeUpstreamConnection? upstream = null;
        var connector = new FakeUpstreamConnector
        {
            DefaultBehavior = (id, _) => upstream = new FakeUpstreamConnection { Id = id, PushesCatalogChanges = false },
        };
        await using var supervisor = Create(connector);
        var server = await supervisor.AddAsync(TestData.StdioConfig("i3-folge"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        var discovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (read, release) = StartBlockingRead(supervisor, server, upstream!);
        var disable = Task.CompletedTask;
        try
        {
            var followUps = upstream!.FollowUpRequests;
            var discoveries = upstream.DiscoverCalls;
            upstream.DiscoverHook = () => discovering.Task;
            var rediscover = supervisor.RediscoverAsync(server, Ct);
            await TestData.WaitUntilAsync(
                () => upstream.DiscoverCalls == discoveries + 1, because: "die Neuabfrage muss in tools/list stecken");

            disable = supervisor.SetEnabledAsync(server, false, Ct);
            await WaitForDrainAsync(supervisor, server);

            discovering.SetResult();
            await rediscover.WaitAsync(Bound, Ct);
            upstream.FollowUpRequests.Should().Be(followUps, "im Drain geht keine Folgeanfrage des Katalogs hinaus");
            upstream.FollowUpsWithheld.Should().Be(1, "die Verbindung wurde vor der Folgeanfrage gefragt");
            upstream.DisposeCount.Should().Be(0, "die Verbindung, auf deren Aufruf der Drain wartet, bleibt offen");

            release();
            await read.WaitAsync(Bound, Ct);
        }
        finally
        {
            discovering.TrySetResult();
            await FinishAsync(release, disable);
        }

        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
    }

    /// <summary>
    /// H3: Eine ausdrückliche Neuabfrage, die erst im Drain beginnt, bleibt ohne Wirkung — wie eine
    /// Neuabfrage ohne Verbindung. Der nächste Aufbau holt den Katalog ohnehin neu und prüft ihn.
    /// </summary>
    [Fact]
    public async Task H3_an_explicit_rediscovery_during_the_drain_sends_nothing()
    {
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector);
        var server = await supervisor.AddAsync(TestData.StdioConfig("h3-ausdruecklich"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var upstream = connector.Connections[0];

        var (read, release) = StartBlockingRead(supervisor, server, upstream);
        var disable = supervisor.SetEnabledAsync(server, false, Ct);
        try
        {
            await WaitForDrainAsync(supervisor, server);
            var discoveries = upstream.DiscoverCalls;

            await supervisor.RediscoverAsync(server, Ct).WaitAsync(Bound, Ct);

            upstream.DiscoverCalls.Should().Be(discoveries, "im Drain geht keine Katalogabfrage hinaus");
            release();
            await read.WaitAsync(Bound, Ct);
        }
        finally
        {
            await FinishAsync(release, disable);
        }

        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
    }

    /// <summary>Ein zugelassener Lesezugriff, der hängt, bis der Test ihn freigibt — der Drain wartet auf ihn.</summary>
    private static (Task Read, Action Release) StartBlockingRead(
        UpstreamSupervisor supervisor, ServerId server, FakeUpstreamConnection upstream)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.ResourceGate = gate.Task;
        var connection = (GuardedUpstreamConnection)supervisor.GetConnection(server)!;
        var read = connection.ReadResourceAsync(new Uri("test://h3/lang"), Ct);
        connection.InFlightCount.Should().Be(1, "der Lesezugriff ist zugelassen und läuft");
        return (read, () => gate.TrySetResult());
    }

    /// <summary>
    /// Wartet, bis der Drain begonnen hat: Die Verbindung nimmt keine neuen Aufrufe mehr an (E4), und
    /// das geschieht erst, nachdem das Stoppen vermerkt ist.
    /// </summary>
    private static Task WaitForDrainAsync(UpstreamSupervisor supervisor, ServerId server)
    {
        var connection = supervisor.GetConnection(server)!;
        return TestData.WaitUntilAsync(() => ProbeRefused(connection), because: "der Drain muss begonnen haben");
    }

    /// <summary>Ein Probeaufruf, der nur zeigt, ob die Verbindung noch neue Aufrufe annimmt.</summary>
    private static bool ProbeRefused(IUpstreamConnection connection)
    {
        try
        {
            connection.CallToolAsync("echo", NoArguments, CancellationToken.None).GetAwaiter().GetResult();
            return false;
        }
        catch (UpstreamConnectionLostException)
        {
            return true;
        }
    }

    /// <summary>Rückt die Fake-Uhr in kleinen Schritten vor und lässt dem Loop dazwischen Zeit.</summary>
    private async Task AdvanceAsync(TimeSpan total)
    {
        var step = TimeSpan.FromMilliseconds(100);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            _time.Advance(step);
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>
    /// Gibt den Aufruf frei und lässt das Abschalten zu Ende laufen — auch wenn eine Prüfung gescheitert
    /// ist: Das Abschalten hält die Verwaltungssperre, und das Beenden des Supervisors wartete sonst
    /// für immer darauf.
    /// </summary>
    private async Task FinishAsync(Action release, Task disable)
    {
        release();
        if (!disable.IsCompleted)
        {
            // Der Drain prüft auf der Fake-Uhr, ob die Verbindung leer ist — ohne Vorlauf endet er nie.
            await TestData.WaitUntilAdvancingAsync(
                _time, () => disable.IsCompleted, timeoutMs: 15000, because: "das Abschalten endet nach dem Drain");
        }

        await disable;
    }
}
