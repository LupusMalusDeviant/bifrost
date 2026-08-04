using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Approvals;
using Bifrost.Core.Webhooks;
using Bifrost.Security.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Approvals;

/// <summary>
/// WP6.3: Benachrichtigungen über wartende Freigaben.
/// <para>
/// Der wichtigste Test dieser Datei ist nicht die Zustellung, sondern
/// <see cref="A_dead_target_does_not_block_the_approval_queue"/>: <b>Eine Freigabe gilt, wenn sie
/// entschieden wurde — nicht, wenn eine Benachrichtigung ankam.</b>
/// </para>
/// </summary>
public sealed class ApprovalNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    private static ApprovalNotificationTarget Target(string name = "chat") =>
        new(name, new Uri("https://chat.example.invalid/hook"), "geheim-fuer-das-ziel");

    [Fact]
    public async Task A_pending_approval_is_delivered_signed_and_verifiable()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var dispatcher = Dispatcher(handler);

        dispatcher.Notify(Request());
        await handler.WaitForAsync(1);

        var (request, body) = handler.Calls.Single();

        // Geprueft wird mit demselben Verifizierer, den der Gateway fuer EINGEHENDE Webhooks
        // benutzt — sonst waere "signiert" eine Behauptung ueber einen eigenen Mechanismus.
        WebhookSignature.Verify(
                "geheim-fuer-das-ziel",
                request.Headers.GetValues(WebhookSignature.SignatureHeader).Single(),
                request.Headers.GetValues(WebhookSignature.TimestampHeader).Single(),
                body,
                Now,
                TimeSpan.FromMinutes(5))
            .Should().Be(WebhookVerification.Valid);
    }

    /// <summary>
    /// Der Replay-Schutz hängt am Zeitstempel <b>in</b> der Signatur. Ein Empfänger, der ein
    /// Zeitfenster prüft, muss einen mitgeschnittenen Ruf später abweisen können.
    /// </summary>
    [Fact]
    public async Task A_replayed_notification_falls_outside_the_window()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var dispatcher = Dispatcher(handler);

        dispatcher.Notify(Request());
        await handler.WaitForAsync(1);
        var (request, body) = handler.Calls.Single();

        WebhookSignature.Verify(
                "geheim-fuer-das-ziel",
                request.Headers.GetValues(WebhookSignature.SignatureHeader).Single(),
                request.Headers.GetValues(WebhookSignature.TimestampHeader).Single(),
                body,
                Now.AddHours(1),
                TimeSpan.FromMinutes(5))
            .Should().Be(WebhookVerification.Stale, "eine Stunde später ist derselbe Ruf ein Replay");
    }

    /// <summary>
    /// <b>Die Nutzlast trägt keine Argumente</b> — auch keine redigierten. Sie geht an ein fremdes
    /// System, oft in einen Chat; was dort steht, steht dauerhaft dort.
    /// </summary>
    [Fact]
    public async Task The_payload_carries_a_deep_link_and_no_arguments()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var dispatcher = Dispatcher(handler);
        var request = Request();

        dispatcher.Notify(request);
        await handler.WaitForAsync(1);

        var body = handler.Calls.Single().Body;
        body.Should().NotContain(SecretCorpus.Marker, "ein Argumentwert im Chat ist ein Leck");
        body.ToLowerInvariant().Should().NotContain("argument");
        body.Should().Contain(request.Tool.Value).And.Contain(request.Id.ToString());
        body.Should().Contain("/approvals#" + request.Id, "ohne Deep-Link muss man die Anfrage suchen");
    }

    /// <summary>
    /// <b>Die DoD dieses Pakets.</b> Das Ziel antwortet nie — die Freigabe entsteht trotzdem, und
    /// zwar sofort. Ohne diesen Fall wäre „Zustellung getrennt von Entscheidung" eine Absicht.
    /// </summary>
    [Fact]
    public async Task A_dead_target_does_not_block_the_approval_queue()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("Ziel tot"));
        await using var dispatcher = Dispatcher(handler);
        var inner = new RecordingStore();
        var store = new NotifyingApprovalStore(inner, dispatcher);

        var enqueue = store.EnqueueAsync(Request(), TestContext.Current.CancellationToken);

        // Kein Warten, kein Timeout: Die Anlage ist fertig, bevor irgendetwas zugestellt wurde.
        (await enqueue.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken))
            .Should().NotBe(Guid.Empty);
        inner.Enqueued.Should().Be(1);
    }

    [Fact]
    public async Task A_failing_target_is_retried_and_then_dead_lettered_visibly()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await using var dispatcher = Dispatcher(handler, o => o with
        {
            MaxAttempts = 3,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            BackoffMultiplier = 1,
        });

        dispatcher.Notify(Request());

        // Der Backoff wartet auf der ÜBERGEBENEN Uhr — im Produktivbetrieb die echte, hier eine
        // gestellte. Das ist der Grund, warum dieser Test nicht Sekunden schlafen muss; er muss
        // die Zeit dafuer aber selbst vorstellen. (Beim ersten Lauf tat er das nicht und sah aus,
        // als wuerde gar nicht wiederholt.)
        using var pump = new CancellationTokenSource();
        var advancing = Task.Run(async () =>
        {
            while (!pump.IsCancellationRequested)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(5, CancellationToken.None);
            }
        });

        await handler.WaitForAsync(3);
        await WaitUntilAsync(() => dispatcher.Status.Any(s => s.State == ApprovalNotificationState.DeadLettered));
        await pump.CancelAsync();
        await advancing;

        var status = dispatcher.Status.Single();
        status.Attempts.Should().Be(3);
        status.LastError.Should().Contain("500");
        handler.Calls.Should().HaveCount(3, "nach dem letzten Versuch wird nicht weiter geklopft");
    }

    /// <summary>
    /// Eine abgelaufene Anfrage wird nicht zugestellt: Wer sie liest, kann nichts mehr entscheiden.
    /// Sie verschwindet aber auch nicht — der Vorgang steht mit Grund da.
    /// </summary>
    [Fact]
    public async Task An_expired_request_is_not_delivered_but_stays_visible()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var dispatcher = Dispatcher(handler);

        dispatcher.Notify(Request() with { ExpiresAt = Now.AddMinutes(-1) });
        await WaitUntilAsync(() => dispatcher.Status.Any(s => s.State == ApprovalNotificationState.Expired));

        handler.Calls.Should().BeEmpty();
        dispatcher.Status.Single().LastError.Should().Contain("abgelaufen");
    }

    /// <summary>
    /// Der Mantel unterrichtet beim Anlegen — und <b>nicht</b> beim Entscheiden. Eine Entscheidung
    /// ist der Zweck der Warteschlange, kein Anlass für eine Benachrichtigung.
    /// </summary>
    [Fact]
    public async Task Deciding_does_not_notify()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var dispatcher = Dispatcher(handler);
        var store = new NotifyingApprovalStore(new RecordingStore(), dispatcher);

        await store.DecideAsync(Guid.NewGuid(), approved: true, TestContext.Current.CancellationToken);

        dispatcher.Status.Should().BeEmpty();
        handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_target_gets_nothing()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var dispatcher = Dispatcher(handler, o => o with
        {
            Targets = [Target() with { Enabled = false }],
        });

        dispatcher.Notify(Request());
        await Task.Delay(50, TestContext.Current.CancellationToken);

        handler.Calls.Should().BeEmpty();
        dispatcher.Status.Should().BeEmpty();
    }

    private ApprovalNotificationDispatcher Dispatcher(
        RecordingHandler handler, Func<ApprovalNotificationOptions, ApprovalNotificationOptions>? tune = null)
    {
        var options = new ApprovalNotificationOptions
        {
            Targets = [Target()],
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            PublicBaseUrl = "https://gateway.example.invalid",
        };

        return new ApprovalNotificationDispatcher(
            tune?.Invoke(options) ?? options, new HttpMessageInvoker(handler), _time);
    }

    private static ApprovalRequest Request() => new(
        Guid.NewGuid(),
        IdentityId.New(),
        "agent-x",
        NamespacedToolName.Create("github", "create_issue"),
        "fingerprint",
        JsonSerializer.Deserialize<JsonElement>($$"""{"token":"{{SecretCorpus.ToolArgument}}"}"""),
        ApprovalState.Pending,
        Now,
        Now.AddMinutes(30));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Bedingung nicht erreicht.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<(HttpRequestMessage Request, string Body)> _calls = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<(HttpRequestMessage Request, string Body)> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public async Task WaitForAsync(int count, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Calls.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Nur {Calls.Count} von {count} Zustellungen.");
                }

                await Task.Delay(10);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_gate)
            {
                _calls.Add((request, body));
            }

            return respond(request);
        }
    }

    private sealed class RecordingStore : IApprovalStore
    {
        public int Enqueued { get; private set; }

        public Task<Guid> EnqueueAsync(ApprovalRequest request, CancellationToken ct)
        {
            Enqueued++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<Guid?> TryConsumeApprovalAsync(
            IdentityId caller, NamespacedToolName tool, string argumentFingerprint, CancellationToken ct)
            => Task.FromResult<Guid?>(null);

        public Task CompleteAsync(Guid taskId, TaskFailure? failure, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ApprovalRequest>>([]);

        public Task DecideAsync(Guid requestId, bool approved, CancellationToken ct) => Task.CompletedTask;
    }
}
