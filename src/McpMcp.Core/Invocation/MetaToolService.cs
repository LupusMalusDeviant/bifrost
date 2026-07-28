using System.Diagnostics;
using System.Text.Json;
using McpMcp.Abstractions;

namespace McpMcp.Core.Invocation;

/// <summary>Definition eines eingebauten Meta-Tools für tools/list (Name, Beschreibung, Schema).</summary>
public sealed record MetaToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// Die drei Meta-Tools des Lazy-Pfads (ADR-0003, FR-12): search_tools, describe_tool, invoke_tool.
/// Sichtbarkeit ist RBAC-konsistent mit tools/list (dieselbe FilterVisible-Quelle);
/// invoke_tool läuft durch den regulären <see cref="IToolInvoker"/> — Ziel-RBAC und Audit
/// greifen dort. search/describe werden hier selbst auditiert.
/// </summary>
public sealed class MetaToolService
{
    public const string SearchToolsName = "search_tools";
    public const string DescribeToolName = "describe_tool";
    public const string InvokeToolName = "invoke_tool";
    public const string ListSkillsName = "list_skills";
    public const string ReadSkillName = "read_skill";

    private const int DefaultSearchLimit = 10;
    private const int MaxSearchLimit = 50;

    public static IReadOnlyList<MetaToolDefinition> Definitions { get; } =
    [
        new(SearchToolsName,
            "Search the gateway's tool catalog by capability keywords. Returns compact matches without schemas; use describe_tool for the full input schema.",
            ParseSchema("""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Keywords describing the capability you need."},
                  "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Maximum number of results (default 10)."}},
                 "required":["query"]}
                """)),
        new(DescribeToolName,
            "Get the full description and JSON input schema of one tool found via search_tools.",
            ParseSchema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Namespaced tool name, e.g. github__create_issue."}},
                 "required":["name"]}
                """)),
        new(InvokeToolName,
            "Invoke any permitted tool by its namespaced name with a JSON arguments object.",
            ParseSchema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Namespaced tool name, e.g. github__create_issue."},
                  "arguments":{"type":"object","description":"Arguments matching the tool's input schema."}},
                 "required":["name"]}
                """)),

        // Skills als TOOLS, nicht nur als MCP-Prompts. Ein Prompt ist in den meisten Clients
        // nutzerinitiiert — der Mensch sieht die Liste, das Modell nicht. Ein Tool ruft das Modell
        // selbst auf; erst damit kann ein Agent von sich aus nachsehen, ob es für seine Aufgabe
        // eine hinterlegte Anleitung gibt. Die Prompt-/Resource-Auslieferung bleibt daneben
        // bestehen, sie bedient den Menschen.
        new(ListSkillsName,
            "List the skills (instructions, playbooks, conventions) published on this gateway. "
            + "Returns names and one-line descriptions only — call read_skill for the full text. "
            + "Worth checking once when a task looks like it might have an established procedure here.",
            ParseSchema("""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Optional keywords to filter by name or description."}}}
                """)),
        new(ReadSkillName,
            "Read the full text of one skill listed by list_skills. The result also names the "
            + "skills it references — follow those with read_skill when they look relevant.",
            ParseSchema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Skill name as returned by list_skills."}},
                 "required":["name"]}
                """)),
    ];

    private readonly IToolCatalog _catalog;
    private readonly IAuthorizationService _authorization;
    private readonly IToolInvoker _invoker;
    private readonly IAuditSink _audit;
    private readonly IRedactionService _redaction;
    private readonly TimeProvider _time;
    private readonly IAssetStore? _assets;

    public MetaToolService(
        IToolCatalog catalog,
        IAuthorizationService authorization,
        IToolInvoker invoker,
        IAuditSink audit,
        IRedactionService redaction,
        TimeProvider? timeProvider = null,
        IAssetStore? assets = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(redaction);
        _assets = assets;
        _catalog = catalog;
        _authorization = authorization;
        _invoker = invoker;
        _audit = audit;
        _redaction = redaction;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Einmal aus <see cref="Definitions"/> gebaut. Aus der Definitionsliste abgeleitet und nicht
    /// als zweite Namensliste gepflegt — sonst erschiene ein neues Meta-Tool im Katalog, wäre aber
    /// nicht aufrufbar, weil der Aufruf im normalen Invoker landete, der es nicht kennt.
    /// <para>
    /// Als Menge statt als Suche über die Liste: Die Prüfung läuft bei <b>jedem</b> Tool-Aufruf,
    /// und dort gehört keine Allokation hin.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> MetaToolNames =
        [.. Definitions.Select(d => d.Name)];

    public static bool IsMetaTool(string name) => MetaToolNames.Contains(name);

    public async Task<ToolInvocationResult> ExecuteAsync(
        IdentityId caller, CallOrigin origin, string metaTool, JsonElement args, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        switch (metaTool)
        {
            case SearchToolsName:
            {
                var result = SearchTools(caller, args, started);
                Audit(caller, origin, SearchToolsName, args, result);
                return result;
            }

            case DescribeToolName:
            {
                var result = DescribeTool(caller, args, started);
                Audit(caller, origin, DescribeToolName, args, result);
                return result;
            }

            case InvokeToolName:
            {
                // Ziel-RBAC + Audit übernimmt die Invoker-Pipeline — kein Doppel-Audit hier.
                return await InvokeToolAsync(caller, origin, args, started, ct).ConfigureAwait(false);
            }

            case ListSkillsName:
            {
                var result = await ListSkillsAsync(args, started, ct).ConfigureAwait(false);
                Audit(caller, origin, ListSkillsName, args, result);
                return result;
            }

            case ReadSkillName:
            {
                var result = await ReadSkillAsync(args, started, ct).ConfigureAwait(false);
                Audit(caller, origin, ReadSkillName, args, result);
                return result;
            }

            default:
                throw new ArgumentException($"'{metaTool}' ist kein Meta-Tool.", nameof(metaTool));
        }
    }

    private ToolInvocationResult SearchTools(IdentityId caller, JsonElement args, long started)
    {
        if (!TryGetString(args, "query", out var query))
        {
            return Fail(InvocationStatus.ValidationFailed, "search_tools erwartet ein 'query'-Argument (string).", started);
        }

        var limit = DefaultSearchLimit;
        if (args.ValueKind is JsonValueKind.Object
            && args.TryGetProperty("limit", out var limitProp)
            && limitProp.ValueKind is JsonValueKind.Number)
        {
            limit = Math.Clamp(limitProp.GetInt32(), 1, MaxSearchLimit);
        }

        var hits = _catalog.Search(caller, query, limit);
        var payload = JsonSerializer.SerializeToElement(new
        {
            tools = hits.Select(h => new { name = h.Name.Value, description = h.ShortDescription, score = h.Score }),
            hint = hits.Count > 0
                ? "Use describe_tool for the input schema, then invoke_tool to call it."
                : "No matching tools. Try broader keywords.",
        });
        return new ToolInvocationResult(InvocationStatus.Success, payload, null, Elapsed(started));
    }

    private ToolInvocationResult DescribeTool(IdentityId caller, JsonElement args, long started)
    {
        if (!TryGetString(args, "name", out var name))
        {
            return Fail(InvocationStatus.ValidationFailed, "describe_tool erwartet ein 'name'-Argument (string).", started);
        }

        var entry = _catalog.Find(new NamespacedToolName(name));
        if (entry is null || !_authorization
                .Evaluate(caller, new PermissionScope(entry.Server, entry.Name), ActionFor(entry.Kind)).Allowed)
        {
            // Sichtbarkeit folgt Berechtigung (FR-29): nicht erlaubte Tools sind auch hier unsichtbar.
            return Fail(InvocationStatus.ToolNotFound, $"Tool '{name}' existiert nicht oder ist nicht sichtbar.", started);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            name = entry.Name.Value,
            description = entry.Description,
            inputSchema = entry.InputSchema,
            estimatedSchemaTokens = entry.EstimatedSchemaTokens,
        });
        return new ToolInvocationResult(InvocationStatus.Success, payload, null, Elapsed(started));
    }

    private async Task<ToolInvocationResult> InvokeToolAsync(
        IdentityId caller, CallOrigin origin, JsonElement args, long started, CancellationToken ct)
    {
        if (!TryGetString(args, "name", out var name))
        {
            var fail = Fail(InvocationStatus.ValidationFailed, "invoke_tool erwartet ein 'name'-Argument (string).", started);
            Audit(caller, origin, InvokeToolName, args, fail);
            return fail;
        }

        var arguments = args.ValueKind is JsonValueKind.Object && args.TryGetProperty("arguments", out var inner)
            ? inner
            : default;

        return await _invoker.InvokeAsync(
            new ToolInvocationRequest(caller, origin, new NamespacedToolName(name), arguments, null), ct)
            .ConfigureAwait(false);
    }

    private void Audit(IdentityId caller, CallOrigin origin, string metaTool, JsonElement args, ToolInvocationResult result)
    {
        // Auch der Meta-Pfad muss maskieren: invoke_tool trägt die kompletten Ziel-Argumente in
        // args.arguments — ungefiltert wären das Secrets im Klartext (DON'T Nr. 2, NFR-04).
        var hasArgs = args.ValueKind is not JsonValueKind.Undefined;
        var redacted = hasArgs
            ? _redaction.RedactArguments(new NamespacedToolName(metaTool), args)
            : default;

        _audit.Record(new AuditEvent(
            _time.GetUtcNow(), caller, origin, AuditEventKind.ToolCall, null, metaTool, result.Status,
            hasArgs ? redacted : null,
            hasArgs ? args.GetRawText().Length : 0,
            result.Content?.GetRawText().Length,
            result.Duration,
            CallerRoles: _authorization.DescribeCaller(caller)));
    }

    /// <summary>
    /// Namen und Kurzbeschreibungen der Skills — <b>ohne Inhalt</b>. Dasselbe Muster wie
    /// search_tools: Entdecken ist billig, der Text kommt auf Abruf. Eine Liste, die den ganzen
    /// Text mitliefert, wäre bei einem Dutzend Skills teurer als der gesamte gepinnte Katalog.
    /// </summary>
    private async Task<ToolInvocationResult> ListSkillsAsync(
        JsonElement args, long started, CancellationToken ct)
    {
        if (_assets is null)
        {
            return Fail(
                InvocationStatus.ToolNotFound,
                "In dieser Zusammenstellung ist keine Skill-Auslieferung eingebunden.",
                started);
        }

        var all = await _assets.ListAsync(ct).ConfigureAwait(false);

        // Kein RBAC-Filter: Skills sind für jede authentifizierte Identität sichtbar (FR-40). Das
        // ist entschieden und getestet — deshalb steht hier auch kein `caller`.
        if (TryGetString(args, "query", out var query))
        {
            all = [.. all.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (a.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))];
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            skills = all.Select(a => new
            {
                name = a.Name,
                description = a.Description,
                // Die Angabe, die ueber den Zugriff entscheidet, gehoert in die LISTE — nicht erst
                // in den Text, den man dafuer schon geladen haben muesste.
                whenToUse = a.MetadataOrEmpty.WhenToUse,
                version = a.LatestVersion.Value,
            }),
        });
        return new ToolInvocationResult(InvocationStatus.Success, payload, null, Elapsed(started));
    }

    /// <summary>Der Text eines Skills. Erst hier kostet er Kontext.</summary>
    private async Task<ToolInvocationResult> ReadSkillAsync(
        JsonElement args, long started, CancellationToken ct)
    {
        if (_assets is null)
        {
            return Fail(
                InvocationStatus.ToolNotFound,
                "In dieser Zusammenstellung ist keine Skill-Auslieferung eingebunden.",
                started);
        }

        if (!TryGetString(args, "name", out var name))
        {
            return Fail(InvocationStatus.ValidationFailed, "read_skill erwartet ein 'name'-Argument (string).", started);
        }

        var match = (await _assets.ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        if (match is null)
        {
            return Fail(InvocationStatus.ToolNotFound, $"Skill '{name}' existiert nicht.", started);
        }

        var content = await _assets.GetAsync(match.Id, null, ct).ConfigureAwait(false);
        var metadata = content.MetadataOrEmpty;
        var payload = JsonSerializer.SerializeToElement(new
        {
            name = content.Name,
            version = content.Version.Value,
            whenToUse = metadata.WhenToUse,
            // Als eigene Felder, nicht in den Text montiert: Der Agent soll dem Verweis folgen
            // koennen, ohne ihn aus Prosa zu raten — und der ausgelieferte Text bleibt genau der,
            // den jemand geschrieben hat.
            references = metadata.ReferencesOrEmpty,
            requiredTools = metadata.RequiredToolsOrEmpty,
            content = content.Content,
        });
        return new ToolInvocationResult(InvocationStatus.Success, payload, null, Elapsed(started));
    }

    private static ToolAction ActionFor(CatalogEntryKind kind) => kind switch
    {
        CatalogEntryKind.Resource => ToolAction.ReadResource,
        CatalogEntryKind.Prompt => ToolAction.UsePrompt,
        _ => ToolAction.UseTool,
    };

    private static bool TryGetString(JsonElement args, string property, out string value)
    {
        value = string.Empty;
        if (args.ValueKind is JsonValueKind.Object
            && args.TryGetProperty(property, out var prop)
            && prop.ValueKind is JsonValueKind.String
            && prop.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }

        return false;
    }

    private static ToolInvocationResult Fail(InvocationStatus status, string message, long started)
        => new(status, null, message, Elapsed(started));

    private static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
