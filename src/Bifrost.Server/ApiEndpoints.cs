using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Capabilities;
using Bifrost.Core.Configuration;
using Bifrost.Core.Upstreams;
using Bifrost.Core.Packaging;
using Bifrost.Persistence;
using Bifrost.Persistence.Startup;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Server;

/// <summary>
/// REST-Fassade (FR-17) + Management-API (WP5.1, Basis für UI-Parität/FR-41).
/// Beide laufen durch dieselben Kernpfade wie MCP: Invoker-Pipeline bzw. Application-Services
/// (ADR-0008 — kein doppelter Enforcement-Code). Management verlangt bis WP6 einen Global-Grant.
/// </summary>
internal static class ApiEndpoints
{
    public static void MapGatewayApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        // ── Tool-Fassade (jede authentifizierte Identität, RBAC filtert) ─────
        api.MapGet("/tools", (HttpContext ctx, IToolCatalog catalog, IAuthorizationService auth) =>
        {
            var identity = Identity(ctx);
            var tools = auth.FilterVisible(identity, catalog.Snapshot)
                .Where(e => e.Kind is CatalogEntryKind.Tool)
                .Select(e => new
                {
                    name = e.Name.Value,
                    description = e.Description,
                    inputSchema = e.InputSchema,
                    estimatedSchemaTokens = e.EstimatedSchemaTokens,
                });
            return Results.Ok(new { tools });
        });

        // ── Capability-Sicht (ADR-0015) ──────────────────────────────────────
        // Additiv neben /tools: dieselben Fähigkeiten, protokollneutral beschrieben. Der alte
        // Endpunkt bleibt unverändert — die doppelte Deskriptorwelt ist der bewusst gewählte
        // Übergang, kein Versehen.
        api.MapGet("/capabilities", (
            HttpContext ctx, IToolCatalog catalog, IAuthorizationService auth) =>
        {
            var identity = Identity(ctx);
            var capabilities = auth.FilterVisible(identity, catalog.Snapshot)
                // Die Transportart kommt hier NICHT mit: Sie steht in der Konfiguration, nicht im
                // Katalog, und sie gehört nicht zur Id — die ServerId ist schon eindeutig. Ein
                // Lookup nur für ein informatives Feld hätte die Sicht an den Config-Store
                // gekettet und Einträge ohne Konfiguration (Meta-Tools) stillschweigend verschluckt.
                .Select(entry => LegacyCapabilityAdapter.FromCatalogEntry(entry))
                // Was das Gateway noch nicht anbieten darf, erscheint hier auch nicht — sonst
                // stünde eine Fähigkeit im Katalog, die kein Aufrufweg bedient (ADR-0015 macht das
                // an ADR-0019 fest).
                .Where(capability => capability.IsPubliclyOffered)
                .Select(capability => new
                {
                    id = capability.Id.Value,
                    nativeName = capability.NativeName,
                    catalogName = capability.CatalogName.Value,
                    displayName = capability.DisplayName,
                    description = capability.Description,
                    kind = capability.Kind.ToString(),
                    execution = capability.Execution.ToString(),
                    sideEffect = capability.SideEffect.ToString(),
                    requiresApproval = capability.RequiresApproval,
                    idempotent = capability.Idempotent,
                    supportsCancellation = capability.SupportsCancellation,
                    inputSchema = capability.Input is null ? null : new
                    {
                        dialect = capability.Input.Dialect,
                        provenance = capability.Input.Provenance.ToString(),
                        hash = capability.Input.Hash,
                        nativeVersion = capability.Input.NativeVersion,
                    },
                });
            return Results.Ok(new { capabilities });
        });

        api.MapPost("/tools/{name}/invoke", async (
            string name, HttpContext ctx, IToolInvoker invoker, CancellationToken ct) =>
        {
            JsonElement args = default;
            if (ctx.Request.ContentLength > 0)
            {
                args = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
            }

            var result = await invoker.InvokeAsync(
                new ToolInvocationRequest(Identity(ctx), CallOrigin.Rest, new NamespacedToolName(name), args, null), ct);

            return result.Status switch
            {
                InvocationStatus.Success => Results.Ok(new { status = "Success", content = result.Content }),
                InvocationStatus.ValidationFailed => Error(StatusCodes.Status400BadRequest, result),
                InvocationStatus.Denied => Error(StatusCodes.Status403Forbidden, result),
                InvocationStatus.ApprovalRequired => Error(StatusCodes.Status409Conflict, result),
                InvocationStatus.ToolNotFound => Error(StatusCodes.Status404NotFound, result),
                InvocationStatus.Timeout => Error(StatusCodes.Status504GatewayTimeout, result),
                _ => Error(StatusCodes.Status502BadGateway, result),
            };
        });

        // Aufruf über die Capability-Id, mit CapabilityResultV1 als Antwort (ADR-0015). Derselbe
        // Invocation-Kern wie /tools/{name}/invoke — kein zweiter Weg an der Governance vorbei,
        // nur eine andere Hülle über demselben Ergebnis.
        api.MapPost("/capabilities/{id}/invoke", async (
            string id, HttpContext ctx, IToolCatalog catalog, IToolInvoker invoker,
            CancellationToken ct) =>
        {
            // Auflösung über den Katalog-Snapshot: Die Id ist aus (ServerId, nativer Name)
            // ableitbar, deshalb braucht es keine Tabelle. Linear über wenige Hundert Einträge —
            // wird das je knapp, ist ein Index im Katalog die Antwort, nicht eine zweite Wahrheit.
            var wanted = new CapabilityId(id);
            var entry = catalog.Snapshot.FirstOrDefault(candidate =>
                LegacyCapabilityAdapter.FromCatalogEntry(candidate).Id == wanted);
            if (entry is null)
            {
                return Results.NotFound(new { error = new { gatewayCode = "not-found", message = $"Capability '{id}' existiert nicht." } });
            }

            JsonElement args = default;
            if (ctx.Request.ContentLength > 0)
            {
                args = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
            }

            var result = await invoker.InvokeAsync(
                new ToolInvocationRequest(Identity(ctx), CallOrigin.Rest, entry.Name, args, null), ct);
            var capability = CapabilityResultMapper.From(result);

            return capability.Kind switch
            {
                // Ein Vorgang ist kein Fehler: Der Aufruf läuft weiter, nur nicht jetzt. 202 mit
                // dem Ort, an dem der Stand steht — vorher war das eine 409 mit Prosa.
                CapabilityResultKind.Task => Results.Accepted(
                    $"/api/v1/tasks/{capability.TaskId}",
                    new { kind = "Task", taskId = capability.TaskId }),
                CapabilityResultKind.Error => Results.Json(
                    new
                    {
                        kind = "Error",
                        error = new
                        {
                            gatewayCode = capability.Error!.GatewayCode,
                            connectorCode = capability.Error.ConnectorCode,
                            message = capability.Error.Message,
                            retryable = capability.Error.Retryable,
                        },
                    },
                    statusCode: StatusFor(result.Status)),
                _ => Results.Ok(new
                {
                    kind = capability.Kind.ToString(),
                    data = capability.Data,
                    text = capability.Text,
                    truncation = capability.Truncation is { } truncation
                        ? new { originalChars = truncation.OriginalChars, truncatedChars = truncation.TruncatedChars }
                        : null,
                }),
            };
        });

        api.MapGet("/openapi.json", (HttpContext ctx, OpenApiDocumentGenerator generator) =>
            Results.Text(generator.GetJsonFor(Identity(ctx)), "application/json"));

        // ── Management: Upstream-Server (FR-34-Basis) ────────────────────────
        var servers = api.MapGroup("/servers").AddEndpointFilter(RequireAdminAsync);

        servers.MapGet("/", (IUpstreamSupervisor supervisor) => Results.Ok(new
        {
            servers = supervisor.Statuses.Select(s => new
            {
                id = s.Id.Value,
                slug = s.Slug,
                state = s.State.ToString(),
                toolCount = s.ToolCount,
                lastError = s.LastError,
                lastHealthyAt = s.LastHealthyAt,
            }),
        }));

        servers.MapPost("/", async (
            UpstreamServerConfig config, HttpContext ctx, UpstreamSupervisor supervisor, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                var id = await supervisor.AddAsync(config, ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, id, $"server-added:{config.Slug}");
                return Results.Created($"/api/v1/servers/{id.Value}", new { id = id.Value });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        servers.MapDelete("/{id:guid}", async (
            Guid id, int? graceSeconds, HttpContext ctx, UpstreamSupervisor supervisor, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                await supervisor.RemoveAsync(
                    new ServerId(id),
                    graceSeconds is { } g ? DrainPolicy.Graceful(TimeSpan.FromSeconds(g)) : DrainPolicy.Immediate,
                    ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, new ServerId(id), "server-removed");
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        servers.MapPost("/{id:guid}/enabled", async (
            Guid id, EnabledRequest body, UpstreamSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                await supervisor.SetEnabledAsync(new ServerId(id), body.Enabled, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        servers.MapPut("/{id:guid}", async (
            Guid id, UpstreamServerConfig config, UpstreamSupervisor supervisor,
            IUpstreamConfigStore store, CancellationToken ct) =>
        {
            try
            {
                var serverId = new ServerId(id);
                var previous = (await store.GetHistoryAsync(serverId, ct))
                    .OrderByDescending(item => item.Version.Value)
                    .FirstOrDefault()?.Config;
                if (previous is null)
                {
                    return Results.NotFound();
                }

                var merged = UpstreamConfigMerge.CarryOverSecrets(config, previous);
                var version = await supervisor.ReconfigureAsync(serverId, merged, ct);
                return Results.Ok(new { version = version.Value });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        servers.MapPost("/{id:guid}/rollback", async (
            Guid id, RollbackRequest body, UpstreamSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                await supervisor.RollbackAsync(new ServerId(id), new ConfigVersionId(body.Version), ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                // Ein Rollback in eine Version aus der Zeit vor der Umstellung wird von der
                // Ausfuehrungs-Policy abgelehnt (ADR-0025). Das ist eine Absage an den Aufrufer und
                // kein Serverfehler; die Meldung traegt den stabilen Reason-Code.
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        servers.MapGet("/{id:guid}/history", async (
            Guid id, IUpstreamConfigStore store, CancellationToken ct) =>
        {
            var history = await store.GetHistoryAsync(new ServerId(id), ct);
            return Results.Ok(new
            {
                versions = history.Select(v => new
                {
                    version = v.Version.Value,
                    savedAt = v.SavedAt,
                    config = UpstreamConfigRedactor.Redact(v.Config),
                }),
            });
        });

        // ── Management: RBAC (FR-36-Basis) ───────────────────────────────────
        var rbac = api.MapGroup("/rbac").AddEndpointFilter(RequireAdminAsync);

        rbac.MapGet("/identities", async (IDbContextFactory<BifrostDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.Identities.AsNoTracking().ToListAsync(ct);
            return Results.Ok(new { identities = rows });
        });

        rbac.MapPost("/identities", async (
            Identity identity, PersistentRbacStore store, IAuditSink audit, TimeProvider time, HttpContext ctx,
            CancellationToken ct) =>
        {
            await store.UpsertIdentityAsync(identity, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.RbacChanged, null, $"identity:{identity.Name}");
            return Results.Ok(new { id = identity.Id.Value });
        });

        rbac.MapDelete("/identities/{id:guid}", async (Guid id, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.RemoveIdentityAsync(new IdentityId(id), ct);
            return Results.NoContent();
        });

        rbac.MapGet("/roles", async (IDbContextFactory<BifrostDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(new { roles = await db.Roles.AsNoTracking().ToListAsync(ct) });
        });

        rbac.MapPost("/roles", async (Role role, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.UpsertRoleAsync(role, ct);
            return Results.Ok(new { id = role.Id.Value });
        });

        rbac.MapDelete("/roles/{id:guid}", async (Guid id, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.RemoveRoleAsync(new RoleId(id), ct);
            return Results.NoContent();
        });

        rbac.MapGet("/profiles", async (IDbContextFactory<BifrostDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(new { profiles = await db.Profiles.AsNoTracking().ToListAsync(ct) });
        });

        rbac.MapPost("/profiles", async (ToolProfile profile, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.UpsertProfileAsync(profile, ct);
            return Results.Ok(new { id = profile.Id.Value });
        });

        rbac.MapDelete("/profiles/{id:guid}", async (Guid id, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.RemoveProfileAsync(new ProfileId(id), ct);
            return Results.NoContent();
        });

        rbac.MapPost("/identities/{id:guid}/keys", async (
            Guid id, IssueKeyRequest body, IApiKeyService keys, CancellationToken ct) =>
        {
            var issued = await keys.IssueAsync(new IdentityId(id), body.Label, body.ExpiresAt, ct);
            return Results.Ok(new
            {
                keyId = issued.KeyId,
                plaintextKey = issued.PlaintextKey,
                warnung = "Dieser Key wird nie wieder angezeigt.",
            });
        });

        rbac.MapGet("/keys", async (Guid? identityId, IApiKeyService keys, CancellationToken ct) =>
        {
            var list = await keys.ListAsync(identityId is { } i ? new IdentityId(i) : null, ct);
            return Results.Ok(new { keys = list });
        });

        rbac.MapDelete("/keys/{keyId:guid}", async (Guid keyId, IApiKeyService keys, CancellationToken ct) =>
        {
            await keys.RevokeAsync(keyId, ct);
            return Results.NoContent();
        });

        // ── Management: Audit-Log (FR-23-Basis) ──────────────────────────────
        api.MapGet("/audit", async (
            HttpContext ctx, IAuditQuery query,
            DateTimeOffset? from, DateTimeOffset? to, Guid? caller, Guid? server, string? tool,
            InvocationStatus? status, AuditEventKind? kind, CallOrigin? origin, int? page, int? pageSize,
            CancellationToken ct) =>
        {
            // page/pageSize nullable: sonst wären es Pflicht-Query-Parameter, und ein simples
            // GET /audit schlägt mit 400 fehl. Fehlt der Wert oder ist er < 1, gilt der Default.
            var result = await query.QueryAsync(
                new AuditFilter(
                    From: from,
                    To: to,
                    Caller: caller is { } c ? new IdentityId(c) : null,
                    Server: server is { } s ? new ServerId(s) : null,
                    ToolPrefix: tool,
                    Status: status,
                    Kind: kind,
                    Origin: origin,
                    Page: page is { } p && p >= 1 ? p : 1,
                    PageSize: pageSize is { } ps && ps >= 1 ? Math.Min(ps, 1000) : 100),
                ct);
            return Results.Ok(result);
        }).AddEndpointFilter(RequireAdminAsync);

        // ── Management: Freigabe-Queue (FR-32) ───────────────────────────────
        var approvals = api.MapGroup("/approvals").AddEndpointFilter(RequireAdminAsync);

        approvals.MapGet("/", async (ApprovalState? state, IApprovalStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(state, ct)));

        approvals.MapPost("/{id:guid}/decide", async (
            Guid id, bool approved, HttpContext ctx, IApprovalStore store, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            await store.DecideAsync(id, approved, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"approval-{(approved ? "granted" : "denied")}:{id}");
            return Results.NoContent();
        });

        approvals.MapGet("/tools", (IApprovalPolicy policy) =>
            Results.Ok(new
            {
                defaultMode = policy.DefaultEnforcement.ToString(),
                tools = policy.All.Select(t => new
                {
                    tool = t.Value,
                    mode = policy.EnforcementFor(t)?.ToString(),
                }),
            }));

        approvals.MapPost("/default-mode", async (
            ApprovalDefaultMode body, HttpContext ctx, IApprovalPolicy policy, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ApprovalEnforcement>(body.Mode, ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(
                    $"Unbekannter Modus '{body.Mode}'. Erlaubt: "
                    + string.Join(", ", Enum.GetNames<ApprovalEnforcement>()));
            }

            await policy.SetDefaultEnforcementAsync(parsed, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"approval-default-mode:{parsed}");
            return Results.NoContent();
        });

        approvals.MapPost("/tools", async (
            ApprovalToolToggle body, HttpContext ctx, IApprovalPolicy policy, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            ApprovalEnforcement? enforcement;
            if (!body.Required)
            {
                enforcement = null;
            }
            else if (body.Mode is null)
            {
                enforcement = policy.DefaultEnforcement;
            }
            else if (Enum.TryParse<ApprovalEnforcement>(body.Mode, ignoreCase: true, out var parsed))
            {
                enforcement = parsed;
            }
            else
            {
                // Laut scheitern statt still auf Queue fallen: Ein Tippfehler soll nicht als
                // "hat funktioniert" durchgehen — der Aufrufer glaubte sonst, er habe den
                // Client-Modus gesetzt.
                return Results.BadRequest(
                    $"Unbekannter Modus '{body.Mode}'. Erlaubt: "
                    + string.Join(", ", Enum.GetNames<ApprovalEnforcement>()));
            }

            await policy.SetAsync(new NamespacedToolName(body.Tool), enforcement, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                enforcement is { } set
                    ? $"approval-tool-required-{set}:{body.Tool}"
                    : $"approval-tool-cleared:{body.Tool}");
            return Results.NoContent();
        });

        // ── Vorgänge (ADR-0019) ──────────────────────────────────────────────
        // Bewusst NICHT hinter RequireAdminAsync: Vorgänge gehören ihrem Aufrufer, und der soll
        // seine eigenen sehen dürfen. Die Sichtbarkeit folgt der Eigentümerschaft — ein Admin sieht
        // alle, jeder andere ausschließlich seine (ADR-0019, Berechtigungen).
        var tasks = api.MapGroup("/tasks");

        tasks.MapGet("/", async (
            HttpContext ctx, ITaskStore store, IAuthorizationService auth,
            TaskState? state, string? tool, int? page, int? pageSize,
            CancellationToken ct) =>
        {
            var caller = Identity(ctx);
            // page/pageSize nullable, damit ein simples GET /tasks nicht mit 400 scheitert.
            var result = await store.ListAsync(
                new TaskFilter(
                    Owner: IsAdmin(auth, caller) ? null : caller,
                    State: state,
                    ToolPrefix: tool,
                    Page: page is { } p && p >= 1 ? p : 1,
                    PageSize: pageSize is { } ps && ps >= 1 ? Math.Min(ps, 500) : 100),
                ct);
            return Results.Ok(result);
        });

        tasks.MapGet("/{id:guid}", async (
            Guid id, HttpContext ctx, ITaskStore store, IAuthorizationService auth,
            CancellationToken ct) =>
        {
            var task = await store.GetAsync(id, ct);
            // Ein fremder Vorgang ist "nicht gefunden", nicht "verboten": Sonst liesse sich über den
            // Statuscode abfragen, welche Ids existieren.
            return task is null || !MaySee(auth, Identity(ctx), task)
                ? Results.NotFound()
                : Results.Ok(task);
        });

        tasks.MapPost("/{id:guid}/cancel", async (
            Guid id, HttpContext ctx, ITaskStore store, IAuthorizationService auth,
            IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            var task = await store.GetAsync(id, ct);
            if (task is null || !MaySee(auth, Identity(ctx), task))
            {
                return Results.NotFound();
            }

            if (task.IsTerminal)
            {
                return Results.Conflict(new { error = "Der Vorgang ist abgeschlossen und nicht mehr abbrechbar." });
            }

            var outcome = await store.CancelAsync(id, task.Revision, ct);
            switch (outcome)
            {
                case TaskUpdateOutcome.RevisionMismatch:
                    return Results.Conflict(new
                    {
                        error = "Der Vorgang wurde zwischenzeitlich geändert — erneut lesen.",
                    });
                case TaskUpdateOutcome.NotCancellable:
                    // Ehrlich statt bequem: Der Aufruf ist bereits gelaufen. Ein 202 wäre hier die
                    // Behauptung, es sei etwas gestoppt worden.
                    return Results.Conflict(new
                    {
                        error = "Der Vorgang wurde bereits eingelöst — der Aufruf ist gelaufen und "
                            + "lässt sich nicht mehr abbrechen.",
                    });
                case TaskUpdateOutcome.Terminal:
                    return Results.Conflict(new
                    {
                        error = "Der Vorgang ist abgeschlossen und nicht mehr abbrechbar.",
                    });
                case TaskUpdateOutcome.NotFound:
                    return Results.NotFound();
            }

            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null, $"task-cancelled:{id}");

            // 200 statt 202: Der Abbruch ist endgültig, nicht bloß angenommen. Eine Freigabe, die
            // so beendet wurde, ist nicht mehr einlösbar.
            return Results.Ok(await store.GetAsync(id, ct));
        });

        MapPublisherManagement(api);
        MapConnectorPackages(api);
        MapToolDefinitionPins(api);
        MapWebhookManagement(api);
        MapOperations(api);
    }

    // ── Betrieb: Sicherung, Wiederherstellung, Diagnose, Konfiguration (M2, WP2.7) ───────────────

    /// <summary>
    /// Stabile Kennungen der Fehlerlage. Sie sind das, worauf die CLI ihre Exit-Codes aus
    /// M2-Vertrag §4 stützt — ein HTTP-Status allein trägt die Unterscheidung nicht (400 kann
    /// „Archiv kaputt" oder „Argument fehlt" heißen, und das sind 5 und 2).
    /// </summary>
    internal static class OperationsErrorCode
    {
        public const string Usage = "usage";
        public const string ArchiveInvalid = "archive-invalid";
        public const string TargetNotEmpty = "target-not-empty";
        public const string Unsupported = "unsupported";
        public const string Conflict = "conflict";
    }

    /// <summary>
    /// Diese Endpunkte sind mächtiger als alles andere im Produkt: Ein Vollbackup enthält den
    /// Key-Ring und ist damit so schützenswert wie die Instanz selbst (ADR-0024 E3), ein Restore
    /// überschreibt sie.
    /// <para>
    /// <b>Berechtigungsstufe: Global-Grant</b>, dieselbe Schwelle wie RBAC-Verwaltung,
    /// Paketinstallation und Publisher-Trust (<see cref="RequireAdminAsync"/>). Das ist keine
    /// Bequemlichkeit: Wer einen Global-Grant hat, darf bereits jedes Werkzeug auf jedem Server
    /// aufrufen, Rollen und Identitäten ändern und sich selbst Schlüssel ausstellen — er kommt an
    /// denselben Inhalt auch ohne diese Endpunkte. Eine zusätzliche Stufe hätte deshalb keine
    /// Angriffsfläche geschlossen, aber eine zweite Berechtigungsachse in ein Modell eingezogen,
    /// dessen Verträge in dieser Welle eingefroren sind.
    /// </para>
    /// <para>
    /// Was stattdessen hinzukommt: Jeder schreibende Vorgang steht im Audit-Log, und Restore wie
    /// Import laufen zweistufig über ein Handle mit 30-Minuten-Geltung und einmaliger Verwendung.
    /// </para>
    /// </summary>
    private static void MapOperations(RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/operations").AddEndpointFilter(RequireAdminAsync);

        // ── Sicherung ────────────────────────────────────────────────────────
        operations.MapPost("/backup", async (
            BackupCreateRequest body, HttpContext ctx, IBackupService backups, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            if (!TryParseSections(body.Sections, out var sections, out var sectionProblem))
            {
                return OperationsError(StatusCodes.Status400BadRequest, OperationsErrorCode.Usage, sectionProblem);
            }

            try
            {
                var result = await backups.CreateAsync(
                    new BackupRequest(body.TargetPath, sections, body.Passphrase), ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                    $"backup-created:{result.Manifest.Sections}");
                return Results.Ok(new
                {
                    archivePath = result.ArchivePath,
                    sizeBytes = result.SizeBytes,
                    manifest = result.Manifest,
                    // ADR-0024 E3: Ein unverschlüsseltes Vollbackup wird beim Erzeugen ausdrücklich
                    // als das benannt, was es ist. Verbieten wäre falsch, verschweigen auch.
                    hinweis = Hinweis(result.Manifest),
                });
            }
            catch (NotSupportedException exception)
            {
                return OperationsError(
                    StatusCodes.Status501NotImplemented, OperationsErrorCode.Unsupported, exception.Message);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
                return OperationsError(
                    StatusCodes.Status400BadRequest, OperationsErrorCode.Usage, exception.Message);
            }
        });

        operations.MapPost("/backup/verify", async (
            ArchiveRequest body, IBackupService backups, CancellationToken ct) =>
        {
            try
            {
                // Ein ungültiges Archiv ist hier kein HTTP-Fehler, sondern das Ergebnis der Prüfung:
                // Der Aufrufer hat genau danach gefragt. Die Bewertung steht im Rumpf.
                var inspection = await backups.InspectAsync(body.ArchivePath, body.Passphrase, ct);
                return Results.Ok(inspection);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
                return OperationsError(
                    StatusCodes.Status400BadRequest, OperationsErrorCode.Usage, exception.Message);
            }
        });

        // ── Wiederherstellung: zweistufig über das Handle im Plan ────────────
        operations.MapPost("/restore/plan", async (
            RestorePlanRequest body, IRestoreService restore, CancellationToken ct) =>
        {
            if (!TryParseRestoreMode(body.Mode, out var mode, out var modeProblem))
            {
                return OperationsError(StatusCodes.Status400BadRequest, OperationsErrorCode.Usage, modeProblem);
            }

            try
            {
                // Der Plan geht als JSON hinaus und kommt als neues Objekt zurück — genau dafür
                // trägt er ein Handle statt seiner Objektidentität (M2-Vertrag, Nachtrag).
                var plan = await restore.PlanAsync(
                    new RestoreRequest(body.ArchivePath, mode, body.Passphrase), ct);
                return Results.Ok(plan);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
                return OperationsError(
                    StatusCodes.Status400BadRequest, OperationsErrorCode.Usage, exception.Message);
            }
        });

        operations.MapPost("/restore/apply", async (
            RestorePlan plan, HttpContext ctx, IRestoreService restore, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                var result = await restore.ApplyAsync(plan, ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                    $"restore-applied:{result.RestoredSections}");
                return Results.Ok(result);
            }
            catch (InvalidOperationException exception)
            {
                // Unbekanntes oder abgelaufenes Handle, oder ein Plan mit Blockern. Beides ist eine
                // Absage mit Begründung — nie ein Versuch auf geratenen Daten.
                return OperationsError(
                    StatusCodes.Status409Conflict, OperationsErrorCode.Conflict, exception.Message);
            }
        });

        // ── Diagnose ─────────────────────────────────────────────────────────
        operations.MapGet("/doctor", async (
            string? scope, IDiagnosticService diagnostics, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsed, out var scopeProblem))
            {
                return OperationsError(StatusCodes.Status400BadRequest, OperationsErrorCode.Usage, scopeProblem);
            }

            var report = await diagnostics.RunAsync(parsed, ct);
            return Results.Ok(new
            {
                scope = report.Scope.ToString(),
                startedAt = report.StartedAt,
                durationMs = report.Duration.TotalMilliseconds,
                // Die Bewertung kommt aus dem Bericht selbst; die Aufrufer bilden daraus nur ihren
                // Exit-Code und rechnen die Regel nicht nach.
                hasWarnings = report.HasWarnings,
                hasFailures = report.HasFailures,
                checks = report.Checks,
            });
        });

        // ── Konfigurationsexport (ADR-0024 E8 — nicht Backup) ────────────────
        operations.MapPost("/config/export", async (
            ConfigExportRequest body, HttpContext ctx, IConfigurationExportService config,
            IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                var export = await config.ExportAsync(
                    new ConfigurationExportRequest(body.IncludeSecrets, body.Passphrase), ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                    export.ContainsSecrets ? "config-exported:credentials" : "config-exported");
                return Results.Ok(export);
            }
            catch (ArgumentException exception)
            {
                return OperationsError(
                    StatusCodes.Status400BadRequest, OperationsErrorCode.Usage, exception.Message);
            }
        });

        operations.MapPost("/config/import/plan", async (
            ConfigImportRequest body, IConfigurationExportService config, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await config.PlanImportAsync(body.Payload, body.Passphrase, ct));
            }
            catch (Exception exception) when (exception is ConfigurationImportException or ArgumentException)
            {
                return OperationsError(
                    StatusCodes.Status400BadRequest, OperationsErrorCode.ArchiveInvalid, exception.Message);
            }
        });

        operations.MapPost("/config/import/apply", async (
            ConfigurationImportPlan plan, HttpContext ctx, IConfigurationExportService config,
            IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                await config.ApplyImportAsync(plan, ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                    $"config-imported:{plan.Additions.Count}");
                return Results.NoContent();
            }
            catch (ConfigurationImportException exception)
            {
                return OperationsError(
                    StatusCodes.Status409Conflict, OperationsErrorCode.Conflict, exception.Message);
            }
        });

        // ── Der Riegel aus BFR-DB-0101 ausdrücklich lösen ────────────────────
        // Ohne diesen Weg müsste ein Betreiber eine Datenbankzeile von Hand löschen. Er repariert
        // nichts und beurteilt nichts — er löst, was der Betreiber geprüft hat.
        operations.MapPost("/database/unblock", async (
            HttpContext ctx, IDbContextFactory<BifrostDbContext> factory, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await MigrationJournal.EnsureTableAsync(db, ct);
            var removed = await MigrationJournal.ClearUnfinishedAsync(db, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"migration-journal-cleared:{removed}");
            return Results.Ok(new
            {
                removed,
                hinweis = removed == 0
                    ? "Es stand kein offener Migrationseintrag an."
                    : "Der Riegel ist gelöst. Der nächste Start migriert weiter — der Schemazustand "
                        + "ist damit NICHT geprüft; das war die Aufgabe davor.",
            });
        });
    }

    private static string Hinweis(BackupManifest manifest)
        => manifest.Encrypted
            ? "Das Archiv ist verschlüsselt. Ohne die Passphrase ist es wertlos."
            : manifest.Sections.HasFlag(BackupSections.KeyRing)
                ? "UNVERSCHLÜSSELTES Vollbackup: Es enthält den DataProtection-Key-Ring und ist damit "
                    + "so schützenswert wie die Instanz selbst (ADR-0024 E3)."
                : "Das Archiv ist unverschlüsselt.";

    private static IResult OperationsError(int statusCode, string code, string message)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    /// <summary>
    /// Bereichsnamen -> Flags. Ein unbekannter Name wird abgewiesen und nicht still weggelassen
    /// (M2-Vertrag §6, Invariante 3): Wer sich vertippt, bekäme sonst ein Archiv ohne den Bereich,
    /// auf den es ihm ankam.
    /// </summary>
    private static bool TryParseSections(
        IReadOnlyList<string>? names, out BackupSections sections, out string problem)
    {
        sections = BackupSections.All;
        problem = string.Empty;
        if (names is null || names.Count == 0)
        {
            return true;
        }

        var parsed = BackupSections.None;
        foreach (var name in names)
        {
            if (!Enum.TryParse<BackupSections>(name, ignoreCase: true, out var one)
                || one is BackupSections.None)
            {
                problem = $"Unbekannter Bereich '{name}'. Erlaubt: "
                    + string.Join(", ", Enum.GetNames<BackupSections>().Where(n => n != nameof(BackupSections.None)));
                return false;
            }

            parsed |= one;
        }

        sections = parsed;
        return true;
    }

    private static bool TryParseRestoreMode(string? value, out RestoreMode mode, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = RestoreMode.EmptyTargetOnly;
            return true;
        }

        if (Enum.TryParse(value, ignoreCase: true, out mode))
        {
            return true;
        }

        problem = $"Unbekannter Modus '{value}'. Erlaubt: "
            + string.Join(", ", Enum.GetNames<RestoreMode>());
        return false;
    }

    private static bool TryParseScope(string? value, out DiagnosticScope scope, out string problem)
    {
        scope = DiagnosticScope.All;
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parsed = DiagnosticScope.None;
        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<DiagnosticScope>(name, ignoreCase: true, out var one)
                || one is DiagnosticScope.None)
            {
                problem = $"Unbekannter Bereich '{name}'. Erlaubt: "
                    + string.Join(", ", Enum.GetNames<DiagnosticScope>().Where(n => n != nameof(DiagnosticScope.None)));
                return false;
            }

            parsed |= one;
        }

        scope = parsed;
        return true;
    }

    private sealed record BackupCreateRequest(
        string TargetPath, IReadOnlyList<string>? Sections = null, string? Passphrase = null);

    private sealed record ArchiveRequest(string ArchivePath, string? Passphrase = null);

    private sealed record RestorePlanRequest(
        string ArchivePath, string? Mode = null, string? Passphrase = null);

    private sealed record ConfigExportRequest(bool IncludeSecrets = false, string? Passphrase = null);

    private sealed record ConfigImportRequest(string Payload, string? Passphrase = null);

    /// <summary>
    /// Festgehaltene Tool-Definitionen (Rug-Pull-Schutz). Ändert ein Upstream still die Beschreibung
    /// oder das Schema eines Tools, wird es zurückgehalten, bis hier jemand die neue Fassung annimmt.
    /// Nur Admins — es ist dieselbe Entscheidung wie „diesem Server vertraue ich".
    /// </summary>
    private static void MapToolDefinitionPins(RouteGroupBuilder api)
    {
        var pins = api.MapGroup("/tool-definitions").AddEndpointFilter(RequireAdminAsync);

        pins.MapGet("/", (IToolDefinitionPinStore store, IUpstreamSupervisor supervisor) =>
        {
            var slugs = supervisor.Statuses.ToDictionary(s => s.Id, s => s.Slug);
            return Results.Ok(new
            {
                pins = store.All.Select(pin => new
                {
                    server = pin.Server.Value,
                    slug = slugs.TryGetValue(pin.Server, out var slug) ? slug : null,
                    tool = pin.Tool,
                    acceptedHash = pin.AcceptedHash,
                    acceptedAt = pin.AcceptedAt,
                    pendingHash = pin.PendingHash,
                    pendingSince = pin.PendingSince,
                    quarantined = pin.HasPendingChange,
                }),
            });
        });

        // Annahme der geänderten Fassung. Danach wird der Katalog des Upstreams neu abgefragt,
        // damit das Tool ohne Neustart zurückkommt — und die Prüfung gegen die echte aktuelle
        // Definition läuft, nicht gegen eine zwischengespeicherte Kopie.
        pins.MapPost("/{serverId:guid}/{tool}/accept", async (
            Guid serverId, string tool, HttpContext ctx, IToolDefinitionPinStore store,
            UpstreamSupervisor supervisor, IAuditSink audit, TimeProvider time,
            CancellationToken ct) =>
        {
            var server = new ServerId(serverId);
            var pin = store.All.FirstOrDefault(p => p.Server == server && p.Tool == tool);
            if (pin is null)
            {
                return Results.NotFound();
            }

            if (!pin.HasPendingChange)
            {
                return Results.BadRequest(new
                {
                    error = $"Für '{tool}' steht keine geänderte Definition an.",
                });
            }

            await store.AcceptAsync(server, tool, ct);
            await supervisor.RediscoverAsync(server, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, server,
                $"tool-definition-accepted:{tool}");
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Connector-Pakete (ADR-0016). Nur Admins: Wer hier installiert, entscheidet, welcher fremde
    /// Code im Gateway läuft — dieselbe Schwelle wie beim Pinnen eines Herausgebers.
    /// </summary>
    private static void MapConnectorPackages(RouteGroupBuilder api)
    {
        var packages = api.MapGroup("/packages").AddEndpointFilter(RequireAdminAsync);

        packages.MapGet("/", async (IConnectorPackageStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        packages.MapGet("/{packageId}", async (
            string packageId, IConnectorPackageStore store, CancellationToken ct) =>
        {
            var versions = await store.GetVersionsAsync(packageId, ct);
            return versions.Count == 0 ? Results.NotFound() : Results.Ok(versions);
        });

        // Der Rumpf ist das Paket selbst (application/octet-stream). Zustimmungen kommen als
        // Query-Parameter, damit die Datei unverändert durchgereicht werden kann.
        packages.MapPost("/", async (
            HttpRequest request, HttpContext ctx, ConnectorPackageInstaller installer,
            ConnectorPackageResolver resolver, CancellationToken ct) =>
        {
            var accepted = request.Query["grant"].Where(g => !string.IsNullOrWhiteSpace(g)).ToArray()!;
            var allowUntrusted = request.Query["allowUntrusted"] is ["1" or "true", ..];

            // Erst vollständig in den Speicher: Der Reader muss mehrfach durch das Archiv, und ein
            // nicht-suchbarer Netzwerk-Stream ließe die Größenprüfung ins Leere laufen.
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length == 0)
            {
                return Results.BadRequest(new { error = "Leerer Rumpf — erwartet wird ein .mcpkg-Archiv." });
            }

            buffer.Position = 0;
            try
            {
                var installed = await installer.InstallAsync(
                    buffer,
                    new ConnectorInstallOptions(accepted!, allowUntrusted),
                    Identity(ctx),
                    ct);
                await resolver.RefreshAsync(ct);
                return Results.Created($"/api/v1/packages/{installed.Package.PackageId}", installed);
            }
            catch (ConnectorPackageException exception)
            {
                // Ein abgewiesenes Paket ist ein Eingabefehler des Administrators, kein
                // Serverfehler — und die Meldung nennt den Grund, damit er behebbar ist.
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                // Dasselbe fuer ein Paket, dessen Transport nativ startet (ADR-0025 E4).
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        packages.MapPost("/{packageId}/rollback", async (
            string packageId, HttpContext ctx, ConnectorPackageInstaller installer,
            ConnectorPackageResolver resolver, CancellationToken ct) =>
        {
            try
            {
                var active = await installer.RollbackAsync(packageId, Identity(ctx), ct);
                await resolver.RefreshAsync(ct);
                return Results.Ok(active);
            }
            catch (ConnectorPackageException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        packages.MapDelete("/{packageId}/{version}", async (
            string packageId, string version, HttpContext ctx,
            ConnectorPackageInstaller installer, ConnectorPackageResolver resolver,
            CancellationToken ct) =>
        {
            try
            {
                await installer.RemoveVersionAsync(packageId, version, Identity(ctx), ct);
                await resolver.RefreshAsync(ct);
                return Results.NoContent();
            }
            catch (ConnectorPackageException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // ADR-0021 F5: Das Entfernen nimmt die Skills des Pakets mit. Die Auflage dazu ist, dass es
        // vorher gesagt wird — ueber die API heisst das: Es gibt einen Weg, es vorher zu erfahren.
        packages.MapGet("/{packageId}/removal-preview", async (
            string packageId, ConnectorPackageInstaller installer, CancellationToken ct) =>
        {
            var skills = await installer.PreviewRemovalAsync(packageId, ct);
            return Results.Ok(new
            {
                skills = skills.Select(s => new
                {
                    name = s.Name,
                    version = s.LatestVersion.Value,

                    // Die neueste Fassung traegt keine Paketherkunft mehr: Jemand hat den Text
                    // angepasst, und diese Arbeit geht mit verloren.
                    locallyEdited = s.Source is null,
                }),
            });
        });

        packages.MapDelete("/{packageId}", async (
            string packageId, HttpContext ctx, ConnectorPackageInstaller installer,
            ConnectorPackageResolver resolver, CancellationToken ct) =>
        {
            try
            {
                var removedSkills = await installer.RemovePackageAsync(packageId, Identity(ctx), ct);
                await resolver.RefreshAsync(ct);

                // Kein NoContent mehr: Was mitgegangen ist, gehoert in die Antwort. Ein leerer
                // Rumpf haette verschwiegen, dass Skills geloescht wurden.
                return Results.Ok(new { removedSkills });
            }
            catch (ConnectorPackageException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
    }

    /// <summary>
    /// <paramref name="Mode"/> ist optional; ohne Angabe gilt die eingestellte Vorgabe. Ausgeliefert
    /// wird die als <c>Queue</c> — ein Skript, das diesen Endpunkt vor ADR-0022 benutzt hat,
    /// verhaelt sich also unveraendert, bis jemand die Vorgabe bewusst umstellt.
    /// </summary>
    private sealed record ApprovalToolToggle(string Tool, bool Required, string? Mode = null);

    private sealed record ApprovalDefaultMode(string Mode);

    /// <summary>
    /// Verwaltung der vertrauenswürdigen Publisher für WASI-Components (Plan 0003, WP4).
    /// Nur Admins — wer hier schreibt, entscheidet, welcher fremde Code im Gateway laufen darf.
    /// </summary>
    private static void MapPublisherManagement(RouteGroupBuilder api)
    {
        var publishers = api.MapGroup("/publishers").AddEndpointFilter(RequireAdminAsync);

        publishers.MapGet("/", (IPublisherTrustStore trust) => Results.Ok(trust.All));

        publishers.MapPost("/", async (
            PinPublisherRequest body, HttpContext ctx, IPublisherTrustStore trust, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            PublisherKey key;
            try
            {
                key = await trust.PinAsync(body.PublicKey, body.Label ?? string.Empty, ct);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // Ein unbrauchbarer Schlüssel ist ein Eingabefehler, kein Serverfehler — und er
                // darf nicht als "gepinnt" durchgehen.
                return Results.BadRequest(new { error = "Ed25519-Public-Key erwartet: Base64, 32 Byte." });
            }

            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"publisher-pinned:{key.KeyId}");
            return Results.Ok(key);
        });

        publishers.MapPost("/{keyId}/revoke", async (
            string keyId, HttpContext ctx, IPublisherTrustStore trust, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            await trust.RevokeAsync(keyId, ct);
            // Der Entzug wirkt sofort: laufende Upstreams dieses Publishers werden gestoppt.
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"publisher-revoked:{keyId}");
            return Results.NoContent();
        });

        publishers.MapPost("/{keyId}/reinstate", async (
            string keyId, HttpContext ctx, IPublisherTrustStore trust, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            await trust.ReinstateAsync(keyId, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"publisher-reinstated:{keyId}");
            return Results.NoContent();
        });

        // Die Vertrauensstufe (ADR-0016) entscheidet, wie viel ein Paket dieses Herausgebers ohne
        // Rückfrage bekommt. Eigener Endpunkt, kein Feld beim Pinnen — sonst wäre „vertrauen" und
        // „viel erlauben" derselbe Klick.
        publishers.MapPut("/{keyId}/trust-level", async (
            string keyId, TrustLevelRequest body, HttpContext ctx, IPublisherTrustStore trust,
            IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ConnectorTrustLevel>(body.Level, ignoreCase: true, out var level))
            {
                return Results.BadRequest(new
                {
                    error = "Unbekannte Stufe. Erlaubt: Official, ThirdParty, Community.",
                });
            }

            try
            {
                await trust.SetTrustLevelAsync(keyId, level, ct);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"publisher-trust-level:{keyId}={level}");
            return Results.NoContent();
        });
    }

    private sealed record TrustLevelRequest(string Level);

    private sealed record PinPublisherRequest(string PublicKey, string? Label);

    private static void MapWebhookManagement(RouteGroupBuilder api)
    {
        // Verwaltung der Webhooks (FR-20). Der Trigger-Endpunkt selbst liegt außerhalb von /api
        // (unauthentifiziert, signaturgeschützt); hier nur das Anlegen/Auflisten/Entfernen.
        // ── Management: Skills (FR-40) ───────────────────────────────────────
        // Jeder andere Speicher hat eine REST-Flaeche; Skills hatten nur die Weboberflaeche. Fuer
        // einen einzelnen Text geht das — fuer eine Sammlung aus Dutzenden Dateien, wie sie ein
        // Agent mitbringt, ist Abtippen keine Bedienung. Ohne diese Endpunkte laesst sich der
        // Bestand weder aus einem Repository befuellen noch versionieren noch sichern.
        var skills = api.MapGroup("/skills").AddEndpointFilter(RequireAdminAsync);

        skills.MapGet("/", async (IAssetStore store, CancellationToken ct) =>
        {
            var all = await store.ListAsync(ct);
            return Results.Ok(new
            {
                skills = all.Select(a => new
                {
                    id = a.Id.Value,
                    name = a.Name,
                    description = a.Description,
                    whenToUse = a.MetadataOrEmpty.WhenToUse,
                    references = a.MetadataOrEmpty.ReferencesOrEmpty,
                    requiredTools = a.MetadataOrEmpty.RequiredToolsOrEmpty,
                    version = a.LatestVersion.Value,
                    updatedAt = a.UpdatedAt,
                    // Herkunft mitgeben: Wer per Skript pflegt, muss erkennen, was aus einem Paket
                    // stammt und beim naechsten Update ueberschrieben wuerde (ADR-0021).
                    source = a.Source is null ? null : $"{a.Source.PackageId}@{a.Source.PackageVersion}",
                }),
            });
        });

        skills.MapGet("/{id:guid}", async (
            Guid id, int? version, IAssetStore store, CancellationToken ct) =>
        {
            try
            {
                var content = await store.GetAsync(
                    new AssetId(id), version is { } v ? new AssetVersion(v) : null, ct);
                return Results.Ok(new
                {
                    id = content.Id.Value,
                    name = content.Name,
                    version = content.Version.Value,
                    whenToUse = content.MetadataOrEmpty.WhenToUse,
                    references = content.MetadataOrEmpty.ReferencesOrEmpty,
                    requiredTools = content.MetadataOrEmpty.RequiredToolsOrEmpty,
                    content = content.Content,
                });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        skills.MapPost("/", async (
            SkillCreate body, HttpContext ctx, IAssetStore store, ISkillValidator validator,
            IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            var metadata = new SkillMetadata(body.WhenToUse, body.References, body.RequiredTools);
            try
            {
                var id = await store.CreateAsync(
                    body.Name, body.Description, body.Content, metadata.IsEmpty ? null : metadata, ct);
                AuditManagement(audit, time, ctx, AuditEventKind.AssetChanged, null, $"skill-created:{body.Name}");

                // Befunde sind Warnungen, keine Fehler (siehe ISkillValidator) — sie kommen mit der
                // Antwort zurueck, damit ein Skript sie sieht, statt dass sie nur in der Oberflaeche
                // erscheinen.
                var findings = await validator.ValidateAsync(body.Name, metadata, ct);
                return Results.Created($"/api/v1/skills/{id.Value}", new
                {
                    id = id.Value,
                    findings = findings.Select(f => new { field = f.Field, message = f.Message }),
                });
            }
            catch (InvalidOperationException exception)
            {
                // Doppelter Name oder Groessengrenze — beides sind Bedienfehler, keine Serverfehler.
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        skills.MapPost("/{id:guid}/versions", async (
            Guid id, SkillPublish body, HttpContext ctx, IAssetStore store, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            var metadata = new SkillMetadata(body.WhenToUse, body.References, body.RequiredTools);
            try
            {
                var version = await store.PublishAsync(
                    new AssetId(id), body.Content, metadata.IsEmpty ? null : metadata, ct,
                    body.Description);
                AuditManagement(audit, time, ctx, AuditEventKind.AssetChanged, null, $"skill-published:{id}");
                return Results.Ok(new { version = version.Value });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        var hooks = api.MapGroup("/webhooks").AddEndpointFilter(RequireAdminAsync);

        hooks.MapGet("/", async (IWebhookStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        hooks.MapPost("/", async (
            WebhookCreate body, HttpContext ctx, IWebhookStore store, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            var (def, secret) = await store.CreateAsync(
                body.Name, new IdentityId(body.CallerId), new NamespacedToolName(body.Tool), ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null, $"webhook-added:{def.Id}");
            // Secret genau einmal — danach nur noch verschlüsselt gehalten.
            return Results.Created($"/api/v1/webhooks/{def.Id}", new { def.Id, Secret = secret });
        });

        hooks.MapPost("/{id:guid}/enabled", async (
            Guid id, EnabledRequest body, HttpContext ctx, IWebhookStore store, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            await store.SetEnabledAsync(id, body.Enabled, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"webhook-{(body.Enabled ? "enabled" : "disabled")}:{id}");
            return Results.NoContent();
        });

        hooks.MapDelete("/{id:guid}", async (
            Guid id, HttpContext ctx, IWebhookStore store, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            await store.RemoveAsync(id, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null, $"webhook-removed:{id}");
            return Results.NoContent();
        });
    }

    private sealed record WebhookCreate(string Name, Guid CallerId, string Tool);

    private sealed record SkillCreate(
        string Name,
        string Content,
        string? Description = null,
        string? WhenToUse = null,
        IReadOnlyList<string>? References = null,
        IReadOnlyList<string>? RequiredTools = null);

    private sealed record SkillPublish(
        string Content,
        string? WhenToUse = null,
        IReadOnlyList<string>? References = null,
        IReadOnlyList<string>? RequiredTools = null,
        // null heisst "unveraendert uebernehmen". Ohne dieses Feld war eine einmal gesetzte
        // Beschreibung fuer immer festgeschrieben — ausgerechnet die Angabe, an der ein Agent
        // entscheidet, ob er den Skill nimmt.
        string? Description = null);

    private static IdentityId Identity(HttpContext ctx) => (IdentityId)ctx.Items[ApiKeyAuthMiddleware.IdentityItemKey]!;

    private static bool IsAdmin(IAuthorizationService auth, IdentityId caller)
        => auth.Evaluate(caller, new PermissionScope(null, null), ToolAction.UseTool).Allowed;

    /// <summary>
    /// Wer einen Vorgang sehen darf: sein Eigentümer, sonst nur eine Identität mit Global-Grant.
    /// </summary>
    private static bool MaySee(IAuthorizationService auth, IdentityId caller, TaskRecord task)
        => task.Owner == caller || IsAdmin(auth, caller);

    /// <summary>Bis WP6 echte UI-Rollen bringt: Management verlangt einen Global-Grant (Plan-Änderungslog WP5).</summary>
    private static async ValueTask<object?> RequireAdminAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        var decision = auth.Evaluate(Identity(context.HttpContext), new PermissionScope(null, null), ToolAction.UseTool);
        return decision.Allowed
            ? await next(context)
            : Results.Json(
                new { error = "Management-API erfordert eine Identität mit Global-Grant." },
                statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>HTTP-Status je Invocation-Status — eine Quelle für beide Aufruf-Flächen.</summary>
    private static int StatusFor(InvocationStatus status) => status switch
    {
        InvocationStatus.ValidationFailed => StatusCodes.Status400BadRequest,
        InvocationStatus.Denied => StatusCodes.Status403Forbidden,
        InvocationStatus.ApprovalRequired => StatusCodes.Status409Conflict,
        InvocationStatus.ToolNotFound => StatusCodes.Status404NotFound,
        InvocationStatus.Timeout => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status502BadGateway,
    };

    private static IResult Error(int statusCode, ToolInvocationResult result)
        => Results.Json(new { status = result.Status.ToString(), error = result.ErrorMessage }, statusCode: statusCode);

    private static void AuditManagement(
        IAuditSink audit, TimeProvider time, HttpContext ctx, AuditEventKind kind, ServerId? server, string subject)
        => audit.Record(new AuditEvent(
            time.GetUtcNow(), Identity(ctx), CallOrigin.Rest, kind, server, subject, null, null, null, null, null));

    private sealed record EnabledRequest(bool Enabled);

    private sealed record RollbackRequest(int Version);

    private sealed record IssueKeyRequest(string Label, DateTimeOffset? ExpiresAt);
}
