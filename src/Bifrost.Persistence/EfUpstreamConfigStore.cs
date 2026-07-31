using System.Text.Json;
using Bifrost.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistente Config-Versionen (FR-10, ADR-0007). Die komplette Config wird als
/// DataProtection-verschlüsselter JSON-Blob gespeichert — Credentials in Env-Vars/Headers
/// landen damit nie im Klartext auf der Platte (NFR-04).
/// </summary>
public sealed class EfUpstreamConfigStore : IUpstreamConfigStore
{
    // NICHT UMBENENNEN: Der Zweck geht in die Schluesselableitung ein. Er traegt den alten
    // Produktnamen, weil jeder gespeicherte Geheimtext sonst unlesbar wuerde — ein
    // kryptografischer Bezeichner ist kein Markenname (siehe Program.cs, SetApplicationName).
    internal const string ProtectionPurpose = CryptographicNames.UpstreamConfigPurpose;

    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public EfUpstreamConfigStore(
        IDbContextFactory<BifrostDbContext> factory,
        IDataProtectionProvider dataProtection,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(dataProtection);
        _factory = factory;
        _protector = dataProtection.CreateProtector(ProtectionPurpose);
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ConfigVersionId> AppendVersionAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var maxVersion = await db.ConfigVersions
            .Where(r => r.ServerId == id.Value)
            .MaxAsync(r => (int?)r.Version, ct)
            .ConfigureAwait(false) ?? 0;

        var payload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(config));
        var row = new ConfigVersionRow
        {
            ServerId = id.Value,
            Version = maxVersion + 1,
            Payload = payload,
            SavedAt = _time.GetUtcNow(),
        };
        db.ConfigVersions.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ConfigVersionId(row.Version);
    }

    public async Task<UpstreamServerConfig?> GetVersionAsync(ServerId id, ConfigVersionId version, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ConfigVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.ServerId == id.Value && r.Version == version.Value, ct)
            .ConfigureAwait(false);

        return row is null ? null : Deserialize(row);
    }

    public async Task<IReadOnlyList<UpstreamConfigVersion>> GetHistoryAsync(ServerId id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ConfigVersions
            .AsNoTracking()
            .Where(r => r.ServerId == id.Value)
            .OrderBy(r => r.Version)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new UpstreamConfigVersion(new ConfigVersionId(r.Version), Deserialize(r), r.SavedAt))];
    }

    public async Task<IReadOnlyDictionary<ServerId, UpstreamConfigVersion>> GetAllLatestAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var latest = await db.ConfigVersions
            .AsNoTracking()
            .GroupBy(r => r.ServerId)
            .Select(g => g.OrderByDescending(r => r.Version).First())
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return latest.ToDictionary(
            r => new ServerId(r.ServerId),
            r => new UpstreamConfigVersion(new ConfigVersionId(r.Version), Deserialize(r), r.SavedAt));
    }

    public async Task RemoveAsync(ServerId id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ConfigVersions
            .Where(r => r.ServerId == id.Value)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    private UpstreamServerConfig Deserialize(ConfigVersionRow row)
        => JsonSerializer.Deserialize<UpstreamServerConfig>(_protector.Unprotect(row.Payload))
            ?? throw new InvalidOperationException($"Config-Version {row.Version} für Server {row.ServerId} ist korrupt.");
}
