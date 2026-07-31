using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistenz der installierten Connector-Pakete (ADR-0016).
/// <para>
/// Kein Cache: Installationen sind selten, und die einzige heiße Abfrage („welche Version ist
/// aktiv?") beantwortet der <c>ConnectorPackageResolver</c> aus seinem Schnappschuss. Ein zweiter
/// Cache hier hätte nur eine weitere Stelle geschaffen, an der ein Zustand veralten kann.
/// </para>
/// </summary>
public sealed class ConnectorPackageStore : IConnectorPackageStore
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;

    public ConnectorPackageStore(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<IReadOnlyList<InstalledConnectorPackage>> ListAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ConnectorPackages.AsNoTracking()
            .OrderBy(r => r.PackageId).ThenBy(r => r.Version)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToPackage)];
    }

    public async Task<InstalledConnectorPackage?> GetActiveAsync(string packageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ConnectorPackages.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.PackageId == packageId && r.State == (int)PackageState.Active, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToPackage(row);
    }

    public async Task<IReadOnlyList<InstalledConnectorPackage>> GetVersionsAsync(
        string packageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ConnectorPackages.AsNoTracking()
            .Where(r => r.PackageId == packageId)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToPackage)];
    }

    public async Task UpsertAsync(InstalledConnectorPackage package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ConnectorPackages
            .FirstOrDefaultAsync(
                r => r.PackageId == package.PackageId && r.Version == package.Version, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new ConnectorPackageRow { PackageId = package.PackageId, Version = package.Version };
            db.ConnectorPackages.Add(row);
        }

        row.DisplayName = package.DisplayName;
        row.Transport = (int)package.Transport;
        row.PublisherKeyId = package.PublisherKeyId;
        row.TrustLevel = (int)package.TrustLevel;
        row.ManifestSha256 = package.ManifestSha256;
        row.Directory = package.Directory;
        row.State = (int)package.State;
        row.InstalledAtTicks = package.InstalledAt.UtcTicks;
        row.ActivatedAtTicks = package.ActivatedAt?.UtcTicks;
        row.GrantedCapabilities = package.GrantedCapabilities.Count == 0
            ? null
            : string.Join('\n', package.GrantedCapabilities);
        row.FailureReason = package.FailureReason;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Der Wechsel läuft in <b>einer</b> Transaktion: Zwei aktive Versionen desselben Pakets wären
    /// ein Zustand, den kein Aufrufer auflösen kann — und genau der entstünde, wenn zwischen
    /// „alte zurückstufen" und „neue aktivieren" etwas dazwischenkommt.
    /// </summary>
    public async Task ActivateAsync(
        string packageId, string version, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var rows = await db.ConnectorPackages
            .Where(r => r.PackageId == packageId)
            .ToListAsync(ct).ConfigureAwait(false);
        var target = rows.FirstOrDefault(r => r.Version == version)
            ?? throw new ConnectorPackageException($"'{packageId}' {version} ist nicht installiert.");

        foreach (var row in rows.Where(r => r.State == (int)PackageState.Active))
        {
            row.State = (int)PackageState.Superseded;
        }

        target.State = (int)PackageState.Active;
        target.ActivatedAtTicks = at.UtcTicks;
        target.FailureReason = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string packageId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ConnectorPackages
            .Where(r => r.PackageId == packageId && r.Version == version)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static InstalledConnectorPackage ToPackage(ConnectorPackageRow row) => new(
        row.PackageId,
        row.Version,
        row.DisplayName,
        (UpstreamTransportKind)row.Transport,
        row.PublisherKeyId,
        (ConnectorTrustLevel)row.TrustLevel,
        row.ManifestSha256,
        row.Directory,
        (PackageState)row.State,
        new DateTimeOffset(row.InstalledAtTicks, TimeSpan.Zero),
        row.ActivatedAtTicks is { } activated ? new DateTimeOffset(activated, TimeSpan.Zero) : null,
        string.IsNullOrEmpty(row.GrantedCapabilities)
            ? []
            : row.GrantedCapabilities.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        row.FailureReason);
}
