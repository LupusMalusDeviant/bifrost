using System.Collections.Concurrent;
using Bifrost.Abstractions;

namespace Bifrost.Core.Rbac;

/// <summary>
/// Einzige Autorisierungsquelle (ADR-0006): Default-Deny, Sichtbarkeit folgt Berechtigung (FR-29).
/// Pro Identität wird ein Berechtigungs-Snapshot kompiliert und gecacht; der Cache invalidiert
/// über <see cref="IRbacDirectory.Version"/>. Evaluate/FilterVisible sind danach reine O(1)-Lookups.
/// </summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private readonly IRbacDirectory _directory;
    private readonly ConcurrentDictionary<IdentityId, CachedSnapshot> _cache = new();

    public AuthorizationService(IRbacDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public AuthorizationDecision Evaluate(IdentityId identity, PermissionScope scope, ToolAction action)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var snapshot = GetSnapshot(identity);
        if (snapshot is null)
        {
            return AuthorizationDecision.Deny($"Identität {identity} ist nicht registriert.");
        }

        return snapshot.Covers(scope, action)
            ? AuthorizationDecision.Allow
            : AuthorizationDecision.Deny(
                $"Kein Grant für {action} auf {(scope.Tool?.ToString() ?? scope.Server?.ToString() ?? "global")} (Default-Deny).");
    }

    public IReadOnlyList<CatalogEntry> FilterVisible(IdentityId identity, IReadOnlyList<CatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var snapshot = GetSnapshot(identity);
        if (snapshot is null)
        {
            return [];
        }

        var visible = new List<CatalogEntry>(catalog.Count);
        foreach (var entry in catalog)
        {
            var action = entry.Kind switch
            {
                CatalogEntryKind.Resource => ToolAction.ReadResource,
                CatalogEntryKind.Prompt => ToolAction.UsePrompt,
                _ => ToolAction.UseTool,
            };
            if (snapshot.Covers(new PermissionScope(entry.Server, entry.Name), action))
            {
                visible.Add(entry);
            }
        }

        return visible;
    }

    public string? DescribeCaller(IdentityId identity) => GetCached(identity)?.CallerDescription;

    private CompiledPermissions? GetSnapshot(IdentityId identity) => GetCached(identity)?.Permissions;

    private CachedSnapshot? GetCached(IdentityId identity)
    {
        var version = _directory.Version;
        if (_cache.TryGetValue(identity, out var cached) && cached.Version == version)
        {
            return cached;
        }

        var compiled = Compile(identity);
        if (compiled is null)
        {
            _cache.TryRemove(identity, out _);
            return null;
        }

        // Mit der VOR dem Kompilieren gelesenen Version stempeln: ändert sich das Directory währenddessen,
        // gilt der Eintrag als veraltet und wird neu gebaut — nie umgekehrt.
        var stamped = compiled with { Version = version };
        return _cache[identity] = stamped;
    }

    private CachedSnapshot? Compile(IdentityId identityId)
    {
        var identity = _directory.GetIdentity(identityId);
        if (identity is null)
        {
            return null;
        }

        var roleNames = new List<string>();
        var global = new HashSet<ToolAction>();
        var perServer = new Dictionary<ServerId, HashSet<ToolAction>>();
        var perTool = new Dictionary<NamespacedToolName, HashSet<ToolAction>>();

        foreach (var roleId in identity.Roles)
        {
            var role = _directory.GetRole(roleId);
            if (role is null)
            {
                continue; // verwaiste Rollen-Referenz gewährt nichts (Default-Deny)
            }

            roleNames.Add(role.Name);
            foreach (var grant in role.Grants)
            {
                var target = grant.Scope switch
                {
                    { Tool: { } tool } => perTool.TryGetValue(tool, out var set)
                        ? set
                        : perTool[tool] = [],
                    { Server: { } server } => perServer.TryGetValue(server, out var set)
                        ? set
                        : perServer[server] = [],
                    _ => global,
                };
                foreach (var action in grant.Actions)
                {
                    target.Add(action);
                }
            }
        }

        // Rollenlose Identitäten kommen vor (registriert, aber ohne Grant) — dann trägt das Audit
        // wenigstens den Namen, statt ein leeres Feld zu zeigen.
        var roles = roleNames.Count > 0 ? string.Join(", ", roleNames) : "ohne Rolle";

        // FR-21 verlangt „Profil/Rolle". Das zugeordnete Tool-Profil bestimmt, welche Tools der
        // Aufrufer überhaupt sieht — beim Nachvollziehen eines Calls ist das oft die wichtigere
        // Angabe als die Rolle. Fehlt es, sagt das Audit das ausdrücklich statt es zu verschweigen.
        var profileName = identity.Profile is { } profileId
            ? _directory.GetProfile(profileId)?.Name ?? "unbekanntes Profil"
            : "kein Profil";

        var description = $"{identity.Name} [{roles}] · Profil: {profileName}";

        return new CachedSnapshot(
            Version: 0, new CompiledPermissions(global, perServer, perTool), description);
    }

    private sealed record CachedSnapshot(long Version, CompiledPermissions Permissions, string CallerDescription);

    private sealed class CompiledPermissions
    {
        private readonly HashSet<ToolAction> _global;
        private readonly Dictionary<ServerId, HashSet<ToolAction>> _perServer;
        private readonly Dictionary<NamespacedToolName, HashSet<ToolAction>> _perTool;

        public CompiledPermissions(
            HashSet<ToolAction> global,
            Dictionary<ServerId, HashSet<ToolAction>> perServer,
            Dictionary<NamespacedToolName, HashSet<ToolAction>> perTool)
        {
            _global = global;
            _perServer = perServer;
            _perTool = perTool;
        }

        public bool Covers(PermissionScope scope, ToolAction action)
        {
            if (_global.Contains(action))
            {
                return true;
            }

            if (scope.Server is { } server
                && _perServer.TryGetValue(server, out var serverActions)
                && serverActions.Contains(action))
            {
                return true; // Server-Grant vererbt auf alle Tools darunter (FR-28)
            }

            return scope.Tool is { } tool
                && _perTool.TryGetValue(tool, out var toolActions)
                && toolActions.Contains(action);
        }
    }
}
