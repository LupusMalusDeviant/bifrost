using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// API-Key-Verwaltung (WP3.3, FR-27/NFR-04): Klartext-Key existiert nur im Issue-Ergebnis;
/// gespeichert wird ein PBKDF2-Hash. Format <c>mcpk_{keyId}_{secret}</c> — die eingebettete
/// KeyId erlaubt den Hash-Lookup ohne Volltabellen-Scan. Erfolgreiche Validierungen werden
/// kurz gecacht (PBKDF2 ist absichtlich teuer und wäre pro Request zu langsam).
/// </summary>
public sealed class ApiKeyService : IApiKeyService
{
    private const string Prefix = "mcpk";
    private static readonly TimeSpan ValidationCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (IdentityId Identity, DateTimeOffset Until)> _validationCache = new();

    public ApiKeyService(IDbContextFactory<BifrostDbContext> factory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IssuedApiKey> IssueAsync(IdentityId identity, string label, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var keyId = Guid.NewGuid();
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.ApiKeys.Add(new ApiKeyRow
        {
            Id = keyId,
            IdentityId = identity.Value,
            Label = label,
            Hash = HashSecret(secret),
            CreatedAt = _time.GetUtcNow(),
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new IssuedApiKey(keyId, $"{Prefix}_{keyId:N}_{secret}");
    }

    public async ValueTask<IdentityId?> ValidateAsync(string presentedKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedKey) || !TryParse(presentedKey, out var keyId, out var secret))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var cacheKey = Fingerprint(presentedKey);
        if (_validationCache.TryGetValue(cacheKey, out var cached) && cached.Until > now)
        {
            return cached.Identity;
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(r => r.Id == keyId, ct).ConfigureAwait(false);
        if (row is null || row.RevokedAt is not null || (row.ExpiresAt is { } expiry && expiry <= now))
        {
            return null;
        }

        if (!VerifySecret(secret, row.Hash))
        {
            return null;
        }

        var identity = new IdentityId(row.IdentityId);
        var cacheUntil = now + ValidationCacheTtl;
        if (row.ExpiresAt is { } exp && exp < cacheUntil)
        {
            cacheUntil = exp;
        }

        _validationCache[cacheKey] = (identity, cacheUntil);
        return identity;
    }

    public async Task RevokeAsync(Guid keyId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ApiKeys
            .Where(r => r.Id == keyId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, _time.GetUtcNow()), ct)
            .ConfigureAwait(false);

        _validationCache.Clear(); // grob, aber korrekt: Widerruf wirkt sofort
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(IdentityId? identity, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.ApiKeys.AsNoTracking().AsQueryable();
        if (identity is { } id)
        {
            query = query.Where(r => r.IdentityId == id.Value);
        }

        var rows = await query.OrderByDescending(r => r.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new ApiKeyInfo(
            r.Id, new IdentityId(r.IdentityId), r.Label, r.CreatedAt, r.ExpiresAt, r.RevokedAt))];
    }

    private static bool TryParse(string presentedKey, out Guid keyId, out string secret)
    {
        keyId = default;
        secret = string.Empty;
        var parts = presentedKey.Split('_', 3);
        if (parts.Length != 3 || parts[0] != Prefix || !Guid.TryParseExact(parts[1], "N", out keyId)
            || parts[2].Length == 0)
        {
            return false;
        }

        secret = parts[2];
        return true;
    }

    private static string HashSecret(string secret) => Pbkdf2Hasher.Hash(secret);

    private static bool VerifySecret(string secret, string stored) => Pbkdf2Hasher.Verify(secret, stored);

    private static string Fingerprint(string presentedKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey)));
}
