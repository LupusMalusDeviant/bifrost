using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Bifrost.Abstractions;
using Bifrost.Core.Approvals;
using Bifrost.Core.Guardrails;
using Bifrost.Core.Upstreams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Core.Invocation;

/// <summary>
/// Der EINZIGE Weg zu einem Tool-Call (ADR-0008, DO Nr. 1):
/// RateLimit → Routing-Lookup → RBAC → Schema-Validierung → Upstream-Call mit Timeout → Audit.
/// Wirft nie bei fachlichen Fehlern; jeder Pfad endet in genau einem <see cref="ToolInvocationResult"/>
/// und genau einem Audit-Event (DO Nr. 5). Nicht validierbare Schemas lassen den Call durch
/// und werden geloggt (Plan-Risiko R3 — Draft-Vielfalt der Server).
/// </summary>
public sealed partial class ToolInvoker : IToolInvoker, IDisposable
{
    /// <summary>Meter-Name für den Metriken-Export (FR-26) — vom Host bei OpenTelemetry registriert.</summary>
    public const string MeterName = "Bifrost.Gateway";

    /// <summary>
    /// Quelle der Traces. Gleicher Name wie der Meter: Es ist dieselbe Komponente, und ein zweiter
    /// Name zwänge jeden Betreiber, zwei Dinge zu registrieren.
    /// <para>
    /// <b>In Spans stehen niemals Argumente oder Ergebnisse.</b> Das Audit-Log ist redigiert, ein
    /// Telemetrie-Backend ist es nicht — ein Payload im Span wäre der bequemste Weg, die Redaction
    /// zu umgehen, und zwar an eine Stelle, die oft weniger geschützt ist als die Datenbank.
    /// </para>
    /// </summary>
    public const string ActivitySourceName = "Bifrost.Gateway";

    private static readonly Meter Meter = new(MeterName);

    private static readonly ActivitySource Activity = new(ActivitySourceName);

    private readonly IAuthorizationService _authorization;
    private readonly IRateLimiter _rateLimiter;
    private readonly IToolCatalog _catalog;
    private readonly IUpstreamSupervisor _supervisor;
    private readonly IAuditSink _audit;
    private readonly IRedactionService _redaction;
    private readonly TimeProvider _time;
    private readonly ILogger<ToolInvoker> _logger;
    private readonly AuditOptions _auditOptions;
    private readonly ResultCompressionOptions _compression;
    private readonly IContentGuard? _guard;
    private readonly GuardOptions _guardOptions;
    private readonly IApprovalPolicy? _approvalPolicy;
    private readonly IApprovalStore? _approvalStore;
    private readonly TimeSpan _approvalTtl = TimeSpan.FromHours(1);
    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;

    public ToolInvoker(
        IAuthorizationService authorization,
        IRateLimiter rateLimiter,
        IToolCatalog catalog,
        IUpstreamSupervisor supervisor,
        IAuditSink audit,
        IRedactionService redaction,
        TimeProvider? timeProvider = null,
        ILogger<ToolInvoker>? logger = null,
        AuditOptions? auditOptions = null,
        ResultCompressionOptions? compression = null,
        IContentGuard? guard = null,
        GuardOptions? guardOptions = null,
        IApprovalPolicy? approvalPolicy = null,
        IApprovalStore? approvalStore = null)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(redaction);
        _authorization = authorization;
        _rateLimiter = rateLimiter;
        _catalog = catalog;
        _supervisor = supervisor;
        _audit = audit;
        _redaction = redaction;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ToolInvoker>.Instance;
        _auditOptions = auditOptions ?? new AuditOptions();
        _compression = compression ?? new ResultCompressionOptions();
        _guard = guard;
        _guardOptions = guardOptions ?? new GuardOptions();
        _approvalPolicy = approvalPolicy;
        _approvalStore = approvalStore;
        _calls = Meter.CreateCounter<long>("bifrost.tool_calls", description: "Tool-Calls durch den Gateway");
        _duration = Meter.CreateHistogram<double>("bifrost.tool_call_duration", unit: "ms");
    }

    public async Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        CatalogEntry? entry = null;
        ToolInvocationResult result;

        // UpstreamError heisst im Vertrag auch "interner Fehler". Die Dimension fault trennt beides,
        // damit eine Fehlerquote je Upstream keinen Gateway-Fehler dem Server anlastet
        // (Review-Befund L-6, zwölfte Runde).
        var gatewayFault = false;

        // Ein Span je Aufruf. Er umfasst die ganze Pipeline; der Upstream-Aufruf bekommt darin einen
        // eigenen Kind-Span, sodass sich Gateway-Anteil und Fremdanteil trennen lassen — genau die
        // Frage, die NFR-01 stellt.
        using var activity = Activity.StartActivity("bifrost.tool_call", ActivityKind.Internal);
        activity?.SetTag("bifrost.tool", request.Tool.Value);
        activity?.SetTag("bifrost.origin", request.Origin.ToString());
        activity?.SetTag("bifrost.caller", request.Caller.Value.ToString());

        try
        {
            if (!_rateLimiter.TryAcquire(request.Caller))
            {
                result = Fail(InvocationStatus.Denied, "Rate-Limit überschritten oder Identität unbekannt (FR-31).", started);
            }
            else if ((entry = _catalog.Find(request.Tool)) is null)
            {
                result = Fail(InvocationStatus.ToolNotFound, $"Tool '{request.Tool}' existiert nicht.", started);
            }
            else
            {
                var decision = _authorization.Evaluate(
                    request.Caller, new PermissionScope(entry.Server, entry.Name), ToolAction.UseTool);
                if (!decision.Allowed)
                {
                    result = Fail(InvocationStatus.Denied, decision.DenyReason ?? "Verweigert (Default-Deny).", started);
                }
                else if (!entry.Name.TrySplit(out _, out _))
                {
                    // Ein Katalogeintrag mit kaputtem Namen ist ein Fehler des Gateways, nicht des
                    // Upstreams (Review-Befund L-3, dreizehnte Runde). Vorher erst in
                    // CallUpstreamAsync erkannt und als fault=none gezählt.
                    result = Fail(InvocationStatus.UpstreamError, $"Interner Gateway-Fehler: '{entry.Name}' ist kein gültiger Namespaced-Name.", started);
                    gatewayFault = true;
                }
                else if (ValidateArguments(entry, request.Arguments) is { } rejection)
                {
                    result = Fail(rejection.Status, rejection.Message, started);
                    gatewayFault = rejection.GatewayFault;
                }
                else
                {
                    result = await CallUpstreamAsync(entry, request, started, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Nur die Typnamen: Die Textform der Ausnahme trüge ihre ungefilterte Meldung ins Log (Plan 0005 D1).
            Log.UnexpectedPipelineError(_logger, UpstreamFailure.TypeChain(ex), request.Tool.Value);
            result = Fail(InvocationStatus.UpstreamError,
                $"Interner Gateway-Fehler: {UpstreamFailure.DescribeWithoutConfig(ex)}", started);
            gatewayFault = true;
        }

        Audit(request, entry, result);

        // FR-26 verlangt Auswertung pro Server UND Tool — der Server-Slug steckt im Namespace.
        var server = request.Tool.TrySplit(out var slug, out _) ? slug : "unknown";

        if (activity is not null)
        {
            activity.SetTag("bifrost.server", server);
            activity.SetTag("bifrost.status", result.Status.ToString());
            activity.SetTag("bifrost.fault", gatewayFault ? "gateway" : "none");
            // Nur Erfolg ist Ok. Ein Deny oder ein Guard-Treffer ist kein Serverfehler, aber auch
            // kein gelungener Aufruf — als Error markiert taucht er in jeder Fehlersuche auf, und
            // genau dort will man ihn haben. Die Beschreibung ist die Fehlermeldung des Invokers,
            // die keine Argumente enthält.
            activity.SetStatus(
                result.Status is InvocationStatus.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                result.Status is InvocationStatus.Success ? null : result.ErrorMessage);
        }

        _calls.Add(1,
            new KeyValuePair<string, object?>("server", server),
            new KeyValuePair<string, object?>("tool", request.Tool.Value),
            new KeyValuePair<string, object?>("status", result.Status.ToString()),
            new KeyValuePair<string, object?>("origin", request.Origin.ToString()),
            new KeyValuePair<string, object?>("fault", gatewayFault ? "gateway" : "none"));
        _duration.Record(result.Duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("server", server),
            new KeyValuePair<string, object?>("tool", request.Tool.Value),
            new KeyValuePair<string, object?>("status", result.Status.ToString()));
        return result;
    }

    public void Dispose()
    {
        // Meter ist statisch/global — nichts freizugeben; Platzhalter für spätere Ressourcen.
    }

    private async Task<ToolInvocationResult> CallUpstreamAsync(
        CatalogEntry entry, ToolInvocationRequest request, long started, CancellationToken ct)
    {
        var connection = _supervisor.GetConnection(entry.Server);
        if (connection is null)
        {
            return Fail(InvocationStatus.UpstreamError,
                $"Upstream-Server für '{entry.Name}' ist nicht verbunden (Status: {_supervisor.GetStatus(entry.Server)?.State.ToString() ?? "unbekannt"}).",
                started);
        }

        if (!entry.Name.TrySplit(out _, out var upstreamToolName))
        {
            // Schon in InvokeAsync abgefangen (als Gateway-Fehler); hier nur noch zur Absicherung.
            return Fail(InvocationStatus.UpstreamError, $"'{entry.Name}' ist kein gültiger Namespaced-Name.", started);
        }

        // Guardrail ausgehend (ADR-0011): VOR dem Upstream — hier gibt es noch keinen
        // Seiteneffekt, ein Treffer kostet nur den Call.
        if (Guard(request.Arguments, GuardDirection.Outbound) is { } outboundBlock)
        {
            return Fail(InvocationStatus.Denied, outboundBlock, started);
        }

        // Freigabe-Pflicht (FR-32, ADR-0012): ebenfalls vor dem Upstream, kein Seiteneffekt.
        var (approval, consumedTask) = await CheckApprovalAsync(entry, request, ct).ConfigureAwait(false);
        if (approval is not null)
        {
            // Die Vorgangs-Id geht maschinenlesbar mit: Der Aufrufer kann den Stand unter
            // /api/v1/tasks/{id} holen, statt auf den Meldungstext angewiesen zu sein.
            return Fail(InvocationStatus.ApprovalRequired, approval.Message, started)
                with { TaskId = approval.TaskId };
        }

        using var overrideCts = request.TimeoutOverride is { } t
            ? new CancellationTokenSource(t, _time)
            : null;
        using var linked = overrideCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, overrideCts.Token);
        var effectiveCt = linked?.Token ?? ct;

        // Der eigentliche Aufruf als lokale Funktion, damit der Abschluss eines eingelösten
        // Vorgangs danach an EINER Stelle steht — statt an jedem der fünf Rückgabepfade.
        async Task<ToolInvocationResult> RunAsync()
        {
        try
        {
            // Eigener Span nur um den Fremdanteil. Die Differenz zum Elternspan ist der
            // Gateway-Overhead — ohne diese Trennung sieht man in einer langsamen Antwort nicht,
            // wer sie verursacht hat.
            using var upstreamActivity = Activity.StartActivity("bifrost.upstream_call", ActivityKind.Client);
            upstreamActivity?.SetTag("bifrost.server", entry.Server.Value.ToString());
            upstreamActivity?.SetTag("bifrost.upstream_tool", upstreamToolName);

            // Wo die Identität zählt, geht sie mit (Plan 0003, Resources): Ein WASI-Upstream mit
            // persistenter Instanz schreibt seine Handles auf diesen Namen. Alle anderen
            // Connectoren kennen das Merkmal nicht und bekommen den bisherigen Aufruf.
            var content = await (connection is ICallerAwareUpstreamConnection aware
                    ? aware.CallToolAsync(request.Caller.ToString(), upstreamToolName, request.Arguments, effectiveCt)
                    : connection.CallToolAsync(upstreamToolName, request.Arguments, effectiveCt))
                .ConfigureAwait(false);

            // FR-16: Kürzen erst hier, nach dem Upstream-Call — das Audit soll die tatsächlich
            // gelieferte Größe festhalten, nicht die gekürzte. Die Kürzung läuft VOR der
            // Guardrail, damit legitime Groß-Ergebnisse gekürzt statt blockiert durchgehen.
            var (compressed, truncation) = ResultCompressor.Compress(content, _compression);
            if (truncation is not null)
            {
                Log.ResultTruncated(_logger, request.Tool.Value, truncation.OriginalChars, truncation.TruncatedChars);
            }

            // Guardrail eingehend (ADR-0011): Der Call ist an dieser Stelle bereits gelaufen.
            // Deshalb GuardBlocked und nicht Denied — der Status muss unterscheidbar bleiben,
            // damit im Audit erkennbar ist, dass der Seiteneffekt eingetreten ist.
            if (Guard(compressed, GuardDirection.Inbound) is { } inboundBlock)
            {
                return new ToolInvocationResult(
                    InvocationStatus.GuardBlocked, null, inboundBlock, Elapsed(started), truncation);
            }

            return new ToolInvocationResult(
                InvocationStatus.Success, compressed, null, Elapsed(started), truncation);
        }
        catch (TimeoutException ex)
        {
            // Auch eine Zeitüberschreitung kann vom Upstream kommen und dessen Text tragen (Plan 0005 D1).
            return Fail(InvocationStatus.Timeout, Reason(connection, ex), started);
        }
        catch (OperationCanceledException) when (overrideCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            return Fail(InvocationStatus.Timeout, $"Timeout-Override von {request.TimeoutOverride} überschritten.", started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail(InvocationStatus.Timeout, "Durch den Aufrufer abgebrochen.", started);
        }
        catch (Exception ex)
        {
            // Nicht die Meldung der Ausnahme: Sie kann den Antwortkörper des Upstreams tragen, und
            // der zitiert womöglich den vorgelegten Schlüssel. Sie ginge an den Agenten, in die
            // Vorgangsablage und in den Trace (Codex H7).
            var reason = Reason(connection, ex);
            return Fail(InvocationStatus.UpstreamError, $"Upstream-Fehler: {reason}", started);
        }
        }

        var result = await RunAsync().ConfigureAwait(false);

        // Ein eingelöster Vorgang bekommt einen Abschluss. Ohne ihn blieb er auf `Working` stehen
        // und lief still in den Verfall — die Vorgangsliste zeigte für einen erfolgreichen Aufruf
        // dauerhaft „läuft" und später „abgelaufen".
        if (consumedTask is { } taskId && _approvalStore is not null)
        {
            await _approvalStore.CompleteAsync(
                taskId,
                result.Status is InvocationStatus.Success
                    ? null
                    : new TaskFailure(result.Status.ToString(), result.ErrorMessage ?? result.Status.ToString()),
                ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Der Grund eines gescheiterten Aufrufs: über die echte Verbindung des Supervisors mit deren
    /// Konfiguration und vorgelegtem Token bereinigt, sonst ohne Antwortkörper (Plan 0005 C7, D1).
    /// </summary>
    private static string Reason(IUpstreamConnection connection, Exception exception)
        => connection is GuardedUpstreamConnection guarded
            ? guarded.DescribeFailure(exception)
            : UpstreamFailure.DescribeWithoutConfig(exception);

    /// <summary>
    /// Das gebaute Eingabeschema je Katalogeintrag. Der Eintrag ist der Schlüssel, nicht der Text:
    /// Ändert sich der Katalog, gibt es neue Einträge, und die alten fallen mit ihnen weg — eine
    /// Obergrenze oder ein Aufräumen braucht es nicht.
    /// <para>
    /// Ein Schema wird von gleichzeitigen Aufrufen geteilt. Dass die Auswertung dabei richtig bleibt,
    /// hält ein Test mit rekursivem <c>$ref</c> unter vielen gleichzeitigen Aufrufen fest
    /// (<c>ToolSchemaRegistryTests</c>); ein Fehler dort liesse einen Aufruf über den Ausweichweg
    /// ungeprüft durch.
    /// </para>
    /// </summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CatalogEntry, JsonSchema>
        CompiledSchemas = new();

    /// <summary>
    /// Einträge, deren Schema sich wegen seines Texts nicht <b>bauen</b> lässt, mit dem Grund. Das
    /// ist je Eintrag beständig: Ein Schema, das einmal nicht baut, baut auch beim nächsten Aufruf
    /// nicht. Weitere Aufrufe gehen denselben R3-Weg; die Ursache steht je Invoker einmal im Log
    /// (<see cref="_loggedSchemaFailures"/>). Ein Ausfall des Augenblicks
    /// (<see cref="IsTransientBuildFailure"/>) wird nicht gemerkt.
    /// <para>
    /// <b>Eine Markierung ist nicht endgültig</b> (Review-Befund L-1, zwölfte Runde): Höchstens alle
    /// <see cref="UnusableRetryInterval"/> baut ein Aufruf das Schema erneut. Hat eine Markierung
    /// einen Fehler des Augenblicks festgehalten, der zweimal gleich ausfiel, heilt sie sich so
    /// selbst, statt die Prüfung bis zum nächsten Katalog-Neubau abzuschalten. Ein wirklich nicht
    /// baubares Schema kostet dafür zwei Bauten je Intervall.
    /// </para>
    /// <para>
    /// <b>Grenze:</b> Auch Verweise löst die Bibliothek teils schon beim Bauen auf, und die eigene
    /// Registry fällt dabei auf <c>SchemaRegistry.Global</c> zurück. Ein Verweis, der beim Bauen
    /// nicht auflöst, macht das Schema hier „nicht baubar" bis zum nächsten Katalog-Neubau — auch
    /// wenn er später, nach einer globalen Registrierung, auflösen würde. Im Produkt registriert
    /// nichts global (nur die Meta-Schemata der Drafts, die die Bibliothek selbst mitbringt); das
    /// Urteil hängt damit nicht an der Startreihenfolge. Käme eine globale Registrierung hinzu,
    /// müsste dieser Zwischenspeicher sie berücksichtigen.
    /// </para>
    /// <para>
    /// <b>Auswertungsfehler gehören ausdrücklich nicht hierher.</b> Ob ein unauflösbarer
    /// <c>$ref</c> überhaupt erreicht wird, hängt an den Argumenten (<c>properties</c>,
    /// <c>anyOf</c>, <c>if/then</c> werten nur aus, was die Instanz berührt). Gemerkt, schaltete ein
    /// einziger Aufruf mit dem passenden Feld die Prüfung für alle Aufrufer ab (Review-Befund H1).
    /// Ein Aufruf, dessen Auswertung wirft, wird abgewiesen; siehe <see cref="ValidateArguments"/>.
    /// </para>
    /// </summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CatalogEntry, UnusableMark>
        UnusableSchemas = new();

    /// <summary>Wie oft ein als nicht baubar markiertes Schema höchstens erneut gebaut wird.</summary>
    internal static readonly TimeSpan UnusableRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Die Markierung „nicht baubar": der Grund und wann frühestens erneut gebaut wird. Die Zeit
    /// beginnt beim ersten Aufruf, der die Markierung sieht — gesetzt wird sie im statischen
    /// <see cref="ObtainSchema"/>, das keine Uhr kennt.
    /// <para>
    /// <b>Monotone Uhr</b> (Review-Befund L-2, dreizehnte Runde): gemessen mit
    /// <see cref="TimeProvider.GetTimestamp"/>, nicht mit der Wanduhr. Wird die Systemzeit
    /// zurückgestellt (NTP, fortgesetzte VM), hielte eine Markierung sonst Stunden statt
    /// Sekunden. Die Markierung liegt prozessweit, die Uhr kommt vom Invoker: Sinnvoll ist sie nur
    /// innerhalb einer Zeitquelle. Im Produkt gibt es genau einen Invoker; Tests mit eigener
    /// <c>FakeTimeProvider</c> haben je Welt eigene Katalogeinträge und damit eigene Markierungen.
    /// </para>
    /// </summary>
    internal sealed class UnusableMark(string reason)
    {
        private const long NotStarted = long.MinValue;
        private long _retryAtTimestamp = NotStarted;

        public string Reason { get; } = reason;

        /// <summary>
        /// Ob dieser Aufruf den nächsten Bauversuch übernimmt. Genau einer je Intervall bekommt
        /// <c>true</c>; der erste Blick startet nur die Uhr.
        /// </summary>
        public bool TryClaimRetry(TimeProvider time, TimeSpan interval)
        {
            var now = time.GetTimestamp();
            var next = now + (long)(interval.TotalSeconds * time.TimestampFrequency);
            var current = Interlocked.Read(ref _retryAtTimestamp);
            if (current == NotStarted)
            {
                Interlocked.CompareExchange(ref _retryAtTimestamp, next, NotStarted);
                return false;
            }

            return now >= current
                && Interlocked.CompareExchange(ref _retryAtTimestamp, next, current) == current;
        }
    }

    /// <summary>
    /// Welche Schemafehler dieser Invoker je Eintrag schon geloggt hat — Bau- wie Auswertungsfehler.
    /// Gegen das Fluten des Logs durch einen Upstream, dessen Schema bei jedem Aufruf scheitert. Je
    /// Instanz, damit ein zweiter Invoker mit eigenem Logger seine Zeilen selbst schreibt; ob ein
    /// Schema baut, merkt sich dagegen prozessweit <see cref="UnusableSchemas"/>.
    /// <para>
    /// Der Schlüssel ist die Art des Fehlers, bei <c>RefResolutionException</c> zusätzlich ihre
    /// Meldung: Sie nennt den Verweis aus dem Schematext, so stehen zwei verschiedene unauflösbare
    /// Verweise beide im Log. Meldungen anderer Fehlerarten gehören nicht in den Schlüssel — eine
    /// künftige Fassung der Bibliothek könnte Werte des Aufrufers hineinschreiben, und die Tabelle
    /// wüchse mit jedem Aufruf. Als zweite Sicherung nimmt sie je Eintrag höchstens
    /// <see cref="MaxLoggedSchemaFailuresPerEntry"/> Schlüssel auf; darüber wird nicht mehr geloggt.
    /// </para>
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        CatalogEntry, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> _loggedSchemaFailures = new();

    internal const int MaxLoggedSchemaFailuresPerEntry = 16;

    /// <summary>Ein leeres Argumentobjekt, einmal geparst statt je Aufruf ohne Argumente.</summary>
    private static readonly JsonElement EmptyArguments = ParseEmptyObject();

    private static JsonElement ParseEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Baut ein Schema mit einer <b>eigenen</b> Registry. Mit den Vorgaben legt JsonSchema.Net jedes
    /// gebaute Schema in <c>SchemaRegistry.Global</c> ab und gibt es nie wieder frei. Gebaut wurde bis
    /// hierher bei jedem Werkzeugaufruf — das war das Speicherwachstum des Soak-Nachtlaufs: rund
    /// 700 Bytes je Aufruf, 269 → 3768 MB in vier Stunden.
    /// <para>
    /// <b>Folge für Verweise:</b> Die eigene Registry fällt beim Nachschlagen auf die globale
    /// zurück; so lösen die Meta-Schemata der Drafts (<c>$schema</c>) weiter auf. Verweise innerhalb
    /// des Schemas (<c>#/$defs/…</c>, ein eigenes <c>$id</c>) lösen wie bisher auf. Ein <c>$ref</c>
    /// auf das <c>$id</c> eines <i>anderen</i> Werkzeugs löst nicht mehr auf — früher gelang das nur,
    /// wenn jenes zufällig schon aufgerufen worden war, und nur, solange nichts im Produkt Schemata
    /// global ablegt. Gebaut wird ein solches Schema noch; erst die Auswertung wirft
    /// <c>RefResolutionException</c>, und nur, wenn die Argumente den Verweis erreichen. Ein solcher
    /// Aufruf wird abgewiesen (siehe <see cref="ValidateArguments"/>); Aufrufe, die den Verweis nicht
    /// erreichen, werden normal geprüft.
    /// Dafür überschreiben sich zwei Werkzeuge mit demselben <c>$id</c> nicht mehr gegenseitig.
    /// </para>
    /// </summary>
    internal static JsonSchema BuildSchema(CatalogEntry entry)
        => JsonSchema.FromText(
            entry.InputSchema.GetRawText(),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    /// <summary>
    /// Serverseitige Argument-Validierung — Pflicht für den Lazy-Pfad ohne Client-Schema (ADR-0003).
    /// <para>
    /// <b>Zwei Ausweichwege, verschieden entschieden (2026-11-24).</b> Ein Schema, das sich nicht
    /// <i>bauen</i> lässt, hängt allein am Upstream: R3, der Aufruf läuft ungeprüft durch und die
    /// Ursache steht im Log. Ein Schema, das sich baut, aber bei der <i>Auswertung</i> wirft (etwa
    /// ein unauflösbarer <c>$ref</c>), weist den Aufruf ab. Ob die Auswertung wirft, wählt der
    /// Aufrufer mit seinen Argumenten; liesse sie durch, bräuchte er nur das passende Feld
    /// mitzuschicken, und jedes andere Feld des Aufrufs bliebe ungeprüft.
    /// </para>
    /// </summary>
    private Rejection? ValidateArguments(CatalogEntry entry, JsonElement args)
        => ValidateArguments(entry, args, BuildSchema);

    /// <summary>
    /// Wie oben, mit übergebenem Bau — nach demselben Muster wie <see cref="ObtainSchema"/>: Der
    /// Invoker übergibt immer <see cref="BuildSchema"/>; Tests speisen hier einen Ausfall ein, den
    /// kein Schematext auslöst, und prüfen den Status, den der Aufruf damit bekommt.
    /// </summary>
    internal Rejection? ValidateArguments(
        CatalogEntry entry,
        JsonElement args,
        System.Runtime.CompilerServices.ConditionalWeakTable<CatalogEntry, JsonSchema>.CreateValueCallback build)
    {
        if (entry.InputSchema.ValueKind is not JsonValueKind.Object)
        {
            return null; // kein Schema vorhanden → nichts zu validieren
        }

        // Ein gebautes Schema geht vor einer Markierung „nicht baubar": Hat ein gleichzeitiger
        // erster Aufruf das Schema gebaut, während ein anderer es als nicht baubar markierte, wird
        // trotzdem geprüft (Review-Befund M-1, elfte Runde).
        if (!CompiledSchemas.TryGetValue(entry, out var schema))
        {
            if (UnusableSchemas.TryGetValue(entry, out var mark)
                && !mark.TryClaimRetry(_time, UnusableRetryInterval))
            {
                LogBuildFailureOnce(entry, mark.Reason);
                return null; // R3, das Schema baut nicht
            }

            // Kein Schema und keine Markierung — oder dieser Aufruf hat den erneuten Bauversuch einer
            // Markierung übernommen (L-1, zwölfte Runde). Gelingt er, nimmt ObtainSchema die
            // Markierung zurück.
            var built = ObtainSchema(entry, build);
            if (built.Interrupted is { } interrupted)
            {
                // Ein Ausfall dieses Augenblicks, nicht des Schemas: nicht gemerkt — der nächste
                // Aufruf baut neu —, und nicht durchgelassen, denn R3 gilt nur für ein Schema, das
                // sich nicht bauen lässt. Kein Urteil über die Argumente, deshalb nicht
                // ValidationFailed, sondern ein Fehler des Gateways (Review-Befund L-3, elfte Runde).
                if (ShouldLogSchemaFailure(entry, $"build-transient:{interrupted.GetType().FullName}"))
                {
                    Log.SchemaBuildInterrupted(_logger, entry.Name.Value, interrupted.GetType().Name);
                }

                return new Rejection(
                    InvocationStatus.UpstreamError,
                    $"Interner Gateway-Fehler: Das Schema des Werkzeugs liess sich gerade nicht bauen ({interrupted.GetType().Name}); erneut versuchen.",
                    GatewayFault: true);
            }

            if (built.Schema is not { } fresh)
            {
                // Nicht baubar, zweimal mit derselben Ausnahme bestätigt: R3, gemerkt und einmal geloggt.
                LogBuildFailureOnce(entry, UnusableSchemas.TryGetValue(entry, out var confirmed) ? confirmed.Reason : "?");
                return null; // R3-Fallback: durchlassen und loggen statt fälschlich ablehnen
            }

            schema = fresh;
        }

        try
        {
            var instance = args.ValueKind is JsonValueKind.Undefined ? EmptyArguments : args;
            var evaluation = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (evaluation.IsValid)
            {
                return null;
            }

            var firstError = (evaluation.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .FirstOrDefault() ?? "Argumente entsprechen nicht dem Tool-Schema.";
            return Rejection.Invalid($"Argument-Validierung fehlgeschlagen — {firstError}");
        }
        catch (Exception ex)
        {
            // Abweisen statt durchlassen, und nicht gemerkt: Ob die Auswertung wirft, hängt an den
            // Argumenten. Den Wortlaut nur bei einem unauflösbaren Verweis weitergeben — er nennt
            // den Verweis aus dem Schema, das der Aufrufer über describe_tool ohnehin sieht. Die
            // Meldung jeder anderen Ausnahme kann Interna tragen; Aufrufer, Audit und Log bekommen
            // dann nur ihre Art (wie die äussere Fehlerbehandlung der Pipeline).
            var detail = ex is RefResolutionException ? ex.Message : ex.GetType().Name;
            if (ShouldLogSchemaFailure(entry, $"eval:{ex.GetType().FullName}:{(ex is RefResolutionException ? ex.Message : string.Empty)}"))
            {
                Log.SchemaNotEvaluable(_logger, entry.Name.Value, detail);
            }

            return Rejection.Invalid(
                $"Argument-Validierung fehlgeschlagen — das Schema des Werkzeugs lässt sich für diese Argumente nicht auswerten ({detail}).");
        }
    }

    /// <summary>
    /// Warum ein Aufruf vor dem Upstream endet: Status für Ergebnis und Audit, Meldung, und ob der
    /// Fehler im Gateway liegt statt beim Upstream (Metrik-Dimension <c>fault</c>).
    /// </summary>
    internal readonly record struct Rejection(InvocationStatus Status, string Message, bool GatewayFault = false)
    {
        public static Rejection Invalid(string message) => new(InvocationStatus.ValidationFailed, message);
    }

    /// <summary>
    /// Ob ein Baufehler vorübergehend ist — an Speicher, Stack oder einem Abbruch dieses
    /// Augenblicks, nicht am Schema. Nur diese Arten gelten so; jede andere kehrt beim nächsten
    /// Bauen wieder und nimmt den R3-Weg.
    /// <para>
    /// <b>Grenzfall Stack:</b> Würfe die Bibliothek <see cref="InsufficientExecutionStackException"/>
    /// für ein bestimmtes, sehr tief verschachteltes Schema jedes Mal, bliebe das Werkzeug
    /// abgewiesen und würde je Aufruf neu gebaut, statt R3 zu nehmen — sicher, aber teurer. Praktisch
    /// kommt es nicht dazu: Das Parsen bricht bei einer Tiefe über 64 vorher mit
    /// <see cref="JsonException"/> ab, und die nimmt R3.
    /// </para>
    /// </summary>
    internal static bool IsTransientBuildFailure(Exception exception)
        => exception is OutOfMemoryException
            or InsufficientExecutionStackException
            or OperationCanceledException
            or ThreadInterruptedException;

    /// <summary>
    /// Ob ein Baufehler am Schematext liegen <i>kann</i> — nur diese Arten führen, zweimal gleich
    /// bestätigt, zu „nicht baubar" und damit zu R3. Belegt: ein ungültiges Schlüsselwort
    /// (<c>{"type":12345}</c>) und ein unbekannter Dialekt werfen <see cref="JsonSchemaException"/>,
    /// ungültiges JSON und eine Tiefe über 64 <see cref="JsonException"/>, ein kaputtes <c>$id</c>
    /// eine <see cref="FormatException"/> (<see cref="UriFormatException"/>), ein beim Bauen
    /// unauflösbarer Verweis <see cref="RefResolutionException"/>.
    /// <para>
    /// <b>Jede andere Art ist ein Ausfall</b> (Review-Befund L-1, dreizehnte Runde): abgewiesen als
    /// Gateway-Fehler, nicht gemerkt. Eine solche Ausnahme — <c>InvalidOperationException</c>,
    /// <c>NullReferenceException</c>, <c>KeyNotFoundException</c> — stammt eher aus einem Wettlauf
    /// in den globalen Registern der Bibliothek als aus dem Schema; als „nicht baubar" gemerkt,
    /// liefen Aufrufe ungeprüft durch. Der Preis (achte Runde, M-A): Würfe die Bibliothek eine
    /// solche Art für ein bestimmtes Schema <i>jedes</i> Mal, bliebe das Werkzeug abgewiesen und
    /// würde je Aufruf neu gebaut. Das schliesst sicher, steht als
    /// <c>fault=gateway</c> in der Metrik und einmal je Invoker im Log — sichtbar, statt still
    /// ungeprüft.
    /// </para>
    /// </summary>
    internal static bool IsSchemaTextFailure(Exception exception)
        => exception is JsonSchemaException or JsonException or FormatException or RefResolutionException;

    /// <summary>Ergebnis des Bauens: ein Schema, „nicht baubar" (beides null) oder ein Ausfall.</summary>
    internal readonly record struct SchemaBuildOutcome(JsonSchema? Schema, Exception? Interrupted);

    /// <summary>
    /// Baut das Schema eines Eintrags über den Zwischenspeicher und entscheidet, was ein Baufehler
    /// bedeutet.
    /// <list type="bullet">
    /// <item>Ein vorübergehender Fehler (<see cref="IsTransientBuildFailure"/>) ist ein Ausfall:
    /// nicht gemerkt, der Aufruf wird abgewiesen.</item>
    /// <item>Jeder andere Fehler bekommt einen zweiten Bau. Gelingt er, zählt sein Schema.</item>
    /// <item>Scheitert auch er, ist das Schema nur dann nicht baubar — gemerkt in
    /// <see cref="UnusableSchemas"/>, R3 —, wenn der Fehler am Schematext liegen kann
    /// (<see cref="IsSchemaTextFailure"/>) und der zweite Bau dieselbe Art wirft. Sonst war es ein
    /// Fehler des Augenblicks oder der Bibliothek (etwa ein Wettlauf in deren globalen Registern
    /// bei gleichzeitigen ersten Aufrufen): ein Ausfall.</item>
    /// </list>
    /// Ohne die Bestätigung schaltete ein einziger solcher Fehler die Prüfung bis zum nächsten
    /// Katalog-Neubau ab — auf main betraf er genau einen Aufruf (Review-Befund M-1, zehnte Runde).
    /// Die Bestätigung kostet einen Bau mehr, nur auf dem Fehlerweg und nur bis zum Merken.
    /// <para>
    /// <b>Gegen gleichzeitige Erstaufrufe (elfte Runde).</b> Der Bestätigungsbau läuft unter einer
    /// prozessweiten Sperre, so wiederholt ihn kein anderer Bestätigungsbau zur selben Zeit (M-2);
    /// verglichen wird nur die Art der Ausnahme (zwölfte Runde, M-1), und nur für Arten aus
    /// <see cref="IsSchemaTextFailure"/> (dreizehnte Runde, L-1). Nach dem Merken wird nachgesehen,
    /// ob ein gleichzeitiger Aufruf das Schema inzwischen gebaut hat — dann gilt dessen Schema und die Markierung fällt; ebenso nimmt jeder gelungene
    /// Bau eine ältere Markierung zurück (M-1). <c>ValidateArguments</c> sieht zudem zuerst in die
    /// gebauten Schemas und erst danach in die Markierungen.
    /// </para>
    /// <para>
    /// Der Bau wird übergeben, damit Tests einen Fehler einspeisen können, den kein Schematext
    /// auslöst; der Invoker übergibt immer <see cref="BuildSchema"/>. Eine Einstellung am Invoker
    /// gibt es dafür nicht.
    /// </para>
    /// </summary>
    internal static SchemaBuildOutcome ObtainSchema(
        CatalogEntry entry,
        System.Runtime.CompilerServices.ConditionalWeakTable<CatalogEntry, JsonSchema>.CreateValueCallback build)
    {
        try
        {
            return Built(entry, CompiledSchemas.GetValue(entry, build));
        }
        catch (Exception first) when (!IsTransientBuildFailure(first))
        {
            lock (ConfirmationLock)
            {
                try
                {
                    return Built(entry, CompiledSchemas.GetValue(entry, build));
                }
                // Nur die Art, nicht die Meldung: Die Bibliothek kann je Bau eine andere Meldung
                // liefern (etwa mit der zufälligen Basis-Adresse eines Schemas ohne $id); ein nicht
                // baubares Schema galte sonst als Ausfall und wäre dauerhaft gesperrt (Review-Befund
                // M-1, zwölfte Runde). Gegen Wettläufe schützt die Sperre, gegen eine stehen
                // gebliebene Markierung der erneute Bauversuch (UnusableRetryInterval).
                catch (Exception second) when (IsSchemaTextFailure(first) && second.GetType() == first.GetType())
                {
                    UnusableSchemas.TryAdd(entry, new UnusableMark(second.Message));

                    // Hat ein gleichzeitiger Aufruf das Schema inzwischen gebaut, gilt seins.
                    if (CompiledSchemas.TryGetValue(entry, out var meanwhile))
                    {
                        UnusableSchemas.Remove(entry);
                        return new SchemaBuildOutcome(meanwhile, null);
                    }

                    return new SchemaBuildOutcome(null, null);
                }
                catch (Exception second)
                {
                    return new SchemaBuildOutcome(null, second);
                }
            }
        }
        catch (Exception interrupted)
        {
            return new SchemaBuildOutcome(null, interrupted);
        }

        // Ein gelungener Bau nimmt eine ältere Markierung zurück, die ein gleichzeitiger Aufruf
        // gesetzt hat, bevor dieses Schema im Zwischenspeicher lag.
        static SchemaBuildOutcome Built(CatalogEntry entry, JsonSchema schema)
        {
            UnusableSchemas.Remove(entry);
            return new SchemaBuildOutcome(schema, null);
        }
    }

    /// <summary>Die Sperre für Bestätigungsbauten; nur auf dem Fehlerweg genommen.</summary>
    private static readonly Lock ConfirmationLock = new();

    private void LogBuildFailureOnce(CatalogEntry entry, string reason)
    {
        if (ShouldLogSchemaFailure(entry, "build"))
        {
            Log.SchemaUnparseable(_logger, entry.Name.Value, reason);
        }
    }

    /// <summary>
    /// Ob dieser Invoker den Fehler zum ersten Mal sieht — mit fester Obergrenze je Eintrag. Unter
    /// einer Sperre je Eintrag, damit gleichzeitige Aufrufe die Grenze nicht überschreiten
    /// (Review-Befund L-5, zehnte Runde); gesperrt wird nur auf dem Fehlerweg.
    /// </summary>
    private bool ShouldLogSchemaFailure(CatalogEntry entry, string key)
    {
        var logged = _loggedSchemaFailures.GetOrCreateValue(entry);
        lock (logged)
        {
            return logged.Count < MaxLoggedSchemaFailuresPerEntry && logged.TryAdd(key, 0);
        }
    }

    /// <summary>
    /// Prüft die Freigabe-Pflicht (FR-32, ADR-0012). Liefert eine Meldung, wenn der Call auf eine
    /// Freigabe warten muss — sonst null (frei oder nicht freigabepflichtig).
    /// </summary>
    /// <summary>Ergebnis der Freigabe-Prüfung: Meldung und — wenn es einen gibt — der Vorgang.</summary>
    private sealed record ApprovalOutcome(string Message, Guid? TaskId);

    /// <summary>
    /// Prüft die Freigabepflicht. <c>Outcome</c> gesetzt heißt: Der Aufruf läuft nicht.
    /// <c>ConsumedTask</c> gesetzt heißt: Er läuft, und dieser Vorgang gehört dazu — er will nach
    /// dem Aufruf abgeschlossen werden.
    /// </summary>
    private async Task<(ApprovalOutcome? Outcome, Guid? ConsumedTask)> CheckApprovalAsync(
        CatalogEntry entry, ToolInvocationRequest request, CancellationToken ct)
    {
        // Zwei Quellen fuer „scharf" (Politik und Selbstauskunft des Katalogs) und zwei Wege
        // (Warteschlange oder Client) — zusammengefuehrt in der Politik, damit es nicht an vier
        // Stellen je anders passiert. Ohne Politik-Dienst bleibt es beim strengeren Verhalten:
        // Was sich selbst als scharf meldet, wartet.
        var enforcement = _approvalPolicy is { } policy
            ? policy.EffectiveFor(request.Tool, entry.RequiresApproval)
            : entry.RequiresApproval ? ApprovalEnforcement.Queue : null;

        // Client-Modus heisst: Das Gateway haelt hier NICHTS mehr auf. Der Aufruf laeuft durch,
        // die Rueckfrage passiert beim Client (ADR-0022) — erzwungen ueber den Aufrufweg
        // invoke_sensitive_tool, den die Protokollschicht prueft, nicht diese.
        if (enforcement is not ApprovalEnforcement.Queue)
        {
            return (null, null);
        }

        if (_approvalStore is null)
        {
            return (new ApprovalOutcome(
                "Dieses Tool erfordert eine menschliche Freigabe, aber der Approval-Store ist nicht verfügbar.",
                TaskId: null), null);
        }

        // Fingerprint über die REDIGIERTEN Argumente — die Queue soll keine Secrets im Klartext
        // halten, und die Freigabe bindet trotzdem an genau diesen Aufruf.
        var redacted = RedactArguments(entry, request.Tool, request.Arguments);
        var fingerprint = ApprovalFingerprint.Compute(request.Caller, request.Tool, redacted);

        if (await _approvalStore.TryConsumeApprovalAsync(request.Caller, request.Tool, fingerprint, ct)
            .ConfigureAwait(false) is { } consumedTask)
        {
            // Freigegeben — der Call läuft einmalig durch. Die Id geht mit, damit der Vorgang danach
            // einen Abschluss bekommt statt still in den Verfall zu laufen.
            return (null, consumedTask);
        }

        var now = _time.GetUtcNow();
        var requestId = await _approvalStore.EnqueueAsync(
            new ApprovalRequest(
                Id: Guid.Empty, // vom Store vergeben
                request.Caller,
                _authorization.DescribeCaller(request.Caller) ?? request.Caller.ToString(),
                request.Tool,
                fingerprint,
                redacted.ValueKind is JsonValueKind.Undefined ? null : redacted,
                ApprovalState.Pending,
                RequestedAt: now,
                ExpiresAt: now + _approvalTtl),
            ct).ConfigureAwait(false);

        Log.ApprovalRequired(_logger, request.Tool.Value, requestId);
        return (new ApprovalOutcome(
            $"Dieses Tool erfordert eine menschliche Freigabe. Anfrage {requestId} wurde in die "
            + "Warteschlange gelegt; nach Freigabe denselben Aufruf erneut absetzen. NICHT sofort "
            + "wiederholen — die Freigabe erfolgt asynchron in der Verwaltungsoberfläche.",
            requestId), null);
    }

    /// <summary>
    /// Führt die Inhaltsprüfung aus und liefert die Fehlermeldung, wenn blockiert wird — sonst null.
    /// Beobachtete Treffer landen im Log, ohne den Call zu stoppen (Probelauf, ADR-0011).
    /// </summary>
    private string? Guard(JsonElement payload, GuardDirection direction)
    {
        if (_guard is null || payload.ValueKind is JsonValueKind.Undefined)
        {
            return null;
        }

        var raw = payload.GetRawText();
        if (raw.Length > _guardOptions.MaxScanChars)
        {
            // Ungeprüft heißt blockiert (ADR-0011, E4): Ein Groß-Ergebnis durchzulassen wäre der
            // blinde Fleck, den ein Angreifer ansteuert.
            Log.GuardPayloadTooLarge(_logger, direction.ToString(), raw.Length, _guardOptions.MaxScanChars);
            return GuardMessages.TooLarge(raw.Length, _guardOptions.MaxScanChars);
        }

        var verdict = _guard.Inspect(raw, direction);
        if (verdict.Findings.Count == 0)
        {
            return null;
        }

        foreach (var finding in verdict.Findings)
        {
            // Nur Regel-Id und Fingerabdruck — nie der Fund selbst.
            Log.GuardFinding(
                _logger, finding.RuleId, direction.ToString(), finding.Mode.ToString(), finding.Fingerprint);
        }

        return verdict.Blocked
            ? direction == GuardDirection.Outbound
                ? GuardMessages.Outbound(verdict.Findings)
                : GuardMessages.Inbound(verdict.Findings)
            : null;
    }

    private void Audit(ToolInvocationRequest request, CatalogEntry? entry, ToolInvocationResult result)
    {
        var redacted = RedactArguments(entry, request.Tool, request.Arguments);

        // FR-24: Ergebnis-Payloads landen nur im ausdrücklich aktivierten Debug-Modus im Log —
        // und auch dann maskiert, denn Antworten tragen genauso Secrets wie Argumente.
        JsonElement? response = _auditOptions.CaptureResponsePayloads && result.Content is { } content
            ? _redaction.RedactArguments(request.Tool, content)
            : null;

        _audit.Record(new AuditEvent(
            _time.GetUtcNow(),
            request.Caller,
            request.Origin,
            AuditEventKind.ToolCall,
            entry?.Server,
            request.Tool.Value,
            result.Status,
            redacted.ValueKind is JsonValueKind.Undefined ? null : redacted,
            RequestBytes: request.Arguments.ValueKind is JsonValueKind.Undefined ? 0 : request.Arguments.GetRawText().Length,
            ResponseBytes: result.Content?.GetRawText().Length,
            Duration: result.Duration,
            CallerRoles: _authorization.DescribeCaller(request.Caller),
            RedactedResponse: response));
    }

    private static ToolInvocationResult Fail(InvocationStatus status, string message, long started)
        => new(status, null, message, Elapsed(started));

    private JsonElement RedactArguments(
        CatalogEntry? entry, NamespacedToolName tool, JsonElement arguments)
    {
        var redacted = _redaction.RedactArguments(tool, arguments);
        if (redacted.ValueKind != JsonValueKind.Object
            || entry?.InputSchema.ValueKind != JsonValueKind.Object
            || !entry.InputSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return redacted;
        }

        var node = JsonNode.Parse(redacted.GetRawText())!.AsObject();
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("writeOnly", out var writeOnly)
                && writeOnly.ValueKind == JsonValueKind.True
                && node.ContainsKey(property.Name))
            {
                node[property.Name] = Bifrost.Core.Audit.RedactionService.Mask;
            }
        }

        return JsonSerializer.SerializeToElement(node);
    }

    private static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Schema von {Tool} nicht validierbar ({Reason}) — Call wird ohne Validierung durchgelassen (R3-Fallback).")]
        public static partial void SchemaUnparseable(ILogger logger, string tool, string reason);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Schema von {Tool} nicht auswertbar ({Reason}) — Call wird abgewiesen.")]
        public static partial void SchemaNotEvaluable(ILogger logger, string tool, string reason);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Schema von {Tool} liess sich gerade nicht bauen ({Reason}) — Call wird abgewiesen, der nächste baut neu.")]
        public static partial void SchemaBuildInterrupted(ILogger logger, string tool, string reason);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Unerwarteter Fehler in der Invoker-Pipeline für {Tool}: {Types}.")]
        public static partial void UnexpectedPipelineError(ILogger logger, string types, string tool);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Ergebnis von {Tool} gekürzt: {OriginalChars} → {TruncatedChars} Zeichen (FR-16).")]
        public static partial void ResultTruncated(ILogger logger, string tool, int originalChars, int truncatedChars);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Guardrail {Mode}: Regel {RuleId} griff {Direction}, Fingerabdruck {Fingerprint} (ADR-0011).")]
        public static partial void GuardFinding(
            ILogger logger, string ruleId, string direction, string mode, string fingerprint);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Guardrail: Nutzlast {Direction} mit {Chars} Zeichen über der Prüfgrenze {Limit} — nicht durchgelassen.")]
        public static partial void GuardPayloadTooLarge(ILogger logger, string direction, int chars, int limit);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Freigabe erforderlich für {Tool}; Anfrage {RequestId} in der Warteschlange (FR-32).")]
        public static partial void ApprovalRequired(ILogger logger, string tool, Guid requestId);
    }
}
