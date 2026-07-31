using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistenz-Schicht des RBAC (WP3.1): hydratisiert beim Start ein Runtime-Directory
/// (Write-Through-Muster — die In-Memory-Sicht bleibt die Hot-Path-Quelle für
/// AuthorizationService/Katalog, jede Mutation geht erst in die DB, dann ins Directory).
/// </summary>
public sealed class PersistentRbacStore : IRbacManagement
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly IMutableRbacDirectory _directory;

    public PersistentRbacStore(IDbContextFactory<BifrostDbContext> factory, IMutableRbacDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(directory);
        _factory = factory;
        _directory = directory;
    }

    /// <summary>Lädt alle Rollen, Profile und Identitäten aus der DB ins Runtime-Directory.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        foreach (var row in await db.Roles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
        {
            _directory.UpsertRole(ToRole(row));
        }

        foreach (var row in await db.Profiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
        {
            _directory.UpsertProfile(ToProfile(row));
        }

        foreach (var row in await db.Identities.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
        {
            _directory.UpsertIdentity(ToIdentity(row));
        }
    }

    public async Task<IReadOnlyList<Identity>> ListIdentitiesAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Identities.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToIdentity)];
    }

    public async Task<IReadOnlyList<Role>> ListRolesAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToRole)];
    }

    public async Task<IReadOnlyList<ToolProfile>> ListProfilesAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Profiles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToProfile)];
    }

    public async Task UpsertIdentityAsync(Identity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Identities.FindAsync([identity.Id.Value], ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new IdentityRow { Id = identity.Id.Value };
            db.Identities.Add(row);
        }

        row.Name = identity.Name;
        row.Kind = (int)identity.Kind;
        row.ProfileId = identity.Profile?.Value;
        row.RolesJson = RbacJson.SerializeRoleIds(identity.Roles);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _directory.UpsertIdentity(identity);
    }

    public async Task RemoveIdentityAsync(IdentityId id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Identities.Where(r => r.Id == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _directory.RemoveIdentity(id);
    }

    public async Task UpsertRoleAsync(Role role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(role);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Roles.FindAsync([role.Id.Value], ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new RoleRow { Id = role.Id.Value };
            db.Roles.Add(row);
        }

        row.Name = role.Name;
        row.RateLimitPerMinute = role.RateLimit?.CallsPerMinute;
        row.GrantsJson = RbacJson.SerializeGrants(role.Grants);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _directory.UpsertRole(role);
    }

    public async Task RemoveRoleAsync(RoleId id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Roles.Where(r => r.Id == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _directory.RemoveRole(id);
    }

    public async Task UpsertProfileAsync(ToolProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Profiles.FindAsync([profile.Id.Value], ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new ProfileRow { Id = profile.Id.Value };
            db.Profiles.Add(row);
        }

        row.Name = profile.Name;
        row.LazyToolsEnabled = profile.LazyToolsEnabled;
        row.PinnedToolsJson = RbacJson.SerializePinnedTools(profile.PinnedTools);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _directory.UpsertProfile(profile);
    }

    public async Task RemoveProfileAsync(ProfileId id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Profiles.Where(r => r.Id == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _directory.RemoveProfile(id);
    }

    private static Role ToRole(RoleRow row) => new(
        new RoleId(row.Id),
        row.Name,
        RbacJson.DeserializeGrants(row.GrantsJson),
        row.RateLimitPerMinute is { } limit ? new RateLimit(limit) : null);

    private static ToolProfile ToProfile(ProfileRow row) => new(
        new ProfileId(row.Id),
        row.Name,
        RbacJson.DeserializePinnedTools(row.PinnedToolsJson),
        row.LazyToolsEnabled);

    private static Identity ToIdentity(IdentityRow row) => new(
        new IdentityId(row.Id),
        row.Name,
        (IdentityKind)row.Kind,
        RbacJson.DeserializeRoleIds(row.RolesJson),
        row.ProfileId is { } p ? new ProfileId(p) : null);
}
