namespace Bifrost.Abstractions;

/// <summary>
/// Ein registrierter Webhook (FR-20, ADR-0013). Löst genau EIN Tool im Namen einer festen
/// Identität aus; die Signaturprüfung sichert den einzigen unauthentifizierten Eingang.
/// </summary>
public sealed record WebhookDefinition(
    Guid Id,
    string Name,
    IdentityId Caller,
    NamespacedToolName Tool,
    bool Enabled,
    DateTimeOffset CreatedAt);

/// <summary>
/// Verwaltung der Webhooks. Das HMAC-Secret liegt DataProtection-verschlüsselt (ADR-0013) — es wird
/// zum Nachrechnen der Signatur gebraucht und darf deshalb, anders als ein API-Key, nicht nur als
/// Hash vorliegen. Es verlässt den Store ausschließlich beim Anlegen (einmalige Anzeige).
/// </summary>
public interface IWebhookStore
{
    Task<IReadOnlyList<WebhookDefinition>> ListAsync(CancellationToken ct);

    /// <summary>Legt einen Webhook an und gibt Definition plus das einmalig sichtbare Secret zurück.</summary>
    Task<(WebhookDefinition Definition, string Secret)> CreateAsync(
        string name, IdentityId caller, NamespacedToolName tool, CancellationToken ct);

    Task RemoveAsync(Guid id, CancellationToken ct);

    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct);

    /// <summary>Liefert Definition + entschlüsseltes Secret für die Signaturprüfung; null wenn unbekannt/deaktiviert.</summary>
    Task<(WebhookDefinition Definition, string Secret)?> GetForVerificationAsync(Guid id, CancellationToken ct);
}

/// <summary>Ergebnis einer Webhook-Signaturprüfung (ADR-0013).</summary>
public enum WebhookVerification
{
    Valid = 0,

    /// <summary>Signatur fehlt, ist falsch formatiert oder stimmt nicht.</summary>
    InvalidSignature = 1,

    /// <summary>Zeitstempel außerhalb des zulässigen Fensters — Replay-Schutz.</summary>
    Stale = 2,
}
