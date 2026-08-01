using System.Globalization;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Abstractions.Setup;
using Bifrost.Core.Execution;
using Bifrost.Core.Upstreams;
using Bifrost.Server.Bootstrap;
using Bifrost.Server.Importing;
using Bifrost.Server.KeyRing;

namespace Bifrost.Server.Setup;

/// <summary>
/// Der Serverdienst hinter dem gefuehrten Erstaufbau (WP4.4).
///
/// <para>
/// <b>Kein HTTP.</b> Die Oberflaeche ist Blazor Interactive Server und laeuft im selben Prozess;
/// dieser Dienst ruft <see cref="IConfigurationImporter"/> und
/// <see cref="ImportPreviewProjection"/> direkt auf. Der Setup-Endpunkt
/// (<c>/setup/import/preview</c>) bleibt auf Loopback beschraenkt und ist fuer lokale Werkzeuge
/// gedacht — die Entscheidung steht in <c>docs/plans/product-readiness-status.md</c> und ist eine
/// Auflage an dieses Paket.
/// </para>
///
/// <para>
/// <b>Hier steht keine Kernregel.</b> Was anwendbar ist, sagt <see cref="ImportPlan.IsApplicable"/>;
/// welche Bestaetigungen eine Auswahl verlangt, sagt <see cref="ImportPlan.ConfirmationsFor"/>; was
/// eine Neuanlage sicher voreingestellt bekommt, sagt <see cref="SecureUpstreamDefaults"/>; ob ein
/// Programm nativ starten darf, sagt die Ausfuehrungs-Policy im Supervisor. Dieser Dienst ordnet
/// die Schritte an und uebersetzt Ergebnisse in Saetze.
/// </para>
///
/// <para>
/// <b>Je Eintrag, nicht planweit.</b> <c>ImportPlan.CanApply</c> heisst seit dem Teilimport
/// „etwas geht" und nicht „alles geht". Der Wizard fragt deshalb nirgends den Plan als Ganzes,
/// sondern jeden Kandidaten einzeln — und benennt die Auslassungen.
/// </para>
/// </summary>
public sealed class SetupWizardService : ISetupWizard
{
    private readonly IBootstrapService _bootstrap;
    private readonly IUiUserService _uiUsers;
    private readonly KeyRingSettings _keyRing;
    private readonly KeyRingStartup _keyRingStartup;
    private readonly HostExecutionCoordinator _execution;
    private readonly IConfigurationImporter _importer;
    private readonly UpstreamSupervisor _supervisor;
    private readonly IRbacManagement _rbac;
    private readonly IToolCatalog _catalog;
    private readonly IAuditSink _audit;
    private readonly TimeProvider _time;

    public SetupWizardService(
        IBootstrapService bootstrap,
        IUiUserService uiUsers,
        KeyRingSettings keyRing,
        KeyRingStartup keyRingStartup,
        HostExecutionCoordinator execution,
        IConfigurationImporter importer,
        UpstreamSupervisor supervisor,
        IRbacManagement rbac,
        IToolCatalog catalog,
        IAuditSink audit,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(uiUsers);
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(keyRingStartup);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(rbac);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);

        _bootstrap = bootstrap;
        _uiUsers = uiUsers;
        _keyRing = keyRing;
        _keyRingStartup = keyRingStartup;
        _execution = execution;
        _importer = importer;
        _supervisor = supervisor;
        _rbac = rbac;
        _catalog = catalog;
        _audit = audit;
        _time = time;
    }

    /// <summary>Dieselbe Grenze wie am HTTP-Weg — sie steht in <see cref="ImportRequestLimits"/>.</summary>
    public int MaxDocumentBytes => ImportRequestLimits.MaxDocumentBytes;

    // ── Der Zustand der Instanz ─────────────────────────────────────────────────────────────────

    public async Task<SetupFacts> ReadFactsAsync(CancellationToken ct)
    {
        var status = await _bootstrap.GetStatusAsync(ct).ConfigureAwait(false);
        var anyAdmin = await _uiUsers.AnyExistAsync(ct).ConfigureAwait(false);
        var identities = await _rbac.ListIdentitiesAsync(ct).ConfigureAwait(false);

        return new SetupFacts(
            new SetupAccessFacts(
                status.Phase.ToString(),
                status.IsPending,
                anyAdmin,
                status.ExpiresAt,
                status.HandoverPath,
                DescribeAccess(status, anyAdmin)),
            KeyRingFacts(),
            ExecutionFacts(),
            [.. _supervisor.Statuses
                .OrderBy(item => item.Slug, StringComparer.Ordinal)
                .Select(item => new SetupUpstreamFacts(
                    item.Id, item.Slug, item.State.ToString(), item.ToolCount, item.LastError))],
            [.. identities
                .Where(identity => identity.Kind is IdentityKind.Agent)
                .Select(identity => identity.Name)
                .OrderBy(name => name, StringComparer.Ordinal)],
            _catalog.Snapshot.Count(entry => entry.Kind is CatalogEntryKind.Tool));
    }

    private static string DescribeAccess(BootstrapStatus status, bool anyAdmin) => status switch
    {
        { IsPending: true } => "Es steht ein Setup-Token aus. Es liegt in der Uebergabedatei auf dem "
            + "Rechner des Gateways und in keinem Log.",
        _ when anyAdmin => "Diese Installation hat einen Zugang. Der Erstzugang ist erledigt.",
        { Phase: BootstrapPhase.Redeemed or BootstrapPhase.Established } => "Der Erstzugang gilt als "
            + "erledigt, aber es gibt keinen UI-Nutzer mehr. Von selbst wird kein neues Token "
            + "ausgestellt — die beiden Auswege sind lokale Kommandos auf dem Rechner des Gateways: "
            + "'--reset-ui-admin' und '--bootstrap-init'.",
        _ => "Es steht kein Setup-Token aus. Ein neues stellt der naechste Start aus, solange diese "
            + "Installation noch keinen Zugang hat.",
    };

    private SetupKeyRingFacts KeyRingFacts()
    {
        var verdict = _keyRingStartup.Verdict;
        return new SetupKeyRingFacts(
            KeyRingSwitch.Format(_keyRing.Mode),
            _keyRing.Declared is not null,
            verdict?.Kind.ToString(),
            verdict?.Summary
                ?? "Der Startlauf hat kein Urteil hinterlassen; ueber den Ring ist hier nichts bekannt.",
            verdict?.Remediation,
            KeyRingSwitch.Protection,
            KeyRingSwitch.NoneValue,
            KeyRingSwitch.CertificatePath);
    }

    private SetupExecutionFacts ExecutionFacts()
    {
        var state = _execution.State;
        return new SetupExecutionFacts(
            state.Allowed,
            state.ReasonCode,
            state.Note,
            state.Allowed
                ? state.Adopted
                    ? "Die Erlaubnis ist uebernommen, nicht gewaehlt. Upstreams auf Container-Isolation "
                        + $"umstellen und {HostExecutionSwitch.Name} anschliessend auf false setzen."
                    : null
                : "Ein Upstream, der ein Programm auf dem Host startet, wird abgewiesen. Wer das "
                    + $"aendern will, setzt {HostExecutionSwitch.Name}=true — mit den Folgen, die in "
                    + "ADR-0025 stehen.",
            HostExecutionSwitch.Name);
    }

    // ── Schritt 4/5: einlesen und ansehen ───────────────────────────────────────────────────────

    public SetupImportOutcome Analyse(SetupSession session, string document, string? originPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(document);

        // originPath ist eine BESCHRIFTUNG. Er landet in den Befunden, damit ein Mensch die
        // Fundstelle wiederfindet — geoeffnet wird er nie. Dieselbe Regel wie am HTTP-Weg.
        var plan = _importer.Plan(document, originPath);

        // Die wertefreie Sicht entsteht aus der Positivliste von WP4.3 — nicht aus dem Kandidaten.
        // Damit gilt hier dieselbe Grenze wie an der Schnittstelle: sichtbar sind Strukturangaben,
        // nie Werte.
        var preview = ImportPreviewProjection.From(plan);
        var views = preview.Candidates.ToDictionary(view => view.SourceName, StringComparer.Ordinal);

        session.Plan = plan;
        session.OriginPath = originPath;
        session.ImportSource = new SetupImportSourceInfo(
            preview.Source.Provider,
            preview.Source.SchemaVersion,
            preview.Source.Confidence,
            preview.Source.OriginPath);
        session.BlockingFindings = plan.BlockingFindings;
        session.Entries =
        [
            .. plan.Candidates.Select(candidate =>
            {
                var view = views[candidate.SourceName];
                return new SetupImportEntry(
                    candidate.SourceName,
                    view.Slug,
                    view.DisplayName,
                    view.Kind,
                    DescribeTransport(view.Transport),
                    view.CanApply,
                    view.Findings,
                    view.Secrets,
                    view.CanApply ? [] : plan.BlockersFor(candidate),
                    view.SourcePath);
            }),
        ];

        // Vorausgewaehlt wird, was geht. Ein gesperrter Eintrag bleibt sichtbar und abgewaehlt —
        // eine Vorschau, die einen Eintrag weglaesst, ist eine Vorschau, die luegt.
        session.Selected.Clear();
        foreach (var entry in session.Entries.Where(entry => entry.CanApply))
        {
            session.Selected.Add(entry.SourceName);
        }

        session.UnreadableEntries = Unreadable(plan);
        session.RisksConfirmed = false;
        session.Applied = null;

        var applicable = session.Entries.Count(entry => entry.CanApply);
        return new SetupImportOutcome(
            applicable > 0,
            Summarise(
                preview.Source.Provider,
                session.Entries.Count,
                applicable,
                session.UnreadableEntries.Count,
                plan.BlockingFindings));
    }

    /// <summary>
    /// Die Fehler auf Planebene, die zu <b>keinem</b> Kandidaten gehoeren — also Stellen der
    /// Quelldatei, aus denen gar nichts wurde.
    /// <para>
    /// <b>Warum ueber <see cref="ImportPlan.BlockersFor"/> und nicht ueber einen Pfadvergleich:</b>
    /// Welcher Befund welchen Eintrag meint, entscheidet der Vertrag. Ein zweiter Pfadvergleich
    /// hier waere eine zweite Wahrheit — und die falsche waere die, die einen Eintrag als
    /// „unlesbar" zaehlt, obwohl er als gesperrter Kandidat ohnehin schon dasteht.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ImportFinding> Unreadable(ImportPlan plan)
    {
        var attached = plan.Candidates.SelectMany(plan.BlockersFor).ToHashSet();
        return
        [
            .. plan.Findings.Where(finding =>
                finding.Severity is ImportSeverity.Error
                && finding.Scope is ImportFindingScope.Entry
                && !attached.Contains(finding)),
        ];
    }

    private static string Summarise(
        string provider,
        int total,
        int applicable,
        int unreadable,
        IReadOnlyList<ImportFinding> blocking)
    {
        if (blocking.Count > 0)
        {
            return $"Format '{provider}' — aber diese Befunde betreffen das ganze Dokument und halten "
                + "es an: " + string.Join(" | ", blocking.Select(f => $"[{f.Code}] {f.Summary}"));
        }

        if (total == 0 && unreadable == 0)
        {
            return $"Format '{provider}' erkannt, aber kein einziger Servereintrag darin.";
        }

        // Die Stellen, aus denen gar kein Kandidat wurde, stehen ausdruecklich in der Zusammenfassung
        // — sonst waere die Zahl oben eine andere als die Zahl der Eintraege in der Datei, und
        // niemand erfuehre, warum.
        var tail = unreadable == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $" Dazu {unreadable} Stelle(n), aus denen gar kein Server wurde — sie stehen unten mit Ort und Grund.");

        return applicable == total
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Format '{provider}': {total} Server, alle anwendbar.{tail}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Format '{provider}': {total} Server, davon {applicable} anwendbar und "
                + $"{total - applicable} nicht. Die Auslassungen stehen unten namentlich.{tail}");
    }

    /// <summary>
    /// Die Kurzfassung der Transportangaben — <b>ausschliesslich</b> aus der Vorschauprojektion,
    /// also aus der Positivliste. Ein neues wertetragendes Feld erscheint hier nicht von selbst.
    /// </summary>
    private static string DescribeTransport(ImportTransportView transport)
    {
        var parts = new List<string>(6);

        if (transport.Program is { Length: > 0 } program)
        {
            parts.Add(program);
        }

        if (transport.ArgumentCount > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"{transport.ArgumentCount} Argument(e)"));
        }

        if (transport.Endpoint is { Length: > 0 } endpoint)
        {
            parts.Add(endpoint + (transport.EndpointCarriedQuery ? " (Query entfernt)" : string.Empty));
        }

        if (transport.SpecLocation is { Length: > 0 } spec)
        {
            parts.Add("Spec " + spec);
        }

        if (transport.EnvironmentNames is { Count: > 0 } environment)
        {
            parts.Add("Env: " + string.Join(", ", environment));
        }

        if (transport.HeaderNames is { Count: > 0 } headers)
        {
            parts.Add("Header: " + string.Join(", ", headers));
        }

        if (transport.CredentialPresent)
        {
            parts.Add("Credential vorhanden");
        }

        if (transport.IsolationMode is { Length: > 0 } isolation)
        {
            parts.Add("Isolation " + isolation
                + (transport.ContainerImage is { Length: > 0 } image ? $" ({image})" : string.Empty));
        }

        return parts.Count == 0 ? transport.Kind : string.Join(" · ", parts);
    }

    public IReadOnlyList<ImportFinding> ConfirmationsFor(SetupSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Plan is not { } plan)
        {
            return [];
        }

        // Die Regel steht im Vertrag, nicht hier: bestaetigt wird, was angelegt wird.
        return plan.ConfirmationsFor(Selected(session, plan));
    }

    private static List<ImportCandidate> Selected(SetupSession session, ImportPlan plan)
        => [.. plan.Candidates.Where(candidate => session.Selected.Contains(candidate.SourceName))];

    // ── Schritt 5: uebernehmen ──────────────────────────────────────────────────────────────────

    public async Task<SetupApplyReport> ApplySelectionAsync(
        SetupSession session, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Plan is not { } plan)
        {
            return SetupApplyReport.Refused(
                "Es liegt keine eingelesene Konfiguration vor. Schritt 4 noch einmal.");
        }

        if (plan.BlockingFindings.Count > 0)
        {
            // Ein planweiter Fehler betrifft auch den unauffaelligen Eintrag: Wer nicht weiss,
            // welches Format er vor sich hat, weiss auch nicht, was der einzelne Eintrag bedeutet.
            return SetupApplyReport.Refused(
                "Diese Befunde betreffen das ganze Dokument und lassen sich nicht durch eine Auswahl "
                + "umgehen: "
                + string.Join(" | ", plan.BlockingFindings.Select(f => $"[{f.Code}] {f.Summary}")));
        }

        var chosen = Selected(session, plan);
        if (chosen.Count == 0)
        {
            return SetupApplyReport.Refused("Es ist kein Eintrag ausgewaehlt.");
        }

        var risks = plan.ConfirmationsFor(chosen);
        if (risks.Count > 0 && !session.RisksConfirmed)
        {
            return SetupApplyReport.Refused(
                "Diese Auswahl traegt Befunde, die eine ausdrueckliche Bestaetigung verlangen: "
                + string.Join(" | ", risks.Select(f => $"[{f.Code}] {f.Summary}")));
        }

        var applicable = chosen.Where(plan.IsApplicable).ToList();

        // Was ausdruecklich gewaehlt wurde und trotzdem nicht geht, wird benannt — nicht
        // stillschweigend uebergangen. Dazu die Stellen, aus denen gar kein Kandidat wurde: Sie
        // gehoeren in dieselbe Liste, denn aus Sicht des Betreibers sind es Eintraege seiner Datei,
        // die keinen Server ergeben haben.
        var skipped = plan.Candidates
            .Where(candidate => session.Selected.Contains(candidate.SourceName))
            .Where(candidate => !plan.IsApplicable(candidate))
            .Select(candidate => new SetupSkippedServer(
                candidate.SourceName,
                string.Join(
                    " | ",
                    plan.BlockersFor(candidate).Select(f => $"[{f.Code}] {f.Summary}"))))
            .Concat(Unreadable(plan).Select(finding => new SetupSkippedServer(
                finding.Path ?? "(Ort unbekannt)",
                $"[{finding.Code}] {finding.Summary}"
                + (finding.Remediation is { } remedy ? " " + remedy : string.Empty))))
            .ToList();

        if (applicable.Count == 0)
        {
            return new SetupApplyReport(
                [],
                skipped,
                "Kein ausgewaehlter Eintrag ist anwendbar. Die Gruende stehen je Eintrag daneben.");
        }

        var created = new List<SetupCreatedServer>();
        var existing = _supervisor.Statuses.Select(item => item.Slug).ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in applicable)
        {
            if (existing.Contains(candidate.Config.Slug))
            {
                skipped.Add(new SetupSkippedServer(
                    candidate.SourceName,
                    $"Den Slug '{candidate.Config.Slug}' gibt es auf dieser Instanz bereits. Welcher "
                    + "der beiden Server den Namen behaelt, wird hier nicht entschieden."));
                continue;
            }

            var outcome = await AddAsync(WithIsolation(candidate.Config, session), ct)
                .ConfigureAwait(false);
            if (outcome.Id is { } id)
            {
                created.Add(new SetupCreatedServer(id, candidate.Config.Slug));
                existing.Add(candidate.Config.Slug);
            }
            else
            {
                // Kein Rueckweg ueber die bereits angelegten: Der Wizard uebernimmt je Eintrag, und
                // ein Eintrag, der scheitert, sagt das — er zieht die uebrigen nicht mit. Das ist
                // der Unterschied zum HTTP-Weg, der atomar arbeitet und dort auch atomar gemeint ist.
                skipped.Add(new SetupSkippedServer(candidate.SourceName, outcome.Error!));
            }
        }

        var report = new SetupApplyReport(created, skipped);
        session.Applied = report;
        Record(actor, string.Create(
            CultureInfo.InvariantCulture,
            $"Setup-Wizard: Import uebernommen — {created.Count} angelegt, {skipped.Count} ausgelassen "
            + $"(Format '{plan.Source.Provider}')."));
        return report;
    }

    [HostExecutionChecked(
        Note = "Legt ueber AddAsync und damit ueber UpstreamSupervisor.AddAsync an; der Torposten "
            + "steht dort. Dieser Weg prueft die Slug-Kollision und uebersetzt die Absage in einen Satz.")]
    public async Task<SetupApplyReport> ApplyManualAsync(
        SetupSession session, UpstreamServerConfig config, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);

        if (_supervisor.Statuses.Any(item =>
                string.Equals(item.Slug, config.Slug, StringComparison.Ordinal)))
        {
            return SetupApplyReport.Refused(
                $"Den Slug '{config.Slug}' gibt es auf dieser Instanz bereits.");
        }

        var outcome = await AddAsync(config, ct).ConfigureAwait(false);
        if (outcome.Id is not { } id)
        {
            return SetupApplyReport.Refused(outcome.Error!);
        }

        var report = new SetupApplyReport([new SetupCreatedServer(id, config.Slug)], []);
        session.Applied = report;
        Record(actor, $"Setup-Wizard: Upstream '{config.Slug}' von Hand angelegt.");
        return report;
    }

    /// <summary>
    /// Die eine Stelle, an der aus einer Konfiguration ein Server wird.
    /// <para>
    /// Sie ruft <see cref="SecureUpstreamDefaults.ForNewUpstream"/> — denselben Schritt wie der
    /// API-Schreibweg und das Serverformular. Das ist kein zweiter Weg, sondern derselbe Aufruf: Die
    /// Regel steht in <c>SecureUpstreamDefaults</c> und nur dort. Ob ein nativ laufender Upstream
    /// starten darf, fragt danach der Supervisor.
    /// </para>
    /// </summary>
    [HostExecutionChecked(
        Note = "Legt ueber UpstreamSupervisor.AddAsync an; der Torposten steht dort. Dieser Weg "
            + "ergaenzt nur die sicheren Vorgaben und uebersetzt die Absage in einen Satz.")]
    private async Task<(ServerId? Id, string? Error)> AddAsync(
        UpstreamServerConfig config, CancellationToken ct)
    {
        try
        {
            var prepared = SecureUpstreamDefaults.ForNewUpstream(config);
            var id = await _supervisor.AddAsync(prepared, ct).ConfigureAwait(false);
            return (id, null);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Der Fehlertext stammt aus Validator, Policy oder Supervisor und kann Werte dieser
            // Konfiguration mitfuehren — ein Prozess, der nicht startet, schreibt gern seine
            // Kommandozeile in die Meldung. Derselbe Entferner wie am HTTP-Weg.
            return (null, ImportValueScrubber.Scrub(exception.Message, config));
        }
    }

    /// <summary>
    /// Setzt die Isolationsentscheidung aus Schritt 1 ein.
    /// <para>
    /// <b>Warum die Entscheidung des Wizards gewinnt:</b> Keines der fuenf Quellformate kennt eine
    /// Isolationsangabe — sie entsteht erst hier. Eine Konfiguration ohne diese Angabe wuerde von
    /// <see cref="SecureUpstreamDefaults.ForNewUpstream"/> abgewiesen, und das zu Recht: Eine
    /// fehlende Angabe hiess frueher stillschweigend „kein Schutz". Ein Image, das die Quelle
    /// wider Erwarten doch mitbringt, bleibt stehen.
    /// </para>
    /// </summary>
    [NoHostExecution(
        "Formt eine Konfiguration um und gibt sie zurueck. Startet nichts und persistiert nichts; "
        + "der Aufrufer reicht das Ergebnis an den Supervisor weiter, und DER fragt die Policy.")]
    private static UpstreamServerConfig WithIsolation(UpstreamServerConfig config, SetupSession session)
    {
        var mode = session.Mode switch
        {
            SetupSecurityMode.Workbench => IsolationMode.Host,
            _ => IsolationMode.Container,
        };

        var image = session.ContainerImage is { Length: > 0 } chosen ? chosen : null;

        return config with
        {
            Stdio = config.Stdio is { } stdio
                ? stdio with { Isolation = Merge(stdio.Isolation, mode, image) }
                : null,
            Cli = config.Cli is { } cli
                ? cli with { Isolation = Merge(cli.Isolation, mode, image) }
                : null,
        };
    }

    private static IsolationOptions Merge(IsolationOptions? existing, IsolationMode mode, string? image)
        => existing is { } present
            ? present with { Mode = mode, Image = present.Image ?? image }
            : new IsolationOptions(mode, Image: image);

    // ── Audit ───────────────────────────────────────────────────────────────────────────────────

    public void Record(string actor, string detail)
        => _audit.Record(new AuditEvent(
            _time.GetUtcNow(),
            Caller: null,
            CallOrigin.Ui,
            AuditEventKind.ConfigChanged,
            Server: null,
            Tool: $"setup-wizard:{actor}",
            InvocationStatus.Success,
            RedactedArguments: null,
            RequestBytes: null,
            ResponseBytes: null,
            Duration: null,
            CallerRoles: null,
            Detail: detail));
}
