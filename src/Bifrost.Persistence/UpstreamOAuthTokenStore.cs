using System.Text.Json;
using Bifrost.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Ablage der Upstream-OAuth-Token. Access- und Refresh-Token liegen
/// DataProtection-verschlüsselt (NFR-04) — dieselbe Behandlung wie Upstream-Credentials, denn
/// nichts anderes sind sie.
/// <para>
/// Kein Cache: Ein Token wird beim Verbindungsaufbau gelesen und bei Erneuerung geschrieben; ein
/// Cache brächte hier nichts außer einer weiteren Stelle, an der ein widerrufenes Token
/// weiterlebt.
/// </para>
/// </summary>
public sealed class UpstreamOAuthTokenStore : IUpstreamOAuthTokenStore
{
    // NICHT UMBENENNEN — siehe EfUpstreamConfigStore.ProtectionPurpose.
    private const string Purpose = CryptographicNames.UpstreamOAuthTokenPurpose;

    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly IDataProtector _protector;

    public UpstreamOAuthTokenStore(
        IDbContextFactory<BifrostDbContext> factory, IDataProtectionProvider protection)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(protection);
        _factory = factory;
        _protector = protection.CreateProtector(Purpose);
    }

    public async Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.UpstreamOAuthTokens.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ServerId == server.Value, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        Secrets secrets;
        try
        {
            secrets = JsonSerializer.Deserialize<Secrets>(_protector.Unprotect(row.Payload))!;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Nicht mehr entschlüsselbar (z. B. Key-Ring verloren). Ein unlesbares Token ist kein
            // Token — die Verbindung muss neu hergestellt werden, und das soll auffallen.
            return null;
        }

        return new UpstreamOAuthToken(
            server,
            secrets.AccessToken,
            secrets.RefreshToken,
            row.ExpiresAtTicks is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
            secrets.Scopes ?? [],
            row.Issuer,
            new DateTimeOffset(row.ObtainedAtTicks, TimeSpan.Zero));
    }

    public async Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.UpstreamOAuthTokens
            .FirstOrDefaultAsync(r => r.ServerId == token.Server.Value, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new UpstreamOAuthTokenRow { ServerId = token.Server.Value };
            db.UpstreamOAuthTokens.Add(row);
        }

        row.Payload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new Secrets(token.AccessToken, token.RefreshToken, token.Scopes)));
        row.Issuer = token.Issuer;
        row.ExpiresAtTicks = token.ExpiresAt?.UtcTicks;
        // Der Zeitpunkt ist zugleich die Kennung des gespeicherten Tokens (ReplaceAsync). Trägt das
        // neue denselben wie das alte, wird er um einen Tick weitergestellt — sonst hielte eine späte
        // Erneuerung das neue Token für das, das sie gelesen hat (Plan 0005 D7).
        row.ObtainedAtTicks = row.ObtainedAtTicks >= token.ObtainedAt.UtcTicks && db.Entry(row).State != EntityState.Added
            ? row.ObtainedAtTicks + 1
            : token.ObtainedAt.UtcTicks;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ersetzt nur, wenn noch das gelesene Token gespeichert ist — in einer Anweisung. Erkannt wird es
    /// am Zeitpunkt seines Erhalts: Die Nutzlast ist verschlüsselt und bei jedem Schreiben anders,
    /// aber jedes neue Token, ob aus einer Autorisierung oder einer Erneuerung, trägt einen neuen.
    /// </summary>
    public async Task<bool> ReplaceAsync(UpstreamOAuthToken expected, UpstreamOAuthToken replacement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        var payload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new Secrets(replacement.AccessToken, replacement.RefreshToken, replacement.Scopes)));
        var obtained = expected.ObtainedAt.UtcTicks;
        var expires = replacement.ExpiresAt?.UtcTicks;
        // Immer ein anderer Zeitpunkt als der ersetzte (siehe SaveAsync).
        var obtainedNew = Math.Max(replacement.ObtainedAt.UtcTicks, obtained + 1);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var changed = await db.UpstreamOAuthTokens
            .Where(r => r.ServerId == expected.Server.Value && r.ObtainedAtTicks == obtained)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Payload, payload)
                .SetProperty(r => r.Issuer, replacement.Issuer)
                .SetProperty(r => r.ExpiresAtTicks, expires)
                .SetProperty(r => r.ObtainedAtTicks, obtainedNew), ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    public async Task RemoveAsync(ServerId server, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.UpstreamOAuthTokens.Where(r => r.ServerId == server.Value)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Was verschlüsselt liegt. Alles andere steht im Klartext daneben und ist filterbar.</summary>
    private sealed record Secrets(string AccessToken, string? RefreshToken, IReadOnlyList<string>? Scopes);
}
