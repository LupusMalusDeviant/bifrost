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
    private readonly IUpstreamSessionRecovery? _recovery;
    private int _inFlight;

    /// <summary>
    /// Gesetzt, sobald der Upstream diese Sitzung einmal abgewiesen hat. Danach geht über diese
    /// Verbindung nichts mehr hinaus: Der SDK-Client ist nach dem ersten 404 tot (gemessen — jeder
    /// weitere Aufruf endet sofort in einer TaskCanceledException), und wer die Hülle noch in der
    /// Hand hält, wartet stattdessen auf den Neuaufbau.
    /// </summary>
    private volatile bool _sessionLost;

    /// <param name="recovery">
    /// Der Weg zum Neuaufbau einer verlorenen Sitzung. Ohne ihn verhält sich die Hülle wie bisher:
    /// Der Fehler geht an den Aufrufer.
    /// </param>
    public GuardedUpstreamConnection(
        IUpstreamConnection inner,
        TimeSpan callTimeout,
        TimeProvider? timeProvider = null,
        IUpstreamSessionRecovery? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(callTimeout, TimeSpan.Zero);
        _inner = inner;
        _callTimeout = callTimeout;
        _time = timeProvider ?? TimeProvider.System;
        _recovery = recovery;
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
    public Task<JsonElement> CallToolAsync(
        string caller, string toolName, JsonElement args, CancellationToken ct)
        => WithRecoveryAsync(
            connection => connection.CallToolOnceAsync(caller, toolName, args, ct), toolName, ct);

    private async Task<JsonElement> CallToolOnceAsync(
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
        => WithRecoveryAsync(
            connection => connection.WithTimeoutAsync(
                (inner, token) => inner.ReadResourceAsync(uri, token), "resources/read", ct),
            toolName: null,
            ct);

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => WithRecoveryAsync(
            connection => connection.WithTimeoutAsync(
                (inner, token) => inner.GetPromptAsync(promptName, args, token), "prompts/get", ct),
            toolName: null,
            ct);

    /// <summary>
    /// Führt einen Aufruf aus und wiederholt ihn <b>genau einmal</b>, wenn der Upstream die Sitzung
    /// nicht mehr kennt (Vertrag Abschnitt 4).
    /// <para>
    /// <b>Nur dieser eine Fehler wird wiederholt.</b> Beim 404 auf die Sitzung steht fest, dass der
    /// Upstream nichts ausgeführt hat. Bei einer abgerissenen Verbindung, einer Zeitüberschreitung
    /// oder einem 5xx ist das nicht zu wissen — und ein Werkzeug zweimal auszuführen ist schlimmer,
    /// als einen Fehler zu melden.
    /// </para>
    /// <para>
    /// <paramref name="attempt"/> bekommt die Verbindung, auf der es laufen soll, und ruft dort den
    /// Weg <b>ohne</b> Wiederholung. So kann der zweite Versuch keinen dritten auslösen.
    /// </para>
    /// </summary>
    private async Task<JsonElement> WithRecoveryAsync(
        Func<GuardedUpstreamConnection, Task<JsonElement>> attempt, string? toolName, CancellationToken ct)
    {
        if (_recovery is null)
        {
            return await attempt(this).ConfigureAwait(false);
        }

        // Auch beim ersten Versuch: Wer den Namen noch aus einem älteren Katalog kennt, darf ein
        // Werkzeug nicht aufrufen, das die Prüfung inzwischen zurückhält (U5).
        if (toolName is not null && !_recovery.IsToolAvailable(toolName))
        {
            throw new InvalidOperationException(
                $"Das Werkzeug '{toolName}' steht nicht im geprüften Katalog des Upstreams — es ist "
                + "unbekannt, oder seine Definition hat sich geändert und wird bis zur Annahme "
                + "zurückgehalten. Der Aufruf wurde nicht ausgeführt.");
        }

        if (!_sessionLost)
        {
            try
            {
                return await attempt(this).ConfigureAwait(false);
            }
            catch (UpstreamSessionLostException)
            {
                _sessionLost = true;
            }
        }

        var replacement = await _recovery.RebuildAsync(this, ct).ConfigureAwait(false);
        if (toolName is not null && !_recovery.IsToolAvailable(toolName))
        {
            throw new InvalidOperationException(
                $"Die Sitzung zum Upstream ging verloren und wurde neu aufgebaut. Das Werkzeug "
                + $"'{toolName}' steht danach nicht mehr im geprüften Katalog — es wurde entfernt, "
                + "oder seine Definition hat sich geändert und wird bis zur Annahme zurückgehalten. "
                + "Der Aufruf wurde nicht ausgeführt.");
        }

        try
        {
            return await attempt(replacement).ConfigureAwait(false);
        }
        catch (UpstreamSessionLostException)
        {
            // Die Wiederholung selbst wird nicht wiederholt. Die neue Verbindung merkt sich aber,
            // dass auch ihre Sitzung fort ist — der nächste Aufrufer baut dann neu auf.
            replacement._sessionLost = true;
            throw;
        }
    }

    /// <summary>
    /// Die Verbindung wird ersetzt, obwohl ihre Sitzung nicht verloren ist — etwa nach einem
    /// abgewiesenen Token (V1). Wer die Hülle noch hält, wartet danach auf die neue Verbindung,
    /// statt in die alte zu laufen.
    /// </summary>
    internal void MarkReplaced() => _sessionLost = true;

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
        catch (UpstreamSessionLostException)
        {
            // Hier und nicht erst im Wiederholungsweg gemerkt: Auch das Lebenszeichen und die
            // Katalogabfrage laufen über diese Methode, und wenn eine von ihnen das 404 bekommt,
            // ist der Client genauso tot. Ohne diese Zeile liefe ein Aufruf, der während des
            // Neuaufbaus ankommt, in die tote Verbindung und bekäme „nicht wiederholt", obwohl
            // nichts das Gateway verlassen hat (Vertrag Abschnitt 4, „nach dem 404 begonnen").
            _sessionLost = true;
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Upstream-Operation '{operationName}' auf '{Id}' überschritt den Timeout von {_callTimeout}.");
        }
        catch (OperationCanceledException exception)
            when (!ct.IsCancellationRequested && !timeoutCts.IsCancellationRequested)
        {
            // Weder der Aufrufer noch die Frist haben abgebrochen: Die Verbindung wurde unter der
            // laufenden Operation abgebaut. Gemessen am MCP-SDK: Bekommt eine Anfrage ein 404 auf
            // die Sitzung, bricht der Client alle anderen ab, die gerade unterwegs sind — sie sehen
            // kein eigenes 404, sondern eine nackte „The operation was canceled".
            //
            // Wiederholt wird hier bewusst NICHT (Vertrag Abschnitt 4): Eine Anfrage, die schon
            // unterwegs war, kann der Upstream ausgeführt haben, bevor seine Sitzung endete.
            throw new UpstreamConnectionLostException(
                $"Die Verbindung zum Upstream wurde während der Operation '{operationName}' abgebaut — "
                + "etwa weil seine Sitzung verloren ging. Ob der Upstream sie noch ausgeführt hat, ist "
                + "nicht bekannt; sie wurde deshalb nicht wiederholt.",
                exception);
        }
    }
}
