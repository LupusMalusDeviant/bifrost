using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Abstractions;
using Bifrost.Core.Webhooks;
using Microsoft.Extensions.Logging;

namespace Bifrost.Core.Approvals;

public sealed record ApprovalNotificationOptions
{
    public IReadOnlyList<ApprovalNotificationTarget> Targets { get; init; } = [];

    /// <summary>Wie oft ein Ziel versucht wird, bevor der Vorgang als tot dasteht.</summary>
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public double BackoffMultiplier { get; init; } = 3;

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Wie viele Benachrichtigungen höchstens warten. Voll heißt: Die älteste wird verworfen und
    /// als solche gezählt — eine unbegrenzte Warteschlange wäre auf einer Instanz mit totem Ziel
    /// der nächste Ausfall.
    /// </summary>
    public int QueueCapacity { get; init; } = 500;

    /// <summary>Wie viele erledigte Vorgänge in der Statusliste stehen bleiben.</summary>
    public int StatusHistory { get; init; } = 200;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Öffentliche Adresse für den Deep-Link. Fehlt sie, geht ein relativer Pfad mit — besser als
    /// eine geratene Adresse, die ins Leere zeigt.
    /// </summary>
    public string? PublicBaseUrl { get; init; }
}

/// <summary>
/// Stellt Benachrichtigungen über wartende Freigaben zu (M6/WP6.3).
/// <para>
/// <b>Drei Sätze tragen dieses Stück Code.</b> Erstens: Zustellung ist von Entscheidung getrennt —
/// <see cref="Notify"/> schreibt in eine Warteschlange und kehrt zurück, komme was wolle. Zweitens:
/// Die Nutzlast trägt keine Toolargumente, sondern einen Deep-Link; wer entscheiden darf, holt den
/// Rest hinter der Anmeldung. Drittens: Ein totes Ziel wird sichtbar — nach dem letzten Versuch
/// steht der Vorgang als <see cref="ApprovalNotificationState.DeadLettered"/> da.
/// </para>
/// <para>
/// Signiert wird mit <see cref="WebhookSignature"/> — <b>demselben</b> Verfahren, mit dem der
/// Gateway eingehende Webhooks prüft. Ein zweiter Signaturmechanismus wäre eine zweite Wahrheit
/// darüber, was eine gültige Signatur ist (M6-Vertrag §4).
/// </para>
/// </summary>
public sealed partial class ApprovalNotificationDispatcher : IApprovalNotifier, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Channel<Pending> _queue;
    private readonly ConcurrentDictionary<Guid, ApprovalNotificationStatus> _status = new();
    private readonly ConcurrentQueue<Guid> _order = new();
    private readonly ApprovalNotificationOptions _options;
    private readonly HttpMessageInvoker _http;
    private readonly TimeProvider _time;
    private readonly ILogger<ApprovalNotificationDispatcher>? _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private long _dropped;

    public ApprovalNotificationDispatcher(
        ApprovalNotificationOptions options,
        HttpMessageInvoker http,
        TimeProvider time,
        ILogger<ApprovalNotificationDispatcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _http = http;
        _time = time;
        _logger = logger;
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _loop = Task.Run(() => DeliverLoopAsync(_stop.Token));
    }

    /// <summary>Wie viele Benachrichtigungen die volle Warteschlange verworfen hat.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public IReadOnlyList<ApprovalNotificationStatus> Status =>
        [.. _status.Values.OrderByDescending(s => s.QueuedAt)];

    public void Notify(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var target in _options.Targets.Where(t => t.Enabled))
        {
            var pending = new Pending(Guid.NewGuid(), target, request, 0);
            var status = new ApprovalNotificationStatus(
                pending.Id, target.Name, request.Id, ApprovalNotificationState.Pending, 0,
                _time.GetUtcNow(), null, null);

            Remember(status);

            if (!_queue.Writer.TryWrite(pending))
            {
                // Die Warteschlange ist begrenzt und verwirft die aeltesten. Der Zaehler ist der
                // Grund, warum das kein stiller Verlust ist.
                Interlocked.Increment(ref _dropped);
                Remember(status with
                {
                    State = ApprovalNotificationState.DeadLettered,
                    LastError = "Warteschlange voll — verworfen.",
                });
            }
        }
    }

    private async Task DeliverLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync(ct))
            {
                await DeliverAsync(pending, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Ende.
        }
    }

    private async Task DeliverAsync(Pending pending, CancellationToken ct)
    {
        var now = _time.GetUtcNow();

        // Eine abgelaufene Freigabe zuzustellen ist Laerm: Wer sie liest, kann nichts mehr
        // entscheiden. Der Vorgang bleibt trotzdem sichtbar — mit Grund.
        if (pending.Request.ExpiresAt <= now)
        {
            Update(pending, ApprovalNotificationState.Expired, pending.Attempt, now,
                "Freigabe war bei der Zustellung bereits abgelaufen.");
            return;
        }

        var body = JsonSerializer.Serialize(Payload(pending.Request), Json);
        var unix = now.ToUnixTimeSeconds();

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, pending.Target.Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            message.Headers.TryAddWithoutValidation(
                WebhookSignature.SignatureHeader,
                WebhookSignature.Compute(pending.Target.Secret, unix, body));
            message.Headers.TryAddWithoutValidation(
                WebhookSignature.TimestampHeader, unix.ToString(CultureInfo.InvariantCulture));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);

            using var response = await _http.SendAsync(message, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                Update(pending, ApprovalNotificationState.Delivered, pending.Attempt + 1, _time.GetUtcNow(), null);
                return;
            }

            await RetryOrDeadLetterAsync(pending, $"HTTP {(int)response.StatusCode}", ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RetryOrDeadLetterAsync(pending, exception.GetType().Name + ": " + exception.Message, ct);
        }
    }

    private async Task RetryOrDeadLetterAsync(Pending pending, string reason, CancellationToken ct)
    {
        var attempt = pending.Attempt + 1;
        var now = _time.GetUtcNow();

        if (attempt >= _options.MaxAttempts)
        {
            Update(pending, ApprovalNotificationState.DeadLettered, attempt, now, reason);
            if (_logger is not null)
            {
                Log.GaveUp(_logger, pending.Target.Name, attempt, reason);
            }
            return;
        }

        Update(pending, ApprovalNotificationState.Pending, attempt, now, reason);

        var delay = TimeSpan.FromMilliseconds(Math.Min(
            _options.MaxBackoff.TotalMilliseconds,
            _options.InitialBackoff.TotalMilliseconds * Math.Pow(_options.BackoffMultiplier, attempt - 1)));

        try
        {
            await Task.Delay(delay, _time, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_queue.Writer.TryWrite(pending with { Attempt = attempt }))
        {
            Interlocked.Increment(ref _dropped);
            Update(pending, ApprovalNotificationState.DeadLettered, attempt, _time.GetUtcNow(),
                "Warteschlange voll — Wiederholung verworfen.");
        }
    }

    /// <summary>
    /// Die Nutzlast: <b>wer</b> etwas will, <b>was</b> es ist und <b>wo</b> man entscheidet.
    /// <para>
    /// Ausdrücklich nicht dabei sind die Argumente — auch nicht die redigierten. Eine
    /// Benachrichtigung geht an ein fremdes System, oft in einen Chat; was dort steht, steht
    /// dauerhaft dort. Der Deep-Link führt hinter die Anmeldung, wo ohnehin nur sieht, wer darf.
    /// </para>
    /// </summary>
    private object Payload(ApprovalRequest request) => new
    {
        @event = "approval.pending",
        approvalId = request.Id,
        tool = request.Tool.Value,
        caller = request.CallerDescription,
        requestedAt = request.RequestedAt,
        expiresAt = request.ExpiresAt,
        url = $"{_options.PublicBaseUrl?.TrimEnd('/') ?? string.Empty}/approvals#{request.Id}",
    };

    private void Update(
        Pending pending, ApprovalNotificationState state, int attempts, DateTimeOffset at, string? error)
    {
        if (_status.TryGetValue(pending.Id, out var existing))
        {
            Remember(existing with { State = state, Attempts = attempts, LastTriedAt = at, LastError = error });
        }
    }

    private void Remember(ApprovalNotificationStatus status)
    {
        if (_status.TryAdd(status.Id, status))
        {
            _order.Enqueue(status.Id);
        }
        else
        {
            _status[status.Id] = status;
        }

        // Aufbewahrung begrenzen: Der Zustand ist eine Sicht, kein Archiv.
        while (_order.Count > _options.StatusHistory && _order.TryDequeue(out var oldest))
        {
            _status.TryRemove(oldest, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync();

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private sealed record Pending(Guid Id, ApprovalNotificationTarget Target, ApprovalRequest Request, int Attempt);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Benachrichtigung an '{Ziel}' nach {Versuche} Versuchen aufgegeben: {Grund}")]
        public static partial void GaveUp(ILogger logger, string ziel, int versuche, string grund);
    }
}
