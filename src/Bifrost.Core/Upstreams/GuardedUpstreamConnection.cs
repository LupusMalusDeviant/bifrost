using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Core.Execution;

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
    /// <param name="config">
    /// Die Konfiguration des Upstreams — damit ein gescheiterter Aufruf mit bereinigtem Grund an den
    /// Agenten geht (ohne Antwortkörper und ohne Geheimwerte der Konfiguration, Codex H7).
    /// </param>
    public GuardedUpstreamConnection(
        IUpstreamConnection inner,
        TimeSpan callTimeout,
        TimeProvider? timeProvider = null,
        IUpstreamSessionRecovery? recovery = null,
        UpstreamServerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(callTimeout, TimeSpan.Zero);
        _inner = inner;
        _callTimeout = callTimeout;
        _time = timeProvider ?? TimeProvider.System;
        _recovery = recovery;
        Config = config;
    }

    /// <summary>Die Konfiguration, unter der diese Verbindung aufgebaut wurde, falls bekannt.</summary>
    internal UpstreamServerConfig? Config { get; }

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

    private Task<JsonElement> CallToolOnceAsync(
        string caller, string toolName, JsonElement args, CancellationToken ct)
        => CountedAsync(
            (inner, token) => inner is ICallerAwareUpstreamConnection aware
                ? aware.CallToolAsync(caller, toolName, args, token)
                : inner.CallToolAsync(toolName, args, token),
            toolName,
            ct);

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => WithRecoveryAsync(
            connection => connection.CountedAsync(
                (inner, token) => inner.ReadResourceAsync(uri, token), "resources/read", ct),
            toolName: null,
            ct);

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => WithRecoveryAsync(
            connection => connection.CountedAsync(
                (inner, token) => inner.GetPromptAsync(promptName, args, token), "prompts/get", ct),
            toolName: null,
            ct);

    /// <summary>
    /// Eine Anfrage, die der Drain abwarten muss — Werkzeug, Ressource oder Prompt (Plan 0005 E4).
    /// Nach <see cref="CloseAdmission"/> wird keine neue mehr angenommen; sie verlässt das Gateway
    /// dann nicht, und der Drain wartet nur auf die, die schon unterwegs waren.
    /// </summary>
    private async Task<JsonElement> CountedAsync(
        Func<IUpstreamConnection, CancellationToken, Task<JsonElement>> operation,
        string operationName,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (Volatile.Read(ref _admissionClosed))
            {
                throw new UpstreamConnectionLostException(
                    $"Der Upstream wird gestoppt; '{operationName}' wurde nicht mehr gesendet.");
            }

            return await WithTimeoutAsync(operation, operationName, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private bool _admissionClosed;

    /// <summary>Ab jetzt keine neuen Anfragen mehr — der Drain beginnt (E4).</summary>
    internal void CloseAdmission() => Volatile.Write(ref _admissionClosed, true);

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
        // Werkzeug nicht aufrufen, das die Prüfung inzwischen zurückhält (U5). Geprüft wird gegen
        // den Katalog DIESER Verbindung; ist sie schon ersetzt, läuft der Aufruf über die neue (E14).
        if (!_sessionLost && toolName is not null)
        {
            switch (_recovery.CheckTool(this, toolName))
            {
                case ToolAvailability.Withheld:
                    throw new InvalidOperationException(
                        $"Das Werkzeug '{toolName}' steht nicht im geprüften Katalog des Upstreams — es ist "
                        + "unbekannt, oder seine Definition hat sich geändert und wird bis zur Annahme "
                        + "zurückgehalten. Der Aufruf wurde nicht ausgeführt.");
                case ToolAvailability.Stale:
                    _sessionLost = true;
                    break;
            }
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

        // Weitergereicht wird nur innerhalb derselben Konfiguration (Plan 0005 F9). Ein Neuaufbau der
        // Sitzung behält sie; ein Umkonfigurieren ersetzt sie — womöglich durch ein anderes Ziel oder
        // andere Zugangsdaten. Ein Aufruf, der dem alten Upstream galt, geht dort nicht hin.
        if (!SameConfiguration(Config, replacement.Config))
        {
            throw new UpstreamConnectionLostException(
                "Der Upstream wurde inzwischen umkonfiguriert; der Aufruf galt der vorherigen Konfiguration "
                + "und wurde nicht gesendet.");
        }

        _replacement = replacement;
        if (toolName is not null && _recovery.CheckTool(replacement, toolName) != ToolAvailability.Available)
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
    /// Laufen zwei Verbindungen unter derselben Konfiguration (Plan 0005 F9)? Ein Neuaufbau der Sitzung
    /// verbindet mit genau dem Objekt, das im Supervisor steht — dann genügt der Verweis. Abschalten und
    /// wieder Einschalten legt ein neues Objekt an, das sich nur in <c>Enabled</c> unterscheidet; das
    /// gilt als dieselbe Konfiguration (Plan 0005 E14). Verglichen wird dann der Inhalt; was sich nicht
    /// sicher als gleich zeigt, gilt als verschieden.
    /// <para>
    /// Eine Hülle ohne Konfiguration (nur außerhalb des Supervisors gebaut) kann nichts vergleichen
    /// und reicht weiter wie bisher; der Supervisor gibt jeder Verbindung ihre Konfiguration mit.
    /// </para>
    /// </summary>
    [NoHostExecution("Vergleicht nur zwei Konfigurationen; startet nichts.")]
    internal static bool SameConfiguration(UpstreamServerConfig? own, UpstreamServerConfig? replacement)
    {
        if (own is null || ReferenceEquals(own, replacement))
        {
            return true;
        }

        if (replacement is null)
        {
            return false;
        }

        return string.Equals(
            JsonSerializer.Serialize(own with { Enabled = true }),
            JsonSerializer.Serialize(replacement with { Enabled = true }),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Verbindung wird ersetzt, obwohl ihre Sitzung nicht verloren ist — etwa nach einem
    /// abgewiesenen Token (V1). Wer die Hülle noch hält, wartet danach auf die neue Verbindung,
    /// statt in die alte zu laufen.
    /// </summary>
    internal void MarkReplaced() => _sessionLost = true;

    /// <summary>Die Verbindung, auf der ein Aufruf über diese Hülle zuletzt wiederholt wurde.</summary>
    private volatile GuardedUpstreamConnection? _replacement;

    /// <summary>
    /// Was diese Verbindung dem Upstream an Zugangsdaten vorgelegt hat — und die Verbindung, auf der
    /// zuletzt wiederholt wurde, denn ein erneuertes OAuth-Token steht in keiner Konfiguration.
    /// </summary>
    /// <summary>Was nur diese Verbindung selbst vorgelegt hat, ohne die ihrer Ersatzverbindung (E11).</summary>
    internal IReadOnlyList<string> OwnPresentedCredentials
        => _inner is IPresentedCredentials presented ? presented.PresentedCredentials : [];

    internal IReadOnlyList<string> PresentedCredentials
    {
        get
        {
            var own = OwnPresentedCredentials;
            return _replacement is { } replacement ? [.. own, .. replacement.PresentedCredentials] : own;
        }
    }

    /// <summary>
    /// Der Grund eines gescheiterten Aufrufs, wie er an Agent, Vorgangsablage und Trace geht: ohne
    /// Antwortkörper, ohne die Geheimwerte der Konfiguration und ohne das vorgelegte Token (Plan 0005
    /// C7, D1).
    /// </summary>
    internal string DescribeFailure(Exception exception)
        => Config is { } config
            ? UpstreamFailure.Describe(exception, config, PresentedCredentials)
            : UpstreamFailure.DescribeWithoutConfig(exception, PresentedCredentials);

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
            //
            // Als Sitzungsverlust gemerkt wird das hier NICHT (Plan 0005 E10): Ein interner Abbruch
            // beweist keinen — auch stdio und sessionloses HTTP kennen ihn, und ein Neuaufbau ginge
            // dort an Abschnitt 4 vorbei in die Bremse aus V2. Ist der Client wirklich beendet,
            // erkennt das die Verbindung selbst vor der nächsten Anfrage (D13).
            throw new UpstreamConnectionLostException(
                $"Die Verbindung zum Upstream wurde während der Operation '{operationName}' abgebaut — "
                + "etwa weil seine Sitzung verloren ging. Ob der Upstream sie noch ausgeführt hat, ist "
                + "nicht bekannt; sie wurde deshalb nicht wiederholt.",
                exception);
        }
    }
}
