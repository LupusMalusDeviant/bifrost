using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Bifrost.Abstractions;
using Bifrost.Core.Approvals;
using Bifrost.Core.Guardrails;
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
                else if (ValidateArguments(entry, request.Arguments) is { } validationError)
                {
                    result = Fail(InvocationStatus.ValidationFailed, validationError, started);
                }
                else
                {
                    result = await CallUpstreamAsync(entry, request, started, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.UnexpectedPipelineError(_logger, ex, request.Tool.Value);
            result = Fail(InvocationStatus.UpstreamError, $"Interner Gateway-Fehler: {ex.Message}", started);
        }

        Audit(request, entry, result);

        // FR-26 verlangt Auswertung pro Server UND Tool — der Server-Slug steckt im Namespace.
        var server = request.Tool.TrySplit(out var slug, out _) ? slug : "unknown";

        if (activity is not null)
        {
            activity.SetTag("bifrost.server", server);
            activity.SetTag("bifrost.status", result.Status.ToString());
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
            new KeyValuePair<string, object?>("origin", request.Origin.ToString()));
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
            return Fail(InvocationStatus.Timeout, ex.Message, started);
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
            return Fail(InvocationStatus.UpstreamError, $"Upstream-Fehler: {ex.Message}", started);
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

    /// <summary>Serverseitige Argument-Validierung — Pflicht für den Lazy-Pfad ohne Client-Schema (ADR-0003).</summary>
    private string? ValidateArguments(CatalogEntry entry, JsonElement args)
    {
        if (entry.InputSchema.ValueKind is not JsonValueKind.Object)
        {
            return null; // kein Schema vorhanden → nichts zu validieren
        }

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(entry.InputSchema.GetRawText());
        }
        catch (Exception ex)
        {
            Log.SchemaUnparseable(_logger, entry.Name.Value, ex.Message);
            return null; // R3-Fallback: durchlassen und loggen statt fälschlich ablehnen
        }

        try
        {
            var instance = args.ValueKind is JsonValueKind.Undefined
                ? JsonDocument.Parse("{}").RootElement
                : args;
            var evaluation = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (evaluation.IsValid)
            {
                return null;
            }

            var firstError = (evaluation.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .FirstOrDefault() ?? "Argumente entsprechen nicht dem Tool-Schema.";
            return $"Argument-Validierung fehlgeschlagen — {firstError}";
        }
        catch (Exception ex)
        {
            Log.SchemaUnparseable(_logger, entry.Name.Value, ex.Message);
            return null;
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

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Unerwarteter Fehler in der Invoker-Pipeline für {Tool}.")]
        public static partial void UnexpectedPipelineError(ILogger logger, Exception ex, string tool);

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
