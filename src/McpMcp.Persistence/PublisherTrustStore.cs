using System.Collections.Concurrent;
using System.Security.Cryptography;
using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// Persistierte Publisher-Schlüssel für WASI-Components (Plan 0003, WP4, ADR-0020).
/// Write-Through wie die Guard-Regeln: erst DB, dann Cache — gelesen wird bei jedem Upstream-Start,
/// geschrieben selten.
/// <para>
/// Ab WP4 ist dieser Store die <b>einzige</b> Vertrauensquelle. Schlüssel aus alten
/// Upstream-Konfigurationen werden beim Start einmalig übernommen
/// (<see cref="ImportAsync"/>) und danach ignoriert; sonst gäbe es zwei Wege, einem Publisher zu
/// vertrauen, und ein Entzug hier bliebe wirkungslos, solange der Schlüssel noch in einer Config
/// steht.
/// </para>
/// </summary>
public sealed class PublisherTrustStore : IPublisherTrustStore
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, PublisherKey> _cache = new(StringComparer.Ordinal);

    public PublisherTrustStore(IDbContextFactory<McpMcpDbContext> factory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(time);
        _factory = factory;
        _time = time;
    }

    public event EventHandler<PublisherRevokedEventArgs>? Revoked;

    public IReadOnlyList<PublisherKey> All =>
        [.. _cache.Values.OrderBy(key => key.Label, StringComparer.Ordinal).ThenBy(key => key.KeyId, StringComparer.Ordinal)];

    public IReadOnlyList<string> ActivePublicKeys =>
        [.. _cache.Values.Where(key => key.IsActive)
            .OrderBy(key => key.KeyId, StringComparer.Ordinal)
            .Select(key => key.PublicKeyBase64)];

    /// <summary>Berechnet den Fingerprint genau wie der Rust-Host: SHA-256 über die 32 Key-Bytes.</summary>
    public static string ComputeKeyId(string publicKeyBase64)
        => Convert.ToHexStringLower(SHA256.HashData(DecodeKey(publicKeyBase64)));

    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.PublisherKeys.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        _cache.Clear();
        foreach (var row in rows)
        {
            _cache[row.KeyId] = ToKey(row);
        }
    }

    public async Task<PublisherKey> PinAsync(string publicKeyBase64, string label, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyBase64);
        var keyId = ComputeKeyId(publicKeyBase64);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.PublisherKeys.FindAsync([keyId], ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new PublisherKeyRow
            {
                KeyId = keyId,
                PublicKey = publicKeyBase64,
                Label = string.IsNullOrWhiteSpace(label) ? keyId[..16] : label,
                AddedAtTicks = _time.GetUtcNow().UtcTicks,
            };
            db.PublisherKeys.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Kein stilles Reaktivieren: Ein erneutes Pinnen eines entzogenen Schlüssels ändert am
        // Entzug nichts — sonst hebt ein Import den Entzug wieder auf.
        var key = ToKey(row);
        _cache[keyId] = key;
        return key;
    }

    public async Task RevokeAsync(string keyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.PublisherKeys.FindAsync([keyId], ct).ConfigureAwait(false);
        if (row is null || row.RevokedAtTicks is not null)
        {
            return;
        }

        row.RevokedAtTicks = _time.GetUtcNow().UtcTicks;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache[keyId] = ToKey(row);

        // Erst persistieren, dann melden: Wer auf das Ereignis hin Upstreams stoppt, darf nicht
        // vor einem Zustand stehen, der einen Neustart überlebt hätte.
        Revoked?.Invoke(this, new PublisherRevokedEventArgs(keyId));
    }

    public async Task ReinstateAsync(string keyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.PublisherKeys.FindAsync([keyId], ct).ConfigureAwait(false);
        if (row is null || row.RevokedAtTicks is null)
        {
            return;
        }

        row.RevokedAtTicks = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache[keyId] = ToKey(row);
    }

    /// <summary>
    /// Setzt die Vertrauensstufe (ADR-0016). Eigener Schritt wie <see cref="ReinstateAsync"/>:
    /// Vertrauen zu erhöhen darf kein Nebeneffekt des Pinnens sein.
    /// <para>
    /// Wirkt nur auf <b>künftige</b> Installationen. Eine bereits installierte Paketversion behält
    /// die Stufe, unter der sie geprüft wurde — sonst änderte ein Klick rückwirkend die Bedingungen,
    /// unter denen ein Administrator einmal zugestimmt hat.
    /// </para>
    /// </summary>
    public async Task SetTrustLevelAsync(string keyId, ConnectorTrustLevel level, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (level is ConnectorTrustLevel.Core)
        {
            throw new ArgumentException(
                "'Core' ist mit dem Produkt ausgelieferter Code und keine Stufe, die ein "
                + "Herausgeber bekommen kann.", nameof(level));
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.PublisherKeys.FindAsync([keyId], ct).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.TrustLevel = (int)level;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache[keyId] = ToKey(row);
    }

    /// <summary>
    /// Übernimmt Schlüssel aus Upstream-Konfigurationen (Migrationspfad, WP4). Liefert die
    /// tatsächlich neu aufgenommenen Schlüssel zurück — nur die gehören ins Audit, sonst stünde
    /// bei jedem Start dieselbe Zeile.
    /// </summary>
    public async Task<IReadOnlyList<PublisherKey>> ImportAsync(
        IEnumerable<(string PublicKeyBase64, string Label)> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var imported = new List<PublisherKey>();
        foreach (var (publicKey, label) in keys)
        {
            if (string.IsNullOrWhiteSpace(publicKey) || _cache.ContainsKey(ComputeKeyId(publicKey)))
            {
                continue;
            }

            imported.Add(await PinAsync(publicKey, label, ct).ConfigureAwait(false));
        }

        return imported;
    }

    private static byte[] DecodeKey(string publicKeyBase64)
    {
        var bytes = Convert.FromBase64String(publicKeyBase64);
        return bytes.Length == 32
            ? bytes
            : throw new ArgumentException(
                "Ein Ed25519-Public-Key ist genau 32 Byte lang.", nameof(publicKeyBase64));
    }

    private static PublisherKey ToKey(PublisherKeyRow row) => new(
        row.KeyId,
        row.PublicKey,
        row.Label,
        new DateTimeOffset(row.AddedAtTicks, TimeSpan.Zero),
        row.RevokedAtTicks is { } revoked ? new DateTimeOffset(revoked, TimeSpan.Zero) : null,
        (ConnectorTrustLevel)row.TrustLevel);
}
