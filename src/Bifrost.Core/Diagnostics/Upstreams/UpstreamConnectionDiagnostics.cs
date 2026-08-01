using System.Globalization;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics.Checks;
using Bifrost.Core.Execution;
using Bifrost.Core.Importing;
using Bifrost.Core.Upstreams;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Core.Diagnostics.Upstreams;

/// <summary>
/// Der Verbindungstest mit Zeitlinie (WP4.6): Statt „Verbindung fehlgeschlagen" nennt er die Stufe,
/// an der es endete, und was danach nicht mehr geprüft werden konnte.
/// </summary>
public interface IUpstreamConnectionDiagnostics
{
    Task<UpstreamDiagnosticReport> DiagnoseAsync(UpstreamServerConfig config, CancellationToken ct);
}

/// <summary>
/// Die Umsetzung. Sie läuft die Stufen der Reihe nach, und die <b>erste</b> scheiternde beendet die
/// Kette — alles danach wird als <i>nicht erreicht</i> geführt, nicht als Fehler.
/// <para>
/// <b>Derselbe Weg wie die Aktivierung, nicht ein zweiter.</b> Zwei Wege wären zwei Wahrheiten
/// darüber, ob eine Konfiguration funktioniert, und der Test wäre irgendwann grün, wo die
/// Aktivierung scheitert. Deshalb ruft diese Klasse an jeder Stufe genau das auf, was auch
/// <c>UpstreamSupervisor.AddAsync</c> aufruft:
/// </para>
/// <list type="bullet">
/// <item>Stufe 1 <see cref="UpstreamConfigValidator.Validate(UpstreamServerConfig)"/> — dieselbe Prüfung.</item>
/// <item>Stufe 2 <see cref="HostExecutionGuard"/> — derselbe Torposten (ADR-0025 E4).</item>
/// <item>Stufen 5–7 <see cref="IUpstreamConnectionTester"/> — der vorhandene, transiente Test, der
/// selbst <c>connector.ConnectAsync</c> + <c>DiscoverAsync</c> macht. Er wird <b>benutzt</b> und
/// nicht nachgebaut; Anmeldung, Handshake und Discovery passieren innerhalb dieser beiden Aufrufe
/// und werden aus dem Ergebnis eingeordnet (<see cref="UpstreamFailureCatalog"/>).</item>
/// </list>
/// <para>
/// Eigen sind nur die Stufen 3 und 4: Namensauflösung und Zielprüfung passieren im Connector zu
/// spät, um noch als Ursache benennbar zu sein. Sie sind hier <b>Vorschau</b>, nicht Ersatz — die
/// bindende Zielprüfung bleibt <c>RemoteSpecFetcher.EnsureTargetAllowedAsync</c> in
/// <c>Bifrost.Upstream</c>. Die Adressbereiche kommen aus <see cref="ImportNetworkTarget"/>, also
/// aus der Einstufung, die es in <c>Bifrost.Core</c> bereits gibt — eine dritte Abschrift derselben
/// Tabelle wäre die, die als Erste veraltet.
/// </para>
/// </summary>
public sealed partial class UpstreamConnectionDiagnostics : IUpstreamConnectionDiagnostics
{
    private readonly IUpstreamConnectionTester _tester;
    private readonly IHostExecutionPolicy? _hostExecution;
    private readonly IHostResolutionProbe _resolution;
    private readonly IFileProbe _files;
    private readonly IProcessProbe _processes;
    private readonly IUpstreamNegotiationProbe? _negotiation;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public UpstreamConnectionDiagnostics(
        IUpstreamConnectionTester tester,
        IHostExecutionPolicy? hostExecution = null,
        IHostResolutionProbe? resolution = null,
        IFileProbe? files = null,
        IProcessProbe? processes = null,
        IUpstreamNegotiationProbe? negotiation = null,
        TimeProvider? timeProvider = null,
        ILogger<UpstreamConnectionDiagnostics>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tester);
        _tester = tester;
        _hostExecution = hostExecution;
        _resolution = resolution ?? SystemHostResolutionProbe.Instance;
        _files = files ?? SystemFileProbe.Instance;
        _processes = processes ?? SystemProcessProbe.Instance;
        _negotiation = negotiation;
        _time = timeProvider ?? TimeProvider.System;
        _log = logger ?? NullLogger<UpstreamConnectionDiagnostics>.Instance;
    }

    /// <summary>
    /// Läuft die Zeitlinie. Der Bericht kommt immer — auch wenn eine Stufe wirft; ein
    /// Diagnosewerkzeug, das selbst abstürzt, beantwortet die Frage nicht und nimmt dem Betreiber
    /// gleichzeitig die Zeit, sie anders zu beantworten.
    /// </summary>
    [HostExecutionChecked]
    public async Task<UpstreamDiagnosticReport> DiagnoseAsync(
        UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var requestId = NewRequestId();
        var startedAt = _time.GetUtcNow();
        var stopwatch = _time.GetTimestamp();
        var timeline = new Timeline();

        // ── Stufe 1: Validierung ────────────────────────────────────────────────────────────────
        // Die Aufbauprüfung des Kerns, nicht eine eigene. Die policyführende Überladung folgt in
        // Stufe 2 getrennt — sonst stünde eine verbotene Ausführungsart als "Formfehler" da.
        try
        {
            UpstreamConfigValidator.Validate(config);
            timeline.Pass(UpstreamStage.Validation, "Die Konfiguration ist in sich stimmig.");
        }
        catch (ArgumentException exception)
        {
            timeline.Fail(
                UpstreamStage.Validation,
                exception.Message,
                "Die Meldung nennt das Feld. Sie kommt aus derselben Prüfung, die auch beim "
                + "Anschliessen läuft — was hier durchfällt, würde auch dort abgewiesen.");
            return Finish(config, requestId, startedAt, stopwatch, timeline, null);
        }

        // ── Stufe 2: Policy (ADR-0025 E4) ───────────────────────────────────────────────────────
        var decision = HostExecutionGuard.Evaluate(_hostExecution, config);
        if (!decision.Allowed)
        {
            timeline.Fail(
                UpstreamStage.Policy,
                $"[{decision.ReasonCode}] {decision.Summary}",
                decision.Remediation ?? "Siehe BFR-POL-0010 im Instanzbericht.",
                ("reason_code", decision.ReasonCode.ToString()));
            return Finish(config, requestId, startedAt, stopwatch, timeline, null);
        }

        timeline.Pass(
            UpstreamStage.Policy,
            decision.Summary,
            ("reason_code", decision.ReasonCode.ToString()));

        // ── Stufe 3: Runtime/DNS ────────────────────────────────────────────────────────────────
        var target = NetworkTargetOf(config);
        var resolved = await RunRuntimeStageAsync(config, target, timeline, ct).ConfigureAwait(false);
        if (timeline.Ended)
        {
            return Finish(config, requestId, startedAt, stopwatch, timeline, null);
        }

        // ── Stufe 4: Zielschutz ─────────────────────────────────────────────────────────────────
        RunTargetGuardStage(target, resolved, timeline);
        if (timeline.Ended)
        {
            return Finish(config, requestId, startedAt, stopwatch, timeline, null);
        }

        // ── Stufen 5–7: der echte Versuch ───────────────────────────────────────────────────────
        var negotiation = await RunAttemptAsync(config, timeline, ct).ConfigureAwait(false);
        return Finish(config, requestId, startedAt, stopwatch, timeline, negotiation);
    }

    /// <summary>
    /// Stufe 3. Für nativ startende Upstreams: Liegt das Programm da, antwortet die Runtime? Für
    /// Netz-Upstreams: Löst der Name auf? Liefert die Adressen für Stufe 4 mit.
    /// </summary>
    private async Task<HostResolution?> RunRuntimeStageAsync(
        UpstreamServerConfig config, NetworkTarget? target, Timeline timeline, CancellationToken ct)
    {
        if (target is not null)
        {
            var resolution = await _resolution.ResolveAsync(target.Uri.Host, ct).ConfigureAwait(false);
            if (!resolution.Resolved)
            {
                timeline.Fail(
                    UpstreamStage.Runtime,
                    $"Der Name '{target.Uri.Host}' liess sich nicht auflösen: {resolution.Failure}",
                    UpstreamFailureCatalog.Classify(resolution.Failure, config.Kind).Remediation,
                    ("host", target.Uri.Host));
                return resolution;
            }

            timeline.Pass(
                UpstreamStage.Runtime,
                resolution.WasLiteral
                    ? $"'{target.Uri.Host}' ist bereits eine Adresse — es war nichts aufzulösen."
                    : $"'{target.Uri.Host}' löst auf {resolution.Addresses.Count} Adresse(n) auf.",
                ("host", target.Uri.Host),
                ("adressen", string.Join(", ", resolution.Addresses)));
            return resolution;
        }

        var isolation = IsolationOf(config);
        if (isolation is { Mode: IsolationMode.Container })
        {
            await RunContainerRuntimeStageAsync(isolation, timeline, ct).ConfigureAwait(false);
            return null;
        }

        var program = ProgramPathOf(config);
        if (program is null)
        {
            timeline.Skip(
                UpstreamStage.Runtime,
                $"Für den Transport {config.Kind} gibt es weder ein Programm noch ein Netzwerkziel, "
                + "das sich vorab prüfen liesse.");
            return null;
        }

        // Ein Name ohne Pfadangabe wird über PATH gesucht — das entscheidet erst der Start, und zwar
        // in der Umgebung, in der das Gateway läuft. Hier so zu tun, als wüsste man es, wäre eine
        // Aussage über den falschen Rechner.
        if (!Path.IsPathFullyQualified(program))
        {
            timeline.Skip(
                UpstreamStage.Runtime,
                $"'{program}' ist kein absoluter Pfad und wird über PATH gesucht. Ob er dort liegt, "
                + "entscheidet sich erst beim Start.",
                ("programm", program));
            return null;
        }

        if (_files.FileExists(program))
        {
            timeline.Pass(UpstreamStage.Runtime, $"Das Programm '{program}' ist vorhanden.",
                ("programm", program));
            return null;
        }

        timeline.Fail(
            UpstreamStage.Runtime,
            $"Am angegebenen Pfad '{program}' liegt keine Datei.",
            "Pfad korrigieren. Läuft das Gateway im Container, muss das Programm IM Container "
            + "liegen — ein Pfad des Hostrechners zeigt dort ins Leere.",
            ("programm", program));
        return null;
    }

    /// <summary>
    /// Die Container-Runtime wird nicht hier neu befragt, sondern durch den vorhandenen Check
    /// BFR-RT-0001 — dieselbe Frage zweimal zu stellen hiesse, sie irgendwann verschieden zu
    /// beantworten. Sein Befund wird unter dem Stufencode wiedergegeben und nennt seine Herkunft.
    /// </summary>
    private async Task RunContainerRuntimeStageAsync(
        IsolationOptions isolation, Timeline timeline, CancellationToken ct)
    {
        var context = new DiagnosticContext
        {
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ContainerIsolationConfigured = true,
            ContainerRuntimeName = isolation.Runtime,
            Processes = _processes,
        };

        var finding = await new ContainerRuntimeCheck().RunAsync(context, ct).ConfigureAwait(false);
        var details = new (string, string)[]
        {
            ("runtime", isolation.Runtime),
            ("herkunft", DiagnosticCodes.ContainerRuntime),
        };

        if (finding.Status is CheckStatus.Fail)
        {
            timeline.Fail(
                UpstreamStage.Runtime,
                finding.Summary,
                finding.Remediation ?? $"Siehe {DiagnosticCodes.ContainerRuntime} im Instanzbericht.",
                details);
            return;
        }

        timeline.Pass(UpstreamStage.Runtime, finding.Summary, details);
    }

    /// <summary>
    /// Stufe 4. Vorschau auf die Zielprüfung, die der Connector bindend macht: Zeigt eine der
    /// aufgelösten Adressen nach innen, ohne dass das freigegeben wäre?
    /// </summary>
    private static void RunTargetGuardStage(
        NetworkTarget? target, HostResolution? resolved, Timeline timeline)
    {
        if (target is null)
        {
            timeline.Skip(
                UpstreamStage.TargetGuard,
                "Dieser Upstream ruft keine vom Betreiber genannte Adresse ab — es gibt kein Ziel "
                + "zu schützen.");
            return;
        }

        if (target.AllowPrivate is null)
        {
            // 'null' heisst bei HttpTransportOptions ausdruecklich "nicht entschieden", nicht
            // "verboten" (Bestandsschutz). Daraus hier ein Nein zu machen, klemmte einen Upstream
            // ab, der laeuft — dieselbe stille Verhaltensaenderung, die ADR-0025 E3 ablehnt.
            timeline.Skip(
                UpstreamStage.TargetGuard,
                "Für diese Konfiguration ist 'Ziele im internen Netz erlauben' nie entschieden "
                + "worden (Altbestand). Sie wird deshalb nicht abgewiesen — die Entscheidung sollte "
                + "beim nächsten Bearbeiten ausdrücklich fallen.",
                ("host", target.Uri.Host));
            return;
        }

        if (target.AllowPrivate is true)
        {
            timeline.Pass(
                UpstreamStage.TargetGuard,
                "Interne Ziele sind für diesen Upstream ausdrücklich freigegeben.",
                ("host", target.Uri.Host), ("intern_erlaubt", "ja"));
            return;
        }

        var addresses = resolved?.Addresses ?? [];
        var internals = addresses
            .Where(address => ImportNetworkTarget.ClassifyHost(address) is ImportTargetReach.Private)
            .ToList();

        if (internals.Count == 0)
        {
            timeline.Pass(
                UpstreamStage.TargetGuard,
                addresses.Count == 0
                    ? "Es liegen keine aufgelösten Adressen vor; erkennbar interne Angaben gibt es nicht."
                    : $"Keine der {addresses.Count} Adressen zeigt in ein internes Netz.",
                ("host", target.Uri.Host), ("intern_erlaubt", "nein"));
            return;
        }

        timeline.Fail(
            UpstreamStage.TargetGuard,
            $"'{target.Uri.Host}' zeigt auf die interne Adresse {internals[0]}"
            + (internals.Count > 1 ? $" (und {internals.Count - 1} weitere)" : string.Empty) + ".",
            "Wenn der Dienst wirklich im eigenen Netz steht, 'Ziele im internen Netz erlauben' "
            + "ausdrücklich setzen — sonst die Adresse korrigieren. Ohne diese Prüfung wäre das "
            + "Gateway ein Weg, interne Dienste zu erreichen (Cloud-Metadaten, Admin-Ports).",
            ("host", target.Uri.Host), ("interne_adresse", internals[0]));
    }

    /// <summary>
    /// Stufen 5–7. Ein einziger echter Versuch über den vorhandenen Verbindungstest; sein Ergebnis
    /// wird auf die drei Stufen verteilt.
    /// </summary>
    private async Task<UpstreamNegotiation?> RunAttemptAsync(
        UpstreamServerConfig config, Timeline timeline, CancellationToken ct)
    {
        var authConfigured = DescribeConfiguredAuth(config);
        var result = await _tester.TestAsync(config, ct).ConfigureAwait(false);

        if (result.Success)
        {
            if (authConfigured is null)
            {
                timeline.Skip(
                    UpstreamStage.Auth,
                    "Für diesen Upstream sind keine Zugangsdaten hinterlegt; es war nichts vorzulegen.");
            }
            else
            {
                timeline.Pass(
                    UpstreamStage.Auth,
                    $"Die Anmeldung ({authConfigured}) wurde angenommen.",
                    ("anmeldung", authConfigured));
            }

            timeline.Pass(UpstreamStage.Handshake, "Der Transport steht und das Protokoll passt.");
            timeline.Pass(
                UpstreamStage.Discovery,
                $"Der Katalog kam an: {result.ToolCount} Werkzeug(e).",
                ("werkzeuge", result.ToolCount.ToString(CultureInfo.InvariantCulture)));

            return await DescribeNegotiationAsync(config, result, ct).ConfigureAwait(false);
        }

        var message = result.Error ?? "Der Versuch scheiterte ohne Meldung.";
        var verdict = UpstreamFailureCatalog.Classify(message, config.Kind);

        // Alles VOR der schuldigen Stufe ist belegt — der Versuch ist ja bis dorthin gekommen.
        foreach (var stage in UpstreamStages.All.Where(stage => stage > UpstreamStage.TargetGuard))
        {
            if (stage >= verdict.Stage)
            {
                break;
            }

            if (stage is UpstreamStage.Auth && authConfigured is null)
            {
                timeline.Skip(
                    UpstreamStage.Auth,
                    "Für diesen Upstream sind keine Zugangsdaten hinterlegt; es war nichts vorzulegen.");
                continue;
            }

            timeline.Pass(stage, $"{UpstreamStages.Label(stage)} durchlaufen.");
        }

        // Landet das Urteil auf einer Stufe, die schon einen Befund hat (Zielschutz per Weiterleitung,
        // Runtime aus dem Connector heraus), gewinnt der Befund des echten Versuchs: Er ist die
        // spätere und die bindende Auskunft.
        timeline.Fail(
            verdict.Stage,
            message,
            verdict.Remediation,
            ("einordnung", verdict.Confident ? "bekanntes Muster" : "Rückfall — nicht eindeutig"));

        return null;
    }

    /// <summary>
    /// Was die Gegenstelle über sich gesagt hat — <b>beobachtet</b>, nie abgeleitet.
    /// <para>
    /// Zwei Quellen, in dieser Reihenfolge: Steht der Upstream bereits angeschlossen, gilt seine
    /// laufende Verbindung — sie ist die, die den Verkehr trägt. Sonst gilt, was der transiente
    /// Versuch soeben selbst gesehen hat. Seit WP-Upstream-Vertrag liefert auch er die Fassung: Sie
    /// wird abgelesen, solange seine Verbindung noch steht.
    /// </para>
    /// </summary>
    private async Task<UpstreamNegotiation?> DescribeNegotiationAsync(
        UpstreamServerConfig config, UpstreamTestResult result, CancellationToken ct)
    {
        if (_negotiation is not null)
        {
            var facts = await _negotiation
                .DescribeAsync(config.Slug, config.Kind, ct).ConfigureAwait(false);
            if (facts is not null)
            {
                return facts;
            }
        }

        var observed = result.Protocol;
        var capabilities = new List<string>();
        if (result.ToolCount > 0)
        {
            capabilities.Add("tools");
        }

        if (observed is not null)
        {
            capabilities.AddRange(observed.Capabilities.Except(capabilities, StringComparer.Ordinal));
        }

        return new UpstreamNegotiation(
            config.Kind.ToString(),
            observed?.Version,
            capabilities,
            result.ToolCount,
            NegotiationNote(observed),
            observed?.Availability ?? UpstreamProtocolAvailability.Unknown);
    }

    /// <summary>
    /// Der Satz unter der Angabe. Fehlt die Fassung, steht hier <b>warum</b> — und zwar mit dem
    /// Grund der Verbindung selbst, nicht mit einem hier erfundenen. Steht sie da, sagt der Satz,
    /// woher sie stammt: Eine Zahl ohne Herkunft ist in einem Störungsfall schwer zu glauben.
    /// </summary>
    private static string? NegotiationNote(UpstreamProtocolInfo? observed) => observed switch
    {
        null => "Der Versuch lieferte keine Angabe zum Protokoll. Kein Ersatzwert: Was aus der "
            + "Konfiguration abgeleitet wäre, sähe aus wie eine Messung.",
        { Availability: UpstreamProtocolAvailability.Negotiated } => "Abgelesen am transienten "
            + "Versuch, solange dessen Verbindung noch stand — nicht aus der Konfiguration.",
        _ => observed.Reason,
    };

    private UpstreamDiagnosticReport Finish(
        UpstreamServerConfig config,
        string requestId,
        DateTimeOffset startedAt,
        long stopwatch,
        Timeline timeline,
        UpstreamNegotiation? negotiation)
    {
        var report = new UpstreamDiagnosticReport(
            config.Slug,
            config.Kind,
            requestId,
            startedAt,
            _time.GetElapsedTime(stopwatch),
            timeline.Complete(),
            Redact(negotiation));

        // Log-Korrelation (WP4.6, Punkt 4): Codes und Ausgänge, kein Fremdtext. Die Request-Id steht
        // auf dem Bildschirm; wer sie im Log sucht, findet dieselbe Kette wieder.
        foreach (var stage in report.Stages)
        {
            var name = UpstreamStages.Label(stage.Stage);
            var outcome = UpstreamStages.Label(stage.Outcome);
            Log.Stage(_log, requestId, config.Slug, stage.Code, name, outcome);
        }

        return report;
    }

    /// <summary>
    /// Die Angaben der Gegenstelle durch dieselbe Redaktion wie jeder Befund (M2-Vertrag §6,
    /// Invariante 2). <b>Hier lief bisher nichts durch</b>, und das war eine Lücke: Fassung,
    /// Fähigkeitsnamen und Begründung kommen ganz oder teilweise vom Upstream, und ein
    /// Capability-Objekt kann Felder tragen, die niemand vorhergesehen hat — Namen inklusive.
    /// <para>
    /// An <b>einer</b> Stelle, weil beide Quellen (stehende Verbindung und transienter Versuch)
    /// hier zusammenlaufen. Eine Maskierung je Quelle wäre an jeder Quelle vergessbar.
    /// </para>
    /// <para>
    /// Die Deckelung der Liste ist die zweite Linie: Die Quelle kürzt bereits, aber ein Bericht
    /// bleibt eine Auskunft für einen Menschen und kein Abbild des Gegenübers.
    /// </para>
    /// </summary>
    private static UpstreamNegotiation? Redact(UpstreamNegotiation? negotiation)
    {
        if (negotiation is null)
        {
            return null;
        }

        var capabilities = negotiation.Capabilities
            .Take(MaxCapabilityNames)
            .Select(name => DiagnosticRedaction.Scrub(name) ?? string.Empty)
            .ToList();

        if (negotiation.Capabilities.Count > MaxCapabilityNames)
        {
            capabilities.Add($"… (+{negotiation.Capabilities.Count - MaxCapabilityNames} weitere)");
        }

        return negotiation with
        {
            Transport = DiagnosticRedaction.Scrub(negotiation.Transport) ?? string.Empty,
            ProtocolVersion = DiagnosticRedaction.Scrub(negotiation.ProtocolVersion),
            Capabilities = capabilities,
            Note = DiagnosticRedaction.Scrub(negotiation.Note),
        };
    }

    /// <summary>Obergrenze der Fähigkeitsnamen im Bericht.</summary>
    private const int MaxCapabilityNames = 40;

    /// <summary>
    /// Nur Kennung, Slug, Code und Ausgang — <b>kein Fremdtext</b>. Der Meldungstext steht auf dem
    /// Bildschirm und in der Antwort; ihn zusätzlich ins Log zu schreiben, öffnete einen zweiten
    /// Ausgabeweg, den niemand gegen den Negativkorpus hält.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Upstream-Diagnose {RequestId} für {Slug}: {Code} {Stage} = {Outcome}.")]
        public static partial void Stage(
            ILogger logger, string requestId, string slug, string code, string stage, string outcome);
    }

    private static string NewRequestId()
        => string.Concat("upd-", Guid.NewGuid().ToString("N").AsSpan(0, 12));

    /// <summary>Das Netzwerkziel eines Upstreams, oder <c>null</c> — nativ startende haben keins.</summary>
    private static NetworkTarget? NetworkTargetOf(UpstreamServerConfig config) => config.Kind switch
    {
        UpstreamTransportKind.StreamableHttp when config.Http is { } http
            => new NetworkTarget(http.Endpoint, http.AllowPrivateTargets),
        UpstreamTransportKind.OpenApi when config.OpenApi is { } openApi
            => new NetworkTarget(openApi.SpecLocation, openApi.AllowPrivateTargets),
        UpstreamTransportKind.OpenRpc when config.OpenRpc is { } openRpc
            => new NetworkTarget(openRpc.SpecLocation ?? openRpc.Endpoint, openRpc.AllowPrivateTargets),
        _ => null,
    };

    private static IsolationOptions? IsolationOf(UpstreamServerConfig config) => config.Kind switch
    {
        UpstreamTransportKind.Stdio => config.Stdio?.Isolation,
        UpstreamTransportKind.Cli => config.Cli?.Isolation,
        _ => null,
    };

    private static string? ProgramPathOf(UpstreamServerConfig config) => config.Kind switch
    {
        UpstreamTransportKind.Stdio => config.Stdio?.Command,
        UpstreamTransportKind.Cli => config.Cli?.Executable,
        UpstreamTransportKind.Wasi => config.Wasi?.HostExecutable,
        _ => null,
    };

    /// <summary>
    /// Wie die Anmeldung heisst, die hinterlegt ist — <c>null</c>, wenn keine hinterlegt ist.
    /// <b>Nur der Name der Art</b>, nie der Wert.
    /// </summary>
    private static string? DescribeConfiguredAuth(UpstreamServerConfig config) => config.Kind switch
    {
        UpstreamTransportKind.StreamableHttp when config.Http?.OAuth is not null => "OAuth",
        UpstreamTransportKind.StreamableHttp when config.Http?.Headers is { Count: > 0 } => "Header",
        UpstreamTransportKind.OpenApi when config.OpenApi is { AuthKind: not OpenApiAuthKind.None } api
            => api.AuthKind.ToString(),
        UpstreamTransportKind.OpenRpc when config.OpenRpc is { AuthKind: not OpenApiAuthKind.None } rpc
            => rpc.AuthKind.ToString(),
        _ => null,
    };

    private sealed record NetworkTarget(Uri Uri, bool? AllowPrivate);

    /// <summary>
    /// Die Buchführung über die Kette. Sie kennt genau eine Regel, und die ist der ganze Zweck des
    /// Pakets: <b>Nach der ersten gescheiterten Stufe wird nichts mehr geprüft und nichts mehr als
    /// Fehler gemeldet.</b> Ein Bericht, in dem nach der ersten Ursache noch sechs Folgefehler
    /// stehen, ist wieder eine Sackgasse, nur länger.
    /// </summary>
    private sealed class Timeline
    {
        private readonly Dictionary<UpstreamStage, UpstreamStageResult> _results = [];

        public bool Ended { get; private set; }

        public void Pass(UpstreamStage stage, string summary, params (string Key, string Value)[] details)
            => Record(stage, UpstreamStageOutcome.Passed,
                CheckOutcome.Pass(UpstreamStages.Code(stage), summary, Details(details)));

        public void Skip(UpstreamStage stage, string reason, params (string Key, string Value)[] details)
            => Record(stage, UpstreamStageOutcome.Skipped,
                CheckOutcome.Skipped(UpstreamStages.Code(stage), reason, Details(details)));

        public void Fail(
            UpstreamStage stage, string summary, string remediation,
            params (string Key, string Value)[] details)
        {
            Record(stage, UpstreamStageOutcome.Failed,
                CheckOutcome.Fail(UpstreamStages.Code(stage), summary, remediation, Details(details)));

            // Was nach der schuldigen Stufe schon eingetragen war, wird zurückgenommen. Das kommt
            // vor: Die Stufen 3 und 4 laufen als Vorschau, und der echte Versuch kann die Ursache
            // nachträglich dorthin zurückverlegen (eine Weiterleitung auf eine interne Adresse).
            // Stünde die spätere Stufe dann weiterhin als „erreicht" da, behauptete der Bericht
            // etwas, das nie geprüft wurde.
            foreach (var later in UpstreamStages.All.Where(candidate => candidate > stage))
            {
                _results.Remove(later);
            }

            Ended = true;
        }

        /// <summary>
        /// Füllt die Lücken: Jede Stufe ohne Befund ist nicht erreicht worden, und der Grund steht
        /// dabei. Eine Stufe, die im Bericht fehlt, wäre die stille Lücke, gegen die das Modell aus
        /// M2 gebaut ist.
        /// </summary>
        public List<UpstreamStageResult> Complete()
        {
            var failure = UpstreamStages.All.FirstOrDefault(
                stage => _results.TryGetValue(stage, out var result)
                    && result.Outcome is UpstreamStageOutcome.Failed,
                UpstreamStage.Discovery);
            var failed = _results.Values.Any(result => result.Outcome is UpstreamStageOutcome.Failed);

            var completed = new List<UpstreamStageResult>(UpstreamStages.All.Count);
            foreach (var stage in UpstreamStages.All)
            {
                if (_results.TryGetValue(stage, out var result))
                {
                    completed.Add(result);
                    continue;
                }

                var reason = failed
                    ? $"Nicht erreicht — die Kette endete bei {UpstreamStages.Code(failure)} "
                        + $"({UpstreamStages.Label(failure)})."
                    : "Nicht erreicht — der Lauf wurde vorher beendet.";
                completed.Add(new UpstreamStageResult(
                    stage,
                    UpstreamStageOutcome.NotReached,
                    DiagnosticRedaction.Scrub(
                        CheckOutcome.Skipped(UpstreamStages.Code(stage), reason))));
            }

            return completed;
        }

        /// <summary>
        /// Jeder Befund läuft durch die Redaktion aus M2 — auch die, deren Text aus der eigenen
        /// Feder stammt. Eine Maskierung, die man je Aufrufstelle entscheidet, ist an jeder
        /// Aufrufstelle vergessbar.
        /// </summary>
        private void Record(UpstreamStage stage, UpstreamStageOutcome outcome, DiagnosticCheck check)
            => _results[stage] = new UpstreamStageResult(
                stage, outcome, DiagnosticRedaction.Scrub(check));

        private static IReadOnlyDictionary<string, string>? Details((string Key, string Value)[] pairs)
            => pairs.Length == 0 ? null : CheckOutcome.Details(pairs);
    }
}
