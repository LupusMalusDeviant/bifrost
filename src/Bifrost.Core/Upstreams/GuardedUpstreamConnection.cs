using System.Text.Json;
using Bifrost.Abstractions;

namespace Bifrost.Core.Upstreams;

/// <summary>
/// Decorator um eine Upstream-Verbindung: erzwingt den Per-Call-Timeout (FR-09) und zählt
/// In-Flight-Calls für die Drain-Semantik (WP1.4). Der Supervisor gibt ausschließlich
/// diese Hülle nach außen — nie die rohe Verbindung.
/// </summary>
public sealed class GuardedUpstreamConnection
    : IUpstreamConnection, ISignedUpstreamConnection, ICallerAwareUpstreamConnection
{
    private readonly IUpstreamConnection _inner;
    private readonly TimeSpan _callTimeout;
    private readonly TimeProvider _time;
    private int _inFlight;

    public GuardedUpstreamConnection(IUpstreamConnection inner, TimeSpan callTimeout, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(callTimeout, TimeSpan.Zero);
        _inner = inner;
        _callTimeout = callTimeout;
        _time = timeProvider ?? TimeProvider.System;
    }

    public ServerId Id => _inner.Id;

    public int InFlightCount => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Reicht die Publisher-Bindung der inneren Verbindung durch (Plan 0003, WP4). Der Supervisor
    /// gibt nur diese Hülle heraus — verschluckte sie das Merkmal, fände ein Schlüssel-Entzug die
    /// betroffenen Upstreams nicht mehr, und der Decorator hätte still eine Sicherheitsfunktion
    /// abgeschaltet.
    /// </summary>
    public string PublisherKeyId =>
        _inner is ISignedUpstreamConnection signed ? signed.PublisherKeyId : string.Empty;

    /// <summary>
    /// Ebenfalls durchgereicht, aus demselben Grund: Verschluckte der Decorator das Merkmal, hielte
    /// der Supervisor jeden Upstream für einen, der sich von selbst meldet — und ein Server auf der
    /// Revision 2026-07-28 bekäme nie wieder eine Katalogabfrage.
    /// </summary>
    public bool PushesCatalogChanges => _inner.PushesCatalogChanges;

    /// <summary>
    /// Ebenfalls durchgereicht. Der Supervisor gibt <b>nur</b> diese Hülle heraus — verschluckte
    /// sie die Angabe, sähe die Diagnose bei jedem angeschlossenen Upstream „nicht ermittelt",
    /// obwohl die ausgehandelte Fassung eine Schicht tiefer bereitliegt. Eine Hülle, die den
    /// Vertrag verkürzt, ist die schlechteste Art, eine Auskunft zu verlieren: Sie sieht aus wie
    /// eine Eigenschaft des Upstreams.
    /// </summary>
    public UpstreamProtocolInfo Protocol => _inner.Protocol;

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add => _inner.NotificationReceived += value;
        remove => _inner.NotificationReceived -= value;
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => WithTimeoutAsync((inner, token) => inner.DiscoverAsync(token), "discover", ct);

    public Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
        => CallToolAsync(string.Empty, toolName, args, ct);

    /// <summary>
    /// Reicht die Aufrufer-Identität durch (Plan 0003, Resources). Aus demselben Grund wie bei
    /// <see cref="PublisherKeyId"/>: Verschluckte der Decorator das Merkmal, fielen alle Aufrufer
    /// auf denselben Namen zusammen — und die Handle-Trennung wäre still abgeschaltet.
    /// </summary>
    public async Task<JsonElement> CallToolAsync(
        string caller, string toolName, JsonElement args, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            return await WithTimeoutAsync(
                (inner, token) => inner is ICallerAwareUpstreamConnection aware
                    ? aware.CallToolAsync(caller, toolName, args, token)
                    : inner.CallToolAsync(toolName, args, token),
                toolName,
                ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => WithTimeoutAsync((inner, token) => inner.ReadResourceAsync(uri, token), "resources/read", ct);

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => WithTimeoutAsync((inner, token) => inner.GetPromptAsync(promptName, args, token), "prompts/get", ct);

    public Task PingAsync(CancellationToken ct)
        => WithTimeoutAsync(
            async (inner, token) =>
            {
                await inner.PingAsync(token).ConfigureAwait(false);
                return true;
            },
            "ping",
            ct);

    /// <summary>Wartet bis alle In-Flight-Calls beendet sind, höchstens <paramref name="grace"/>. True = idle erreicht.</summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan grace, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + grace;
        while (InFlightCount > 0)
        {
            if (_time.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), _time, ct).ConfigureAwait(false);
        }

        return true;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private async Task<T> WithTimeoutAsync<T>(
        Func<IUpstreamConnection, CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(_callTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await operation(_inner, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Upstream-Operation '{operationName}' auf '{Id}' überschritt den Timeout von {_callTimeout}.");
        }
    }
}
