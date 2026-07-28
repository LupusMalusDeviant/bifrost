using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Core.Capabilities;
using McpMcp.Core.Upstreams;
using McpMcp.Core.Packaging;
using McpMcp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Server;

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

        rbac.MapGet("/identities", async (IDbContextFactory<McpMcpDbContext> factory, CancellationToken ct) =>
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

        rbac.MapGet("/roles", async (IDbContextFactory<McpMcpDbContext> factory, CancellationToken ct) =>
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

        rbac.MapGet("/profiles", async (IDbContextFactory<McpMcpDbContext> factory, CancellationToken ct) =>
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
            Results.Ok(policy.All.Select(t => t.Value)));

        approvals.MapPost("/tools", async (
            ApprovalToolToggle body, HttpContext ctx, IApprovalPolicy policy, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            await policy.SetAsync(new NamespacedToolName(body.Tool), body.Required, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null,
                $"approval-tool-{(body.Required ? "required" : "cleared")}:{body.Tool}");
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
    }

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

    private sealed record ApprovalToolToggle(string Tool, bool Required);

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
