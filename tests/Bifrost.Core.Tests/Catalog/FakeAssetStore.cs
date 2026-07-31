using Bifrost.Abstractions;

namespace Bifrost.Core.Tests.Catalog;

/// <summary>
/// Skill-Ablage im Speicher — append-only wie die echte, weil die Versionierung Teil des Vertrags
/// ist und ein Fake, der überschreibt, die Tests darüber wertlos machen würde.
/// </summary>
internal sealed class FakeAssetStore : IAssetStore
{
    private readonly List<AssetContent> _versions = [];
    private readonly Dictionary<Guid, string?> _descriptions = [];

    public Task<IReadOnlyList<AssetInfo>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<AssetInfo> latest =
        [
            .. _versions
                .GroupBy(v => v.Id.Value)
                .Select(g => g.OrderByDescending(v => v.Version.Value).First())
                .Select(v => new AssetInfo(
                    v.Id, v.Name, _descriptions.GetValueOrDefault(v.Id.Value), v.Version, v.PublishedAt,
                    v.Metadata, v.Source)),
        ];
        return Task.FromResult(latest);
    }

    public Task<AssetContent> GetAsync(AssetId id, AssetVersion? version, CancellationToken ct)
    {
        var candidates = _versions.Where(v => v.Id == id);
        var match = version is { } v
            ? candidates.SingleOrDefault(c => c.Version == v)
            : candidates.OrderByDescending(c => c.Version.Value).FirstOrDefault();
        return match is null
            ? throw new KeyNotFoundException($"Asset {id} existiert nicht.")
            : Task.FromResult(match);
    }

    public Task<IReadOnlyList<AssetContent>> GetVersionsAsync(AssetId id, CancellationToken ct)
    {
        IReadOnlyList<AssetContent> all =
            [.. _versions.Where(v => v.Id == id).OrderByDescending(v => v.Version.Value)];
        return Task.FromResult(all);
    }

    public Task<AssetVersion> PublishAsync(
        AssetId id, string content, SkillMetadata? metadata, CancellationToken ct,
        string? description = null)
    {
        var previous = _versions.Where(v => v.Id == id).OrderByDescending(v => v.Version.Value).First();
        var version = new AssetVersion(previous.Version.Value + 1);
        if (description is not null)
        {
            _descriptions[id.Value] = description;
        }

        _versions.Add(new AssetContent(
            id, version, previous.Name, content, DateTimeOffset.UnixEpoch, metadata));
        return Task.FromResult(version);
    }

    public Task<IReadOnlyList<AssetInfo>> ListFromPackageAsync(string packageId, CancellationToken ct)
    {
        IReadOnlyList<AssetInfo> matches =
        [
            .. _versions
                .Where(v => IdsFromPackage(packageId).Contains(v.Id))
                .GroupBy(v => v.Id)
                .Select(g => g.OrderByDescending(v => v.Version.Value).First())
                .Select(v => new AssetInfo(
                    v.Id, v.Name, _descriptions.GetValueOrDefault(v.Id.Value), v.Version, v.PublishedAt,
                    v.Metadata, v.Source))
                .OrderBy(a => a.Name, StringComparer.Ordinal),
        ];
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<string>> DeleteFromPackageAsync(string packageId, CancellationToken ct)
    {
        var ids = IdsFromPackage(packageId);
        IReadOnlyList<string> names =
        [
            .. _versions.Where(v => ids.Contains(v.Id))
                .GroupBy(v => v.Id)
                .Select(g => g.OrderByDescending(v => v.Version.Value).First().Name)
                .OrderBy(n => n, StringComparer.Ordinal),
        ];
        _versions.RemoveAll(v => ids.Contains(v.Id));
        return Task.FromResult(names);
    }

    /// <summary>
    /// Über <em>irgendeine</em> Version, nicht nur die neueste — sonst fiele die lokal angepasste
    /// Fassung durch, und genau die ist der interessante Fall.
    /// </summary>
    private HashSet<AssetId> IdsFromPackage(string packageId)
        => [.. _versions.Where(v => v.Source?.PackageId == packageId).Select(v => v.Id)];

    public Task<SkillPublication> PublishFromPackageAsync(
        string name, string? description, string content, SkillMetadata? metadata,
        SkillSource source, CancellationToken ct)
    {
        var existing = _versions.Where(v => v.Name == name)
            .OrderByDescending(v => v.Version.Value)
            .FirstOrDefault();
        var replacedLocalEdit = existing is not null && existing.Source is null;
        var id = existing?.Id ?? AssetId.New();
        var version = new AssetVersion((existing?.Version.Value ?? 0) + 1);
        _descriptions[id.Value] = description ?? _descriptions.GetValueOrDefault(id.Value);
        _versions.Add(new AssetContent(
            id, version, name, content, DateTimeOffset.UnixEpoch, metadata, source));
        return Task.FromResult(new SkillPublication(name, id, version, replacedLocalEdit));
    }

    public Task<AssetId> CreateAsync(
        string name, string? description, string content, SkillMetadata? metadata, CancellationToken ct)
    {
        if (_versions.Any(v => v.Name == name))
        {
            throw new InvalidOperationException($"Es gibt bereits einen Skill namens '{name}'.");
        }

        var id = AssetId.New();
        _descriptions[id.Value] = description;
        _versions.Add(new AssetContent(
            id, new AssetVersion(1), name, content, DateTimeOffset.UnixEpoch, metadata));
        return Task.FromResult(id);
    }
}
