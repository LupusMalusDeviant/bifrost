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
    private readonly InMemoryUpstreamConfigStore _store;

    public UpstreamReconnectTests()
    {
        _store = new InMemoryUpstreamConfigStore(_time);
        _supervisor = Create(_connector, jitter: 0d);
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    /// <summary>Was zuletzt gespeichert ist — ob eine Aktion wirklich nicht gespeichert wurde.</summary>
    private async Task<bool> LatestEnabledAsync(ServerId id)
        => (await _store.GetAllLatestAsync(Ct))[id].Config.Enabled;

    private UpstreamSupervisor Create(
        FakeUpstreamConnector connector, double jitter, IAuditSink? audit = null, IUpstreamOAuthTokenStore? oauthTokens = null)
    {
        var supervisor = new UpstreamSupervisor(
            [connector],
            _store,
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
            audit ?? _audit,
            hostExecution: HostExecutionPolicy.AllowedByOperator(),
            oauthTokens: oauthTokens);
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

    /// <summary>
    /// Wartet, bis der n-te Fehlversuch verbucht ist und der Loop auf den nächsten wartet. Erst dann
    /// stehen auch Audit und Log: Die neue Frist ist im Status schon sichtbar, bevor sie geschrieben sind.
    /// </summary>
    private Task<UpstreamReconnectInfo> FailedAttemptAsync(ServerId id, int attempt)
        => FailedAttemptAsync(_supervisor, id, attempt);

    private static async Task<UpstreamReconnectInfo> FailedAttemptAsync(
        UpstreamSupervisor supervisor, ServerId id, int attempt)
    {
        await TestData.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.Reconnect is { NextAttemptAt: not null } info
                && info.Attempt == attempt
                && supervisor.IsWaitingForNextAttempt(id),
            because: $"der {attempt}. Fehlversuch muss verbucht sein");
        return supervisor.GetStatus(id)!.Reconnect!;
    }

    /// <summary>Wartet, bis die gesetzte Grenze erreicht und alles dazu verbucht ist.</summary>
    private static Task ExhaustedAsync(UpstreamSupervisor supervisor, ServerId id)
        => TestData.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.Reconnect?.Exhausted == true && supervisor.IsWaitingForNextAttempt(id),
            because: "die gesetzte Grenze muss erreicht und verbucht sein");

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
        // Die Fehlerart steht schon in der Zeile zum Eintritt, nicht erst in der zum Wechsel (L6).
        Lifecycle[0].Should().Contain("Failed (Unreachable)").And.Contain("Verbindung verweigert");
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

    // ── Z1: Der Loop überlebt seine eigene Buchführung ───────────────────────────────────────

    [Fact]
    public async Task An_audit_that_refuses_the_failure_does_not_end_the_retries()
    {
        // Das Audit darf werfen — im Compliance-Modus tut es das, wenn sein Kanal voll ist. Trifft
        // das die Zeile über den Eintritt in Failed, darf daran nicht die Wiederverbindung hängen:
        // Ein Loop, der hier stirbt, hinterlässt Failed ohne jeden weiteren Versuch.
        // Abgewiesen werden nur die Failed-Zeilen, solange der Kanal „voll" ist.
        var full = true;
        var audit = new RefusingAuditSink
        {
            Refuses = e => Volatile.Read(ref full) && e.Detail?.Contains("Failed", StringComparison.Ordinal) == true,
        };
        var connector = new FakeUpstreamConnector();
        var available = false;
        connector.DefaultBehavior = (id, _) => Volatile.Read(ref available)
            ? new FakeUpstreamConnection { Id = id }
            : throw new IOException("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("audit-voll"), Ct);

        // Auf die verbuchte Frist warten, nicht nur auf den Versuch: Während eines Versuchs ist
        // die Frist leer, und erst danach steht die neue.
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => connector.ConnectCalls >= 4
                && supervisor.GetStatus(server)?.Reconnect?.NextAttemptAt is not null
                && supervisor.IsWaitingForNextAttempt(server),
            step: TimeSpan.FromSeconds(30), timeoutMs: 15000,
            because: "nach der abgewiesenen Audit-Zeile folgen weitere Versuche");

        audit.Refused.Should().BeGreaterThan(0, "sonst hätte der Test den Fall gar nicht hergestellt");
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Failed);
        _log.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("audit-voll: Failed (Unreachable)", StringComparison.Ordinal),
            "die Logzeile zum Eintritt in Failed hängt nicht daran, dass das Audit annimmt");
        _log.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("erneut angeboten", StringComparison.Ordinal),
            "dass eine Audit-Zeile zurückgestellt ist, steht im Log");

        Volatile.Write(ref full, false);
        Volatile.Write(ref available, true);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(30), timeoutMs: 15000,
            because: "kommt der Upstream zurück, verbindet der Loop von selbst");

        // Die abgewiesene Zeile ist nachgereicht: genau einmal, mit ihrem Zeitpunkt, vor dem
        // „wieder verbunden". Der Zustand Healthy steht vor seiner Audit-Zeile — auf die warten.
        await TestData.WaitUntilAsync(
            () => audit.Accepted.Any(e => e.Detail!.Contains("wieder verbunden", StringComparison.Ordinal)),
            because: "die Zeile zur Rückkehr folgt dem Zustand");
        var lines = audit.Accepted.Where(e => e.Kind == AuditEventKind.ServerLifecycle).ToList();
        var failed = lines.Where(e => e.Detail!.StartsWith("audit-voll: Failed (", StringComparison.Ordinal)).ToList();
        failed.Should().ContainSingle("jeder Wechsel steht genau einmal im Audit");
        var back = lines.Single(e => e.Detail!.Contains("wieder verbunden", StringComparison.Ordinal));
        lines.IndexOf(failed[0]).Should().BeLessThan(lines.IndexOf(back));
        failed[0].Timestamp.Should().BeBefore(back.Timestamp, "die Zeile trägt den Zeitpunkt des Wechsels");
    }

    [Fact]
    public async Task Failed_bookkeeping_does_not_lift_an_explicit_limit()
    {
        // E1 gilt auch im Ausnahmefall: Scheitert die Buchführung, darf daraus weder ein „weiter
        // versuchen" werden noch ein toter Loop. Gebrochen wird sie hier an der Logzeile.
        _log.Throws = m => m.Contains("begrenzt-gestoert: Failed (", StringComparison.Ordinal);
        var connector = new FakeUpstreamConnector { DefaultBehavior = (_, _) => throw new IOException("weg (Test)") };
        await using var supervisor = Create(connector, jitter: 0d);
        var limited = new RestartPolicy(MaxRetries: 0, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5));
        var server = await supervisor.AddAsync(TestData.StdioConfig("begrenzt-gestoert", restart: limited), Ct);

        await TestData.WaitUntilAsync(
            () => _log.Entries.Any(e => e.Message.Contains("nicht vollständig verbucht", StringComparison.Ordinal)),
            because: "der Test muss die Buchführung wirklich gebrochen haben");
        for (var i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            await Task.Delay(5, Ct);
        }

        connector.ConnectCalls.Should().Be(1);
        var info = supervisor.GetStatus(server)!.Reconnect!;
        info.Exhausted.Should().BeTrue();
        info.NextAttemptAt.Should().BeNull();
        var line = _log.Entries.Single(e => e.Message.Contains("nicht vollständig verbucht", StringComparison.Ordinal));
        line.Level.Should().Be(LogLevel.Error);
        line.Message.Should().Contain("eingestellt").And.NotContain("läuft trotzdem weiter");
        Lifecycle.Should().ContainSingle(
            l => l.StartsWith("begrenzt-gestoert:", StringComparison.Ordinal) && l.Contains("eingestellt", StringComparison.Ordinal),
            "die Audit-Zeile zur Grenze hängt nicht an der Logzeile davor");

        // Der Loop lebt: „Jetzt verbinden" wird angenommen und führt zu genau einem Versuch.
        _log.Throws = null;
        await ConnectNowAsync(supervisor, server);
        await TestData.WaitUntilAsync(
            () => connector.ConnectCalls == 2, because: "der Anstoß führt zu einem Versuch");
    }

    [Fact]
    public async Task Failed_bookkeeping_keeps_the_long_wait_after_a_rejected_login()
    {
        // E2 auch im Ausnahmefall: Die Frist ist schon bestimmt, wenn die Buchführung bricht — nach
        // abgelehnter Anmeldung bleibt es bei 15 min, nicht beim Deckel der Policy (5 min).
        _log.Throws = m => m.Contains("anmeldung-gestoert: Failed (Authentication)", StringComparison.Ordinal);
        var connector = new FakeUpstreamConnector
        {
            DefaultBehavior = static (_, _) => throw new HttpRequestException(
                "Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized),
        };
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("anmeldung-gestoert"), Ct);

        await TestData.WaitUntilAsync(
            () => _log.Entries.Any(e => e.Message.Contains("nicht vollständig verbucht", StringComparison.Ordinal)),
            because: "der Test muss die Buchführung wirklich gebrochen haben");

        var info = supervisor.GetStatus(server)!.Reconnect!;
        info.Kind.Should().Be(UpstreamFailureKind.Authentication);
        (info.NextAttemptAt!.Value - _time.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task A_logger_that_throws_on_every_line_does_not_end_the_loop()
    {
        // Z1 auch dann, wenn selbst die Meldung über die gescheiterte Buchführung scheitert: Der
        // .NET-Logger reicht Ausnahmen seiner Ausgaben weiter.
        _log.Throws = m => m.Contains("log-kaputt", StringComparison.Ordinal);
        var connector = new FakeUpstreamConnector();
        var available = false;
        connector.DefaultBehavior = (id, _) => Volatile.Read(ref available)
            ? new FakeUpstreamConnection { Id = id }
            : throw new IOException("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("log-kaputt"), Ct);

        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.ConnectCalls >= 3,
            step: TimeSpan.FromSeconds(30), timeoutMs: 15000,
            because: "der Loop versucht es weiter, obwohl jede seiner Logzeilen wirft");
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Failed);

        _log.Throws = null;
        Volatile.Write(ref available, true);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(30), timeoutMs: 15000,
            because: "ist das Log wieder in Ordnung und der Upstream da, verbindet der Loop");
    }

    [Fact]
    public async Task A_refused_failed_line_reaches_the_audit_while_the_outage_lasts()
    {
        // Ein Versuch, der wieder scheitert, schreibt keine neue Zeile (Abschnitt 6). Die
        // zurückgestellte Failed-Zeile darf deshalb nicht auf den nächsten Wechsel warten — der
        // kann Tage entfernt sein, und gerade in dieser Zeit fragt jemand das Audit.
        var full = true;
        var audit = new RefusingAuditSink
        {
            Refuses = e => Volatile.Read(ref full) && e.Detail?.Contains("Failed", StringComparison.Ordinal) == true,
        };
        var connector = new FakeUpstreamConnector { DefaultBehavior = (_, _) => throw new IOException("weg (Test)") };
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("lange-weg"), Ct);

        await FailedAttemptAsync(supervisor, server, 1);
        audit.Refused.Should().BeGreaterThan(0, "sonst hätte der Test den Fall gar nicht hergestellt");
        Volatile.Write(ref full, false);

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => audit.Accepted.Any(e => e.Detail!.StartsWith("lange-weg: Failed (", StringComparison.Ordinal)),
            step: TimeSpan.FromSeconds(30), timeoutMs: 15000,
            because: "die Zeile wird beim nächsten Versuch nachgereicht, ohne dass sich etwas ändert");
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Failed);
    }

    [Fact]
    public async Task Lines_the_audit_never_takes_are_reported_when_the_upstream_is_removed()
    {
        var audit = new RefusingAuditSink { Refuses = e => e.Detail?.Contains("Failed", StringComparison.Ordinal) == true };
        var connector = new FakeUpstreamConnector { DefaultBehavior = (_, _) => throw new IOException("weg (Test)") };
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("entfernt"), Ct);
        await FailedAttemptAsync(supervisor, server, 1);

        await supervisor.RemoveAsync(server, DrainPolicy.Immediate, Ct);

        _log.Entries.Should().Contain(
            e => e.Level == LogLevel.Error
                && e.Message.Contains("entfernt", StringComparison.Ordinal)
                && e.Message.Contains("verloren", StringComparison.Ordinal),
            "eine Zeile, die nie ins Audit kam, verschwindet nicht still");
    }

    [Fact]
    public async Task A_broken_cleanup_still_records_the_failure_and_its_kind()
    {
        // Scheitert das Aufräumen der alten Verbindung, kommt MarkFailed gar nicht zum Zug. Der
        // Eintritt in Failed muss trotzdem im Audit stehen, und die Fehlerart darf nicht None
        // bleiben — sonst meldete der nächste Versuch einen Wechsel „von None".
        _log.Throws = m => m.Contains("Fehler beim Schließen", StringComparison.Ordinal);
        var first = new FakeUpstreamConnection { DisposeException = new IOException("Schließen klemmt (Test)") };
        var connector = new FakeUpstreamConnector();
        connector.DefaultBehavior = (id, _) => connector.ConnectCalls <= 1
            ? first
            : throw new IOException("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("aufraeumen"), Ct);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromMilliseconds(500), timeoutMs: 15000);

        first.FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.ConnectCalls >= 3,
            step: TimeSpan.FromSeconds(5), timeoutMs: 15000,
            because: "nach dem gescheiterten Aufräumen folgen weitere Versuche");

        Lifecycle.Where(l => l.StartsWith("aufraeumen: Failed (", StringComparison.Ordinal)
                && !l.Contains("Fehlerart wechselt", StringComparison.Ordinal))
            .Should().ContainSingle("der Eintritt in Failed steht genau einmal im Audit");
        Lifecycle.Should().NotContain(l => l.Contains("von None", StringComparison.Ordinal));
        supervisor.GetStatus(server)!.Reconnect!.Kind.Should().Be(UpstreamFailureKind.Unreachable);
    }

    [Fact]
    public async Task A_throwing_log_line_about_the_deferral_does_not_cost_the_limit_line()
    {
        // Audit voll und Logger gestört zugleich: Wirft die Logzeile über das Zurückstellen der
        // Failed-Zeile, darf die Zeile „eingestellt" dahinter nicht verloren gehen — ein späterer
        // Versuch sähe keinen Wechsel mehr, den er nachholen könnte.
        var full = true;
        var audit = new RefusingAuditSink
        {
            Refuses = e => Volatile.Read(ref full) && e.Detail?.Contains("Failed", StringComparison.Ordinal) == true,
        };
        _log.Throws = m => m.Contains("grenze-stumm", StringComparison.Ordinal)
            && m.Contains("nicht angenommen", StringComparison.Ordinal);
        var connector = new FakeUpstreamConnector { DefaultBehavior = (_, _) => throw new IOException("weg (Test)") };
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var limited = new RestartPolicy(MaxRetries: 0, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5));
        var server = await supervisor.AddAsync(TestData.StdioConfig("grenze-stumm", restart: limited), Ct);
        // Erst freigeben, wenn der Fall wirklich eingetreten ist: Die Grenze steht im Status schon,
        // bevor der Loop seine Zeilen geschrieben hat.
        await ExhaustedAsync(supervisor, server);
        audit.Refused.Should().BeGreaterThan(0, "das Audit hat die Failed-Zeile abgewiesen");
        _log.Thrown.Should().BeGreaterThan(0, "die Logzeile über das Zurückstellen hat geworfen");

        _log.Throws = null;
        Volatile.Write(ref full, false);
        await ConnectNowAsync(supervisor, server);
        await TestData.WaitUntilAsync(
            () => audit.Accepted.Any(e => e.Detail!.StartsWith("grenze-stumm:", StringComparison.Ordinal)
                && e.Detail.Contains("eingestellt", StringComparison.Ordinal)),
            because: "die Zeile zur Grenze wird nachgereicht, sobald das Audit wieder annimmt");
        audit.Accepted.Where(e => e.Detail!.StartsWith("grenze-stumm: Failed (", StringComparison.Ordinal)
                && !e.Detail.Contains("eingestellt", StringComparison.Ordinal))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task A_refused_reconnect_line_arrives_with_a_later_health_ping()
    {
        // Bleibt der Upstream gesund, gibt es keinen nächsten Wechsel. Ein abgewiesenes „wieder
        // verbunden" kommt dann mit einem der folgenden Lebenszeichen an.
        var full = true;
        var audit = new RefusingAuditSink
        {
            Refuses = e => Volatile.Read(ref full) && e.Detail?.Contains("wieder verbunden", StringComparison.Ordinal) == true,
        };
        var connector = new FakeUpstreamConnector();
        var available = false;
        connector.DefaultBehavior = (id, _) => Volatile.Read(ref available)
            ? new FakeUpstreamConnection { Id = id }
            : throw new IOException("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("gesund-still"), Ct);
        await FailedAttemptAsync(supervisor, server, 1);

        Volatile.Write(ref available, true);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(1), timeoutMs: 15000);

        // Healthy steht vor seiner Audit-Zeile: auf die Abweisung warten, nicht nur auf den Zustand.
        await TestData.WaitUntilAsync(
            () => audit.Refused > 0, because: "sonst hätte der Test den Fall gar nicht hergestellt");

        Volatile.Write(ref full, false);
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => audit.Accepted.Any(e => e.Detail!.StartsWith("gesund-still:", StringComparison.Ordinal)
                && e.Detail.Contains("wieder verbunden", StringComparison.Ordinal)),
            step: TimeSpan.FromSeconds(1), timeoutMs: 15000,
            because: "das nächste Lebenszeichen reicht die Zeile nach");
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy);
    }

    [Fact]
    public async Task A_failing_close_logs_type_names_only()
    {
        // Die Meldung einer Transport-Ausnahme kann Text des Upstreams tragen — auch beim Schließen.
        var first = new FakeUpstreamConnection { DisposeException = new IOException($"Schließen klemmt: {Secret}") };
        var connector = new FakeUpstreamConnector();
        connector.DefaultBehavior = (id, _) => connector.ConnectCalls <= 1
            ? first
            : throw new IOException("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("schliessen"), Ct);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromMilliseconds(500), timeoutMs: 15000);

        first.FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time, () => _log.Entries.Any(e => e.Message.Contains("Fehler beim Schließen", StringComparison.Ordinal)),
            step: TimeSpan.FromSeconds(1), timeoutMs: 15000);

        var line = _log.Entries.First(e => e.Message.Contains("Fehler beim Schließen", StringComparison.Ordinal));
        line.Exception.Should().BeNull("das Ausnahmeobjekt trüge die ungefilterte Meldung");
        line.Message.Should().Contain(nameof(IOException));
        _log.Entries.Should().NotContain(e => e.Message.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_full_deferral_queue_drops_its_oldest_line()
    {
        // Die neueren Zeilen beschreiben den Zustand, in dem der Upstream jetzt ist — fällt eine
        // heraus, dann die älteste.
        var full = true;
        var audit = new RefusingAuditSink
        {
            Refuses = e => Volatile.Read(ref full) && e.Detail?.StartsWith("grenze:", StringComparison.Ordinal) == true,
        };
        var calls = 0;
        var connector = new FakeUpstreamConnector
        {
            // Abwechselnd nicht erreichbar und abgewiesen: Jeder Versuch ist ein Wechsel der
            // Fehlerart und schreibt eine Zeile.
            DefaultBehavior = (_, _) => Interlocked.Increment(ref calls) % 2 == 0
                ? throw new HttpRequestException(
                    "Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized)
                : throw new IOException("weg (Test)"),
        };
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("grenze"), Ct);

        // Starting, Eintritt in Failed und 39 Wechsel: 41 Zeilen für 32 Plätze.
        await FailedAttemptAsync(supervisor, server, 1);
        for (var attempt = 2; attempt <= 40; attempt++)
        {
            await ConnectNowAsync(supervisor, server);
            await FailedAttemptAsync(supervisor, server, attempt);
        }

        _log.Entries.Should().Contain(e => e.Message.Contains("älteste zurückgestellte", StringComparison.Ordinal));

        // Verloren sind genau die verworfenen Zeilen (je eine Logzeile) — nicht jede der vielen
        // Abweisungen davor (V3).
        var dropped = _log.Entries.Count(e => e.Message.Contains("älteste zurückgestellte", StringComparison.Ordinal));
        audit.Discarded.Should().Be(dropped);
        audit.Refused.Should().BeGreaterThan(dropped, "abgewiesen wurde weit öfter, als verworfen");

        Volatile.Write(ref full, false);
        await ConnectNowAsync(supervisor, server);
        await FailedAttemptAsync(supervisor, server, 41);

        var lines = audit.Accepted.Where(e => e.Detail!.StartsWith("grenze:", StringComparison.Ordinal)).ToList();
        // Die 32 zurückgestellten Zeilen kommen beim Aufwachen an, danach die des 41. Versuchs.
        lines.Should().HaveCount(UpstreamSupervisor.MaxDeferredLifecycleLines + 1);
        lines.Should().NotContain(e => e.Detail == "grenze: Starting", "die älteste Zeile ist herausgefallen");
        lines.Select(e => e.Timestamp).Should().BeInAscendingOrder();
        lines[^1].Detail.Should().Contain("Fehlerart wechselt", "die neueste Zeile ist angekommen");
    }

    // ── Zustandspflege im Loop (W10) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Connect_now_from_the_failed_event_is_not_lost()
    {
        // Wer auf das Ereignis „Failed" sofort „Jetzt verbinden" drückt, drückt in der Lücke
        // zwischen dem Verbuchen und dem Warten. Der Klick darf dort nicht verpuffen (L1).
        var connector = new FakeUpstreamConnector();
        connector.EnqueueConnectFailure("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d);
        bool? clicked = null;
        supervisor.Changed += (_, e) =>
        {
            if (e.Kind == UpstreamChangeKind.StateChanged && e.State == UpstreamState.Failed && clicked is null)
            {
                clicked = supervisor.ConnectNowAsync(e.Server, Ct).GetAwaiter().GetResult();
            }
        };

        var server = await supervisor.AddAsync(TestData.StdioConfig("sofort"), Ct);

        // Ohne die Uhr vorzustellen: Die Frist von 5 s läuft nie ab, nur der Klick führt weiter.
        await TestData.WaitUntilAsync(
            () => supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            because: "der Klick im Ereignis stößt den nächsten Versuch an");
        clicked.Should().BeTrue();
        connector.ConnectCalls.Should().Be(2);
    }

    [Fact]
    public async Task After_connect_now_the_loop_is_no_longer_reported_as_waiting()
    {
        _connector.DefaultBehavior = static (_, _) => throw new IOException("weg (Test)");
        var server = await _supervisor.AddAsync(TestData.StdioConfig("klick"), Ct);
        await FailedAttemptAsync(server, 1);

        // Der nächste Versuch hängt, bis der Test ihn freigibt.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.ConnectHook = _ => release.Task;

        (await _supervisor.ConnectNowAsync(server, Ct)).Should().BeTrue();
        _supervisor.IsWaitingForNextAttempt(server).Should().BeFalse(
            "der Loop hat den Anstoß angenommen, auch wenn er die Sperre noch nicht zurück hat");
        (await _supervisor.ConnectNowAsync(server, Ct)).Should().BeFalse("ein zweiter Klick findet keinen wartenden Loop");

        release.SetResult();
        await FailedAttemptAsync(server, 2);
    }

    [Fact]
    public async Task Deferred_lines_arrive_at_the_limit_without_a_click()
    {
        // An der gesetzten Obergrenze wartet der Loop nur noch auf den Knopf. Zurückgestellte
        // Audit-Zeilen dürfen nicht mit ihm warten (T2).
        var full = true;
        var audit = new RefusingAuditSink
        {
            Refuses = e => Volatile.Read(ref full) && e.Detail?.StartsWith("still:", StringComparison.Ordinal) == true,
        };
        var connector = new FakeUpstreamConnector { DefaultBehavior = static (_, _) => throw new IOException("weg (Test)") };
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var limited = new RestartPolicy(MaxRetries: 1, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5));
        var server = await supervisor.AddAsync(TestData.StdioConfig("still", restart: limited), Ct);

        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.Reconnect?.Exhausted == true && supervisor.IsWaitingForNextAttempt(server),
            step: TimeSpan.FromSeconds(1), because: "1 Erstversuch + 1 Wiederholung, dann ist die Grenze erreicht");
        audit.Refused.Should().BeGreaterThan(0, "sonst hätte der Test den Fall gar nicht hergestellt");
        audit.Accepted.Should().NotContain(e => e.Detail!.StartsWith("still: Failed", StringComparison.Ordinal));

        Volatile.Write(ref full, false);
        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => audit.Accepted.Any(e => e.Detail!.StartsWith("still: Failed (Unreachable)", StringComparison.Ordinal)),
            step: TimeSpan.FromSeconds(1), timeoutMs: 15000,
            because: "der wartende Loop reicht im Takt des Lebenszeichens nach");

        connector.ConnectCalls.Should().Be(2, "nachgereicht wurde ohne neuen Versuch");
        supervisor.GetStatus(server)!.Reconnect!.Exhausted.Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_during_a_rebuild_answers_the_waiting_caller_with_a_lost_connection()
    {
        // Der Aufrufer hat das Gateway nie verlassen; er bekommt „Verbindung verloren" wie bei
        // jedem anderen gescheiterten Neuaufbau, keinen Abbruch, den er für seinen eigenen hielte (U8).
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        _connector.Connections[0].CallException = new UpstreamSessionLostException("404 (Test)");
        _connector.ConnectHook = ct => Task.Delay(Timeout.Infinite, ct);
        var stale = _supervisor.GetConnection(server)!;

        var call = stale.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        await TestData.WaitUntilAsync(() => _connector.ConnectCalls == 2, because: "der Neuaufbau läuft");

        await _supervisor.SetEnabledAsync(server, enabled: false, Ct);

        var act = () => call;
        await act.Should().ThrowExactlyAsync<UpstreamConnectionLostException>();
    }

    // ── Werfende Log-Ausgabe (W11) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_throwing_log_line_on_connect_does_not_give_up_the_connection()
    {
        _log.Throws = m => m.Contains("Healthy mit", StringComparison.Ordinal);
        var server = await _supervisor.AddAsync(TestData.StdioConfig("laut"), Ct);
        await TestData.WaitUntilAsync(() => _log.Thrown > 0, because: "der Fall muss eintreten");

        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(5, Ct);
        }

        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy);
        _connector.ConnectCalls.Should().Be(1, "die Verbindung steht; eine gescheiterte Logzeile ist kein Fehlversuch");
        _connector.Connections[0].DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task A_throwing_log_line_during_the_rebuild_still_serves_the_caller_and_closes_the_old_connection()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        _log.Throws = m => m.Contains("Sitzung verloren", StringComparison.Ordinal)
            || m.Contains("Sitzung neu aufgebaut", StringComparison.Ordinal);
        var old = _connector.Connections[0];
        old.CallException = new UpstreamSessionLostException("404 (Test)");

        var result = await _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        result.GetProperty("tool").GetString().Should().Be("echo");
        _log.Thrown.Should().BeGreaterThan(0, "sonst hätte der Test den Fall gar nicht hergestellt");
        await TestData.WaitUntilAsync(() => old.DisposeCount == 1, because: "die tote Verbindung wird geschlossen");
        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy);
    }

    [Fact]
    public async Task A_throwing_log_line_about_a_failed_close_does_not_break_the_rebuild()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        _log.Throws = m => m.Contains("Fehler beim Schließen", StringComparison.Ordinal);
        var old = _connector.Connections[0];
        old.DisposeException = new IOException("Prozess schon fort (Test)");
        old.CallException = new UpstreamSessionLostException("404 (Test)");

        await _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        await TestData.WaitUntilAsync(() => _log.Thrown > 0, because: "der Fall muss eintreten");

        // Ein zweiter Verlust zeigt, dass der Loop die Verbindung weiter bedient.
        _connector.Connections[1].CallException = new UpstreamSessionLostException("404 (Test)");
        var result = await _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        result.GetProperty("tool").GetString().Should().Be("echo");
        _connector.ConnectCalls.Should().Be(3);
    }

    [Fact]
    public async Task A_throwing_changed_handler_is_logged_with_type_names_only()
    {
        _supervisor.Changed += (_, _) => throw new InvalidOperationException("Handler kaputt: " + Secret);
        await _supervisor.AddAsync(TestData.StdioConfig(), Ct);

        await TestData.WaitUntilAsync(
            () => _log.Entries.Any(e => e.Message.Contains("Changed-Handler", StringComparison.Ordinal)));
        var line = _log.Entries.First(e => e.Message.Contains("Changed-Handler", StringComparison.Ordinal));
        line.Exception.Should().BeNull("das Ausnahmeobjekt trüge die Meldung des Handlers");
        line.Message.Should().Contain(nameof(InvalidOperationException));
        _log.Entries.Should().NotContain(e => e.Message.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disposing_stops_every_upstream_even_when_closing_one_fails_loudly()
    {
        var connector = new FakeUpstreamConnector
        {
            DefaultBehavior = static (id, _) => new FakeUpstreamConnection
            {
                Id = id,
                DisposeException = new IOException("Prozess schon fort (Test)"),
            },
        };
        var supervisor = Create(connector, jitter: 0d);
        var first = await supervisor.AddAsync(TestData.StdioConfig("eins"), Ct);
        var second = await supervisor.AddAsync(TestData.StdioConfig("zwei"), Ct);
        await TestData.WaitUntilAsync(
            () => supervisor.GetStatus(first)?.State == UpstreamState.Healthy
                && supervisor.GetStatus(second)?.State == UpstreamState.Healthy);
        _log.Throws = m => m.Contains("Fehler beim Schließen", StringComparison.Ordinal);

        var act = async () => await supervisor.DisposeAsync();

        await act.Should().NotThrowAsync();
        connector.Connections.Should().HaveCount(2).And.OnlyContain(c => c.DisposeCount == 1);
    }

    // ── Inventar (W12) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rediscovery_on_a_replaced_connection_does_not_overwrite_the_new_catalog()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var old = _connector.Connections[0];
        old.Inventory = TestData.InventoryWithTools("echo", "alt");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.DiscoverHook = () => gate.Task;
        _connector.DefaultBehavior = static (id, _) => new FakeUpstreamConnection
        {
            Id = id,
            Inventory = TestData.InventoryWithTools("echo", "neu"),
        };

        // Die Abfrage hängt auf der alten Verbindung, während die Sitzung neu aufgebaut wird.
        var rediscovery = _supervisor.RediscoverAsync(server, Ct);
        await TestData.WaitUntilAsync(() => old.DiscoverCalls == 2, because: "die Abfrage läuft");
        old.CallException = new UpstreamSessionLostException("404 (Test)");
        await _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        gate.SetResult();
        await rediscovery;

        _supervisor.GetInventory(server)!.Tools.Select(t => t.Name).Should().BeEquivalentTo(["echo", "neu"],
            "der Katalog der neuen Verbindung bleibt; der der alten kam zu spät (L3)");
    }

    [Fact]
    public async Task A_lost_session_on_rediscovery_starts_the_rebuild()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        _connector.Connections[0].DiscoverException = new UpstreamSessionLostException("404 (Test)");

        await _supervisor.RediscoverAsync(server, Ct);

        // Ohne die Uhr vorzustellen: Kein Lebenszeichen hilft hier nach (R6).
        await TestData.WaitUntilAsync(
            () => _connector.ConnectCalls == 2 && _supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            because: "die Abfrage selbst fordert den Neuaufbau an");
        _supervisor.GetConnection(server)!.Should().NotBeNull();

        // Die alte Verbindung wird nach dem Veröffentlichen der neuen geschlossen, nicht davor —
        // unter Last ist das ein paar Millisekunden später (im vollen Testlauf gemessen).
        await TestData.WaitUntilAsync(
            () => _connector.Connections[0].DisposeCount == 1, because: "die alte Verbindung wird geschlossen");
    }

    [Fact]
    public async Task A_call_to_a_tool_outside_the_checked_catalog_is_refused_on_the_first_attempt()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        var act = () => _supervisor.GetConnection(server)!.CallToolAsync("zurueckgehalten", TestData.EmptySchema(), Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*nicht im geprüften Katalog*");
        _connector.Connections[0].CallCount.Should().Be(0, "der Aufruf hat das Gateway nicht verlassen (U5)");
    }

    // ── Verwaltungsaktionen (W13) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabling_with_a_refusing_audit_changes_nothing()
    {
        var audit = new RefusingAuditSink { Refuses = e => e.Detail == "verweigert: Stopped" };
        await using var supervisor = Create(_connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("verweigert"), Ct);

        // Auf das Katalog-Ereignis des Verbindens warten: Es folgt dem Zustand Healthy erst nach.
        await TestData.WaitUntilAsync(() => Events.Any(
            e => e.Server == server && e.Kind == UpstreamChangeKind.InventoryChanged && e.State == UpstreamState.Healthy));
        var events = Events.Count;

        var act = () => supervisor.SetEnabledAsync(server, enabled: false, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy, "verweigert heißt: nicht geschehen (U3)");
        supervisor.GetConnection(server).Should().NotBeNull();
        _connector.Connections[0].DisposeCount.Should().Be(0);
        Events.Skip(events).Should().BeEmpty("der Katalog hat nichts zu erfahren");
        (await LatestEnabledAsync(server)).Should().BeTrue();
    }

    [Fact]
    public async Task Enabling_with_a_refusing_audit_leaves_no_half_started_upstream()
    {
        var audit = new RefusingAuditSink { Refuses = e => e.Detail == "halb: Starting" };
        await using var supervisor = Create(_connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("halb") with { Enabled = false }, Ct);

        var act = () => supervisor.SetEnabledAsync(server, enabled: true, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped, "kein Starting ohne Loop (R7)");
        _connector.ConnectCalls.Should().Be(0);
        (await LatestEnabledAsync(server)).Should().BeFalse(
            "eine verweigerte Aktion ist nicht gespeichert (L4)");
    }

    [Fact]
    public async Task A_session_lost_while_draining_for_a_disable_is_not_rebuilt()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var first = _connector.Connections[0];
        first.CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        await TestData.WaitUntilAsync(() => first.CallCount == 1, because: "der Aufruf läuft");

        // Abschalten wartet auf den laufenden Aufruf; in dieser Zeit geht die Sitzung verloren.
        var disabling = _supervisor.SetEnabledAsync(server, enabled: false, Ct);
        first.PingException = new UpstreamSessionLostException("404 (Test)");
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }

        _connector.ConnectCalls.Should().Be(1, "ein abgeschalteter Upstream wird nicht neu aufgebaut (R7)");

        first.CallGate.SetResult(JsonSerializer.SerializeToElement(new { ok = true }));
        await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => disabling.IsCompleted, step: TimeSpan.FromMilliseconds(100), because: "der Drain ist durch");
        await disabling.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
        _connector.ConnectCalls.Should().Be(1);
    }

    // ── Abgewiesenes Token auf stehender Verbindung (V1) ─────────────────────────────────────

    private static UpstreamServerConfig OAuthConfig(string slug)
        => new(
            slug, slug, UpstreamTransportKind.StreamableHttp, Enabled: true,
            Http: new HttpTransportOptions(
                new Uri("https://upstream.invalid/mcp"), OAuth: new UpstreamOAuthOptions("gateway")));

    private static HttpRequestException Unauthorized()
        => new("Response status code does not indicate success: 401 (Unauthorized).", null, HttpStatusCode.Unauthorized);

    private (FakeUpstreamConnector Connector, UpstreamSupervisor Supervisor) CreateOAuth(bool renewedTokenIsRefused = false)
    {
        var connector = new FakeUpstreamConnector { Kind = UpstreamTransportKind.StreamableHttp };
        var supervisor = Create(connector, jitter: 0d);
        if (renewedTokenIsRefused)
        {
            // Jede Verbindung nach der ersten weist ihr Token gleich wieder ab.
            connector.DefaultBehavior = (id, _) => new FakeUpstreamConnection
            {
                Id = id,
                PingException = connector.ConnectCalls > 1 ? Unauthorized() : null,
            };
        }

        return (connector, supervisor);
    }

    /// <summary>
    /// E2: Ein Token gilt für einen Endpunkt und einen Client. Ändert sich eins davon, wird es
    /// gelöscht; eine Änderung, die beides lässt, behält es. Mit dem Upstream geht auch sein Token.
    /// </summary>
    [Fact]
    public async Task A_token_is_dropped_when_its_binding_changes()
    {
        var tokens = new InMemoryOAuthTokens();
        var connector = new FakeUpstreamConnector { Kind = UpstreamTransportKind.StreamableHttp };
        await using var supervisor = Create(connector, jitter: 0d, oauthTokens: tokens);
        var config = OAuthConfig("bindung");
        var server = await supervisor.AddAsync(config, Ct);
        tokens.Put(server);

        await supervisor.ReconfigureAsync(server, config with { DisplayName = "Anders benannt" }, Ct);
        tokens.Has(server).Should().BeTrue("Name geändert, Bindung nicht");

        await supervisor.ReconfigureAsync(
            server, config with { Http = config.Http! with { OAuth = new UpstreamOAuthOptions("anderer-client") } }, Ct);
        tokens.Has(server).Should().BeFalse("andere Client-ID");

        tokens.Put(server);
        await supervisor.ReconfigureAsync(
            server,
            config with
            {
                Http = config.Http! with { Endpoint = new Uri("https://anderer.invalid/mcp"), OAuth = new UpstreamOAuthOptions("anderer-client") },
            },
            Ct);
        tokens.Has(server).Should().BeFalse("anderer Endpunkt, also andere Ressource");

        tokens.Put(server);
        await supervisor.RemoveAsync(server, DrainPolicy.Immediate, Ct);
        tokens.Has(server).Should().BeFalse("mit dem Upstream geht sein Token");
    }

    private sealed class InMemoryOAuthTokens : IUpstreamOAuthTokenStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ServerId, UpstreamOAuthToken> _tokens = new();

        public void Put(ServerId server) => _tokens[server] = new UpstreamOAuthToken(
            server, "at-" + Guid.NewGuid().ToString("N"), null, null, [], "https://as.invalid", DateTimeOffset.UnixEpoch);

        public bool Has(ServerId server) => _tokens.ContainsKey(server);

        public Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct)
            => Task.FromResult(_tokens.GetValueOrDefault(server));

        public Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct)
        {
            _tokens[token.Server] = token;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(ServerId server, CancellationToken ct)
        {
            _tokens.TryRemove(server, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ReplaceAsync(UpstreamOAuthToken expected, UpstreamOAuthToken replacement, CancellationToken ct)
            => Task.FromResult(_tokens.TryUpdate(expected.Server, replacement, expected));
    }

    /// <summary>
    /// E11: Gemeldet wird das Token, das die abgewiesene Verbindung selbst vorgelegt hat — nicht das
    /// der zuletzt aufgebauten Verbindung.
    /// </summary>
    [Fact]
    public async Task The_renewal_names_the_token_of_the_refused_connection()
    {
        var (connector, supervisor) = CreateOAuth();
        await using var _ = supervisor;
        connector.DefaultBehavior = (id, _) => new FakeUpstreamConnection
        {
            Id = id,
            PresentedCredentials = [$"token-{connector.ConnectCalls}-{Guid.NewGuid():N}"],
        };
        var server = await supervisor.AddAsync(OAuthConfig("oauth-e11"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var first = connector.Connections[0];

        first.PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.CredentialRenewals == 1, step: TimeSpan.FromSeconds(1));

        connector.LastRejectedCredential.Should().Be(first.PresentedCredentials[0]);
    }

    [Fact]
    public async Task A_refused_token_on_a_standing_connection_is_renewed_and_reconnected_at_once()
    {
        var (connector, supervisor) = CreateOAuth();
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(OAuthConfig("oauth"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var first = connector.Connections[0];
        var stale = supervisor.GetConnection(server)!;

        first.PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.ConnectCalls == 2 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(1), because: "abgewiesenes Token: erneuern und sofort neu verbinden (V1)");

        connector.CredentialRenewals.Should().Be(1);
        Events.Should().NotContain(e => e.Server == server && e.State == UpstreamState.Failed,
            "ohne Frist und ohne Fehlversuch");
        await TestData.WaitUntilAsync(() => first.DisposeCount == 1);

        // Wer die alte Hülle hält, landet auf der neuen Verbindung.
        await stale.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        first.CallCount.Should().Be(0);
        connector.Connections[1].CallCount.Should().Be(1);

        // Eine Stunde später läuft das Token wieder ab: Auch das wird erneuert, nicht abgelehnt.
        _time.Advance(TimeSpan.FromHours(1));
        connector.Connections[1].PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.ConnectCalls == 3 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(1), because: "jeder Ablauf nach einer gesunden Minute wird erneuert");
        connector.CredentialRenewals.Should().Be(2);
    }

    [Fact]
    public async Task A_token_refused_again_right_after_renewal_is_a_rejected_login()
    {
        var (connector, supervisor) = CreateOAuth(renewedTokenIsRefused: true);
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(OAuthConfig("abgelehnt"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        connector.Connections[0].PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.Reconnect is { NextAttemptAt: not null },
            step: TimeSpan.FromSeconds(1), because: "ein zweites 401 gleich danach ist eine abgelehnte Anmeldung (E2)");

        supervisor.GetStatus(server)!.Reconnect!.Kind.Should().Be(UpstreamFailureKind.Authentication);
        connector.CredentialRenewals.Should().Be(1, "erneuert wird einmal, nicht in einer Schleife");
    }

    [Fact]
    public async Task A_refused_static_key_is_not_renewed()
    {
        var connector = new FakeUpstreamConnector { Kind = UpstreamTransportKind.StreamableHttp };
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(HttpConfig(new Uri("https://upstream.invalid/mcp")), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        connector.Connections[0].PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.Reconnect is { NextAttemptAt: not null },
            step: TimeSpan.FromSeconds(1), because: "ein fester Schlüssel lässt sich nicht erneuern");

        connector.CredentialRenewals.Should().Be(0);
        supervisor.GetStatus(server)!.Reconnect!.Kind.Should().Be(UpstreamFailureKind.Authentication);
    }

    // ── Bremse für Neuaufbauten (V2) ─────────────────────────────────────────────────────────

    private async Task LoseSessionAsync(ServerId server)
    {
        _connector.Connections[^1].CallException = new UpstreamSessionLostException("404 (Test)");
        await _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);
    }

    [Fact]
    public async Task A_session_lost_again_and_again_counts_as_an_outage()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("flattert"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        for (var i = 0; i < UpstreamSupervisor.MaxRebuildsPerWindow; i++)
        {
            await LoseSessionAsync(server);
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        _connector.ConnectCalls.Should().Be(1 + UpstreamSupervisor.MaxRebuildsPerWindow, "drei Neuaufbauten sind erlaubt");

        var act = () => LoseSessionAsync(server);

        await act.Should().ThrowAsync<UpstreamConnectionLostException>().WithMessage("*wiederholt verloren*");
        var info = await FailedAttemptAsync(server, 1);
        info.Kind.Should().Be(UpstreamFailureKind.Unreachable, "ab hier übernimmt die Frist aus Abschnitt 3 (V2)");
        _connector.ConnectCalls.Should().Be(1 + UpstreamSupervisor.MaxRebuildsPerWindow, "der vierte wird nicht mehr gebaut");
    }

    [Fact]
    public async Task Rebuilds_spread_over_more_than_five_minutes_do_not_count_as_an_outage()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("selten"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        for (var i = 0; i < 6; i++)
        {
            await LoseSessionAsync(server);
            _time.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(5, Ct);
        }

        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy);
        _connector.ConnectCalls.Should().Be(7, "jeder Verlust ein Neuaufbau, keiner zu viel in fünf Minuten");
    }

    // ── Codex-Review 2026-11-18 (C-Pakete im Plan 0005) ──────────────────────────────────────

    /// <summary>C12: Die Bremse leert beim Auslösen ihr Fenster nicht — der nächste Verlust im selben Fenster geht wieder in die Frist.</summary>
    [Fact]
    public async Task The_rebuild_brake_keeps_its_window_after_tripping()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("bremse"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        for (var i = 0; i < UpstreamSupervisor.MaxRebuildsPerWindow; i++)
        {
            await LoseSessionAsync(server);
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        var tripping = () => LoseSessionAsync(server);
        await tripping.Should().ThrowAsync<UpstreamConnectionLostException>();
        await FailedAttemptAsync(server, 1);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(1), because: "nach der Frist wird neu verbunden");
        var connects = _connector.ConnectCalls;

        var again = () => LoseSessionAsync(server);

        await again.Should().ThrowAsync<UpstreamConnectionLostException>().WithMessage("*wiederholt verloren*",
            "die drei Neuaufbauten liegen noch im Fenster");
        _connector.ConnectCalls.Should().Be(connects, "kein sofortiger Neuaufbau");
    }

    /// <summary>C8: Ein 403 sagt „darf das nicht" — ein neues Token derselben Anmeldung darf es auch nicht. Keine Erneuerung.</summary>
    [Fact]
    public async Task A_forbidden_ping_does_not_renew_the_token()
    {
        var (connector, supervisor) = CreateOAuth();
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(OAuthConfig("verboten"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        connector.Connections[0].PingException = new HttpRequestException("403", null, HttpStatusCode.Forbidden);
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.Reconnect is { NextAttemptAt: not null },
            step: TimeSpan.FromSeconds(1), because: "ein 403 ist eine abgelehnte Anmeldung");

        connector.CredentialRenewals.Should().Be(0);
        supervisor.GetStatus(server)!.Reconnect!.Kind.Should().Be(UpstreamFailureKind.Authentication);
    }

    /// <summary>
    /// C8: Die gesunde Minute nach einer Erneuerung zählt ab der neuen Verbindung. Dauert der
    /// Neuaufbau, ist ein 401 eine Minute nach dem Entschluss, aber vor einer gesunden Minute, eine
    /// abgelehnte Anmeldung (E2) — keine zweite Erneuerung.
    /// </summary>
    [Fact]
    public async Task The_healthy_minute_after_a_renewal_starts_with_the_new_connection()
    {
        var (connector, supervisor) = CreateOAuth();
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(OAuthConfig("langsam"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.ConnectHook = _ => gate.Task;

        connector.Connections[0].PingException = Unauthorized();
        _time.Advance(TimeSpan.FromSeconds(1));
        await TestData.WaitUntilAsync(() => connector.ConnectCalls == 2, because: "die Erneuerung baut neu auf");
        _time.Advance(TimeSpan.FromSeconds(10));
        connector.ConnectHook = null;
        gate.SetResult();
        await TestData.WaitUntilAsync(
            () => connector.Connections.Count == 2 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        connector.Connections[1].PingException = Unauthorized();
        _time.Advance(TimeSpan.FromSeconds(52));
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.Reconnect is { NextAttemptAt: not null },
            step: TimeSpan.FromSeconds(1), because: "keine gesunde Minute seit der neuen Verbindung");

        connector.CredentialRenewals.Should().Be(1, "63 s nach dem Entschluss, aber unter einer Minute gesund");
        supervisor.GetStatus(server)!.Reconnect!.Kind.Should().Be(UpstreamFailureKind.Authentication);
    }

    /// <summary>
    /// D11, E16: „Jetzt verbinden" während eines laufenden Versuchs tut nichts (Abschnitt 5.1) —
    /// scheitert der Versuch, gilt die Frist wie sonst.
    /// </summary>
    [Fact]
    public async Task Connect_now_during_a_running_retry_does_nothing()
    {
        var connector = new FakeUpstreamConnector();
        connector.EnqueueConnectFailure("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("nichts"), Ct);
        await FailedAttemptAsync(supervisor, server, 1);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.ConnectHook = _ => gate.Task;
        connector.EnqueueConnectFailure("immer noch weg (Test)");
        await ConnectNowAsync(supervisor, server);
        await TestData.WaitUntilAsync(() => connector.ConnectCalls == 2);

        (await supervisor.ConnectNowAsync(server, Ct)).Should().BeFalse("ein Versuch läuft schon");
        connector.ConnectHook = null;
        gate.SetResult();

        var info = await FailedAttemptAsync(supervisor, server, 2);
        info.NextAttemptAt.Should().NotBeNull("die Frist gilt");
        await Task.Delay(200, Ct);
        connector.ConnectCalls.Should().Be(2, "ohne Zeitvorlauf folgt kein weiterer Versuch");
    }

    /// <summary>
    /// D11: Neue Zugangsdaten während eines laufenden Versuchs gehen nicht verloren: Scheitert der
    /// laufende Versuch noch mit den alten, folgt der nächste ohne Frist.
    /// </summary>
    [Fact]
    public async Task New_credentials_during_a_running_retry_are_not_lost()
    {
        var connector = new FakeUpstreamConnector();
        connector.EnqueueConnectFailure("weg (Test)");
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("rueckruf"), Ct);
        await FailedAttemptAsync(supervisor, server, 1);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.ConnectHook = _ => gate.Task;
        connector.EnqueueConnectFailure("noch mit dem alten Token (Test)");
        await ConnectNowAsync(supervisor, server);
        await TestData.WaitUntilAsync(() => connector.ConnectCalls == 2);

        supervisor.CredentialsChanged(server);
        connector.ConnectHook = null;
        gate.SetResult();

        await TestData.WaitUntilAsync(
            () => connector.ConnectCalls == 3 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            because: "neue Zugangsdaten während des Versuchs führen gleich zum nächsten, ohne die Frist abzuwarten");
        supervisor.GetStatus(server)!.ToolCount.Should().Be(1);
    }

    /// <summary>D11: Auch der erste Aufbau (Starting) verliert neue Zugangsdaten nicht.</summary>
    [Fact]
    public async Task New_credentials_during_the_first_attempt_are_not_lost()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new FakeUpstreamConnector { ConnectHook = _ => gate.Task };
        connector.EnqueueConnectFailure("noch ohne Token (Test)");
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("erster"), Ct);
        await TestData.WaitUntilAsync(() => connector.ConnectCalls == 1);
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Starting);

        supervisor.CredentialsChanged(server);
        connector.ConnectHook = null;
        gate.SetResult();

        await TestData.WaitUntilAsync(
            () => connector.ConnectCalls == 2 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            because: "der zweite Versuch folgt ohne Frist");
    }

    /// <summary>D11: Neue Zugangsdaten für eine stehende Verbindung verkürzen keine spätere Frist.</summary>
    [Fact]
    public async Task New_credentials_for_a_healthy_upstream_change_nothing()
    {
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("steht"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        supervisor.CredentialsChanged(server);
        supervisor.CredentialsChanged(new ServerId(Guid.NewGuid()));

        connector.EnqueueConnectFailure("weg (Test)");
        connector.Connections[0].FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.IsWaitingForNextAttempt(server),
            step: TimeSpan.FromSeconds(5), timeoutMs: 15000,
            because: "der Aufbau nach dem Verlust scheitert und wartet seine Frist ab");
        var calls = connector.ConnectCalls;
        await Task.Delay(200, Ct);
        connector.ConnectCalls.Should().Be(calls, "der alte Anstoß weckt den Loop nicht vorzeitig");
        supervisor.GetStatus(server)!.Reconnect!.NextAttemptAt.Should().NotBeNull();
    }

    /// <summary>C6: Die Frist gilt für den ganzen Aufbau — auch eine Katalogabfrage, die ihren Abbruch ignoriert, hält den Loop nicht fest.</summary>
    [Fact]
    public async Task A_discovery_that_ignores_cancellation_does_not_hold_the_loop()
    {
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new FakeUpstreamConnector();
        connector.Enqueue((id, _) => new FakeUpstreamConnection { Id = id, DiscoverHook = () => hang.Task });
        await using var supervisor = Create(connector, jitter: 0d);
        try
        {
            var server = await supervisor.AddAsync(TestData.StdioConfig("haengt"), Ct);
            await TestData.WaitUntilAsync(() => connector.ConnectCalls == 1);

            _time.Advance(TimeSpan.FromSeconds(61));
            var info = await FailedAttemptAsync(supervisor, server, 1);
            supervisor.GetStatus(server)!.LastError.Should().Contain("Frist");

            // Kommt die Abfrage doch noch zurück, wird die späte Verbindung geschlossen, nicht veröffentlicht.
            hang.SetResult();
            await TestData.WaitUntilAsync(() => connector.Connections[0].DisposeCount == 1);
            supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Failed);
            info.Kind.Should().Be(UpstreamFailureKind.Unreachable);
        }
        finally
        {
            // Sonst wartete das Aufräumen auf die hängende Abfrage (gegen einen Stand ohne die Frist).
            hang.TrySetResult();
        }
    }

    /// <summary>C11: Wer die frisch veröffentlichte Verbindung sofort wieder verliert, schließt sich nicht dem alten Neuaufbau an.</summary>
    [Fact]
    public async Task A_loss_on_the_just_published_connection_gets_its_own_rebuild()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("gleich-wieder"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        Task<JsonElement>? second = null;
        _supervisor.Changed += (_, e) =>
        {
            // Mitten im Veröffentlichen der zweiten Verbindung: Sie ist schon die aktuelle, der alte
            // Neuaufbau aber noch nicht abgeschlossen.
            if (e.Server == server && e.State == UpstreamState.Healthy
                && _connector.Connections.Count == 2 && second is null)
            {
                _connector.Connections[1].CallException = new UpstreamSessionLostException("404 (Test)");
                second = _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);
            }
        };

        // Der erste Verlust kommt über das Lebenszeichen — so hat nur der zweite Aufrufer einen Versuch.
        _connector.Connections[0].PingException = new UpstreamSessionLostException("404 (Test)");
        await TestData.WaitUntilAdvancingAsync(
            _time, () => second is not null, step: TimeSpan.FromSeconds(1), because: "die zweite Verbindung wird veröffentlicht");
        await second!.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        _connector.Connections.Should().HaveCount(3, "der Verlust der zweiten Verbindung bekam einen eigenen Neuaufbau");
        _connector.Connections[2].CallCount.Should().Be(1);
    }

    /// <summary>C4: Ein abgebrochenes Abschalten wird zu Ende geführt, sobald es gespeichert ist.</summary>
    [Fact]
    public async Task A_cancelled_disable_still_stops_the_upstream()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("abschalten"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var connection = _connector.Connections[0];
        connection.CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        using var cts = new CancellationTokenSource();

        var disable = _supervisor.SetEnabledAsync(server, false, cts.Token);
        await TestData.WaitUntilAsync(() => !LatestEnabledAsync(server).GetAwaiter().GetResult());
        await cts.CancelAsync();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => disable.IsCompleted, step: TimeSpan.FromSeconds(1), because: "der Drain endet mit der Gnadenfrist");

        await disable.Invoking(t => t).Should().NotThrowAsync("ab dem Speichern ist das Abschalten beschlossen");
        _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
        await TestData.WaitUntilAsync(() => connection.DisposeCount == 1, because: "die Verbindung ist geschlossen");
        connection.CallGate.TrySetResult(TestData.EmptySchema());
        await Task.WhenAny(call);
    }

    /// <summary>C4: Scheitert das Speichern einer neuen Konfiguration, läuft der alte Stand weiter.</summary>
    [Fact]
    public async Task A_failed_reconfiguration_keeps_the_old_loop()
    {
        var store = new FailingStore(_store);
        var supervisor = new UpstreamSupervisor(
            [_connector], store, new SupervisorOptions { HealthCheckInterval = TimeSpan.FromSeconds(1) }, _time,
            hostExecution: HostExecutionPolicy.AllowedByOperator());
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(TestData.StdioConfig("umbau"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        store.FailAppend = true;

        var act = () => supervisor.ReconfigureAsync(server, TestData.StdioConfig("umbau") with { DisplayName = "Neu" }, Ct);

        await act.Should().ThrowAsync<IOException>();
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy, "der alte Loop läuft weiter");
        supervisor.GetConnection(server).Should().NotBeNull();
        _connector.Connections[0].DisposeCount.Should().Be(0);
    }

    /// <summary>C5: Eine Anfrage, die hinter dem Entfernen gewartet hat, holt den Upstream nicht zurück.</summary>
    [Fact]
    public async Task A_request_queued_behind_removal_does_not_resurrect_the_upstream()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("weg"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var connection = _connector.Connections[0];
        connection.CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        var remove = _supervisor.RemoveAsync(server, DrainPolicy.Graceful(TimeSpan.FromMinutes(1)), Ct);
        await Task.Delay(50, Ct);
        var reconfigure = _supervisor.ReconfigureAsync(server, TestData.StdioConfig("weg") with { DisplayName = "Zurück" }, Ct);
        var enable = _supervisor.SetEnabledAsync(server, false, Ct);
        await Task.Delay(50, Ct);
        connection.CallGate.SetResult(TestData.EmptySchema());
        await call;
        // Der Drain prüft in Testzeit, ob es leer ist.
        await TestData.WaitUntilAdvancingAsync(
            _time, () => remove.IsCompleted, step: TimeSpan.FromMilliseconds(50), because: "das Entfernen endet nach dem Drain");
        await remove;

        await reconfigure.Invoking(t => t).Should().ThrowAsync<KeyNotFoundException>();
        await enable.Invoking(t => t).Should().ThrowAsync<KeyNotFoundException>();
        (await _store.GetAllLatestAsync(Ct)).Should().NotContainKey(server, "nach einem Neustart stünde er sonst wieder da");
        _connector.ConnectCalls.Should().Be(1, "kein Loop auf dem entfernten Eintrag");
    }

    /// <summary>C15: Ist die Warteschlange voll, hat das Audit inzwischen aber Platz, geht keine Zeile verloren.</summary>
    [Fact]
    public async Task A_full_deferral_queue_offers_before_it_drops()
    {
        var full = true;
        var audit = new RefusingAuditSink
        {
            Refuses = e => Volatile.Read(ref full) && e.Detail?.StartsWith("platz:", StringComparison.Ordinal) == true,
        };
        var calls = 0;
        var connector = new FakeUpstreamConnector
        {
            DefaultBehavior = (_, _) => Interlocked.Increment(ref calls) % 2 == 0
                ? throw new HttpRequestException("401", null, HttpStatusCode.Unauthorized)
                : throw new IOException("weg (Test)"),
        };
        await using var supervisor = Create(connector, jitter: 0d, audit);
        var server = await supervisor.AddAsync(TestData.StdioConfig("platz"), Ct);

        // Eintritt in Failed und 31 Wechsel: 32 Zeilen, die Warteschlange ist voll.
        await FailedAttemptAsync(supervisor, server, 1);
        for (var attempt = 2; attempt <= 32; attempt++)
        {
            await ConnectNowAsync(supervisor, server);
            await FailedAttemptAsync(supervisor, server, attempt);
        }

        audit.Discarded.Should().Be(0);

        // Während des nächsten Versuchs bekommt das Audit wieder Platz — nach dem Nachreichen zu
        // Beginn des Versuchs, vor dessen eigener Zeile.
        connector.ConnectHook = _ =>
        {
            Volatile.Write(ref full, false);
            return Task.CompletedTask;
        };
        await ConnectNowAsync(supervisor, server);
        await FailedAttemptAsync(supervisor, server, 33);

        audit.Discarded.Should().Be(0, "erst nachreichen, dann verdrängen");
        var accepted = audit.Accepted.Where(e => e.Detail!.StartsWith("platz:", StringComparison.Ordinal))
            .Select(e => e.Detail!).ToList();
        accepted.Should().HaveCount(UpstreamSupervisor.MaxDeferredLifecycleLines + 1, string.Join(" | ", accepted));
    }

    private sealed class FailingStore(IUpstreamConfigStore inner) : IUpstreamConfigStore
    {
        public volatile bool FailAppend;

        /// <summary>Wenn gesetzt, wartet das Speichern darauf — ein langsames Speichern (E8).</summary>
        public TaskCompletionSource? AppendGate { get; set; }

        public TaskCompletionSource AppendReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ConfigVersionId> AppendVersionAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
        {
            if (FailAppend)
            {
                throw new IOException("Speicher voll (Test).");
            }

            if (AppendGate is { } gate)
            {
                AppendReached.TrySetResult();
                await gate.Task;
            }

            return await inner.AppendVersionAsync(id, config, ct);
        }

        public Task<UpstreamServerConfig?> GetVersionAsync(ServerId id, ConfigVersionId version, CancellationToken ct)
            => inner.GetVersionAsync(id, version, ct);

        public Task<IReadOnlyList<UpstreamConfigVersion>> GetHistoryAsync(ServerId id, CancellationToken ct)
            => inner.GetHistoryAsync(id, ct);

        public Task<IReadOnlyDictionary<ServerId, UpstreamConfigVersion>> GetAllLatestAsync(CancellationToken ct)
            => inner.GetAllLatestAsync(ct);

        public volatile bool FailRemove;

        public Task RemoveAsync(ServerId id, CancellationToken ct)
            => FailRemove ? throw new IOException("Datenbank weg (Test).") : inner.RemoveAsync(id, ct);
    }

    /// <summary>D12: Scheitert das Löschen, läuft der Upstream unverändert weiter.</summary>
    [Fact]
    public async Task A_failed_removal_keeps_the_upstream_running()
    {
        var store = new FailingStore(_store);
        var supervisor = new UpstreamSupervisor(
            [_connector], store, new SupervisorOptions { HealthCheckInterval = TimeSpan.FromSeconds(1) }, _time,
            hostExecution: HostExecutionPolicy.AllowedByOperator());
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(TestData.StdioConfig("bleibt"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        store.FailRemove = true;

        var act = () => supervisor.RemoveAsync(server, DrainPolicy.Immediate, Ct);

        await act.Should().ThrowAsync<IOException>();
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Healthy, "nichts ist gestoppt");
        supervisor.GetConnection(server).Should().NotBeNull();
        _connector.Connections[0].DisposeCount.Should().Be(0);
        (await _store.GetAllLatestAsync(Ct)).Should().ContainKey(server);
    }

    /// <summary>D12: Ist gelöscht, wird auch zu Ende gestoppt — selbst wenn der Aufrufer währenddessen abbricht.</summary>
    [Fact]
    public async Task A_removal_finishes_after_the_caller_gives_up()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("abbruch"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var connection = _connector.Connections[0];
        connection.CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = _supervisor.GetConnection(server)!.CallToolAsync("echo", TestData.EmptySchema(), Ct);

        using var caller = new CancellationTokenSource();
        var remove = _supervisor.RemoveAsync(server, DrainPolicy.Graceful(TimeSpan.FromMinutes(1)), caller.Token);
        await TestData.WaitUntilAsync(
            () => !_store.GetAllLatestAsync(Ct).GetAwaiter().GetResult().ContainsKey(server),
            because: "das Löschen kommt vor dem Drain");
        await caller.CancelAsync();
        connection.CallGate.SetResult(TestData.EmptySchema());
        await call;
        await TestData.WaitUntilAdvancingAsync(
            _time, () => remove.IsCompleted, step: TimeSpan.FromMilliseconds(50), because: "das Entfernen endet nach dem Drain");

        await remove;
        _supervisor.GetStatus(server).Should().BeNull();
        connection.DisposeCount.Should().Be(1, "die Verbindung ist geschlossen, nicht stehen gelassen");
    }

    /// <summary>
    /// E10: Ein interner Abbruch auf einer Verbindung ohne Sitzung ist kein Sitzungsverlust — der
    /// Aufruf wird nicht wiederholt, aber die Verbindung bleibt benutzbar, ohne Neuaufbau.
    /// </summary>
    [Fact]
    public async Task An_internal_cancellation_does_not_force_a_rebuild()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("abbruch-intern"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var connection = _supervisor.GetConnection(server)!;
        _connector.Connections[0].CallException = new OperationCanceledException("intern (Test)");

        var first = () => connection.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        await first.Should().ThrowAsync<UpstreamConnectionLostException>();

        _connector.Connections[0].CallException = null;
        await connection.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        _connector.ConnectCalls.Should().Be(1, "kein Neuaufbau");
        _connector.Connections[0].CallCount.Should().Be(2);
    }

    /// <summary>E3: Ein Lebenszeichen, dessen Fehler das vorgelegte Token zitiert, zeigt es nirgends.</summary>
    [Fact]
    public async Task A_ping_error_does_not_show_the_presented_token()
    {
        var token = "at-" + Guid.NewGuid().ToString("N");
        _connector.DefaultBehavior = (id, _) => new FakeUpstreamConnection { Id = id, PresentedCredentials = [token] };
        var server = await _supervisor.AddAsync(TestData.StdioConfig("zitiert"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        _connector.Connections[0].PingException = new IOException($"{{\"error\":{{\"message\":\"Bearer {token} abgelehnt\"}}}}");
        await TestData.WaitUntilAdvancingAsync(
            _time, () => _supervisor.GetStatus(server)?.Reconnect is { NextAttemptAt: not null },
            step: TimeSpan.FromSeconds(1), because: "zwei gescheiterte Lebenszeichen beenden die Verbindung");

        _supervisor.GetStatus(server)!.LastError.Should().NotContain(token);
        Lifecycle.Should().NotContain(l => l.Contains(token, StringComparison.Ordinal));
        _log.Entries.Should().NotContain(e => e.Message.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// E6: Zeit in Degraded zählt nicht als gesund. Gestört bei etwa 50 s, noch gestört, 401 nach
    /// mehr als 60 s: keine ununterbrochene gesunde Minute seit der Erneuerung — also keine zweite.
    /// </summary>
    [Fact]
    public async Task Time_spent_degraded_does_not_confirm_a_renewal()
    {
        var (connector, supervisor) = CreateOAuth();
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(OAuthConfig("gestoert"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        connector.Connections[0].PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.Connections.Count == 2 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(1), because: "erneuert und neu verbunden");
        connector.CredentialRenewals.Should().Be(1);
        var renewedAt = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromSeconds(50));
        await Task.Delay(50, Ct);
        connector.Connections[1].FailPing = true;
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.State == UpstreamState.Degraded,
            step: TimeSpan.FromMilliseconds(500), because: "gestört");

        // Noch gestört, und die Minute seit der Erneuerung ist vorbei: Das nächste Lebenszeichen
        // bringt das 401. Die gestörte Zeit darf nicht als gesunde Minute zählen.
        connector.Connections[1].PingException = Unauthorized();
        _time.Advance(renewedAt + TimeSpan.FromSeconds(62) - _time.GetUtcNow());
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.Reconnect is { NextAttemptAt: not null },
            step: TimeSpan.FromSeconds(1), because: "die neue Verbindung wird abgewiesen");

        connector.CredentialRenewals.Should().Be(1, "seit der Störung keine gesunde Minute");
        supervisor.GetStatus(server)!.Reconnect!.Kind.Should().Be(UpstreamFailureKind.Authentication);
    }

    /// <summary>
    /// D8: Eine Erneuerung gilt erst nach einer gesunden Minute als bestätigt — ein gewöhnlicher
    /// Fehlversuch dazwischen setzt das nicht zurück. Wird die neue Verbindung binnen der Minute
    /// wieder abgewiesen, wird nicht ein zweites Mal erneuert.
    /// </summary>
    [Fact]
    public async Task An_ordinary_retry_does_not_confirm_a_renewal()
    {
        var (connector, supervisor) = CreateOAuth();
        await using var _ = supervisor;
        var server = await supervisor.AddAsync(OAuthConfig("unbestaetigt"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        // Vier gewöhnliche Fehlversuche (5 + 10 + 20 + 40 s): Seit der Erneuerung vergeht mehr als
        // eine Minute — gesund war die neue Verbindung in dieser Zeit aber nie.
        for (var i = 0; i < 4; i++)
        {
            connector.EnqueueConnectFailure("kurz weg (Test)");
        }

        connector.Connections[0].PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => connector.Connections.Count == 2 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
            step: TimeSpan.FromSeconds(1), timeoutMs: 20000,
            because: "nach der Erneuerung und einem gewöhnlichen Fehlversuch steht die neue Verbindung");
        connector.CredentialRenewals.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(20));
        connector.Connections[1].PingException = Unauthorized();
        await TestData.WaitUntilAdvancingAsync(
            _time, () => supervisor.GetStatus(server)?.Reconnect is { NextAttemptAt: not null },
            step: TimeSpan.FromSeconds(1), because: "die neue Verbindung wird abgewiesen");

        connector.CredentialRenewals.Should().Be(1, "die Erneuerung ist unbestätigt, eine zweite folgt nicht");
        supervisor.GetStatus(server)!.Reconnect!.Kind.Should().Be(UpstreamFailureKind.Authentication);
    }

    /// <summary>
    /// D4: Ein Loop, der seinen Abbruch ignoriert (hier ein hängendes Lebenszeichen), hält das
    /// Abschalten nicht fest — es endet nach <see cref="UpstreamSupervisor.StopJoinTimeout"/>. Kommt
    /// das Lebenszeichen danach doch zurück, wird nichts mehr veröffentlicht.
    /// </summary>
    [Fact]
    public async Task A_loop_that_ignores_its_cancellation_does_not_hold_the_stop()
    {
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("stur"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        try
        {
            connector.Connections[0].PingHang = hang.Task;
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(50, Ct);

            var disable = supervisor.SetEnabledAsync(server, false, Ct);
            await TestData.WaitUntilAdvancingAsync(
                _time, () => disable.IsCompleted, step: TimeSpan.FromSeconds(1), timeoutMs: 15000,
                because: "das Abschalten wartet höchstens die Frist auf den Loop");
            await disable;
            supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
            _log.Entries.Should().Contain(e => e.Message.Contains("beachtet ihren Abbruch nicht", StringComparison.Ordinal));
        }
        finally
        {
            hang.TrySetResult();
        }

        await Task.Delay(100, Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100, Ct);
        supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped, "der späte Loop veröffentlicht nichts");
        connector.ConnectCalls.Should().Be(1, "und baut nichts mehr auf");
    }

    /// <summary>
    /// E1: Nach dem begrenzten Warten aus D4 läuft ein Nachfolger. Scheitert danach das hängende
    /// Lebenszeichen des alten Loops, bucht dieser nichts mehr — die neue Verbindung bleibt stehen.
    /// </summary>
    [Fact]
    public async Task A_retired_loop_does_not_touch_its_successor()
    {
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new FakeUpstreamConnector();
        await using var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("abgeloest"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        try
        {
            connector.Connections[0].PingHang = hang.Task;
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(50, Ct);

            var reconfigure = supervisor.ReconfigureAsync(
                server, TestData.StdioConfig("abgeloest") with { DisplayName = "Neu" }, Ct);
            await TestData.WaitUntilAdvancingAsync(
                _time, () => reconfigure.IsCompleted, step: TimeSpan.FromSeconds(1), timeoutMs: 15000,
                because: "das Umkonfigurieren wartet höchstens die Frist auf den alten Loop");
            await reconfigure;
            await TestData.WaitUntilAsync(
                () => connector.Connections.Count == 2 && supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
                because: "der Nachfolger steht");

            // Das alte Lebenszeichen scheitert jetzt — zweimal hintereinander wäre „verloren".
            hang.SetException(new IOException("spät gescheitert (Test)"));
            await Task.Delay(200, Ct);
        }
        finally
        {
            hang.TrySetResult();
        }

        var status = supervisor.GetStatus(server)!;
        status.State.Should().Be(UpstreamState.Healthy, "der alte Loop setzt keinen Zustand des neuen");
        status.Reconnect.Should().BeNull();
        connector.Connections[1].DisposeCount.Should().Be(0, "die neue Verbindung bleibt offen");
        supervisor.GetConnection(server).Should().NotBeNull();
        Lifecycle.Should().NotContain(l => l.Contains("Degraded", StringComparison.Ordinal)
            || l.Contains("Failed", StringComparison.Ordinal));
    }

    /// <summary>D12: Nach dem Beginn des Herunterfahrens startet kein Loop mehr.</summary>
    [Fact]
    public async Task Nothing_starts_after_shutdown_began()
    {
        var connector = new FakeUpstreamConnector();
        var supervisor = Create(connector, jitter: 0d);
        var server = await supervisor.AddAsync(TestData.StdioConfig("zaun"), Ct);
        await TestData.WaitUntilAsync(() => supervisor.GetStatus(server)?.State == UpstreamState.Healthy);

        await supervisor.DisposeAsync();

        var add = () => supervisor.AddAsync(TestData.StdioConfig("spaet"), Ct);
        await add.Should().ThrowAsync<ObjectDisposedException>();
        var enable = () => supervisor.SetEnabledAsync(server, true, Ct);
        await enable.Should().ThrowAsync<ObjectDisposedException>("N17: ab dem Herunterfahren dieselbe Antwort");
        var connect = () => supervisor.ConnectNowAsync(server, Ct);
        await connect.Should().ThrowAsync<ObjectDisposedException>();
        connector.ConnectCalls.Should().Be(1);
        supervisor.Statuses.Should().BeEmpty();
    }

    /// <summary>
    /// E4: Der Drain wartet auf eine laufende Ressourcen-Anfrage, nimmt aber keine neue Anfrage mehr
    /// an — die verlässt das Gateway nicht.
    /// </summary>
    [Fact]
    public async Task The_drain_waits_for_running_work_and_admits_none()
    {
        var server = await _supervisor.AddAsync(TestData.StdioConfig("drain"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var connection = _supervisor.GetConnection(server)!;
        var fake = _connector.Connections[0];
        var resourceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ResourceGate = resourceGate.Task;
        var resource = connection.ReadResourceAsync(new Uri("file:///x"), Ct);

        var disable = _supervisor.SetEnabledAsync(server, false, Ct);
        await Task.Delay(50, Ct);

        var late = () => connection.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        await late.Should().ThrowAsync<UpstreamConnectionLostException>("während des Drains wird nichts Neues angenommen");
        fake.CallCount.Should().Be(0, "der späte Aufruf hat das Gateway nicht verlassen");
        disable.IsCompleted.Should().BeFalse("die Ressourcen-Anfrage läuft noch und zählt");

        resourceGate.SetResult();
        await resource;
        await TestData.WaitUntilAdvancingAsync(
            _time, () => disable.IsCompleted, step: TimeSpan.FromMilliseconds(50), because: "danach endet der Drain");
        await disable;
    }

    /// <summary>
    /// E14: Wer eine ersetzte Hülle hält, wird nicht gegen den Katalog der NEUEN Verbindung geprüft
    /// und dann über die ALTE geschickt. Der Aufruf läuft über die Verbindung, deren Katalog geprüft ist.
    /// </summary>
    [Fact]
    public async Task A_stale_handle_is_checked_against_the_connection_that_runs_the_call()
    {
        _connector.Enqueue((id, _) => new FakeUpstreamConnection { Id = id, Inventory = TestData.InventoryWithTools("echo") });
        _connector.Enqueue((id, _) => new FakeUpstreamConnection { Id = id, Inventory = TestData.InventoryWithTools("echo", "neu") });
        var server = await _supervisor.AddAsync(TestData.StdioConfig("veraltet"), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var stale = _supervisor.GetConnection(server)!;

        await _supervisor.SetEnabledAsync(server, false, Ct);
        await _supervisor.SetEnabledAsync(server, true, Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetInventory(server)?.Tools.Count == 2);

        await stale.CallToolAsync("neu", TestData.EmptySchema(), Ct);

        _connector.Connections[0].CallCount.Should().Be(0, "die alte Verbindung hat 'neu' nie geprüft angeboten");
        _connector.Connections[1].LastToolName.Should().Be("neu");
    }

    /// <summary>E4: Ein Schließen, das hängt, hält das Abschalten nicht fest.</summary>
    [Fact]
    public async Task A_hanging_close_does_not_hold_the_stop()
    {
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var server = await _supervisor.AddAsync(TestData.StdioConfig("haengt-beim-schliessen"), Ct);
            await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
            _connector.Connections[0].DisposeHang = hang.Task;

            var disable = _supervisor.SetEnabledAsync(server, false, Ct);
            await TestData.WaitUntilAdvancingAsync(
                _time, () => disable.IsCompleted, step: TimeSpan.FromSeconds(1), timeoutMs: 15000,
                because: "das Schließen ist befristet");
            await disable;
            _supervisor.GetStatus(server)!.State.Should().Be(UpstreamState.Stopped);
            _log.Entries.Should().Contain(e => e.Message.Contains("Schließen der Verbindung", StringComparison.Ordinal));
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    /// <summary>
    /// E8: Beginnt das Herunterfahren, während ein Hinzufügen speichert, wartet es darauf. Das
    /// Hinzufügen gelingt — gespeichert und gemeldet passen zusammen —, und der Loop wird mit den
    /// übrigen gestoppt.
    /// </summary>
    [Fact]
    public async Task An_add_racing_the_shutdown_is_either_complete_or_absent()
    {
        var store = new FailingStore(_store) { AppendGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var connector = new FakeUpstreamConnector();
        var supervisor = new UpstreamSupervisor(
            [connector], store, new SupervisorOptions { HealthCheckInterval = TimeSpan.FromSeconds(1) }, _time,
            hostExecution: HostExecutionPolicy.AllowedByOperator());

        var add = supervisor.AddAsync(TestData.StdioConfig("wettlauf"), Ct);
        await store.AppendReached.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var dispose = supervisor.DisposeAsync().AsTask();
        await Task.Delay(50, Ct);
        dispose.IsCompleted.Should().BeFalse("das Herunterfahren wartet auf das laufende Hinzufügen");

        store.AppendGate!.SetResult();
        var id = await add;
        await dispose;

        (await _store.GetAllLatestAsync(Ct)).Should().ContainKey(id, "gespeichert, wie gemeldet");
        await TestData.WaitUntilAsync(
            () => connector.Connections.All(c => c.DisposeCount >= 1),
            because: "der Loop wurde mit den übrigen gestoppt");
        var late = () => supervisor.AddAsync(TestData.StdioConfig("danach"), Ct);
        await late.Should().ThrowAsync<ObjectDisposedException>();
        (await _store.GetAllLatestAsync(Ct)).Should().HaveCount(1, "das späte Hinzufügen hat nichts gespeichert");
    }

    // ── Späte Verbindungen (W17) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Connect_attempts_that_ignore_their_cancellation_are_reported_once()
    {
        // Jeder Aufbau hängt und beachtet seinen Abbruch nicht — wie ein Connector, der auf einen
        // Prozess wartet, der nie antwortet.
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.ConnectHook = _ => never.Task;
        var server = await _supervisor.AddAsync(TestData.StdioConfig("haengt"), Ct);

        await TestData.WaitUntilAdvancingAsync(
            _time, () => _connector.ConnectCalls >= 6 && _supervisor.IsWaitingForNextAttempt(server),
            step: TimeSpan.FromSeconds(30), timeoutMs: 20000,
            because: "jeder Versuch endet an seiner Frist, und der nächste folgt");

        _log.Entries.Count(e => e.Message.Contains("Verbindungsaufbauten hängen", StringComparison.Ordinal))
            .Should().Be(1, "gemeldet wird beim Erreichen der Schwelle, nicht bei jedem weiteren (U7)");

        never.SetResult();
        await TestData.WaitUntilAsync(
            () => _connector.Connections.Count(c => c.DisposeCount == 1) >= 6,
            because: "was spät doch noch kommt, wird geschlossen");
    }

    [Fact]
    public void A_delay_never_exceeds_what_a_timer_accepts()
    {
        // Der Validator nimmt jede MaxBackoff ≥ InitialBackoff an. Oberhalb von rund 49,7 Tagen
        // wirft Task.Delay — und der Loop wäre an einer gültigen Konfiguration gestorben.
        var absurd = new RestartPolicy(
            MaxRetries: null, TimeSpan.FromDays(60), BackoffMultiplier: 2.0, MaxBackoff: TimeSpan.FromDays(400));

        foreach (var attempt in new[] { 1, 2, 40 })
        {
            var delay = _supervisor.ComputeDelay(absurd, UpstreamFailureKind.Unreachable, attempt);
            delay.Should().Be(UpstreamSupervisor.LongestWait);

            // Der Timer nimmt die Frist an. Eine Frist außerhalb seines Bereichs würfe schon hier,
            // beim Anlegen, und nicht erst beim Warten.
            using var cts = new CancellationTokenSource();
            var pending = Task.Delay(delay, _time, cts.Token);
            pending.IsFaulted.Should().BeFalse();
            cts.Cancel();
        }
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
            _time, () => _supervisor.GetStatus(server)?.Reconnect?.Exhausted == true && _supervisor.IsWaitingForNextAttempt(server),
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
    public async Task A_session_lost_on_the_health_ping_makes_new_calls_wait_for_the_rebuild()
    {
        // Nicht nur ein Aufruf bemerkt die verlorene Sitzung — oft ist es das Lebenszeichen. Der
        // Client ist danach genauso tot. Ein Aufruf, der während des Neuaufbaus ankommt, hat das
        // Gateway noch nicht verlassen: Er wartet und läuft auf der neuen Verbindung (Vertrag
        // Abschnitt 4, „nach dem 404 begonnen"), statt in die tote zu laufen.
        var server = await _supervisor.AddAsync(TestData.StdioConfig(), Ct);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(server)?.State == UpstreamState.Healthy);
        var first = _connector.Connections[0];
        var stale = _supervisor.GetConnection(server)!;

        // So verhält sich der tote SDK-Client: Jeder weitere Aufruf endet sofort im Abbruch.
        first.CallException = new TaskCanceledException("toter Client (Test)");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector.ConnectHook = _ => release.Task;
        first.PingException = new UpstreamSessionLostException("404 auf die Sitzung (Test)");

        await TestData.WaitUntilAdvancingAsync(
            _time, () => _connector.ConnectCalls == 2,
            step: TimeSpan.FromMilliseconds(500), timeoutMs: 15000,
            because: "das Lebenszeichen bemerkt den Verlust und stößt den Neuaufbau an");

        var call = stale.CallToolAsync("echo", TestData.EmptySchema(), Ct);
        await TestData.StaysFalseAsync(
            () => call.IsCompleted, because: "der Aufruf wartet auf den Neuaufbau, statt zu scheitern");

        release.SetResult();
        var result = await call;

        result.GetProperty("tool").GetString().Should().Be("echo");
        first.CallCount.Should().Be(0, "über die tote Verbindung ging nichts mehr hinaus");
        _connector.Connections[1].CallCount.Should().Be(1);
        _connector.ConnectCalls.Should().Be(2, "ein Neuaufbau, kein zweiter");
    }

    [Fact]
    public async Task Any_other_failure_in_the_middle_of_a_call_is_not_repeated()
    {
        _connector.DefaultBehavior = static (id, _) => new FakeUpstreamConnection
        {
            Id = id,
            Inventory = TestData.InventoryWithTools("create_issue"),
        };
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

        public ToolAvailability CheckTool(GuardedUpstreamConnection connection, string toolName)
            => ToolAvailability.Available;
    }

    /// <summary>
    /// Ein Audit, das bestimmte Zeilen nicht annimmt und dabei wirft — wie das echte im
    /// Compliance-Modus bei vollem Kanal.
    /// </summary>
    private sealed class RefusingAuditSink : IAuditSink
    {
        private readonly List<AuditEvent> _accepted = [];
        private int _refused;

        public required Func<AuditEvent, bool> Refuses { get; init; }

        public int Refused => Volatile.Read(ref _refused);

        private int _discarded;

        /// <summary>Was der Supervisor als endgültig verloren gemeldet hat (V3).</summary>
        public int Discarded => Volatile.Read(ref _discarded);

        public void ReportDiscarded(int count) => Interlocked.Add(ref _discarded, count);

        public IReadOnlyList<AuditEvent> Accepted
        {
            get
            {
                lock (_accepted)
                {
                    return [.. _accepted];
                }
            }
        }

        public void Record(AuditEvent evt)
        {
            if (Refuses(evt))
            {
                Interlocked.Increment(ref _refused);
                throw new InvalidOperationException("Audit-Kanal voll (Test).");
            }

            lock (_accepted)
            {
                _accepted.Add(evt);
            }
        }
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

        /// <summary>
        /// Lässt das Schreiben bestimmter Zeilen scheitern — der Hebel, mit dem ein Test die
        /// Buchführung eines Fehlversuchs mitten im Gang brechen lässt.
        /// </summary>
        public Func<string, bool>? Throws { get; set; }

        private int _thrown;

        /// <summary>Wie oft <see cref="Throws"/> zugeschlagen hat — damit ein Test weiß, dass sein Fall eintrat.</summary>
        public int Thrown => Volatile.Read(ref _thrown);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (Throws?.Invoke(message) == true)
            {
                Interlocked.Increment(ref _thrown);
                throw new InvalidOperationException("Logger gestört (Test).");
            }

            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, message, exception));
            }
        }
    }
}
