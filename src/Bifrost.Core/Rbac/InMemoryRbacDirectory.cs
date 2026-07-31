using System.Collections.Concurrent;
using Bifrost.Abstractions;

namespace Bifrost.Core.Rbac;

/// <summary>
/// WP2-Implementierung des RBAC-Verzeichnisses; WP3 ersetzt sie durch die persistente Variante.
/// Jede Mutation erhöht <see cref="Version"/> und feuert <see cref="Changed"/> —
/// AuthZ-Snapshot-Cache und Katalog invalidieren darüber (FR-07: PermissionsChanged).
/// </summary>
public sealed class InMemoryRbacDirectory : IMutableRbacDirectory
{
    private readonly ConcurrentDictionary<IdentityId, Identity> _identities = new();
    private readonly ConcurrentDictionary<RoleId, Role> _roles = new();
    private readonly ConcurrentDictionary<ProfileId, ToolProfile> _profiles = new();
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public event EventHandler? Changed;

    public Identity? GetIdentity(IdentityId id) => _identities.GetValueOrDefault(id);

    public Role? GetRole(RoleId id) => _roles.GetValueOrDefault(id);

    public ToolProfile? GetProfile(ProfileId id) => _profiles.GetValueOrDefault(id);

    public void UpsertIdentity(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identities[identity.Id] = identity;
        Bump();
    }

    public void RemoveIdentity(IdentityId id)
    {
        if (_identities.TryRemove(id, out _))
        {
            Bump();
        }
    }

    public void UpsertRole(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        _roles[role.Id] = role;
        Bump();
    }

    public void RemoveRole(RoleId id)
    {
        if (_roles.TryRemove(id, out _))
        {
            Bump();
        }
    }

    public void UpsertProfile(ToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles[profile.Id] = profile;
        Bump();
    }

    public void RemoveProfile(ProfileId id)
    {
        if (_profiles.TryRemove(id, out _))
        {
            Bump();
        }
    }

    private void Bump()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
