namespace McpMcp.Abstractions;

/// <summary>
/// Ein administrativ gepinnter Publisher-Schlüssel (Plan 0003, WP4). Nur Components, deren
/// Signatur zu einem dieser Schlüssel passt, werden vom WASI-Host geladen.
/// <para>
/// <see cref="KeyId"/> ist der SHA-256-Fingerprint des Public Keys und damit derselbe Wert, den
/// der Host in seinen Grant-Audit schreibt — Audit-Zeilen und Store-Einträge lassen sich ohne
/// Umweg zuordnen. <see cref="RevokedAt"/> gesetzt heißt: nicht mehr vertrauenswürdig. Entzogene
/// Schlüssel werden behalten statt gelöscht, damit ältere Audit-Zeilen zuordenbar bleiben.
/// </para>
/// </summary>
public sealed record PublisherKey(
    string KeyId,
    string PublicKeyBase64,
    string Label,
    DateTimeOffset AddedAt,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsActive => RevokedAt is null;
}

/// <summary>Ein Publisher wurde entzogen — Upstreams mit dessen Components müssen stoppen.</summary>
public sealed class PublisherRevokedEventArgs : EventArgs
{
    public PublisherRevokedEventArgs(string keyId) => KeyId = keyId;

    public string KeyId { get; }
}

/// <summary>
/// Verwaltung der vertrauenswürdigen Publisher (FR/WP4, ADR-0020). Die Keys liegen persistiert;
/// die Upstream-Konfiguration ist ab WP4 <b>keine</b> Vertrauensquelle mehr, sondern wird beim
/// Start einmalig in diesen Store übernommen.
/// </summary>
public interface IPublisherTrustStore
{
    /// <summary>Alle Schlüssel, auch entzogene (für Verwaltung und Audit-Zuordnung).</summary>
    IReadOnlyList<PublisherKey> All { get; }

    /// <summary>Base64-Public-Keys, denen aktuell vertraut wird — genau diese gehen an den Host.</summary>
    IReadOnlyList<string> ActivePublicKeys { get; }

    /// <summary>Feuert nach einem Entzug; der Supervisor stoppt daraufhin betroffene Upstreams.</summary>
    event EventHandler<PublisherRevokedEventArgs>? Revoked;

    Task LoadAsync(CancellationToken ct);

    /// <summary>
    /// Pinnt einen Public Key (Base64, 32 Byte). Idempotent: Ein bereits bekannter Schlüssel wird
    /// nicht dupliziert; ein zuvor entzogener wird dadurch <b>nicht</b> reaktiviert — dafür ist ein
    /// ausdrücklicher Aufruf von <see cref="ReinstateAsync"/> nötig.
    /// </summary>
    Task<PublisherKey> PinAsync(string publicKeyBase64, string label, CancellationToken ct);

    /// <summary>Entzieht das Vertrauen. Wirkt sofort — laufende Upstreams werden gestoppt.</summary>
    Task RevokeAsync(string keyId, CancellationToken ct);

    /// <summary>Hebt einen Entzug auf. Bewusst ein eigener Schritt, nie ein Nebeneffekt von Pin.</summary>
    Task ReinstateAsync(string keyId, CancellationToken ct);
}

/// <summary>
/// Eine Upstream-Verbindung, deren Inhalt von einem Publisher signiert ist (WASI). Über die
/// KeyId findet der Entzug die betroffenen Upstreams, ohne dass der Supervisor WASI kennen muss.
/// </summary>
public interface ISignedUpstreamConnection
{
    /// <summary>Fingerprint des Publishers, dessen Signatur beim Laden akzeptiert wurde.</summary>
    string PublisherKeyId { get; }
}
