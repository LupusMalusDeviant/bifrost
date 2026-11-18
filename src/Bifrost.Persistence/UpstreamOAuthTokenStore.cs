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
            new DateTimeOffset(row.ObtainedAtTicks, TimeSpan.Zero))
        {
            Resource = row.Resource,
            ClientId = row.ClientId,
            // Eine Zeile aus der Zeit vor den Kennungen hat keine — Guid.Empty steht dafür, und
            // ReplaceAsync/RemoveIfUnchangedAsync vergleichen dann mit „keine Kennung".
            Revision = row.Revision ?? Guid.Empty,
        };
    }

    public Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct)
        => SaveWithRevisionAsync(token, ct);

    /// <summary>
    /// Speichert unter einer <b>frischen</b> Kennung — bei jedem Einfügen und jedem Überschreiben, gleich
    /// was das Token mitbringt (Plan 0005 F14). Eine vom Aufrufer mitgebrachte Kennung könnte eine
    /// schon einmal gespeicherte sein (ein per <c>with</c> abgeleitetes Token), und dann hielte eine
    /// späte Erneuerung das neue Token für das, das sie gelesen hat (D7). Der Zeitpunkt des Erhalts
    /// wird unverändert abgelegt; er ist Angabe, keine Kennung.
    /// </summary>
    public async Task<Guid> SaveWithRevisionAsync(UpstreamOAuthToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        var payload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new Secrets(token.AccessToken, token.RefreshToken, token.Scopes)));
        var obtained = token.ObtainedAt.UtcTicks;
        var expires = token.ExpiresAt?.UtcTicks;

        // Je Speicherung eine eigene — auch gleichzeitige Speicherungen bleiben so unterscheidbar (E15).
        Guid? revision = Guid.NewGuid();

        // Zwei Versuche: Legt ein gleichzeitiges Speichern die Zeile zwischen unserem Update und
        // unserem Einfügen an, schreibt der zweite Durchgang über das Update.
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var changed = await db.UpstreamOAuthTokens
                .Where(r => r.ServerId == token.Server.Value)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Payload, payload)
                    .SetProperty(r => r.Issuer, token.Issuer)
                    .SetProperty(r => r.ExpiresAtTicks, expires)
                    .SetProperty(r => r.ObtainedAtTicks, obtained)
                    .SetProperty(r => r.Resource, token.Resource)
                    .SetProperty(r => r.ClientId, token.ClientId)
                    .SetProperty(r => r.Revision, revision), ct)
                .ConfigureAwait(false);
            if (changed > 0)
            {
                return revision.Value;
            }

            db.UpstreamOAuthTokens.Add(new UpstreamOAuthTokenRow
            {
                ServerId = token.Server.Value,
                Payload = payload,
                Issuer = token.Issuer,
                ExpiresAtTicks = expires,
                ObtainedAtTicks = obtained,
                Resource = token.Resource,
                ClientId = token.ClientId,
                Revision = revision,
            });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return revision.Value;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Ein anderer Schreiber hat die Zeile eben angelegt — dann gilt der Update-Weg.
            }
        }
    }

    /// <summary>
    /// Ersetzt nur, wenn noch das gelesene Token gespeichert ist — in einer Anweisung. Erkannt wird es
    /// an seiner Kennung (Plan 0005 F14): Die Nutzlast ist verschlüsselt und bei jedem Schreiben
    /// anders, der Zeitpunkt des Erhalts kann nach Trennen und neuem Autorisieren wiederkehren —
    /// die Kennung nicht.
    /// </summary>
    public async Task<bool> ReplaceAsync(UpstreamOAuthToken expected, UpstreamOAuthToken replacement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        var payload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new Secrets(replacement.AccessToken, replacement.RefreshToken, replacement.Scopes)));
        var expires = replacement.ExpiresAt?.UtcTicks;
        var obtained = replacement.ObtainedAt.UtcTicks;

        // Immer eine frische Kennung (siehe SaveWithRevisionAsync).
        Guid? revision = Guid.NewGuid();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var changed = await Matching(db, expected)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Payload, payload)
                .SetProperty(r => r.Issuer, replacement.Issuer)
                .SetProperty(r => r.ExpiresAtTicks, expires)
                .SetProperty(r => r.ObtainedAtTicks, obtained)
                .SetProperty(r => r.Resource, replacement.Resource)
                .SetProperty(r => r.ClientId, replacement.ClientId)
                .SetProperty(r => r.Revision, revision), ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    public async Task RemoveAsync(ServerId server, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.UpstreamOAuthTokens.Where(r => r.ServerId == server.Value)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Entfernt nur, wenn noch genau dieser Stand gespeichert ist — in einer Anweisung (F5).</summary>
    public async Task<bool> RemoveIfUnchangedAsync(UpstreamOAuthToken expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await Matching(db, expected).ExecuteDeleteAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Die Zeile, solange sie noch den Stand von <paramref name="expected"/> trägt. Eine Zeile aus der
    /// Zeit vor den Kennungen hat keine; sie wird über „keine Kennung" getroffen, und jedes Schreiben
    /// gibt ihr eine.
    /// </summary>
    private static IQueryable<UpstreamOAuthTokenRow> Matching(BifrostDbContext db, UpstreamOAuthToken expected)
    {
        var server = expected.Server.Value;
        if (expected.Revision == Guid.Empty)
        {
            return db.UpstreamOAuthTokens.Where(r => r.ServerId == server && r.Revision == null);
        }

        Guid? revision = expected.Revision;
        return db.UpstreamOAuthTokens.Where(r => r.ServerId == server && r.Revision == revision);
    }


    /// <summary>Was verschlüsselt liegt. Alles andere steht im Klartext daneben und ist filterbar.</summary>
    private sealed record Secrets(string AccessToken, string? RefreshToken, IReadOnlyList<string>? Scopes);
}
