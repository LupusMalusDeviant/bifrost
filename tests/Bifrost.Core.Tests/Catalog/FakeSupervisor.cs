using Bifrost.Abstractions;
using Bifrost.Core.Tests.Upstreams;

namespace Bifrost.Core.Tests.Catalog;

/// <summary>Steuerbarer IUpstreamSupervisor für Katalog-Tests: Server + Inventare setzen, Events feuern.</summary>
internal sealed class FakeSupervisor : IUpstreamSupervisor
{
    private readonly Dictionary<ServerId, (UpstreamStatus Status, UpstreamInventory? Inventory)> _servers = [];
    private readonly Dictionary<ServerId, IUpstreamConnection> _connections = [];

    public event EventHandler<UpstreamChangedEventArgs>? Changed;

    public IReadOnlyList<UpstreamStatus> Statuses => [.. _servers.Values.Select(v => v.Status)];

    public UpstreamStatus? GetStatus(ServerId id) => _servers.TryGetValue(id, out var v) ? v.Status : null;

    public UpstreamInventory? GetInventory(ServerId id) => _servers.TryGetValue(id, out var v) ? v.Inventory : null;

    public IUpstreamConnection? GetConnection(ServerId id) => _connections.GetValueOrDefault(id);

    public void SetConnection(ServerId id, IUpstreamConnection? connection)
    {
        if (connection is null)
        {
            _connections.Remove(id);
        }
        else
        {
            _connections[id] = connection;
        }
    }

    public ServerId SetServer(string slug, UpstreamInventory? inventory, UpstreamState state = UpstreamState.Healthy)
    {
        var id = ServerId.New();
        SetServer(id, slug, inventory, state);
        return id;
    }

    public void SetServer(ServerId id, string slug, UpstreamInventory? inventory, UpstreamState state = UpstreamState.Healthy)
        => _servers[id] = (
            new UpstreamStatus(id, slug, state, null, inventory?.Tools.Count ?? 0, DateTimeOffset.UtcNow),
            inventory);

    public void RemoveServer(ServerId id) => _servers.Remove(id);

    public void RaiseChanged(ServerId id, UpstreamChangeKind kind, UpstreamState state = UpstreamState.Healthy)
        => Changed?.Invoke(this, new UpstreamChangedEventArgs { Server = id, Kind = kind, State = state });

    public Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct)
        => Task.FromResult(SetServer(config.Slug, TestData.InventoryWithTools("echo")));

    public Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct)
    {
        RemoveServer(id);
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(ServerId id, bool enabled, CancellationToken ct) => Task.CompletedTask;

    public Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
        => Task.FromResult(new ConfigVersionId(1));

    public Task RollbackAsync(ServerId id, ConfigVersionId version, CancellationToken ct) => Task.CompletedTask;
}
