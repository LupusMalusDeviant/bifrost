using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Execution;
using Bifrost.Core.Upstreams;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

public sealed class UpstreamSupervisorTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeUpstreamConnector _connector = new();
    private readonly InMemoryUpstreamConfigStore _store;
    private readonly UpstreamSupervisor _supervisor;
    private readonly List<UpstreamChangedEventArgs> _events = [];
    private readonly Invocation.FakeAuditSink _audit = new();

    private static readonly RestartPolicy TestPolicy =
        new(MaxRetries: 2, InitialBackoff: TimeSpan.FromSeconds(1), BackoffMultiplier: 2.0, MaxBackoff: TimeSpan.FromSeconds(10));

    public UpstreamSupervisorTests()
    {
        _store = new InMemoryUpstreamConfigStore(_time);
        _supervisor = new UpstreamSupervisor(
            [_connector],
            _store,
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromSeconds(1),
                HealthyResetWindow = TimeSpan.FromSeconds(60),
                DefaultCallTimeout = TimeSpan.FromSeconds(30),
                DefaultDrainGrace = TimeSpan.FromSeconds(5),
                DefaultRestartPolicy = TestPolicy,
            },
            _time,
            logger: null,
            audit: _audit,
            // Diese Tests pruefen den Lebenszyklus, nicht die Ausfuehrungs-Policy. Sie arbeiten mit
            // stdio-Konfigurationen, und stdio laeuft nativ (ADR-0025) — ohne ausdrueckliche
            // Erlaubnis kaeme kein einziger Upstream hoch. Die Erlaubnis steht hier sichtbar, statt
            // dass der Kern sie stillschweigend annimmt.
            hostExecution: HostExecutionPolicy.AllowedByOperator());
        _supervisor.Changed += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    private IReadOnlyList<UpstreamChangedEventArgs> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    [Fact]
    public async Task Add_connects_discovers_and_becomes_healthy()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);

        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        _supervisor.GetStatus(id)!.ToolCount.Should().Be(1);
        _supervisor.GetInventory(id)!.Tools.Should().ContainSingle(t => t.Name == "echo");
        _supervisor.GetConnection(id).Should().NotBeNull();
        Events.Should().Contain(e => e.Kind == UpstreamChangeKind.Added && e.Server == id);
        Events.Should().Contain(e => e.Kind == UpstreamChangeKind.InventoryChanged && e.Server == id);
    }

    [Fact]
    public async Task State_changes_are_written_to_the_audit_log()
    {
        // FR-22: Systemereignisse müssen im Audit stehen, nicht nur im ILogger — ein Ausfall
        // ist sonst nachträglich nicht nachvollziehbar.
        var id = await _supervisor.AddAsync(TestData.StdioConfig("lifecycle"), TestContext.Current.CancellationToken);

        // Auf das Audit-Ereignis warten, nicht auf den Zustand: SetState macht den Zustand
        // sichtbar, BEVOR es den Eintrag schreibt. Wer auf Healthy wartet und dann das Audit
        // liest, gewinnt das Rennen nur auf einer unbelasteten Maschine.
        await TestData.WaitUntilAsync(
            () => _audit.Events.Any(e => e.Kind == AuditEventKind.ServerLifecycle
                && e.Detail!.Contains(nameof(UpstreamState.Healthy), StringComparison.Ordinal)),
            because: "der Wechsel auf Healthy muss auditiert sein");

        var lifecycle = _audit.Events.Where(e => e.Kind == AuditEventKind.ServerLifecycle).ToList();
        lifecycle.Should().NotBeEmpty();
        lifecycle.Should().OnlyContain(e => e.Server == id && e.Caller == null && e.Origin == CallOrigin.System);
        lifecycle.Should().Contain(e => e.Detail!.Contains("lifecycle") && e.Detail.Contains(nameof(UpstreamState.Healthy)));
    }

    [Fact]
    public async Task Failed_state_records_the_error_in_the_audit_detail()
    {
        _connector.EnqueueConnectFailure("Upstream nicht erreichbar (Test)");

        _ = await _supervisor.AddAsync(TestData.StdioConfig("kaputt"), TestContext.Current.CancellationToken);

        // Auf das Audit-Ereignis warten, nicht auf den Zustand — dieselbe Begründung wie in
        // Lifecycle_transitions_are_audited_as_system_events: SetState macht den Zustand sichtbar,
        // BEVOR es den Eintrag schreibt. Wer auf Failed wartet und dann das Audit liest, gewinnt
        // das Rennen nur auf einer unbelasteten Maschine — unter der Parallellast eines
        // vollständigen Laufs verliert er es gelegentlich.
        await TestData.WaitUntilAsync(
            () => _audit.Events.Any(e => e.Kind == AuditEventKind.ServerLifecycle
                && e.Detail!.Contains(nameof(UpstreamState.Failed), StringComparison.Ordinal)),
            because: "der Fehlertext gehört zum Systemereignis dazu");
    }

    [Fact]
    public async Task Add_disabled_config_stays_stopped_without_connecting()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(enabled: false), TestContext.Current.CancellationToken);

        // Kein Wanduhr-Warten: ein deaktivierter Server startet gar keinen Verbindungs-Loop,
        // die Aussage ist also nicht "noch nicht verbunden", sondern "verbindet nie".
        await TestData.StaysFalseAsync(() => _connector.ConnectCalls > 0, because: "deaktiviert heißt: kein Connect");

        _supervisor.GetStatus(id)!.State.Should().Be(UpstreamState.Stopped);
        _connector.ConnectCalls.Should().Be(0);
        _supervisor.GetConnection(id).Should().BeNull();
    }

    [Fact]
    public async Task Add_rejects_duplicate_slug()
    {
        await _supervisor.AddAsync(TestData.StdioConfig("dup"), TestContext.Current.CancellationToken);

        var act = () => _supervisor.AddAsync(TestData.StdioConfig("dup"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*dup*");
    }

    [Fact]
    public async Task Connect_failures_retry_with_backoff_then_recover()
    {
        _connector.EnqueueConnectFailure();
        _connector.EnqueueConnectFailure();

        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "nach 2 Fehlversuchen verbindet der dritte Versuch");

        _connector.ConnectCalls.Should().Be(3, "2 skriptete Fehlversuche + 1 erfolgreicher Versuch");
        Events.Should().Contain(
            e => e.Kind == UpstreamChangeKind.StateChanged && e.Server == id && e.State == UpstreamState.Failed,
            "die Fehlversuche müssen als Failed sichtbar gewesen sein");
    }

    [Fact]
    public void Backoff_formula_is_exponential_and_capped()
    {
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 1).Should().Be(TimeSpan.FromSeconds(1));
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 2).Should().Be(TimeSpan.FromSeconds(2));
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 3).Should().Be(TimeSpan.FromSeconds(4));
        UpstreamSupervisor.ComputeBackoff(TestPolicy, 10).Should().Be(TimeSpan.FromSeconds(10), "MaxBackoff deckelt");
    }

    [Fact]
    public async Task Permanent_failure_after_exhausted_retries_stops_restarting()
    {
        _connector.DefaultBehavior = static (_, _) => throw new IOException("dauerhaft kaputt (Test)");

        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _connector.ConnectCalls == 3,
            step: TimeSpan.FromMilliseconds(250),
            because: "1 Erstversuch + 2 Retries (MaxRetries=2)");

        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10);
        }

        _connector.ConnectCalls.Should().Be(3, "nach Erschöpfen der RestartPolicy darf kein weiterer Versuch folgen");
        _supervisor.GetStatus(id)!.State.Should().Be(UpstreamState.Failed);
        _supervisor.GetStatus(id)!.LastError.Should().Contain("dauerhaft kaputt");
    }

    [Fact]
    public async Task Ping_failure_marks_failed_and_auto_restarts_to_healthy()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        _connector.Connections[0].FailPing = true;

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _connector.Connections.Count == 2 && _supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "Ping-Fehler → Failed → Auto-Restart mit frischer Verbindung");

        Events.Should().Contain(
            e => e.Kind == UpstreamChangeKind.StateChanged && e.Server == id && e.State == UpstreamState.Failed,
            "der Verbindungsverlust muss als Failed sichtbar gewesen sein");
        _connector.Connections[0].DisposeCount.Should().BeGreaterThan(0, "alte Verbindung wird aufgeräumt");
    }

    [Fact]
    public async Task Single_ping_failure_degrades_without_dropping_the_connection()
    {
        // FR-08 verlangt Degraded als eigenen Zustand. Vorher sprang ein Ping-Fehler direkt auf
        // Failed und riss die Verbindung mit — eine Netzdelle kostete alle In-Flight-Calls.
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);
        var connection = _connector.Connections[0];

        connection.FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Degraded,
            because: "der erste Ping-Fehler liegt in der Toleranz und wird als Degraded sichtbar");

        // GetConnection liefert die guarded Hülle, nicht die Fake-Instanz — entscheidend ist,
        // dass überhaupt geroutet wird und die darunterliegende Verbindung lebt.
        _supervisor.GetConnection(id).Should().NotBeNull("in Degraded wird weiter geroutet");
        connection.DisposeCount.Should().Be(0, "die Verbindung darf für einen einzelnen Aussetzer nicht sterben");
        _supervisor.GetStatus(id)!.LastError.Should().Contain("Health-Ping");

        // Erholt sich der Upstream, geht der Zustand ohne Neuverbindung zurück auf Healthy.
        connection.FailPing = false;
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "ein erfolgreicher Ping hebt Degraded wieder auf");

        _connector.Connections.Should().HaveCount(1, "es gab keinen Neustart");
        _audit.Events.Should().Contain(
            e => e.Kind == AuditEventKind.ServerLifecycle && e.Detail!.Contains(nameof(UpstreamState.Degraded)),
            "auch der Zwischenzustand gehört ins Audit (FR-22)");
    }

    [Fact]
    public async Task Repeated_ping_failures_escalate_from_degraded_to_failed()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        _connector.Connections[0].FailPing = true;

        // Bleibt der Ping weg, muss die Toleranz aufgebraucht werden und der Restart greifen —
        // Degraded darf kein Zustand sein, in dem ein toter Server ewig hängen bleibt.
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => Events.Any(e => e.Server == id && e.State == UpstreamState.Failed),
            because: "nach der Toleranz eskaliert Degraded auf Failed");
    }

    [Fact]
    public async Task Remove_disposes_connection_clears_entry_and_history()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        await _supervisor.RemoveAsync(id, DrainPolicy.Immediate, TestContext.Current.CancellationToken);

        _supervisor.GetStatus(id).Should().BeNull();
        _supervisor.GetConnection(id).Should().BeNull();
        (await _store.GetHistoryAsync(id, TestContext.Current.CancellationToken)).Should().BeEmpty();
        _connector.Connections[0].DisposeCount.Should().BeGreaterThan(0);
        Events.Should().Contain(e => e.Kind == UpstreamChangeKind.Removed && e.Server == id);
    }

    [Fact]
    public async Task Remove_with_graceful_drain_waits_for_inflight_call()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var gate = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.Connections[0].CallGate = gate;
        var connection = (GuardedUpstreamConnection)_supervisor.GetConnection(id)!;
        var call = connection.CallToolAsync("echo", TestData.EmptySchema(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => connection.InFlightCount == 1);

        var remove = _supervisor.RemoveAsync(id, DrainPolicy.Graceful(TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);

        // Die Gnadenfrist hängt an der Fake-Uhr, die hier nicht vorgestellt wird — Remove kann
        // also nur durch das Call-Ende fertig werden, und das steckt im Gate.
        await TestData.StaysFalseAsync(() => remove.IsCompleted, because: "Drain wartet auf den laufenden Call");
        _connector.Connections[0].DisposeCount.Should().Be(0, "während des Drains lebt die Verbindung weiter");

        gate.TrySetResult(TestData.EmptySchema());
        await call;
        _time.Advance(TimeSpan.FromMilliseconds(50));

        await TestData.WaitUntilAsync(() => remove.IsCompleted, because: "nach Call-Ende muss Remove abschließen");
        _supervisor.GetStatus(id).Should().BeNull();
    }

    [Fact]
    public async Task Remove_with_expired_drain_completes_anyway()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        _connector.Connections[0].CallGate = new TaskCompletionSource<System.Text.Json.JsonElement>();
        var connection = (GuardedUpstreamConnection)_supervisor.GetConnection(id)!;
        _ = connection.CallToolAsync("echo", TestData.EmptySchema(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => connection.InFlightCount == 1);

        var remove = _supervisor.RemoveAsync(id, DrainPolicy.Graceful(TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken);
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(3));

        await TestData.WaitUntilAsync(() => remove.IsCompleted, because: "abgelaufene Drain-Frist darf Remove nicht blockieren");
        _supervisor.GetStatus(id).Should().BeNull();
    }

    [Fact]
    public async Task SetEnabled_toggles_and_versions_config()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        await _supervisor.SetEnabledAsync(id, false, TestContext.Current.CancellationToken);
        _supervisor.GetStatus(id)!.State.Should().Be(UpstreamState.Stopped);
        _supervisor.GetConnection(id).Should().BeNull();

        await _supervisor.SetEnabledAsync(id, true, TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var history = await _store.GetHistoryAsync(id, TestContext.Current.CancellationToken);
        history.Should().HaveCount(3, "Initial + Disable + Enable");
        history[^1].Config.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Reconfigure_restarts_with_new_config_and_returns_new_version()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig("alt"), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var version = await _supervisor.ReconfigureAsync(id, TestData.StdioConfig("neu"), TestContext.Current.CancellationToken);

        version.Should().Be(new ConfigVersionId(2));
        await TestData.WaitUntilAsync(
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy && _connector.LastConfig?.Slug == "neu");
        _connector.Connections[0].DisposeCount.Should().BeGreaterThan(0, "alte Verbindung wird beim Reconfigure geschlossen");
    }

    [Fact]
    public async Task Rollback_restores_previous_config_as_new_version()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig("v1"), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);
        await _supervisor.ReconfigureAsync(id, TestData.StdioConfig("v2"), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _connector.LastConfig?.Slug == "v2");

        await _supervisor.RollbackAsync(id, new ConfigVersionId(1), TestContext.Current.CancellationToken);

        await TestData.WaitUntilAsync(
            () => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy && _connector.LastConfig?.Slug == "v1");
        var history = await _store.GetHistoryAsync(id, TestContext.Current.CancellationToken);
        history.Should().HaveCount(3);
        history[^1].Config.Slug.Should().Be("v1", "Rollback erzeugt eine neue Version mit altem Inhalt");
    }

    [Fact]
    public async Task Rollback_to_unknown_version_throws()
    {
        var id = await _supervisor.AddAsync(TestData.StdioConfig(), TestContext.Current.CancellationToken);

        var act = () => _supervisor.RollbackAsync(id, new ConfigVersionId(99), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Operations_on_unknown_server_throw()
    {
        var unknown = ServerId.New();

        await ((Func<Task>)(() => _supervisor.RemoveAsync(unknown, DrainPolicy.Immediate, TestContext.Current.CancellationToken)))
            .Should().ThrowAsync<KeyNotFoundException>();
        await ((Func<Task>)(() => _supervisor.SetEnabledAsync(unknown, false, TestContext.Current.CancellationToken)))
            .Should().ThrowAsync<KeyNotFoundException>();
        _supervisor.GetStatus(unknown).Should().BeNull();
    }

    [Fact]
    public async Task Crash_of_one_upstream_does_not_affect_the_other()
    {
        var idA = await _supervisor.AddAsync(TestData.StdioConfig("stable"), TestContext.Current.CancellationToken);
        var idB = await _supervisor.AddAsync(TestData.StdioConfig("flaky"), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() =>
            _supervisor.GetStatus(idA)?.State == UpstreamState.Healthy &&
            _supervisor.GetStatus(idB)?.State == UpstreamState.Healthy);

        var flakyConnection = _connector.Connections.Single(c => c.Id == idB);
        flakyConnection.FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => Events.Any(e =>
                e.Kind == UpstreamChangeKind.StateChanged && e.Server == idB && e.State == UpstreamState.Failed),
            because: "Ping-Fehler von B muss als Failed sichtbar werden");

        _supervisor.GetStatus(idA)!.State.Should().Be(UpstreamState.Healthy, "Fault Isolation: Nachbar bleibt unberührt");
        var stableResult = await _supervisor.GetConnection(idA)!
            .CallToolAsync("echo", TestData.EmptySchema(), TestContext.Current.CancellationToken);
        stableResult.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }
}
