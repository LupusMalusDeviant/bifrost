using System.Net;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Upstreams;
using Bifrost.Server.Bootstrap;

namespace Bifrost.Server.Importing;

/// <summary>
/// Die Aufrufwege des Konfigurationsimports (WP4.3) — <b>eine</b> Fachlogik hinter zwei Türen.
///
/// <para>
/// <b>Warum eine eigene Datei.</b> <c>ApiEndpoints</c> ist auf 1400 Zeilen gewachsen und trägt elf
/// Endpunktgruppen; die zwölfte dort einzuhängen hieße, sie in einem Fließtext zu verstecken. Die
/// Regel, an der es hängt — Management verlangt einen Global-Grant — wird trotzdem <b>nicht</b>
/// nachgebaut: <see cref="ApiEndpoints.RequireAdminAsync"/> wird aufgerufen. Zwei Kopien derselben
/// Zusicherung sind zwei Behauptungen und eine Prüfung.
/// </para>
///
/// <para>
/// <b>Die Stopp-Regel dieses Pakets.</b> Kein Endpunkt hier nimmt einen Pfad entgegen und liest ihn.
/// Der Quellpfad reist als <em>Herkunftsangabe</em> mit (er steht in den Befunden, damit ein Mensch
/// die Fundstelle wiederfindet) — gelesen wird ausschließlich der Rumpf der Anfrage. Ein Endpunkt,
/// der eine Datei serverseitig öffnet, weil ein Client ihren Pfad genannt hat, ist ein Werkzeug zum
/// Auslesen fremder Dateien, egal wie er heißt.
/// </para>
///
/// <para>
/// <b>Was auf keinem dieser Wege hinausgeht:</b> die Werte. Der Plan mit den Klartextwerten bleibt
/// im <see cref="ImportPlanStore"/>; hinaus geht <see cref="ImportPreviewView"/>, und das ist eine
/// Positivliste (siehe <see cref="ImportPreviewProjection"/>).
/// </para>
/// </summary>
public static class ImportEndpoints
{
    /// <summary>Die authentifizierten Endpunkte.</summary>
    public const string ApiBase = "/api/v1/import";

    /// <summary>Der lokale Weg während des Erstzugangs. Danach gibt es ihn nicht mehr.</summary>
    public const string SetupPreviewPath = "/setup/import/preview";

    /// <summary>Der Kopf, in dem der Setup-Weg das Erstzugangs-Token erwartet.</summary>
    public const string SetupTokenHeader = "X-Bifrost-Setup-Token";

    public static void MapImportEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapAuthenticated(app);
        MapSetupPreview(app);
    }

    // ── Authentifiziert: Vorschau, Probe, Übernahme ─────────────────────────────────────────────

    private static void MapAuthenticated(WebApplication app)
    {
        var import = app.MapGroup(ApiBase)
            // Reihenfolge ist Absicht: Erst der Grant, dann der Zaehler. Andersherum verbrauchte
            // ein Aufrufer ohne Berechtigung das Kontingent eines Aufrufers mit.
            .AddEndpointFilter(ApiEndpoints.RequireAdminAsync)
            .AddEndpointFilter(RateLimitByIdentityAsync);

        // ── Vorschau ────────────────────────────────────────────────────────
        import.MapPost("/preview", async (
            HttpContext ctx,
            IConfigurationImporter importer,
            ImportPlanStore plans,
            IAuditSink audit,
            TimeProvider time,
            string? originPath,
            CancellationToken ct) =>
        {
            var (document, failure) = await ImportRequestLimits.ReadDocumentAsync(ctx, ct);
            if (failure is not null)
            {
                return failure;
            }

            // originPath ist eine BESCHRIFTUNG. Er geht in den Plan, damit ein Befund seine
            // Fundstelle nennen kann — er wird nicht geoeffnet.
            var plan = importer.Plan(document!, originPath);
            var identity = ApiEndpoints.Identity(ctx);
            var (token, expiresAt) = plans.Register(plan, identity);

            ApiEndpoints.AuditManagement(
                audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"import-preview:{plan.Source.Provider}:{plan.Candidates.Count}");

            return Results.Ok(ImportPreviewProjection.From(plan, token, expiresAt));
        });

        // ── Probe: verbindet einen einzelnen Kandidaten, ohne ihn anzulegen ──
        import.MapPost("/probe", async (
            ImportProbeRequest body,
            HttpContext ctx,
            ImportPlanStore plans,
            IUpstreamConnectionTester tester,
            IAuditSink audit,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var identity = ApiEndpoints.Identity(ctx);

            // Peek, nicht Claim: Eine Probe aendert nichts und darf deshalb wiederholt werden.
            var entry = plans.Peek(body?.Token, identity);
            if (entry is null)
            {
                return UnknownHandle();
            }

            var candidate = entry.Plan.Candidates.FirstOrDefault(item =>
                string.Equals(item.SourceName, body!.SourceName, StringComparison.Ordinal));
            if (candidate is null)
            {
                return ImportErrors.Result(
                    StatusCodes.Status404NotFound,
                    ImportErrors.Usage,
                    $"Der vorgemerkte Plan kennt keinen Server '{body!.SourceName}'.");
            }

            var result = await tester.TestAsync(candidate.Config, ct);

            ApiEndpoints.AuditManagement(
                audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"import-probe:{candidate.Config.Slug}:{(result.Success ? "ok" : "fehlgeschlagen")}");

            return Results.Ok(new
            {
                sourceName = candidate.SourceName,
                slug = candidate.Config.Slug,
                success = result.Success,
                toolCount = result.ToolCount,
                // Die Meldung stammt aus einem fremden Prozess oder Dienst. Sie wird um die Werte
                // GENAU DIESER Konfiguration bereinigt — ein Prozess, der nicht startet, schreibt
                // gern seine Kommandozeile in den Fehlertext.
                error = ImportValueScrubber.Scrub(result.Error, candidate.Config),
            });
        });

        // ── Übernahme: atomar oder gar nicht ────────────────────────────────
        import.MapPost("/commit", async (
            ImportCommitRequest body,
            HttpContext ctx,
            ImportPlanStore plans,
            UpstreamSupervisor supervisor,
            IAuditSink audit,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var identity = ApiEndpoints.Identity(ctx);

            // Erst ansehen, dann beanspruchen: Ein Plan, der ohnehin nicht anwendbar ist, soll das
            // Handle nicht verbrennen — sonst kostet ein vergessenes Haekchen einen neuen Upload.
            var preview = plans.Peek(body?.Token, identity);
            if (preview is null)
            {
                return UnknownHandle();
            }

            var refusal = Refuse(preview.Plan, body!);
            if (refusal is not null)
            {
                return refusal;
            }

            var claimed = plans.Claim(body!.Token, identity);
            if (claimed is null)
            {
                // Genau hier endet die zweite von zwei gleichzeitigen Uebernahmen.
                return UnknownHandle();
            }

            return await ApplyAsync(claimed.Plan, body, ctx, supervisor, audit, time, ct);
        });
    }

    // ── Der lokale Setup-Weg ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Vorschau während des Erstzugangs — und <b>nur</b> dann.
    ///
    /// <para>
    /// <b>Drei Bedingungen, alle drei nötig.</b> Der Erstzugang muss ausstehen (sonst gibt es diesen
    /// Endpunkt nicht mehr — das ist die DoD dieses Pakets), die Gegenstelle muss der Rechner selbst
    /// sein, und das Erstzugangs-Token muss vorliegen. Die dritte Bedingung ist der Grund, warum das
    /// hier kein anonymer Weg ist: Wer das Token hat, hat die Übergabedatei gelesen, und wer die
    /// gelesen hat, ist bereits der Betreiber.
    /// </para>
    ///
    /// <para>
    /// <b>Er merkt nichts vor.</b> Die Antwort trägt kein Handle. Vor dem Einlösen gibt es niemanden,
    /// dem ein vorgemerkter Vorgang gehören könnte — und ein Vorgang ohne Eigentümer wäre ein
    /// Vorgang, den der Nächste übernimmt. Angelegt wird nach dem Einlösen, über den
    /// authentifizierten Weg.
    /// </para>
    ///
    /// <para>
    /// <b>Warum das Token nicht verbraucht wird.</b> Es richtet den Erstzugang ein und gilt dafür
    /// genau einmal. Eine Vorschau ist kein Einlösen; sie hier zu verrechnen hieße, dass ein Blick
    /// in eine Konfigurationsdatei den Zugang zur Installation kostet.
    /// </para>
    /// </summary>
    private static void MapSetupPreview(WebApplication app)
    {
        app.MapPost(SetupPreviewPath, async (
            HttpContext ctx,
            IBootstrapService bootstrap,
            IBootstrapStateStore state,
            IConfigurationImporter importer,
            ImportRateLimiter limiter,
            IAuditSink audit,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;

            if (!limiter.TryAcquire("setup:" + (remote?.ToString() ?? "unbekannt")))
            {
                return RateLimited();
            }

            // 1. Der Zustand. Nach dem Einloesen gibt es diesen Endpunkt nicht mehr — und zwar als
            //    404, nicht als 403: Ein 403 bestaetigte, dass es ihn gibt.
            var status = await bootstrap.GetStatusAsync(ct);
            if (!status.IsPending)
            {
                RecordSetup(audit, time, InvocationStatus.Denied,
                    $"Import-Vorschau im Setup abgelehnt: Erstzugang ist {status.Phase}, es steht keiner aus.");
                return Results.NotFound();
            }

            // 2. Der Rechner selbst. Eine fehlende Adresse zaehlt NICHT als lokal: Fail-closed ist
            //    hier billig, und die einzige Lage, in der sie fehlt, ist ein In-Memory-Transport.
            if (remote is null || !IPAddress.IsLoopback(remote))
            {
                RecordSetup(audit, time, InvocationStatus.Denied,
                    "Import-Vorschau im Setup abgelehnt: Die Anfrage kam nicht vom Rechner des Gateways.");
                return Results.NotFound();
            }

            // 3. Das Token. Gegen den gespeicherten Hash, in konstanter Zeit — und ohne es zu
            //    verbrauchen.
            var record = state.Read();
            var presented = ctx.Request.Headers[SetupTokenHeader].ToString().Trim();
            if (record is not { Phase: BootstrapPhase.Pending, TokenHash: not null }
                || record.ExpiresAt is null
                || record.ExpiresAt <= time.GetUtcNow()
                || !BootstrapToken.Matches(presented, record.TokenHash))
            {
                RecordSetup(audit, time, InvocationStatus.Denied,
                    "Import-Vorschau im Setup abgelehnt: kein gueltiges Erstzugangs-Token vorgelegt.");
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            code = ImportErrors.Usage,
                            message = "Diese Vorschau verlangt das Erstzugangs-Token im Kopf '"
                                + SetupTokenHeader + "'.",
                        },
                    },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var (document, failure) = await ImportRequestLimits.ReadDocumentAsync(ctx, ct);
            if (failure is not null)
            {
                return failure;
            }

            var plan = importer.Plan(document!, ctx.Request.Query["originPath"].ToString() is { Length: > 0 } origin
                ? origin
                : null);

            RecordSetup(audit, time, InvocationStatus.Success,
                $"Import-Vorschau im Setup: Format '{plan.Source.Provider}', {plan.Candidates.Count} Server. "
                + "Es wurde nichts angelegt und nichts vorgemerkt.");

            // Ohne Handle: Vorgemerkt wird nur fuer einen Eigentuemer, und den gibt es hier noch nicht.
            return Results.Ok(ImportPreviewProjection.From(plan));
        })
        // Wie beim Einloesepfad des Erstzugangs: Vor dem Zugang gibt es kein gueltiges
        // Antiforgery-Token, und der Rumpf ist hier ohnehin kein Formular.
        .DisableAntiforgery();
    }

    // ── Übernahme ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die Gründe, aus denen gar nicht erst angelegt wird. Sie werden <b>alle vor der ersten
    /// Änderung</b> geprüft — das ist die billigste Form von Atomizität und die einzige, die auch
    /// dann noch stimmt, wenn das Zurückrollen selbst scheitert.
    /// </summary>
    private static IResult? Refuse(ImportPlan plan, ImportCommitRequest body)
    {
        if (!plan.CanApply)
        {
            return ImportErrors.Result(
                StatusCodes.Status400BadRequest,
                ImportErrors.DocumentInvalid,
                "Der Plan ist nicht anwendbar: "
                + string.Join(
                    " | ",
                    plan.Findings.Concat(plan.Candidates.SelectMany(c => c.Findings))
                        .Where(f => f.Severity is ImportSeverity.Error)
                        .Select(f => $"[{f.Code}] {f.Summary}")));
        }

        var selected = Select(plan, body).ToList();
        if (selected.Count == 0)
        {
            return ImportErrors.Result(
                StatusCodes.Status400BadRequest,
                ImportErrors.Usage,
                "Die Auswahl trifft keinen der Server aus dem vorgemerkten Plan.");
        }

        var risks = plan.RequiresConfirmation;
        if (risks.Count > 0 && !body.ConfirmRisks)
        {
            return ImportErrors.Result(
                StatusCodes.Status409Conflict,
                ImportErrors.ConfirmationRequired,
                "Dieser Import traegt Befunde, die eine ausdrueckliche Bestaetigung verlangen "
                + "(confirmRisks): "
                + string.Join(" | ", risks.Select(f => $"[{f.Code}] {f.Summary}")));
        }

        return null;
    }

    private static async Task<IResult> ApplyAsync(
        ImportPlan plan,
        ImportCommitRequest body,
        HttpContext ctx,
        UpstreamSupervisor supervisor,
        IAuditSink audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var selected = Select(plan, body).ToList();

        // ── Vorbereiten, ohne etwas anzufassen ──────────────────────────────
        var prepared = new List<(ImportCandidate Candidate, UpstreamServerConfig Config)>();
        foreach (var candidate in selected)
        {
            var named = body.Servers?.FirstOrDefault(item =>
                string.Equals(item.SourceName, candidate.SourceName, StringComparison.Ordinal));

            // Die Angabe je Server schlaegt die Angabe fuer den ganzen Vorgang. Wer eine Ausnahme
            // eintraegt, meint sie so.
            var selection = new ImportCommitSelection(
                candidate.SourceName,
                named?.Isolation ?? body.Isolation,
                named?.ContainerImage ?? body.ContainerImage);

            try
            {
                // Der Import IST ein Erzeugungsweg (ADR-0025 E4). Die sicheren Vorgaben gelten
                // deshalb hier genauso wie beim API-POST — samt der Absage, wenn die
                // Isolationsentscheidung fehlt. Sie wird nicht geraten.
                prepared.Add((candidate, SecureUpstreamDefaults.ForNewUpstream(
                    WithIsolation(candidate.Config, selection))));
            }
            catch (ArgumentException exception)
            {
                return ImportErrors.Result(
                    StatusCodes.Status409Conflict,
                    ImportErrors.ConfirmationRequired,
                    exception.Message);
            }
        }

        // ── Namenskollisionen mit dem Bestand ───────────────────────────────
        var existing = supervisor.Statuses
            .Select(status => status.Slug)
            .ToHashSet(StringComparer.Ordinal);
        var collisions = prepared
            .Where(item => existing.Contains(item.Config.Slug))
            .Select(item => item.Config.Slug)
            .ToList();
        if (collisions.Count > 0)
        {
            return ImportErrors.Result(
                StatusCodes.Status409Conflict,
                ImportErrors.Conflict,
                "Diese Slugs gibt es auf dieser Instanz bereits: "
                + string.Join(", ", collisions)
                + ". Welcher der beiden Server den Namen behaelt, wird hier nicht entschieden.");
        }

        // ── Anlegen, mit Rueckweg ───────────────────────────────────────────
        var created = new List<(ServerId Id, string Slug)>();
        try
        {
            foreach (var (_, config) in prepared)
            {
                var id = await supervisor.AddAsync(config, ct);
                created.Add((id, config.Slug));
                ApiEndpoints.AuditManagement(
                    audit, time, ctx, AuditEventKind.ConfigChanged, id, $"import-added:{config.Slug}");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Rueckweg: Was bereits steht, wird wieder abgeraeumt. Ein halb uebernommener Import
            // waere der schlimmste Ausgang — er sieht aus wie ein gelungener, bis jemand die Liste
            // zaehlt.
            var rolledBack = await RollBackAsync(created, supervisor, audit, time, ctx, ct);

            return ImportErrors.Result(
                StatusCodes.Status409Conflict,
                ImportErrors.Conflict,
                $"Die Uebernahme ist bei '{prepared[created.Count].Config.Slug}' gescheitert: "
                + ImportValueScrubber.Scrub(exception.Message, prepared[created.Count].Config)
                + (rolledBack
                    ? " Die bereits angelegten Server wurden wieder entfernt; es wurde nichts uebernommen."
                    : " ACHTUNG: Das Zurueckrollen ist selbst gescheitert. Die bereits angelegten "
                        + "Server stehen noch und muessen von Hand geprueft werden."));
        }

        ApiEndpoints.AuditManagement(
            audit, time, ctx, AuditEventKind.ConfigChanged, null,
            $"import-committed:{plan.Source.Provider}:{created.Count}");

        return Results.Ok(new
        {
            imported = created.Select(item => new { id = item.Id.Value, slug = item.Slug }),
            count = created.Count,
        });
    }

    private static async Task<bool> RollBackAsync(
        List<(ServerId Id, string Slug)> created,
        UpstreamSupervisor supervisor,
        IAuditSink audit,
        TimeProvider time,
        HttpContext ctx,
        CancellationToken ct)
    {
        var complete = true;
        foreach (var (id, slug) in Enumerable.Reverse(created))
        {
            try
            {
                await supervisor.RemoveAsync(id, DrainPolicy.Immediate, ct);
                ApiEndpoints.AuditManagement(
                    audit, time, ctx, AuditEventKind.ConfigChanged, id, $"import-rolled-back:{slug}");
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
            {
                complete = false;
                ApiEndpoints.AuditManagement(
                    audit, time, ctx, AuditEventKind.ConfigChanged, id, $"import-rollback-failed:{slug}");
            }
        }

        return complete;
    }

    /// <summary>
    /// Setzt die Isolationsentscheidung des Aufrufers ein. Ohne Angabe bleibt die Konfiguration, wie
    /// die Quelle sie mitbrachte — und läuft dann in die Absage aus
    /// <see cref="SecureUpstreamDefaults.ForNewUpstream"/>. Das ist gewollt: Die Entscheidung trifft
    /// der Betreiber, nicht dieser Endpunkt.
    /// </summary>
    private static UpstreamServerConfig WithIsolation(
        UpstreamServerConfig config, ImportCommitSelection? selection)
    {
        if (selection?.Isolation is not { Length: > 0 } requested
            || !Enum.TryParse<IsolationMode>(requested, ignoreCase: true, out var mode))
        {
            return config;
        }

        var isolation = (config.Stdio?.Isolation ?? config.Cli?.Isolation ?? new IsolationOptions())
            with
            {
                Mode = mode,
                Image = selection.ContainerImage is { Length: > 0 } image
                    ? image
                    : config.Stdio?.Isolation?.Image ?? config.Cli?.Isolation?.Image,
            };

        return config with
        {
            Stdio = config.Stdio is { } stdio ? stdio with { Isolation = isolation } : null,
            Cli = config.Cli is { } cli ? cli with { Isolation = isolation } : null,
        };
    }

    private static IEnumerable<ImportCandidate> Select(ImportPlan plan, ImportCommitRequest body)
    {
        if (body.Servers is not { Count: > 0 } wanted)
        {
            return plan.Candidates;
        }

        var names = wanted.Select(item => item.SourceName).ToHashSet(StringComparer.Ordinal);
        return plan.Candidates.Where(candidate => names.Contains(candidate.SourceName));
    }

    // ── Gemeinsames ─────────────────────────────────────────────────────────────────────────────

    private static async ValueTask<object?> RateLimitByIdentityAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var limiter = context.HttpContext.RequestServices.GetRequiredService<ImportRateLimiter>();
        return limiter.TryAcquire("identity:" + ApiEndpoints.Identity(context.HttpContext).Value)
            ? await next(context)
            : RateLimited();
    }

    private static IResult RateLimited() => ImportErrors.Result(
        StatusCodes.Status429TooManyRequests,
        ImportErrors.RateLimited,
        $"Zu viele Importanfragen. Erlaubt sind {ImportRateLimiter.PermitsPerWindow} je "
        + $"{ImportRateLimiter.Window.TotalMinutes:0} Minute(n).");

    private static IResult UnknownHandle() => ImportErrors.Result(
        StatusCodes.Status409Conflict,
        ImportErrors.HandleUnknown,
        "Das vorgelegte Handle ist unbekannt, abgelaufen, bereits verbraucht oder gehoert einer "
        + "anderen Identitaet. Es wird abgewiesen statt geraten — ein Import auf geratenen Daten "
        + "legt fremde Server an.");

    private static void RecordSetup(
        IAuditSink audit, TimeProvider time, InvocationStatus status, string detail)
        => audit.Record(new AuditEvent(
            time.GetUtcNow(),
            Caller: null,
            CallOrigin.System,
            AuditEventKind.ConfigChanged,
            Server: null,
            Tool: "import-setup",
            status,
            RedactedArguments: null,
            RequestBytes: null,
            ResponseBytes: null,
            Duration: null,
            CallerRoles: null,
            Detail: detail));
}

/// <param name="Token">Das Handle aus der Vorschau.</param>
/// <param name="SourceName">Der Name des Servers, wie er in der Quelle stand.</param>
public sealed record ImportProbeRequest(string? Token, string SourceName);

/// <param name="Servers">
/// Die Auswahl. Ohne Angabe gilt der ganze Plan — ein Import, der von selbst weniger übernimmt als
/// angezeigt, wäre die unangenehmste Sorte Überraschung.
/// </param>
/// <param name="ConfirmRisks">
/// Die Bestätigung der Befunde aus <see cref="ImportPlan.RequiresConfirmation"/>. Sie blockieren
/// nicht, aber sie werden auch nicht wegentschieden.
/// </param>
/// <param name="Isolation">
/// Die Isolationsentscheidung für alle Server, die keine eigene mitbringen (ADR-0025 E2/E5). Sie
/// steht hier zusätzlich zur Angabe je Server, weil der Regelfall genau so aussieht: Ein Betreiber
/// entscheidet einmal für die Datei und nicht dreißigmal für dieselbe Datei.
/// </param>
public sealed record ImportCommitRequest(
    string? Token,
    IReadOnlyList<ImportCommitSelection>? Servers = null,
    bool ConfirmRisks = false,
    string? Isolation = null,
    string? ContainerImage = null);

/// <param name="Isolation"><c>Host</c> oder <c>Container</c> (ADR-0025 E2/E5).</param>
public sealed record ImportCommitSelection(
    string SourceName,
    string? Isolation = null,
    string? ContainerImage = null);
