using System.Text.Json;
using McpMcp.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpMcp.Core.Catalog;

/// <summary>
/// Aggregierter Gesamtkatalog (FR-01/03/04): baut aus den Inventaren aller Healthy-Upstreams
/// einen unveränderlichen Snapshot (immutable-swap, DON'T Nr. 7), namespaced per
/// <c>slug__name</c>. Reagiert auf Supervisor- und RBAC-Änderungen und feuert
/// <see cref="Changed"/> — den Auslöser für <c>tools/list_changed</c> (FR-07).
/// </summary>
public sealed partial class ToolCatalog : IToolCatalog, IDisposable
{
    /// <summary>
    /// Konservative Schätzung für die Meta-Tool-Schemas des Lazy-Pfads (ADR-0003).
    /// <para>
    /// Seit dem 2026-07-28 fünf statt drei: <c>list_skills</c> und <c>read_skill</c> sind
    /// dazugekommen. Beide sind absichtlich schmal — <c>list_skills</c> liefert nur Namen und
    /// Kurzbeschreibungen, der Text kommt über <c>read_skill</c> auf Abruf. Dasselbe Muster wie
    /// search/describe, und aus demselben Grund: Entdecken soll billig sein.
    /// </para>
    /// </summary>
    public const int MetaToolTokenEstimate = 950;

    /// <summary>
    /// Die Kontextkosten eines Profils: Summe der gepinnten Schemas plus die Meta-Tools, wenn der
    /// Lazy-Pfad an ist.
    /// <para>
    /// Öffentlich, weil die Oberfläche dieselbe Zahl zeigt. Vorher stand die Formel zweimal da —
    /// einmal hier, einmal im Token-Cockpit; zwei Rechnungen für dieselbe Frage driften
    /// auseinander, sobald eine davon angefasst wird.
    /// </para>
    /// </summary>
    public static int EstimateContextTokens(IEnumerable<CatalogEntry> pinned, bool lazyEnabled)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        return pinned.Sum(e => e.EstimatedSchemaTokens) + (lazyEnabled ? MetaToolTokenEstimate : 0);
    }

    private readonly IUpstreamSupervisor _supervisor;
    private readonly IAuthorizationService _authorization;
    private readonly IRbacDirectory _directory;
    private readonly IToolDescriptionOverrides? _overrides;
    private readonly ILogger<ToolCatalog> _logger;
    private volatile IReadOnlyList<CatalogEntry> _snapshot = [];
    private volatile Dictionary<NamespacedToolName, CatalogEntry> _byName = [];

    public ToolCatalog(
        IUpstreamSupervisor supervisor,
        IAuthorizationService authorization,
        IRbacDirectory directory,
        IToolDescriptionOverrides? overrides = null,
        ILogger<ToolCatalog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(directory);
        _supervisor = supervisor;
        _authorization = authorization;
        _directory = directory;
        _overrides = overrides;
        _logger = logger ?? NullLogger<ToolCatalog>.Instance;

        _supervisor.Changed += OnSupervisorChanged;
        _directory.Changed += OnDirectoryChanged;
        if (_overrides is not null)
        {
            _overrides.Changed += OnOverridesChanged;
        }

        Rebuild();
    }

    public event EventHandler<CatalogChangedEventArgs>? Changed;

    public IReadOnlyList<CatalogEntry> Snapshot => _snapshot;

    public CatalogEntry? Find(NamespacedToolName name) => _byName.GetValueOrDefault(name);

    public ProfileView GetViewFor(IdentityId identity)
    {
        var visible = _authorization.FilterVisible(identity, _snapshot);

        var profile = _directory.GetIdentity(identity)?.Profile is { } profileId
            ? _directory.GetProfile(profileId)
            : null;

        // Ohne Profil: nichts gepinnt, Long Tail lazy — die sparsamste Default-Sicht (ADR-0003).
        var pinnedNames = profile?.PinnedTools ?? [];
        var lazyEnabled = profile?.LazyToolsEnabled ?? true;

        var pinnedSet = new HashSet<NamespacedToolName>(pinnedNames);
        var pinned = visible.Where(e => pinnedSet.Contains(e.Name)).ToList();

        var estimated = EstimateContextTokens(pinned, lazyEnabled);
        return new ProfileView(pinned, lazyEnabled, estimated);
    }

    public IReadOnlyList<ToolSearchHit> Search(IdentityId identity, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visible = _authorization.FilterVisible(identity, _snapshot);

        return
        [
            .. visible
                .Select(entry => new ToolSearchHit(entry.Name, Shorten(entry.Description), Score(entry, terms)))
                .Where(hit => hit.Score > 0)
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.Name.Value, StringComparer.Ordinal)
                .Take(limit),
        ];
    }

    public void Dispose()
    {
        _supervisor.Changed -= OnSupervisorChanged;
        _directory.Changed -= OnDirectoryChanged;
        if (_overrides is not null)
        {
            _overrides.Changed -= OnOverridesChanged;
        }
    }

    internal static int EstimateTokens(string name, string? description, JsonElement schema)
    {
        var schemaLength = schema.ValueKind is JsonValueKind.Undefined ? 2 : schema.GetRawText().Length;
        return Math.Max(1, (name.Length + (description?.Length ?? 0) + schemaLength) / 4);
    }

    private static double Score(CatalogEntry entry, string[] terms)
    {
        var score = 0.0;
        foreach (var term in terms)
        {
            if (entry.Name.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3.0;
            }

            if (entry.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.0;
            }
        }

        return score;
    }

    private static string Shorten(string description)
        => description.Length <= 160 ? description : description[..157] + "…";

    private void OnSupervisorChanged(object? sender, UpstreamChangedEventArgs e)
    {
        if (e.Kind is UpstreamChangeKind.StateChanged)
        {
            return; // reine Statuswechsel ändern den Katalog nicht; Inventarverlust kommt als InventoryChanged
        }

        Rebuild();
        Raise(MapKind(e.Kind), [e.Server]);
    }

    private void OnDirectoryChanged(object? sender, EventArgs e)
        => Raise(CatalogChangeKind.PermissionsChanged, []);

    private void OnOverridesChanged(object? sender, EventArgs e)
    {
        Rebuild();
        Raise(CatalogChangeKind.InventoryChanged, []);
    }

    private static CatalogChangeKind MapKind(UpstreamChangeKind kind) => kind switch
    {
        UpstreamChangeKind.Added => CatalogChangeKind.ServerAdded,
        UpstreamChangeKind.Removed => CatalogChangeKind.ServerRemoved,
        _ => CatalogChangeKind.InventoryChanged,
    };

    private void Rebuild()
    {
        var entries = new List<CatalogEntry>();
        var seen = new HashSet<NamespacedToolName>();

        foreach (var status in _supervisor.Statuses)
        {
            if (_supervisor.GetInventory(status.Id) is not { } inventory)
            {
                continue;
            }

            foreach (var tool in inventory.Tools)
            {
                AddEntry(entries, seen, status, tool.Name, tool.Description ?? string.Empty,
                    tool.InputSchema, CatalogEntryKind.Tool, tool.Risk, tool.RequiresApproval);
            }

            foreach (var resource in inventory.Resources)
            {
                AddEntry(entries, seen, status, resource.Name, resource.Description ?? string.Empty,
                    default, CatalogEntryKind.Resource);
            }

            foreach (var prompt in inventory.Prompts)
            {
                AddEntry(entries, seen, status, prompt.Name, prompt.Description ?? string.Empty,
                    default, CatalogEntryKind.Prompt);
            }
        }

        _snapshot = entries;
        _byName = entries.ToDictionary(e => e.Name);
    }

    private void AddEntry(
        List<CatalogEntry> entries,
        HashSet<NamespacedToolName> seen,
        UpstreamStatus status,
        string name,
        string description,
        JsonElement schema,
        CatalogEntryKind kind,
        CapabilityRisk risk = CapabilityRisk.Read,
        bool requiresApproval = false)
    {
        var namespaced = NamespacedToolName.Create(status.Slug, name);

        // FR-14: serverseitige Beschreibung schlägt die des Upstreams — wirkt damit automatisch
        // auf tools/list, search_tools, describe_tool UND die Token-Schätzung.
        if (_overrides?.GetOverride(namespaced) is { Length: > 0 } overridden)
        {
            description = overridden;
        }

        if (!seen.Add(namespaced))
        {
            // Slug-Eindeutigkeit erzwingt der Supervisor; Duplikate kann nur ein Server liefern,
            // der denselben Namen doppelt meldet — erster gewinnt, Rest wird geloggt (FR-03).
            Log.DuplicateEntry(_logger, namespaced.Value, status.Slug);
            return;
        }

        entries.Add(new CatalogEntry(
            namespaced, status.Id, description, schema, kind,
            EstimateTokens(name, description, schema), risk, requiresApproval));
    }

    private void Raise(CatalogChangeKind kind, IReadOnlyList<ServerId> servers)
    {
        try
        {
            Changed?.Invoke(this, new CatalogChangedEventArgs { Kind = kind, AffectedServers = servers });
        }
        catch (Exception ex)
        {
            Log.ChangedHandlerThrew(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Katalog: doppelter Eintrag {Entry} von Upstream {Slug} — erster gewinnt, Duplikat ignoriert.")]
        public static partial void DuplicateEntry(ILogger logger, string entry, string slug);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Catalog-Changed-Handler warf eine Exception — Handler müssen exception-frei sein.")]
        public static partial void ChangedHandlerThrew(ILogger logger, Exception ex);
    }
}
