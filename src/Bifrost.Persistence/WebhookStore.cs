using System.Security.Cryptography;
using System.Text;
using Bifrost.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistente Webhook-Registrierung (FR-20, ADR-0013). Das HMAC-Secret wird zum Nachrechnen der
/// Signatur gebraucht und liegt deshalb DataProtection-verschlüsselt (wie Upstream-Credentials,
/// NFR-04) — nicht als Hash. Im Klartext verlässt es den Store nur zweimal: einmal bei der
/// Erzeugung (einmalige Anzeige) und intern zur Signaturprüfung.
/// </summary>
public sealed class WebhookStore : IWebhookStore
{
    // NICHT UMBENENNEN — siehe EfUpstreamConfigStore.ProtectionPurpose.
    private const string ProtectionPurpose = CryptographicNames.WebhookSecretPurpose;

    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public WebhookStore(
        IDbContextFactory<BifrostDbContext> factory,
        IDataProtectionProvider dataProtection,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(dataProtection);
        _factory = factory;
        _protector = dataProtection.CreateProtector(ProtectionPurpose);
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<WebhookDefinition>> ListAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Webhooks.AsNoTracking()
            .OrderBy(r => r.CreatedAtTicks).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToDefinition)];
    }

    public async Task<(WebhookDefinition Definition, string Secret)> CreateAsync(
        string name, IdentityId caller, NamespacedToolName tool, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // 32 Byte Zufall, url-safe base64 — genug Entropie, damit das Secret nicht ratbar ist.
        var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var row = new WebhookRow
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            CallerId = caller.Value,
            Tool = tool.Value,
            EncryptedSecret = _protector.Protect(Encoding.UTF8.GetBytes(secret)),
            Enabled = true,
            CreatedAtTicks = _time.GetUtcNow().UtcTicks,
        };

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Webhooks.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (ToDefinition(row), secret);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Webhooks.Where(r => r.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Webhooks.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, enabled), ct).ConfigureAwait(false);
    }

    public async Task<(WebhookDefinition Definition, string Secret)?> GetForVerificationAsync(
        Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Webhooks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.Enabled, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var secret = Encoding.UTF8.GetString(_protector.Unprotect(row.EncryptedSecret));
        return (ToDefinition(row), secret);
    }

    private static WebhookDefinition ToDefinition(WebhookRow r) => new(
        r.Id,
        r.Name,
        new IdentityId(r.CallerId),
        new NamespacedToolName(r.Tool),
        r.Enabled,
        new DateTimeOffset(r.CreatedAtTicks, TimeSpan.Zero));
}
