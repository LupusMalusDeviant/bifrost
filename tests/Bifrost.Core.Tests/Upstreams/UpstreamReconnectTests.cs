using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Execution;
using Bifrost.Core.Upstreams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

/// <summary>
/// Der Vertrag „Selbsttätige Wiederverbindung" (docs/contracts/upstream-wiederverbindung.md),
/// Zusage für Zusage.
/// <para>
/// Der Anlass: Am 2026-11-16 blieb ein Upstream zwölf Stunden auf Failed, weil der Supervisor nach
/// sechs Versuchen in rund 31 Sekunden aufgab. Der erste Test hier ist dieser Fall.
/// </para>
/// </summary>
public sealed class UpstreamReconnectTests : IAsyncDisposable
{
    /// <summary>
    /// Der Geheimwert, den die Tests in keinem Text wiederfinden dürfen. Je Lauf neu erzeugt: So
    /// steht kein Schlüssel im Repository, und die Prüfung hängt nicht an einem bestimmten Wert.
    /// Der feste Anfang macht ihn von Hex-Folgen unterscheidbar, die zufällig in einem Text stehen.
    /// </summary>
    private static readonly string Secret = "wk-" + Guid.NewGuid().ToString("N");

    private static readonly RestartPolicy Unlimited =
        new(MaxRetries: null, InitialBackoff: TimeSpan.FromSeconds(5), BackoffMultiplier: 2.0, MaxBackoff: TimeSpan.FromMinutes(5));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 2, 6, 0, TimeSpan.Zero));
    private readonly FakeUpstreamConnector _connector = new();
    private readonly Invocation.FakeAuditSink _audit = new();
    private readonly CapturingLogger _log = new();
    private readonly List<UpstreamChangedEventArgs> _events = [];
    private readonly UpstreamSupervisor _supervisor;

    public UpstreamReconnectTests() => _supervisor = Create(_connector, jitter: 0d);

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    private UpstreamSupervisor Create(FakeUpstreamConnector connector, double jitter)
    {
        var supervisor = new UpstreamSupervisor(
            [connector],
            new InMemoryUpstreamConfigStore(_time),
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromSeconds(1),
                HealthyResetWindow = TimeSpan.FromSeconds(60),
                DefaultCallTimeout = TimeSpan.FromSeconds(30),
                DefaultDrainGrace = TimeSpan.FromSeconds(5),
                DefaultRestartPolicy = Unlimited,
                AuthenticationRetryInterval = TimeSpan.FromMinutes(15),
                ConnectAttemptTimeout = TimeSpan.FromSeconds(60),
                SessionRebuildTimeout = TimeSpan.FromSeconds(15),
                // Fest statt zufällig: Nur so lassen sich die Fristen auf die Sekunde prüfen.
                JitterSource = () => jitter,
            },
            _time,
            _log,
            _audit,
            hostExecution: HostExecutionPolicy.AllowedByOperator());
        supervisor.Changed += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
        return supervisor;
    }

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

    private IReadOnlyList<string> Lifecycle
        => [.. _audit.Events.Where(e => e.Kind == AuditEventKind.ServerLifecycle).Select(e => e.Detail!)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Wartet, bis der n-te Fehlversuch verbucht ist und der Loop auf den nächsten wartet.</summary>
    private Task<UpstreamReconnectInfo> FailedAttemptAsync(ServerId id, int attempt)
        => FailedAttemptAsync(_supervisor, id, attempt);

    private static async Task<UpstreamReconnectInfo> FailedAttemptAsync(
        UpstreamSupervisor supervisor, ServerId id, int attempt)
    {
        await TestData.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.Reconnect is { NextAttemptAt: not null } info && info.Attempt == attempt,
            because: $"der {attempt}. Fehlversuch muss verbucht sein");
        return supervisor.GetStatus(id)!.Reconnect!;
    }

    /// <summary>„Jetzt verbinden" — wiederholt, bis der Loop wirklich wartet und den Anstoß annimmt.</summary>
    private Task ConnectNowAsync(ServerId id) => ConnectNowAsync(_supervisor, id);

    private static async Task ConnectNowAsync(UpstreamSupervisor supervisor, ServerId id)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await supervisor.ConnectNowAsync(id, Ct))
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("ConnectNowAsync wurde nie angenommen.");
    }

    // ── Der Anlass ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_upstream_that_stays_away_for_a_long_time_comes_back_without_anyone_touching_it()
    {
        // Zwanzig Fehlversuche — mit der alten Vorgabe war nach dem sechsten endgültig Schluss.
        var failures = 0;
        _connector.DefaultBehavior = (id, _) => Interlocked.Increment(ref failures) <= 20
            ? throw new HttpRequestException(
                "Connection refused (192.168.178.61:5100)", new SocketException((int)SocketError.ConnectionRefused))
            : new FakeUpstreamConnection { Id = id };

        var server = await _supervisor.AddAsync(TestData.StdioConfig("whiskers"), Ct);

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(30),
            timeoutMs: 30000,
            because: "ein aktivierter Upstream wird ohne Obergrenze erneut versucht");

        _connector.ConnectCalls.Should().Be(21, "20 Fehlversuche und der eine, der gelingt");
        _supervisor.GetStatus(server)!.ToolCount.Should().Be(1, "die Werkzeuge sind wieder da");
        _supervisor.GetStatus(server)!.Reconnect.Should().BeNull("das Feld gibt es nur im Zustand Failed");
        _supervisor.GetConnection(server).Should().NotBeNull();

        // Ein Ausfall, zwei Zeilen: sein Anfang und sein Ende. Nicht 42.
        await TestData.WaitUntilAsync(() => Lifecycle.Any(d => d.Contains("wieder verbunden", StringComparison.Ordinal)));
        Lifecycle.Should().HaveCount(2);
        Lifecycle[0].Should().Contain("Failed").And.Contain("Verbindung verweigert");
        Lifecycle[1].Should().Contain("Healthy").And.Contain("20 Fehlversuchen").And.Contain("1 Werkzeuge");
    }

    // ── Fristen ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delays_grow_from_five_seconds_to_the_five_minute_cap_and_stay_there()
    {
        _connector.DefaultBehavior = static (_, _) => throw new IOException("weg (Test)");
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);

        int[] expectedSeconds = [5, 10, 20, 40, 80, 160, 300, 300, 300];
        for (var attempt = 1; attempt <= expectedSeconds.Length; attempt++)
        {
            var info = await FailedAttemptAsync(server, attempt);

            // Die Testuhr steht still, also ist „geplanter Zeitpunkt minus jetzt" genau die Frist.
            (info.NextAttemptAt!.Value - _time.GetUtcNow())
                .Should().Be(TimeSpan.FromSeconds(expectedSeconds[attempt - 1]), $"Frist nach Fehlversuch {attempt}");
            info.Kind.Should().Be(UpstreamFailureKind.Unreachable);
            info.Exhausted.Should().BeFalse();

            await ConnectNowAsync(server);
        }
    }

    [Theory]
    [InlineData(0.0, 300.0)]
    [InlineData(0.5, 270.0)]
    [InlineData(1.0, 240.0)]
    public async Task Jitter_only_shortens_the_delay_by_at_most_a_fifth(double random, double expectedSeconds)
    {
        await using var supervisor = Create(new FakeUpstreamConnector(), jitter: random);

        supervisor.ComputeDelay(Unlimited, UpstreamFailureKind.Unreachable, attempt: 50)
            .Should().BeCloseTo(
                TimeSpan.FromSeconds(expectedSeconds), TimeSpan.FromMilliseconds(1),
                "der Deckel bleibt Deckel — der Jitter geht nur nach unten");
    }

    [Fact]
    public void Backoff_survives_attempt_counts_that_overflow_a_long()
    {
        // Mit unbegrenzten Versuchen wächst der Exponent ohne Ende. 5 s × 2^n verlässt bei n ≈ 40
        // den Wertebereich; eine dabei negativ gewordene Frist hätte Task.Delay werfen lassen.
        foreach (var attempt in new[] { 40, 64, 1100, 100_000, int.MaxValue })
        {
            UpstreamSupervisor.ComputeBackoff(Unlimited, attempt).Should().Be(TimeSpan.FromMinutes(5));
        }
    }

    [Fact]
    public async Task Connect_now_tries_immediately_and_keeps_the_place_in_the_delay_sequence()
    {
        _connector.DefaultBehavior = static (_, _) => throw new IOException("weg (Test)");
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await FailedAttemptAsync(server, 1);
        await ConnectNowAsync(server);
        await FailedAttemptAsync(server, 2);

        await ConnectNowAsync(server);

        var third = await FailedAttemptAsync(server, 3);
        (third.NextAttemptAt!.Value - _time.GetUtcNow()).Should().Be(
            TimeSpan.FromSeconds(20), "der Knopf versucht sofort, löst aber die Bremse nicht");
        _connector.ConnectCalls.Should().Be(3, "kein Versuch ist durch Ablauf einer Frist entstanden — die Uhr stand");
    }

    [Fact]
    public async Task Connect_now_reports_that_there_is_nothing_to_do_for_a_connected_upstream()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        (await _supervisor.ConnectNowAsync(server, Ct)).Should().BeFalse();
        _connector.ConnectCalls.Should().Be(1);

        var unknown = () => _supervisor.ConnectNowAsync(ServerId.New(), Ct);
        await unknown.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ── Authentifizierung ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rejected_login_is_not_hammered()
    {
        _connector.DefaultBehavior = static (_, _) => throw new HttpRequestException(
            "Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized);
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);

        var info = await FailedAttemptAsync(server, 1);
        info.Kind.Should().Be(UpstreamFailureKind.Authentication);
        (info.NextAttemptAt!.Value - _time.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(15));
        _supervisor.GetStatus(server)!.LastError.Should().Contain("Authentifizierung abgelehnt (HTTP 401)");

        // Vierzehn Minuten lang: kein einziger weiterer Versuch gegen den Schlüssel.
        for (var i = 0; i < 28; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5, Ct);
        }

        _connector.ConnectCalls.Should().Be(1, "eine abgelehnte Anmeldung wird durch Wiederholen nicht richtig");

        await TestData.WaitUntilAdvancingAsync(
            _time, () => _connector.ConnectCalls == 2, step: TimeSpan.FromSeconds(10),
            because: "nach der langen Frist folgt trotzdem ein Versuch — der Schlüssel kann inzwischen stimmen");
    }

    [Fact]
    public void A_forbidden_answer_and_a_missing_token_count_as_authentication_too()
    {
        UpstreamFailure.Classify(new HttpRequestException("x", null, HttpStatusCode.Forbidden))
            .Should().Be(UpstreamFailureKind.Authentication);
        UpstreamFailure.Classify(new UpstreamAuthenticationException("kein Token"))
            .Should().Be(UpstreamFailureKind.Authentication);

        // Auch verpackt: Ein 401 beim Lebenszeichen kommt als „Verbindung verloren" an.
        UpstreamFailure.Classify(new UpstreamConnectionLostException(
                "Health-Ping fehlgeschlagen", new HttpRequestException("x", null, HttpStatusCode.Unauthorized)))
            .Should().Be(UpstreamFailureKind.Authentication);

        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable })
        {
            UpstreamFailure.Classify(new HttpRequestException("x", null, status))
                .Should().Be(UpstreamFailureKind.Unreachable, $"HTTP {(int)status} kann von selbst vorübergehen");
        }

        UpstreamFailure.Classify(new TimeoutException()).Should().Be(UpstreamFailureKind.Unreachable);
    }

    [Fact]
    public async Task A_changed_failure_kind_changes_the_delay_and_is_recorded_once()
    {
        var unauthorized = false;
        _connector.DefaultBehavior = (_, _) => Volatile.Read(ref unauthorized)
            ? throw new HttpRequestException("401", null, HttpStatusCode.Unauthorized)
            : throw new IOException("weg (Test)");
        var server = await _supervisor.AddAsync(TestData.StdioConfig("wechsel"), Ct);
        await FailedAttemptAsync(server, 1);

        // Der Upstream ist wieder da, lehnt aber ab.
        Volatile.Write(ref unauthorized, true);
        await ConnectNowAsync(server);
        var second = await FailedAttemptAsync(server, 2);

        second.Kind.Should().Be(UpstreamFailureKind.Authentication);
        (second.NextAttemptAt!.Value - _time.GetUtcNow()).Should().Be(
            TimeSpan.FromMinutes(15), "ab dem Wechsel gilt die Frist der neuen Fehlerart");

        await ConnectNowAsync(server);
        await FailedAttemptAsync(server, 3);

        Lifecycle.Should().HaveCount(2, "der Eintritt in Failed und der eine Wechsel der Fehlerart");
        Lifecycle[1].Should().Contain("Fehlerart wechselt von Unreachable auf Authentication");
    }

    // ── Deaktiviert bleibt aus ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_disabled_upstream_is_never_connected()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(enabled: false), Ct);

        for (var i = 0; i < 24; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            await Task.Delay(5, Ct);
        }

        _connector.ConnectCalls.Should().Be(0, "deaktiviert heißt: kein Versuch, auch nicht nach zwölf Stunden");
        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);

        var act = () => _supervisor.ConnectNowAsync(server, Ct);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*deaktiviert*");
        _connector.ConnectCalls.Should().Be(0, "auch der Knopf verbindet einen ausgeschalteten Upstream nicht");
    }

    [Fact]
    public async Task Disabling_a_failing_upstream_stops_the_retries()
    {
        _connector.DefaultBehavior = static (_, _) => throw new IOException("weg (Test)");
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await FailedAttemptAsync(server, 1);

        await _supervisor.SetEnabledAsync(server, false, Ct);
        var callsAtDisable = _connector.ConnectCalls;

        for (var i = 0; i < 24; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(5, Ct);
        }

        _connector.ConnectCalls.Should().Be(callsAtDisable);
        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
        _supervisor.GetStatus(server)!.Reconnect.Should().BeNull();
    }

    // ── Z3 bis Z5 ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fifty_failed_retries_write_one_audit_line_and_never_flap_to_starting()
    {
        _connector.DefaultBehavior = static (_, _) => throw new IOException("weg (Test)");
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);

        for (var attempt = 1; attempt <= 50; attempt++)
        {
            await FailedAttemptAsync(server, attempt);
            await ConnectNowAsync(server);
        }

        await FailedAttemptAsync(server, 51);
        Lifecycle.Should().ContainSingle("ein Ausfall ist ein Eintrag, nicht einer je Versuch")
            .Which.Should().Contain("Failed");
        Events.Should().NotContain(
            e => e.Kind == UpstreamChangeKind.StateChanged && e.State == UpstreamState.Starting,
            "eine Wiederholung ist kein neuer Zustand — der Upstream bleibt Failed, bis er verbunden ist");
        _log.Entries.Count(e => e.Level >= LogLevel.Information).Should().Be(
            1, "oberhalb von Debug steht nur der Eintritt in Failed im Log");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hanging_attempt_ends_at_its_deadline_and_the_next_one_follows(bool honoursCancellation)
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.ConnectHook = honoursCancellation
            ? ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)
            : _ => never.Task;
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(server)?.State == UpstreamState.Failed,
            step: TimeSpan.FromSeconds(5),
            because: "ein Versuch, der hängt, wäre derselbe stille Stillstand wie ein Supervisor, der aufgegeben hat");

        _supervisor.GetStatus(server)!.LastError.Should().Contain("Frist von 60 s");

        _connector.ConnectHook = null;
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(1),
            timeoutMs: 15000,
            because: "nach dem abgebrochenen Versuch geht es mit dem nächsten weiter");
    }

    [Fact]
    public async Task The_attempt_counter_only_falls_after_a_healthy_minute()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);

        // Ein Upstream, der verbindet und nach Sekunden stirbt: Der Zähler steigt weiter. Fiele er
        // schon beim Verbinden, würde er auf Dauer im Takt der Anfangsfrist neu gestartet.
        for (var loss = 1; loss <= 3; loss++)
        {
            await TestData.WaitUntilAdvancingAsync(
                _time, () => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
                step: TimeSpan.FromMilliseconds(500), timeoutMs: 15000);
            _connector.Connections[^1].FailPing = true;

            // Der Zustand Failed hält mindestens 5 s Testzeit; bei Schritten von 250 ms kann die
            // Abfrage ihn nicht überspringen.
            var expected = loss;
            await TestData.WaitUntilAdvancingAsync(
                _time, () => _supervisor.GetStatus(server)?.Reconnect?.Attempt == expected,
                step: TimeSpan.FromMilliseconds(250), timeoutMs: 15000,
                because: $"Verlust {loss} zählt als Fehlversuch {loss}");
        }

        // Jetzt mehr als eine gesunde Minute — danach beginnt die Folge von vorn. Gewartet wird auf
        // das letzte verbuchte Lebenszeichen: Es wird im selben Schritt gesetzt wie der Zähler.
        await TestData.WaitUntilAdvancingAsync(
            _time, () => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromMilliseconds(500), timeoutMs: 15000);
        var healthyFrom = _time.GetUtcNow();
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetStatus(server)!.LastHealthyAt >= healthyFrom + TimeSpan.FromSeconds(70),
            step: TimeSpan.FromSeconds(1), timeoutMs: 15000);

        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy);
        _connector.Connections[^1].FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time, () => _supervisor.GetStatus(server)?.Reconnect is not null,
            step: TimeSpan.FromMilliseconds(250), timeoutMs: 15000);

        _supervisor.GetStatus(server)!.Reconnect!.Attempt.Should().Be(1);
    }

    // ── Ausdrückliche Obergrenze ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_explicit_limit_still_holds_is_visible_and_leaves_the_button_working()
    {
        var available = false;
        _connector.DefaultBehavior = (id, _) => Volatile.Read(ref available)
            ? new FakeUpstreamConnection { Id = id }
            : throw new IOException("weg (Test)");
        var limited = new RestartPolicy(
            MaxRetries: 2, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5));
        var server = await _supervisor.AddAsync(TestData.StdioConfig("begrenzt", restart: limited), Ct);

        await TestData.WaitUntilAdvancingAsync(
            _time, () => _supervisor.GetStatus(server)?.Reconnect?.Exhausted == true,
            step: TimeSpan.FromSeconds(1), because: "1 Erstversuch + 2 Wiederholungen, dann ist die gesetzte Grenze erreicht");

        _connector.ConnectCalls.Should().Be(3);
        for (var i = 0; i < 24; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            await Task.Delay(5, Ct);
        }

        _connector.ConnectCalls.Should().Be(3, "von selbst folgt nach der ausdrücklich gesetzten Grenze nichts mehr");
        var info = _supervisor.GetStatus(server)!.Reconnect!;
        info.NextAttemptAt.Should().BeNull();
        info.Attempt.Should().Be(3);
        Lifecycle.Should().Contain(d => d.Contains("eingestellt (so konfiguriert)", StringComparison.Ordinal),
            "eine erreichte Grenze steht da, statt still zu wirken");

        // Der Loop ist nicht beendet — er wartet auf den Knopf.
        Volatile.Write(ref available, true);
        await ConnectNowAsync(server);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
    }

    // ── Verlorene Sitzung ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lost_session_is_rebuilt_once_and_the_call_is_repeated()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var first = _connector.Connections[0];
        first.CallException = new UpstreamSessionLostException("404 auf die Sitzung (Test)");

        var result = await _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        result.GetProperty("tool").GetString().Should().Be("echo", "der Aufruf ist auf der neuen Verbindung angekommen");
        _connector.ConnectCalls.Should().Be(2, "genau ein Neuaufbau");
        _connector.Connections[1].CallCount.Should().Be(1);
        first.CallCount.Should().Be(1, "die alte Verbindung hat den Aufruf einmal abgewiesen und danach nichts mehr gesehen");
        await TestData.WaitUntilAsync(() => first.DisposeCount > 0, because: "die alte Verbindung wird aufgeräumt");

        var status = _supervisor.GetStatus(server)!;
        status.State.Should().Be(UpstreamState.Healthy);
        status.Reconnect.Should().BeNull("ein gelungener Neuaufbau ist kein Fehlversuch");
        Lifecycle.Should().Contain(d => d.Contains("Sitzung verloren", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ten_callers_of_the_same_lost_session_share_one_rebuild()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        _connector.Connections[0].CallException = new UpstreamSessionLostException("404 (Test)");
        var stale = _supervisor.GetConnection(server)!;

        var calls = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => stale.CallToolAsync("echo", TestData.EmptySchema(), Ct), Ct))
            .ToArray();
        await Task.WhenAll(calls);

        _connector.ConnectCalls.Should().Be(2, "zehn Aufrufer, ein Neuaufbau");
        _connector.Connections[1].CallCount.Should().Be(10);

        // Wer die alte Hülle noch in der Hand hält, landet danach ebenfalls auf der neuen
        // Verbindung — ohne dass die tote noch einmal angefragt wird.
        var before = _connector.Connections[0].CallCount;
        await stale.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        _connector.Connections[0].CallCount.Should().Be(before);
        _connector.ConnectCalls.Should().Be(2);
    }

    [Fact]
    public async Task Any_other_failure_in_the_middle_of_a_call_is_not_repeated()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var connection = _connector.Connections[0];
        connection.CallException = new IOException("Verbindung zurückgesetzt (Test)");

        var act = () => _supervisor.GetConnection(server)!.CallToolAsync("create_issue", TestData.EmptySchema(), Ct);

        await act.Should().ThrowAsync<IOException>();
        connection.CallCount.Should().Be(
            1, "ob der Upstream den Aufruf schon ausgeführt hat, ist hier nicht zu wissen — ein zweites create_issue wäre schlimmer als der Fehler");
        _connector.ConnectCalls.Should().Be(1, "und es wird auch nichts neu aufgebaut");
    }

    [Fact]
    public async Task A_tool_that_is_gone_after_the_rebuild_is_not_called()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        _connector.Connections[0].CallException = new UpstreamSessionLostException("404 (Test)");
        _connector.DefaultBehavior = static (id, _) => new FakeUpstreamConnection
        {
            Id = id,
            Inventory = TestData.InventoryWithTools("etwas_anderes"),
        };

        var act = () => _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*nicht mehr im geprüften Katalog*nicht ausgeführt*");
        _connector.Connections[1].CallCount.Should().Be(0);
        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy, "der Upstream selbst ist verbunden");
    }

    [Fact]
    public async Task A_failed_rebuild_reaches_the_caller_and_hands_over_to_the_retry_loop()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        _connector.Connections[0].CallException = new UpstreamSessionLostException("404 (Test)");
        _connector.DefaultBehavior = static (_, _) => throw new IOException("immer noch weg (Test)");

        var act = () => _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        (await act.Should().ThrowAsync<UpstreamConnectionLostException>()).WithMessage("*Neuaufbau gescheitert*");
        var info = await FailedAttemptAsync(server, 1);
        info.Kind.Should().Be(UpstreamFailureKind.Unreachable);
        _connector.ConnectCalls.Should().Be(2, "ein Neuaufbau — kein zweiter für denselben Aufruf");
    }

    [Fact]
    public async Task Reads_are_repeated_after_a_rebuild_as_well()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var lost = new SessionLosingConnection { Id = server };
        var stale = new GuardedUpstreamConnection(
            lost, TimeSpan.FromSeconds(30), _time, new FixedRecovery(_supervisor, server));

        var resource = await stale.ReadResourceAsync(new Uri("file:///a"), Ct);
        var prompt = await stale.GetPromptAsync("p", null, Ct);

        resource.TryGetProperty("contents", out _).Should().BeTrue();
        prompt.TryGetProperty("messages", out _).Should().BeTrue();
        lost.Reads.Should().Be(1, "nach dem ersten 404 geht über die tote Verbindung nichts mehr hinaus");
    }

    // ── Was nie im Text steht ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Neither_the_response_body_nor_the_key_nor_its_beginning_reach_any_sink()
    {
        var connector = new FakeUpstreamConnector { Kind = UpstreamTransportKind.StreamableHttp };
        await using var supervisor = Create(connector, jitter: 0d);
        var config = HttpConfig(new Uri("http://192.168.178.61:5100/mcp?probe=nicht-in-den-text"));

        // Gemessen am SDK: Es hängt den Antwortkörper an die Meldung. Dieser Upstream zitiert
        // darin, was ihm vorgelegt wurde.
        var leaks = new Queue<Exception>([
            new HttpRequestException(
                "Response status code does not indicate success: 401 (Unauthorized). Response body: "
                + $"{{\"error\":\"abgelehnt: {Secret}\"}}", null, HttpStatusCode.Unauthorized),
            new InvalidOperationException($"Upstream sagt: {Secret[..12]}… ist abgelaufen"),
            new IOException($"Abbruch. Response body: {{\"vorgelegt\":\"{Secret}\"}}"),
        ]);
        connector.DefaultBehavior = (_, _) => throw leaks.Dequeue();

        var server = await supervisor.AddAsync(config, Ct);
        var texts = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await FailedAttemptAsync(supervisor, server, attempt);
            texts.Add(supervisor.GetStatus(server)!.LastError!);
            if (attempt < 3)
            {
                await ConnectNowAsync(supervisor, server);
            }
        }

        texts.AddRange(Lifecycle);
        texts.AddRange(_log.Entries.Select(e => e.Message));

        texts.Should().HaveCountGreaterThan(5);
        texts.Should().AllSatisfy(text =>
        {
            text.Should().NotContain(Secret);
            text.Should().NotContain(
                Secret[..UpstreamConnectionTester.MinimumPrefixLength], "auch nicht der Anfang des Geheimwerts");
            text.Should().NotContain("Response body");
            text.Should().NotContain("abgelehnt:");
            text.Should().NotContain("nicht-in-den-text", "die Abfragezeichenkette der Adresse gehört nicht in eine Meldung");
        });
        texts[0].Should().Be("Authentifizierung abgelehnt (HTTP 401) (192.168.178.61:5100).");
        texts[2].Should().Be("Abbruch.");
        _log.Entries.Should().OnlyContain(
            e => e.Exception == null, "das Ausnahmeobjekt trüge die ungefilterte Meldung ins Log");
        _log.Entries.Should().Contain(e => e.Message.Contains("HttpRequestException", StringComparison.Ordinal),
            "statt des Objekts stehen die Typnamen da");
    }

    [Fact]
    public void A_bearer_scheme_is_not_mistaken_for_the_beginning_of_a_secret()
    {
        var config = HttpConfig(new Uri("https://mcp.example.test/mcp"));

        UpstreamConnectionTester.ScrubSecrets("Bearer token rejected", config)
            .Should().Be("Bearer token rejected", "das Schema ist kein Geheimnis");
        UpstreamConnectionTester.ScrubSecrets($"rejected: {Secret[..9]}…", config)
            .Should().Be("rejected: ***…");
        UpstreamConnectionTester.ScrubSecrets($"rejected: Bearer {Secret}", config)
            .Should().Be("rejected: ***");
    }

    [Fact]
    public void A_refused_connection_is_described_from_what_the_gateway_knows()
    {
        var config = HttpConfig(new Uri("http://192.168.178.61:5100/mcp"));
        var refused = new HttpRequestException(
            "Es konnte keine Verbindung hergestellt werden (192.168.178.61:5100)",
            new SocketException((int)SocketError.ConnectionRefused));

        UpstreamFailure.Describe(refused, config)
            .Should().Be("Nicht erreichbar: Verbindung verweigert (192.168.178.61:5100).");
        UpstreamFailure.Describe(new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), config)
            .Should().Be("Die Gegenstelle antwortete mit HTTP 503 (192.168.178.61:5100).");
        UpstreamFailure.TypeChain(refused).Should().Be("HttpRequestException → SocketException");
    }

    /// <summary>Ein HTTP-Upstream, der den Geheimwert dieser Testklasse als Bearer-Kopfzeile schickt.</summary>
    private static UpstreamServerConfig HttpConfig(Uri endpoint)
        => new(
            "whiskers", "Whiskers", UpstreamTransportKind.StreamableHttp, Enabled: true,
            Http: new HttpTransportOptions(
                endpoint, new Dictionary<string, string> { ["Authorization"] = "Bearer " + Secret }));

    /// <summary>Wirft bei jedem Lesen „Sitzung verloren" und zählt mit.</summary>
    private sealed class SessionLosingConnection : IUpstreamConnection
    {
        private int _reads;

        public ServerId Id { get; init; }

        public int Reads => Volatile.Read(ref _reads);

        public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
        {
            add { }
            remove { }
        }

        public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
            => Task.FromResult(TestData.InventoryWithTools("echo"));

        public Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
            => throw new UpstreamSessionLostException("404 (Test)");

        public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            throw new UpstreamSessionLostException("404 (Test)");
        }

        public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            throw new UpstreamSessionLostException("404 (Test)");
        }

        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Ein Neuaufbau, der schon geschehen ist: liefert die aktuelle Verbindung des Supervisors.</summary>
    private sealed class FixedRecovery(UpstreamSupervisor supervisor, ServerId server) : IUpstreamSessionRecovery
    {
        public Task<GuardedUpstreamConnection> RebuildAsync(GuardedUpstreamConnection lost, CancellationToken ct)
            => Task.FromResult((GuardedUpstreamConnection)supervisor.GetConnection(server)!);

        public bool IsToolAvailable(string toolName) => true;
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<UpstreamSupervisor>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}
