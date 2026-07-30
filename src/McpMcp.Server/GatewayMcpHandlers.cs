using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Core.Invocation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMcp.Server;

/// <summary>
/// MCP-Handler des Gateways (WP4.2): tools/list aus der Profil-Sicht, tools/call über die
/// Invoker-Pipeline (inkl. Meta-Tools), Resources/Prompts-Passthrough (FR-04).
/// Alle Sichtbarkeit kommt aus derselben RBAC-Quelle wie überall (DO Nr. 2).
/// </summary>
internal static class GatewayMcpHandlers
{
    public static ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        LogClientCapabilitiesOnce(ctx.Services!, ctx.Server);
        var catalog = ctx.Services!.GetRequiredService<IToolCatalog>();
        var view = catalog.GetViewFor(identity);

        var tools = view.PinnedTools
            .Where(e => e.Kind is CatalogEntryKind.Tool)
            .Select(e => new Tool { Name = e.Name.Value, Description = e.Description, InputSchema = e.InputSchema })
            .ToList();

        if (view.LazyToolsEnabled)
        {
            tools.AddRange(MetaToolService.Definitions.Select(d => new Tool
            {
                Name = d.Name,
                Description = d.Description,
                InputSchema = d.InputSchema,
            }));
        }

        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    public static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        // Auch hier, nicht nur bei tools/list: Ein Client, der seine Werkzeugliste aus dem
        // Zwischenspeicher nimmt, ruft nach dem Neuverbinden direkt tools/call — dann bliebe die
        // Zeile aus, obwohl eine Session steht.
        LogClientCapabilitiesOnce(ctx.Services!, ctx.Server);
        var name = ctx.Params?.Name ?? throw new McpException("tools/call ohne Tool-Namen.");
        var args = ToJsonElement(ctx.Params.Arguments);

        ToolInvocationResult result;
        if (MetaToolService.IsMetaTool(name))
        {
            var metaTools = ctx.Services!.GetRequiredService<MetaToolService>();
            result = await metaTools.ExecuteAsync(identity, CallOrigin.Mcp, name, args, ct).ConfigureAwait(false);
        }
        else
        {
            var invoker = ctx.Services!.GetRequiredService<IToolInvoker>();
            result = await invoker.InvokeAsync(
                new ToolInvocationRequest(identity, CallOrigin.Mcp, new NamespacedToolName(name), args, null), ct)
                .ConfigureAwait(false);
        }

        // Ob ein Ergebnis von einem Upstream durchgereicht wurde, WEISS diese Stelle — sie hat die
        // Entscheidung gerade selbst getroffen. Vorher wurde es am Nutzinhalt geraten, und das ging
        // schief, sobald ein Meta-Tool selbst ein Feld 'content' fuehrt (read_skill).
        // Fehlt eine Freigabe und kann der Client fragen, wird JETZT gefragt statt in die
        // Warteschlange gelegt (ADR-0012 bleibt gueltig — nur der Weg zum Menschen ist kuerzer).
        // Bei Zustimmung laeuft derselbe Aufruf einmalig durch; die Freigabe ist an Identitaet,
        // Werkzeug und Argument-Fingerabdruck gebunden, ein zweiter Aufruf fragt wieder.
        if (result.Status is InvocationStatus.ApprovalRequired && result.TaskId is { } approvalId)
        {
            result = await RetryAfterElicitedApprovalAsync(
                ctx, identity, name, args, approvalId, result, ct).ConfigureAwait(false);
        }

        // invoke_tool ist zwar ein Meta-Tool, reicht aber das Ergebnis eines Upstreams durch —
        // die Unterscheidung ist nicht "Meta-Tool oder nicht", sondern "fremdes Ergebnis oder
        // eigener Nutzinhalt". Sie steht bei den Definitionen, nicht hier.
        return ToCallToolResult(
            result,
            isPassthrough: !MetaToolService.IsMetaTool(name) || MetaToolService.ForwardsUpstreamResult(name));
    }

    public static async ValueTask<ListResourcesResult> ListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var (catalog, authorization, supervisor) = ResolveCatalogServices(ctx.Services!);

        var visible = authorization.FilterVisible(identity, catalog.Snapshot)
            .Where(e => e.Kind is CatalogEntryKind.Resource)
            .Select(e => e.Name)
            .ToHashSet();

        var resources = new List<Resource>();
        foreach (var status in supervisor.Statuses)
        {
            if (supervisor.GetInventory(status.Id) is not { } inventory)
            {
                continue;
            }

            resources.AddRange(inventory.Resources
                .Where(r => visible.Contains(NamespacedToolName.Create(status.Slug, r.Name)))
                .Select(r => new Resource
                {
                    Uri = r.Uri.ToString(),
                    Name = NamespacedToolName.Create(status.Slug, r.Name).Value,
                    Description = r.Description,
                    MimeType = r.MimeType,
                }));
        }

        // Zentrale Assets (FR-40): zusätzlich zu den Upstream-Resources, unter reserviertem URI-Schema.
        var assets = ctx.Services!.GetRequiredService<IAssetStore>();
        foreach (var asset in await assets.ListAsync(ct).ConfigureAwait(false))
        {
            resources.Add(new Resource
            {
                Uri = AssetDelivery.ResourceUri(asset.Name),
                Name = AssetDelivery.PromptName(asset.Name),
                Description = asset.Description ?? $"Zentral verwaltetes Asset (v{asset.LatestVersion.Value}).",
                MimeType = "text/markdown",
            });
        }

        return new ListResourcesResult { Resources = resources };
    }

    public static async ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var uri = ctx.Params?.Uri ?? throw new McpException("resources/read ohne URI.");
        var (_, authorization, supervisor) = ResolveCatalogServices(ctx.Services!);

        // Zentrale Assets (FR-40) vor den Upstreams prüfen — eigener URI-Namespace.
        if (AssetDelivery.TryGetAssetName(uri) is { } assetResourceName)
        {
            var content = await LoadAssetAsync(ctx.Services!, identity, assetResourceName, ct).ConfigureAwait(false);
            return new ReadResourceResult
            {
                Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = content.Content }],
            };
        }

        foreach (var status in supervisor.Statuses)
        {
            var resource = supervisor.GetInventory(status.Id)?.Resources
                .FirstOrDefault(r => string.Equals(r.Uri.ToString(), uri, StringComparison.Ordinal));
            if (resource is null)
            {
                continue;
            }

            var name = NamespacedToolName.Create(status.Slug, resource.Name);
            var decision = authorization.Evaluate(
                identity, new PermissionScope(status.Id, name), ToolAction.ReadResource);
            AuditPassthrough(ctx.Services!, identity, status.Id, name.Value,
                decision.Allowed ? InvocationStatus.Success : InvocationStatus.Denied);
            if (!decision.Allowed)
            {
                throw new McpException($"Resource '{uri}' ist nicht sichtbar.");
            }

            var connection = supervisor.GetConnection(status.Id)
                ?? throw new McpException($"Upstream für Resource '{uri}' ist nicht verbunden.");
            var payload = await connection.ReadResourceAsync(resource.Uri, ct).ConfigureAwait(false);
            return Deserialize<ReadResourceResult>(payload);
        }

        throw new McpException($"Resource '{uri}' existiert nicht.");
    }

    public static async ValueTask<ListPromptsResult> ListPromptsAsync(
        RequestContext<ListPromptsRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var (catalog, authorization, _) = ResolveCatalogServices(ctx.Services!);

        var prompts = authorization.FilterVisible(identity, catalog.Snapshot)
            .Where(e => e.Kind is CatalogEntryKind.Prompt)
            .Select(e => new Prompt { Name = e.Name.Value, Description = e.Description })
            .ToList();

        // Zentrale Assets (FR-40): der eigentliche Zweck von Keyfeature 7 — ein Ort, von dem sich
        // alle Agenten ihre Skills/Instructions ziehen.
        var assets = ctx.Services!.GetRequiredService<IAssetStore>();
        prompts.AddRange((await assets.ListAsync(ct).ConfigureAwait(false))
            .Select(a => new Prompt
            {
                Name = AssetDelivery.PromptName(a.Name),
                Description = a.Description ?? $"Zentral verwaltetes Asset (v{a.LatestVersion.Value}).",
            }));

        return new ListPromptsResult { Prompts = prompts };
    }

    public static async ValueTask<GetPromptResult> GetPromptAsync(
        RequestContext<GetPromptRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var name = ctx.Params?.Name ?? throw new McpException("prompts/get ohne Namen.");
        var (catalog, authorization, supervisor) = ResolveCatalogServices(ctx.Services!);

        // Zentrale Assets (FR-40) vor den Upstreams prüfen — reservierter Namespace.
        if (AssetDelivery.TryGetAssetName(name) is { } assetPromptName)
        {
            var content = await LoadAssetAsync(ctx.Services!, identity, assetPromptName, ct).ConfigureAwait(false);
            return new GetPromptResult
            {
                Description = $"{content.Name} (v{content.Version.Value})",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = ModelContextProtocol.Protocol.Role.User,
                        Content = new TextContentBlock { Text = content.Content },
                    },
                ],
            };
        }

        var namespaced = new NamespacedToolName(name);
        var entry = catalog.Find(namespaced);
        var allowed = entry is not null
            && entry.Kind is CatalogEntryKind.Prompt
            && authorization.Evaluate(identity, new PermissionScope(entry.Server, entry.Name), ToolAction.UsePrompt).Allowed;
        AuditPassthrough(ctx.Services!, identity, entry?.Server, name,
            allowed ? InvocationStatus.Success : InvocationStatus.Denied);
        if (!allowed || !namespaced.TrySplit(out _, out var promptName))
        {
            throw new McpException($"Prompt '{name}' existiert nicht oder ist nicht sichtbar.");
        }

        var connection = supervisor.GetConnection(entry!.Server)
            ?? throw new McpException($"Upstream für Prompt '{name}' ist nicht verbunden.");
        var args = ctx.Params?.Arguments is { } dict ? JsonSerializer.SerializeToElement(dict) : (JsonElement?)null;
        var payload = await connection.GetPromptAsync(promptName, args, ct).ConfigureAwait(false);
        return Deserialize<GetPromptResult>(payload);
    }

    /// <param name="isPassthrough">
    /// Kam das Ergebnis von einem Upstream? Dann ist es bereits ein serialisiertes
    /// <see cref="CallToolResult"/> und wird nur zurueckgereicht.
    /// <para>
    /// <b>Warum das ein Parameter ist und keine Erkennung am Inhalt:</b> Vorher galt „hat ein Feld
    /// namens <c>content</c>" als Beweis fuer Passthrough. Das ist eine Heuristik an der Stelle
    /// eines Wissens — der Aufrufer hat die Unterscheidung gerade selbst getroffen. Sie fiel um,
    /// als <c>read_skill</c> dazukam: Dessen Nutzinhalt fuehrt legitim ein <c>content</c> (den
    /// Skill-Text als Zeichenkette), das Protokoll erwartet dort aber eine Liste von
    /// ContentBlocks — jeder Aufruf endete in einer JsonException.
    /// </para>
    /// </param>
    /// <summary>
    /// Holt die fehlende Freigabe beim Menschen ein und wiederholt den Aufruf.
    /// <para>
    /// Der Aufruf wird <b>einmal</b> wiederholt, nicht in einer Schleife: Fuehrt die zweite Runde
    /// wieder zu <c>ApprovalRequired</c>, stimmt etwas nicht (abgelaufen, widerrufen, anderer
    /// Fingerabdruck) — und eine Schleife wuerde daraus eine Endlosfrage an den Menschen machen.
    /// </para>
    /// </summary>
    private static async Task<ToolInvocationResult> RetryAfterElicitedApprovalAsync(
        RequestContext<CallToolRequestParams> ctx,
        IdentityId identity,
        string name,
        JsonElement args,
        Guid approvalId,
        ToolInvocationResult original,
        CancellationToken ct)
    {
        var outcome = await ApprovalElicitation.TryObtainAsync(
            ctx.Services!, ctx.Server, approvalId, new NamespacedToolName(name), ct).ConfigureAwait(false);

        if (outcome is ApprovalElicitation.Outcome.NotPossible)
        {
            // Unveraendert zurueck: Der Aufruf steht in der Warteschlange, die Meldung nennt die Id.
            return original;
        }

        if (outcome is ApprovalElicitation.Outcome.Declined)
        {
            return original with
            {
                Status = InvocationStatus.Denied,
                ErrorMessage = $"Die Freigabe fuer '{name}' wurde abgelehnt.",
            };
        }

        var invoker = ctx.Services!.GetRequiredService<IToolInvoker>();
        return await invoker.InvokeAsync(
            new ToolInvocationRequest(identity, CallOrigin.Mcp, new NamespacedToolName(name), args, null),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Schreibt einmal je Session, was der Client kann. Hier und nicht im Session-Aufbau: Dort
    /// laeuft der Initialize-Handshake noch, und <c>ClientCapabilities</c> ist null — der erste
    /// Versuch meldete deshalb fuer jeden Client „kann nichts".
    /// <para>
    /// Der Wert ist nicht nur Neugier: Nur ein Client, der <b>Elicitation</b> anmeldet, kann eine
    /// Freigabe im Moment des Aufrufs beim Menschen einholen, statt sie in die Warteschlange zu
    /// legen.
    /// </para>
    /// </summary>
    private static void LogClientCapabilitiesOnce(IServiceProvider services, McpServer? server)
    {
        if (server is null
            || services.GetService<McpSessionRegistry>() is not { } registry
            || !registry.ShouldLogCapabilities(server))
        {
            return;
        }

        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("McpMcp.Server.McpSession");
        if (!log.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var caps = server.ClientCapabilities;
#pragma warning disable CA1848 // Einmal je Session; der Codegen braechte hier nichts.
        log.LogInformation(
            "MCP-Client: {Client} {Version}. Faehigkeiten — Elicitation: {Elicitation}, "
            + "Sampling: {Sampling}, Roots: {Roots}.",
            server.ClientInfo?.Name ?? "?", server.ClientInfo?.Version ?? "?",
            caps?.Elicitation is not null, caps?.Sampling is not null, caps?.Roots is not null);
#pragma warning restore CA1848
    }

    internal static CallToolResult ToCallToolResult(ToolInvocationResult result, bool isPassthrough = true)
    {
        if (result.Status is not InvocationStatus.Success)
        {
            // DoD WP4: RBAC-Deny und andere Fehler als sauberer Tool-Error, nie als Protokoll-Absturz.
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"[{result.Status}] {result.ErrorMessage}" }],
            };
        }

        var content = result.Content!.Value;
        if (isPassthrough && content.ValueKind is JsonValueKind.Object && content.TryGetProperty("content", out _))
        {
            // Upstream-Passthrough: das Ergebnis IST bereits ein serialisiertes CallToolResult.
            return Deserialize<CallToolResult>(content);
        }

        // Meta-Tool-Payloads (search/describe/list_skills/read_skill) als JSON-Text ausliefern.
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = content.GetRawText() }],
        };
    }

    /// <summary>
    /// Lädt ein Asset für die Auslieferung und auditiert den Zugriff (FR-40). Assets sind bewusst für
    /// jede authentifizierte Identität sichtbar — sie sind zentral gepflegte Instruktionstexte, kein
    /// Zugriff auf fremde Systeme. Eine per-Asset-Berechtigung ist bewusst nicht Teil von FR-40.
    /// </summary>
    private static async Task<AssetContent> LoadAssetAsync(
        IServiceProvider services, IdentityId identity, string assetName, CancellationToken ct)
    {
        var assets = services.GetRequiredService<IAssetStore>();
        var info = (await assets.ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.Ordinal));

        if (info is null)
        {
            AuditPassthrough(services, identity, null, AssetDelivery.PromptName(assetName), InvocationStatus.ToolNotFound);
            throw new McpException($"Asset '{assetName}' existiert nicht.");
        }

        var content = await assets.GetAsync(info.Id, null, ct).ConfigureAwait(false);
        AuditPassthrough(services, identity, null, AssetDelivery.PromptName(assetName), InvocationStatus.Success);
        return content;
    }

    internal static IdentityId RequireIdentity(IServiceProvider services)
    {
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new McpException("Kein HTTP-Kontext für die Session.");
        return http.Items.TryGetValue(ApiKeyAuthMiddleware.IdentityItemKey, out var value) && value is IdentityId id
            ? id
            : throw new McpException("Session ist nicht authentifiziert.");
    }

    private static (IToolCatalog Catalog, IAuthorizationService Authorization, IUpstreamSupervisor Supervisor)
        ResolveCatalogServices(IServiceProvider services)
        => (services.GetRequiredService<IToolCatalog>(),
            services.GetRequiredService<IAuthorizationService>(),
            services.GetRequiredService<IUpstreamSupervisor>());

    private static void AuditPassthrough(
        IServiceProvider services, IdentityId identity, ServerId? server, string name, InvocationStatus status)
        => services.GetRequiredService<IAuditSink>().Record(new AuditEvent(
            services.GetRequiredService<TimeProvider>().GetUtcNow(),
            identity, CallOrigin.Mcp, AuditEventKind.ToolCall, server, name, status, null, null, null, null));

    private static T Deserialize<T>(JsonElement element)
        => JsonSerializer.Deserialize<T>(element, McpJsonUtilities.DefaultOptions)
            ?? throw new McpException("Upstream lieferte ein nicht deserialisierbares Ergebnis.");

    private static JsonElement ToJsonElement(IDictionary<string, JsonElement>? arguments)
        => arguments is null ? default : JsonSerializer.SerializeToElement(arguments);
}
