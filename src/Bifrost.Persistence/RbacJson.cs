using System.Text.Json;
using Bifrost.Abstractions;

namespace Bifrost.Persistence;

/// <summary>JSON-Mapping der RBAC-Listen (Grants, Rollen-Referenzen, Pinned Tools) auf provider-neutrale Text-Spalten.</summary>
internal static class RbacJson
{
    private sealed record GrantDto(Guid? Server, string? Tool, int[] Actions);

    public static string SerializeGrants(IReadOnlyList<Grant> grants)
        => JsonSerializer.Serialize(grants.Select(g => new GrantDto(
            g.Scope.Server?.Value,
            g.Scope.Tool?.Value,
            [.. g.Actions.Select(a => (int)a)])));

    public static IReadOnlyList<Grant> DeserializeGrants(string json)
        => [.. (JsonSerializer.Deserialize<List<GrantDto>>(json) ?? []).Select(d => new Grant(
            new PermissionScope(
                d.Server is { } s ? new ServerId(s) : null,
                d.Tool is { } t ? new NamespacedToolName(t) : null),
            [.. d.Actions.Select(a => (ToolAction)a)]))];

    public static string SerializeRoleIds(IReadOnlyList<RoleId> roles)
        => JsonSerializer.Serialize(roles.Select(r => r.Value));

    public static IReadOnlyList<RoleId> DeserializeRoleIds(string json)
        => [.. (JsonSerializer.Deserialize<List<Guid>>(json) ?? []).Select(g => new RoleId(g))];

    public static string SerializePinnedTools(IReadOnlyList<NamespacedToolName> tools)
        => JsonSerializer.Serialize(tools.Select(t => t.Value));

    public static IReadOnlyList<NamespacedToolName> DeserializePinnedTools(string json)
        => [.. (JsonSerializer.Deserialize<List<string>>(json) ?? []).Select(s => new NamespacedToolName(s))];
}
