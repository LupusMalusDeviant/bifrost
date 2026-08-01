using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Abstractions;

namespace Bifrost.Server;

/// <summary>
/// Generiert pro Identität eine OpenAPI-3.1-Beschreibung der REST-Fassade (FR-18) —
/// ausschließlich über deren RBAC-Sicht. Cache pro Identität, invalidiert über
/// Katalog-Änderungen (inkl. PermissionsChanged), da die Sicht key-abhängig ist (ADR-0008).
/// </summary>
public sealed class OpenApiDocumentGenerator : IDisposable
{
    private readonly IToolCatalog _catalog;
    private readonly IAuthorizationService _authorization;
    private readonly ConcurrentDictionary<IdentityId, CachedDocument> _cache = new();
    private readonly EventHandler<CatalogChangedEventArgs> _onChanged;

    public OpenApiDocumentGenerator(IToolCatalog catalog, IAuthorizationService authorization)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(authorization);
        _catalog = catalog;
        _authorization = authorization;
        _onChanged = (_, _) => _cache.Clear();
        _catalog.Changed += _onChanged;
    }

    public string GetJsonFor(IdentityId identity)
    {
        var snapshot = _catalog.Snapshot;
        if (_cache.TryGetValue(identity, out var cached) && ReferenceEquals(cached.SnapshotReference, snapshot))
        {
            return cached.Json;
        }

        var json = Build(identity, snapshot);
        _cache[identity] = new CachedDocument(snapshot, json);
        return json;
    }

    public void Dispose() => _catalog.Changed -= _onChanged;

    private string Build(IdentityId identity, IReadOnlyList<CatalogEntry> snapshot)
    {
        var visibleTools = _authorization.FilterVisible(identity, snapshot)
            .Where(e => e.Kind is CatalogEntryKind.Tool)
            .OrderBy(e => e.Name.Value, StringComparer.Ordinal);

        var paths = new JsonObject();
        foreach (var entry in visibleTools)
        {
            paths[$"/api/v1/tools/{entry.Name.Value}/invoke"] = new JsonObject
            {
                ["post"] = new JsonObject
                {
                    ["operationId"] = entry.Name.Value,
                    ["summary"] = entry.Description,
                    ["requestBody"] = new JsonObject
                    {
                        ["required"] = true,
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject
                            {
                                ["schema"] = entry.InputSchema.ValueKind is JsonValueKind.Object
                                    ? JsonNode.Parse(entry.InputSchema.GetRawText())
                                    : new JsonObject { ["type"] = "object" },
                            },
                        },
                    },
                    ["responses"] = new JsonObject
                    {
                        ["200"] = new JsonObject { ["description"] = "Tool-Ergebnis" },
                        ["400"] = new JsonObject { ["description"] = "Argumente verletzen das Tool-Schema" },
                        ["403"] = new JsonObject { ["description"] = "Verweigert (RBAC oder Rate-Limit)" },
                        ["404"] = new JsonObject { ["description"] = "Tool existiert nicht" },
                        ["502"] = new JsonObject { ["description"] = "Upstream-Fehler" },
                        ["504"] = new JsonObject { ["description"] = "Upstream-Timeout" },
                    },
                    ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
                },
            };
        }

        // WP4.3: Die Importendpunkte gehoeren ins Dokument — aber nur fuer einen Schluessel, der
        // sie auch bedienen darf. Was drinsteht, entscheidet ImportOpenApi; hier steht nur, WANN.
        if (_authorization.Evaluate(identity, new PermissionScope(null, null), ToolAction.UseTool).Allowed)
        {
            Importing.ImportOpenApi.AddPaths(paths);
        }

        var document = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "B.I.F.R.O.S.T REST-Fassade",
                ["description"] = "RBAC-gefilterte Sicht dieses API-Keys auf die aggregierten Tools (FR-17/18).",
                ["version"] = "1.0.0",
            },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" },
                },
            },
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record CachedDocument(object SnapshotReference, string Json);
}
