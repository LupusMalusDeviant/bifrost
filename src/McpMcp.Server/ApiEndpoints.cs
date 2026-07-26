using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
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

            // `Requested`, nicht `Cancelled`: Ob der Upstream wirklich aufgehört hat, weiss hier
            // niemand — das bestätigt der Ausführende (ADR-0019, Entscheidung 3).
            var outcome = await store.UpdateAsync(
                new TaskUpdate(id, Cancellation: TaskCancellation.Requested), task.Revision, ct);
            if (outcome is TaskUpdateOutcome.RevisionMismatch)
            {
                return Results.Conflict(new { error = "Der Vorgang wurde zwischenzeitlich geändert — erneut lesen." });
            }

            AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, null, $"task-cancel-requested:{id}");
            return Results.Accepted($"/api/v1/tasks/{id}");
        });

        MapPublisherManagement(api);
        MapWebhookManagement(api);
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
    }

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
