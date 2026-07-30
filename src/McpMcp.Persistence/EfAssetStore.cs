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
            new AssetId(r.Id), r.Name, r.Description, new AssetVersion(r.Version), r.PublishedAt,
            ToMetadata(r), ToSource(r)))];
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
            : ToContent(row);
    }

    /// <summary>Alle Versionen, neueste zuerst — Grundlage für Historie und Zurückschalten.</summary>
    public async Task<IReadOnlyList<AssetContent>> GetVersionsAsync(AssetId id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Assets.AsNoTracking()
            .Where(a => a.Id == id.Value)
            .OrderByDescending(a => a.Version)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToContent)];
    }

    public async Task<AssetVersion> PublishAsync(
        AssetId id, string content, SkillMetadata? metadata, CancellationToken ct,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        SkillLimits.EnsureWithinLimit(content);
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
            Description = description ?? existing?.Description,
            Content = content,
            PublishedAt = _time.GetUtcNow(),

            // Herkunft wird BEWUSST nicht übernommen: Wer hier veröffentlicht, hat den Text von
            // Hand geschrieben. Genau daran erkennt ein späteres Paket-Update, dass es eine
            // angepasste Fassung ablösen würde.
            SourcePackageId = null,
            SourcePackageVersion = null,
        };
        ApplyMetadata(row, metadata);
        db.Assets.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new AssetVersion(row.Version);
    }

    public async Task<AssetId> CreateAsync(
        string name, string? description, string content, SkillMetadata? metadata, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SkillLimits.EnsureWithinLimit(content);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureNameFreeAsync(db, name, ct).ConfigureAwait(false);
        var id = AssetId.New();
        var row = new AssetRow
        {
            Id = id.Value,
            Version = 1,
            Name = name,
            Description = description,
            Content = content,
            PublishedAt = _time.GetUtcNow(),
        };
        ApplyMetadata(row, metadata);
        db.Assets.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Anlegen oder anhängen — je nachdem, ob es den Namen schon gibt. Die Entscheidung liegt hier,
    /// weil sie eine Namenssuche braucht.
    /// </summary>
    public async Task<SkillPublication> PublishFromPackageAsync(
        string name,
        string? description,
        string content,
        SkillMetadata? metadata,
        SkillSource source,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        SkillLimits.EnsureWithinLimit(content);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Assets.AsNoTracking()
            .Where(a => a.Name == name)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // Die bisherige neueste Fassung kam nicht aus einem Paket: Jemand hat den Text angepasst.
        // Das Update wird trotzdem angehängt — die Historie behält beide, und Zurückschalten
        // existiert. Gemeldet wird es aber, denn still verdrängt wäre es ein Vertrauensbruch.
        var replacedLocalEdit = existing is not null && existing.SourcePackageId is null;

        var row = new AssetRow
        {
            Id = existing?.Id ?? AssetId.New().Value,
            Version = (existing?.Version ?? 0) + 1,
            Name = name,
            Description = description ?? existing?.Description,
            Content = content,
            PublishedAt = _time.GetUtcNow(),
            SourcePackageId = source.PackageId,
            SourcePackageVersion = source.PackageVersion,
        };
        ApplyMetadata(row, metadata);
        db.Assets.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new SkillPublication(
            name, new AssetId(row.Id), new AssetVersion(row.Version), replacedLocalEdit);
    }

    public async Task<IReadOnlyList<AssetInfo>> ListFromPackageAsync(
        string packageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await IdsFromPackageAsync(db, packageId, ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Assets.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows
            .GroupBy(a => a.Id)
            .Select(g => g.OrderByDescending(a => a.Version).First())
            .Select(r => new AssetInfo(
                new AssetId(r.Id), r.Name, r.Description, new AssetVersion(r.Version), r.PublishedAt,
                ToMetadata(r), ToSource(r)))
            .OrderBy(a => a.Name, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<string>> DeleteFromPackageAsync(
        string packageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await IdsFromPackageAsync(db, packageId, ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Assets.Where(a => ids.Contains(a.Id)).ToListAsync(ct).ConfigureAwait(false);
        var names = rows
            .GroupBy(a => a.Id)
            .Select(g => g.OrderByDescending(a => a.Version).First().Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Alle Versionen, nicht nur die aus dem Paket: Ein Skill mit einer angepassten Fassung
        // obenauf ginge sonst halb weg — die Historie bliebe stehen und der Name wäre belegt.
        db.Assets.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return names;
    }

    /// <summary>
    /// Skills, von denen <em>irgendeine</em> Version aus diesem Paket stammt. Nicht nur die
    /// neueste — sonst fiele genau der Fall durch, um den es geht: die lokal angepasste Fassung.
    /// </summary>
    private static async Task<List<Guid>> IdsFromPackageAsync(
        McpMcpDbContext db, string packageId, CancellationToken ct)
        => await db.Assets.AsNoTracking()
            .Where(a => a.SourcePackageId == packageId)
            .Select(a => a.Id)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    private static async Task EnsureNameFreeAsync(McpMcpDbContext db, string name, CancellationToken ct)
    {
        // Der eindeutige Index faengt es ohnehin ab; diese Pruefung ist fuer die Meldung da. Ein
        // Datenbankfehler auf dem Bildschirm sagt niemandem, was zu tun ist.
        if (await db.Assets.AsNoTracking().AnyAsync(a => a.Name == name, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Es gibt bereits einen Skill namens '{name}'. Skills werden über ihren Namen "
                + "ausgeliefert — zwei gleiche Namen wären nicht unterscheidbar.");
        }
    }

    private static AssetContent ToContent(AssetRow row) => new(
        new AssetId(row.Id), new AssetVersion(row.Version), row.Name, row.Content, row.PublishedAt,
        ToMetadata(row), ToSource(row));

    private static SkillSource? ToSource(AssetRow row)
        => row.SourcePackageId is { Length: > 0 } id
            ? new SkillSource(id, row.SourcePackageVersion ?? "?")
            : null;

    /// <summary>
    /// Listen zeilenweise statt als JSON: Sie sind kurz, sie werden von Menschen im Editor
    /// eingetippt, und eine Zeile je Eintrag ist beim Blick in die Datenbank lesbar.
    /// </summary>
    private static SkillMetadata? ToMetadata(AssetRow row)
    {
        var metadata = new SkillMetadata(
            row.WhenToUse,
            Split(row.References),
            Split(row.RequiredTools));
        return metadata.IsEmpty ? null : metadata;
    }

    private static void ApplyMetadata(AssetRow row, SkillMetadata? metadata)
    {
        row.WhenToUse = string.IsNullOrWhiteSpace(metadata?.WhenToUse) ? null : metadata.WhenToUse.Trim();
        row.References = Join(metadata?.References);
        row.RequiredTools = Join(metadata?.RequiredTools);
    }

    private static IReadOnlyList<string>? Split(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : [.. value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string? Join(IReadOnlyList<string>? values)
    {
        var cleaned = (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        return cleaned.Count == 0 ? null : string.Join('\n', cleaned);
    }
}
