using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Execution;
using Bifrost.Core.Upstreams;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

/// <summary>
/// Befunde G2, G4 und G7 aus dem Review des Supervisors (Plan 0005): neue Zugangsdaten während eines
/// Sitzungs-Neuaufbaus, Lebenszeichen im Drain und Pins nach dem Entfernen.
/// </summary>
public sealed class SupervisorReviewG2G4G7Tests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly RestartPolicy Unlimited =
        new(MaxRetries: null, InitialBackoff: TimeSpan.FromSeconds(5), BackoffMultiplier: 2.0, MaxBackoff: TimeSpan.FromMinutes(5));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero));
    private readonly Invocation.FakeAuditSink _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement NoArguments => JsonSerializer.SerializeToElement(new { });

    private UpstreamSupervisor Create(FakeUpstreamConnector connector, IToolDefinitionPinStore? pins = null)
        => new(
            [connector],
            new InMemoryUpstreamConfigStore(_time),
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromSeconds(1),
                HealthyResetWindow = TimeSpan.FromSeconds(60),
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
            pins: pins,
            hostExecution: HostExecutionPolicy.AllowedByOperator());

    /// <summary>
    /// Lässt die Sitzung der stehenden Verbindung verloren gehen und hält den Neuaufbau an, bis der
    /// Test ihn freigibt. Der Neuaufbau scheitert dann mit <paramref name="failure"/> — er hat das
    /// alte Token schon gelesen.
    /// </summary>
    private async Task<TaskCompletionSource> RebuildHangingWithOldTokenAsync(
        UpstreamSupervisor supervisor, FakeUpstreamConnector connector, ServerId server, Func<Exception> failure)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.ConnectHook = _ => gate.Task;
        connector.Enqueue((_, _) => throw failure());
        connector.Connections[0].PingException = new UpstreamSessionLostException("Sitzung fort (Test).");

        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.ConnectCalls == 2, because: "der Neuaufbau der Sitzung muss begonnen haben");
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Degraded, "ein Neuaufbau steht auf Degraded");
        return gate;
    }

    /// <summary>
    /// G2: Ausdrücklich gesetzte Grenze <c>maxRetries = 0</c>. Ein Neuaufbau der Sitzung hat das alte
    /// Token schon gelesen und hängt; währenddessen wird neu autorisiert. Scheitert der Neuaufbau, muss
    /// das neue Token einen sofortigen Versuch auslösen (3.3, N12) — sonst bliebe der Upstream an der
    /// Grenze stehen, obwohl ein gültiges Token vorliegt.
    /// </summary>
    [Fact]
    public async Task G2_new_credentials_during_a_session_rebuild_trigger_one_attempt_at_the_retry_limit()
    {
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector);
        var limit = new RestartPolicy(MaxRetries: 0, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5));
        var server = await supervisor.AddAsync(TestData.StdioConfig("g2-grenze", restart: limit), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        var gate = await RebuildHangingWithOldTokenAsync(
            supervisor, connector, server, () => new IOException("Neuaufbau mit dem alten Token gescheitert (Test)."));

        supervisor.CredentialsChanged(server);
        connector.ConnectHook = null;
        gate.SetResult();

        await TestData.WaitUntilAsync(
            () => connector.ConnectCalls == 3 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            because: "das neue Token löst trotz erreichter Grenze sofort einen Versuch aus");
        supervisor.GetStatus(server)!.Reconnect.Should().BeNull();
    }

    /// <summary>
    /// G2: Ohne Grenze, aber mit abgewiesener Anmeldung (401) im Neuaufbau: Das neue Token darf nicht die
    /// 15 Minuten aus E2 absitzen.
    /// </summary>
    [Fact]
    public async Task G2_new_credentials_during_a_session_rebuild_skip_the_authentication_backoff()
    {
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector);
        var server = await supervisor.AddAsync(TestData.StdioConfig("g2-anmeldung"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        var gate = await RebuildHangingWithOldTokenAsync(
            supervisor, connector, server,
            () => new HttpRequestException("abgewiesen (Test)", null, HttpStatusCode.Unauthorized));

        supervisor.CredentialsChanged(server);
        connector.ConnectHook = null;
        gate.SetResult();

        await TestData.WaitUntilAsync(
            () => connector.ConnectCalls == 3 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            because: "das neue Token wartet nicht die Frist nach abgewiesener Anmeldung ab");
    }

    /// <summary>
    /// G4: Ein zugelassener Aufruf läuft noch, der Upstream wird mit Gnadenfrist abgeschaltet, und das
    /// Lebenszeichen überschreitet im Drain die Toleranz. Das Schließen gehört allein dem Stopp-Pfad:
    /// Der Aufruf läuft zu Ende, und der Upstream endet Stopped — ohne Failed mitten im Abschalten.
    /// </summary>
    [Fact]
    public async Task G4_ping_failures_during_the_drain_neither_close_the_connection_nor_book_failed()
    {
        var probe = new DrainProbeConnection();
        var connector = new FakeUpstreamConnector { DefaultBehavior = (_, _) => probe };
        await using var supervisor = Create(connector);
        var server = await supervisor.AddAsync(TestData.StdioConfig("g4-drain"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var connection = supervisor.GetConnection(server)!;

        var call = connection.CallToolAsync(DrainProbeConnection.SlowTool, NoArguments, Ct);
        await TestData.WaitUntilAsync(() => probe.SlowCalls == 1, because: "der lange Aufruf muss laufen");

        var disable = supervisor.SetEnabledAsync(server, false, Ct);
        try
        {
            // Der Drain hat begonnen, sobald die Verbindung keine neuen Aufrufe mehr annimmt (E4) — und
            // das geschieht erst, nachdem das Stoppen vermerkt ist.
            await TestData.WaitUntilAsync(
                () => ProbeRefused(connection),
                because: "der Drain muss begonnen haben");

            probe.FailPing = true;
            var pingsBefore = probe.Pings;
            await TestData.WaitUntilAdvancingAsync(
                _time,
                () => probe.DisposeCount > 0 || probe.Pings >= pingsBefore + 4,
                because: "das Lebenszeichen muss im Drain mehrfach über die Toleranz hinaus scheitern");

            probe.DisposeCount.Should().Be(0, "die Verbindung, auf deren Aufruf der Drain wartet, bleibt offen");
            _audit.Events.Where(e => e.Kind == AuditEventKind.ServerLifecycle)
                .Select(e => e.Detail).Should().NotContain(d => d!.Contains("Failed", StringComparison.Ordinal),
                    "ein Lebenszeichen im Drain bucht kein Failed");

            probe.ReleaseSlowCall();
            var result = await call.WaitAsync(Bound, Ct);
            result.GetProperty("tool").GetString().Should().Be(DrainProbeConnection.SlowTool, "der Aufruf läuft zu Ende");

            // Der Drain prüft auf der Fake-Uhr, ob die Verbindung leer ist — ohne Vorlauf endet er nie.
            await TestData.WaitUntilAdvancingAsync(
                _time, () => disable.IsCompleted, because: "das Abschalten endet nach dem Drain");
            await disable;
        }
        finally
        {
            // Auch wenn eine Prüfung oben scheitert: Das Abschalten hält die Verwaltungssperre, und das
            // Beenden des Supervisors wartete sonst für immer darauf.
            probe.ReleaseSlowCall();
            if (!disable.IsCompleted)
            {
                await TestData.WaitUntilAdvancingAsync(_time, () => disable.IsCompleted, timeoutMs: 15000);
            }
        }

        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
        _audit.Events.Where(e => e.Kind == AuditEventKind.ServerLifecycle)
            .Select(e => e.Detail).Should().NotContain(d => d!.Contains("Failed", StringComparison.Ordinal));
    }

    /// <summary>Ein Probeaufruf, der nur zeigt, ob die Verbindung noch neue Aufrufe annimmt.</summary>
    private static bool ProbeRefused(IUpstreamConnection connection)
    {
        try
        {
            connection.CallToolAsync(DrainProbeConnection.FastTool, NoArguments, CancellationToken.None)
                .GetAwaiter().GetResult();
            return false;
        }
        catch (UpstreamConnectionLostException)
        {
            return true;
        }
    }

    /// <summary>
    /// G7: Eine explizite Neuabfrage steckt im Schreiben eines Pins („kein Pin gefunden"), als der
    /// Upstream entfernt wird — samt Vergessen seiner Pins. Danach darf ihr Schreiben keinen Pin
    /// hinterlassen, weder in der Ablage noch in der Anzeige (<see cref="IToolDefinitionPinStore.All"/>).
    /// </summary>
    [Fact]
    public async Task G7_a_rediscovery_writing_a_pin_while_the_upstream_is_removed_leaves_no_pin()
    {
        var pins = new BarrierPinStore();
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector, pins);
        var server = await supervisor.AddAsync(TestData.StdioConfig("g7-entfernt"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        pins.All.Should().ContainSingle(p => p.Server == server && p.Tool == "echo");

        connector.Connections[0].Inventory = TestData.InventoryWithTools("echo", "neu");
        pins.Arm("neu");
        var rediscover = supervisor.RediscoverAsync(server, Ct);
        await pins.NoPinFound.Task.WaitAsync(Bound, Ct);

        await supervisor.RemoveAsync(server, DrainPolicy.Immediate, Ct).WaitAsync(Bound, Ct);
        pins.ForgetCalls.Should().BeGreaterThan(0, "das Entfernen hat die Pins vergessen");
        pins.All.Should().NotContain(p => p.Server == server);

        pins.Release();
        try
        {
            await rediscover.WaitAsync(Bound, Ct);
        }
        catch (UpstreamConnectionLostException)
        {
            // erwartet: Der Upstream ist fort, die Neuabfrage meldet es.
        }

        pins.Stored.Should().NotContain(key => key.Server == server, "in der Ablage steht kein Pin des entfernten Upstreams");
        pins.All.Should().NotContain(p => p.Server == server, "die Anzeige zeigt keinen Pin des entfernten Upstreams");
    }

    /// <summary>Eine Verbindung mit einem langen Aufruf, zählbaren Lebenszeichen und echtem Schließen.</summary>
    private sealed class DrainProbeConnection : IUpstreamConnection
    {
        public const string SlowTool = "langsam";
        public const string FastTool = "schnell";

        private readonly TaskCompletionSource<JsonElement> _slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pings;
        private int _slowCalls;
        private int _disposeCount;
        private volatile bool _failPing;

        public ServerId Id { get; init; }

        public int Pings => Volatile.Read(ref _pings);

        public int SlowCalls => Volatile.Read(ref _slowCalls);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool FailPing
        {
            get => _failPing;
            set => _failPing = value;
        }

        public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
        {
            add { }
            remove { }
        }

        public void ReleaseSlowCall()
            => _slow.TrySetResult(JsonSerializer.SerializeToElement(new { tool = SlowTool }));

        public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
            => Task.FromResult(TestData.InventoryWithTools(SlowTool, FastTool));

        public Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
        {
            if (toolName != SlowTool)
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new { tool = toolName }));
            }

            Interlocked.Increment(ref _slowCalls);
            return _slow.Task;
        }

        public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
            => Task.FromResult(JsonSerializer.SerializeToElement(new { }));

        public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
            => Task.FromResult(JsonSerializer.SerializeToElement(new { }));

        public Task PingAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _pings);
            return _failPing ? Task.FromException(new IOException("Lebenszeichen fehlt (Test).")) : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // Wie eine echte Verbindung: Wer geschlossen wird, reißt laufende Aufrufe mit.
            Interlocked.Increment(ref _disposeCount);
            _slow.TrySetException(new ObjectDisposedException(nameof(DrainProbeConnection)));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Pin-Ablage im Speicher, die im Schreiben einer Erstsichtung anhalten kann — nach „kein Pin
    /// gefunden", vor dem Einfügen.
    /// </summary>
    private sealed class BarrierPinStore : IToolDefinitionPinStore
    {
        private readonly ConcurrentDictionary<(ServerId Server, string Tool), ToolDefinitionPin> _pins = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _armed;
        private int _forgetCalls;

        public event EventHandler<ToolDefinitionPinChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public TaskCompletionSource NoPinFound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ForgetCalls => Volatile.Read(ref _forgetCalls);

        public IReadOnlyList<(ServerId Server, string Tool)> Stored => [.. _pins.Keys];

        /// <summary>Was die Anzeige liest — hier dieselbe Quelle wie die Ablage.</summary>
        public IReadOnlyList<ToolDefinitionPin> All => [.. _pins.Values];

        public void Arm(string tool) => Volatile.Write(ref _armed, tool);

        public void Release() => _release.TrySetResult();

        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<ToolDefinitionVerdict> VerifyAsync(ServerId server, string tool, string hash, CancellationToken ct)
        {
            if (_pins.TryGetValue((server, tool), out var pin))
            {
                return string.Equals(pin.AcceptedHash, hash, StringComparison.Ordinal)
                    ? ToolDefinitionVerdict.Unchanged
                    : ToolDefinitionVerdict.Changed;
            }

            if (string.Equals(Volatile.Read(ref _armed), tool, StringComparison.Ordinal))
            {
                NoPinFound.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            _pins[(server, tool)] = new ToolDefinitionPin(server, tool, hash, DateTimeOffset.UnixEpoch);
            return ToolDefinitionVerdict.FirstSeen;
        }

        public Task<bool> AcceptAsync(ServerId server, string tool, string expectedPendingHash, CancellationToken ct)
            => Task.FromResult(false);

        public Task ForgetServerAsync(ServerId server, CancellationToken ct)
        {
            Interlocked.Increment(ref _forgetCalls);
            foreach (var key in _pins.Keys.Where(k => k.Server == server).ToList())
            {
                _pins.TryRemove(key, out _);
            }

            return Task.CompletedTask;
        }
    }
}
