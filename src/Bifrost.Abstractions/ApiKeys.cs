namespace Bifrost.Abstractions;

/// <summary>Ergebnis einer Key-Erzeugung. <see cref="PlaintextKey"/> existiert nur in diesem Objekt — nie persistiert, nie geloggt (FR-27, NFR-04).</summary>
public sealed record IssuedApiKey(Guid KeyId, string PlaintextKey);

public sealed record ApiKeyInfo(
    Guid KeyId,
    IdentityId Identity,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary>Verwaltung der API-Keys (FR-27, WP3.3): Erzeugung, Widerruf, Auflistung — plus Validierung aus <see cref="IApiKeyValidator"/>.</summary>
public interface IApiKeyService : IApiKeyValidator
{
    Task<IssuedApiKey> IssueAsync(IdentityId identity, string label, DateTimeOffset? expiresAt, CancellationToken ct);

    Task RevokeAsync(Guid keyId, CancellationToken ct);

    Task<IReadOnlyList<ApiKeyInfo>> ListAsync(IdentityId? identity, CancellationToken ct);
}
