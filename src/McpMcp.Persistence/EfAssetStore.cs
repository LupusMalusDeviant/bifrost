using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// Versionierte Text-Assets (FR-40, WP6.4). Die Auslieferung als MCP-Prompt/Resource erfolgt in
/// <c>GatewayMcpHandlers</c> unter dem reservierten Namespace <c>assets</c>.
/// Bewusste Grenze: keine per-Asset-RBAC — Assets sind zentrale Instruktionstexte und für jede
/// authentifizierte Identität sichtbar; sie eröffnen keinen Zugriff auf Fremdsysteme.
/// </summary>
public sealed class EfAssetStore : IAssetStore
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly TimeProvider _time;

    public EfAssetStore(IDbContextFactory<McpMcpDbContext> factory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<AssetInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var latest = await db.Assets.AsNoTracking()
            .GroupBy(a => a.Id)
            .Select(g => g.OrderByDescending(a => a.Version).First())
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. latest.Select(r => new AssetInfo(
            new AssetId(r.Id), r.Name, r.Description, new AssetVersion(r.Version), r.PublishedAt))];
    }

    public async Task<AssetContent> GetAsync(AssetId id, AssetVersion? version, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Assets.AsNoTracking().Where(a => a.Id == id.Value);
        var row = version is { } v
            ? await query.SingleOrDefaultAsync(a => a.Version == v.Value, ct).ConfigureAwait(false)
            : await query.OrderByDescending(a => a.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return row is null
            ? throw new KeyNotFoundException($"Asset {id} (Version {version?.Value.ToString() ?? "latest"}) existiert nicht.")
            : new AssetContent(new AssetId(row.Id), new AssetVersion(row.Version), row.Name, row.Content, row.PublishedAt);
    }

    public async Task<AssetVersion> PublishAsync(AssetId id, string content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Assets.AsNoTracking()
            .Where(a => a.Id == id.Value)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var row = new AssetRow
        {
            Id = id.Value,
            Version = (existing?.Version ?? 0) + 1,
            Name = existing?.Name ?? id.ToString(),
            Description = existing?.Description,
            Content = content,
            PublishedAt = _time.GetUtcNow(),
        };
        db.Assets.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new AssetVersion(row.Version);
    }

    public async Task<AssetId> CreateAsync(string name, string? description, string content, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = AssetId.New();
        db.Assets.Add(new AssetRow
        {
            Id = id.Value,
            Version = 1,
            Name = name,
            Description = description,
            Content = content,
            PublishedAt = _time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return id;
    }
}
