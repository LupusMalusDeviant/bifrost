using Bifrost.Abstractions;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// Trust-Store-Ersatz für Connector-Tests (Plan 0003, WP4): liefert eine feste Schlüsselliste,
/// ohne Datenbank. Die Persistenz- und Entzugslogik ist an ihrem eigenen Ort getestet.
/// </summary>
internal sealed class FakePublisherTrustStore : IPublisherTrustStore
{
    private readonly List<string> _active;

    public FakePublisherTrustStore(params string[] activePublicKeys) => _active = [.. activePublicKeys];

    public IReadOnlyList<PublisherKey> All =>
        [.. _active.Select(key => new PublisherKey(key, key, "test", DateTimeOffset.UnixEpoch))];

    public IReadOnlyList<string> ActivePublicKeys => _active;

    public event EventHandler<PublisherRevokedEventArgs>? Revoked
    {
        add { }
        remove { }
    }

    public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<PublisherKey> PinAsync(string publicKeyBase64, string label, CancellationToken ct)
    {
        _active.Add(publicKeyBase64);
        return Task.FromResult(new PublisherKey(publicKeyBase64, publicKeyBase64, label, DateTimeOffset.UnixEpoch));
    }

    public Task RevokeAsync(string keyId, CancellationToken ct)
    {
        _active.Remove(keyId);
        return Task.CompletedTask;
    }

    public Task ReinstateAsync(string keyId, CancellationToken ct) => Task.CompletedTask;

    public Task SetTrustLevelAsync(string keyId, ConnectorTrustLevel level, CancellationToken ct)
        => Task.CompletedTask;
}
