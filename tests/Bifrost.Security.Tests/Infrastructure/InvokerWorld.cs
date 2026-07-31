using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Core.Audit;
using Bifrost.Core.Catalog;
using Bifrost.Core.Invocation;
using Bifrost.Core.Rbac;

namespace Bifrost.Security.Tests.Infrastructure;

/// <summary>
/// Eine vollstaendige Invoker-Welt mit echtem Katalog, echtem RBAC und echter Redaktion — nur
/// Upstream, Audit und Ratenbegrenzung sind steuerbar.
/// <para>
/// <b>Warum echt, wo es geht:</b> Die Frage lautet, ob das Audit den <em>tatsaechlichen</em>
/// Ausgang eines Aufrufs festhaelt. Ein nachgebauter Autorisierungsdienst wuerde die Entscheidung
/// mitliefern, die der Test gerade beweisen will.
/// </para>
/// <para>
/// Diese Datei ist eine bewusste Zweitschrift von <c>InvokerTestWorld</c> aus
/// <c>Bifrost.Core.Tests</c>. Das Sicherheitsprojekt darf fremde Testprojekte nicht anfassen und
/// soll nicht an ihnen haengen — ein Architekturtest, der mit einer Umbenennung in einem anderen
/// Testprojekt kaputtgeht, meldet die falsche Sache.
/// </para>
/// </summary>
public sealed class InvokerWorld
{
    public FakeSupervisor Supervisor { get; } = new();

    public InMemoryRbacDirectory Directory { get; } = new();

    public AuthorizationService Authorization { get; }

    public ToolCatalog Catalog { get; }

    public RecordingAuditSink Audit { get; } = new();

    public SwitchableRateLimiter RateLimiter { get; } = new();

    public RedactionService Redaction { get; } = new();

    public ToolInvoker Invoker { get; }

    public ServerId Server { get; }

    public FakeUpstreamConnection Connection { get; }

    /// <summary>Eigener Slug je Welt — die Metriken laufen ueber einen prozessweiten Meter.</summary>
    public string Slug { get; } = $"srv{Guid.NewGuid():N}"[..12];

    public InvokerWorld()
    {
        Authorization = new AuthorizationService(Directory);
        Server = Supervisor.SetServer(Slug, new UpstreamInventory(
            [new ToolDescriptor("echo", "Echo.", Schema())], [], []));
        Connection = new FakeUpstreamConnection { Id = Server };
        Supervisor.SetConnection(Server, Connection);
        Catalog = new ToolCatalog(Supervisor, Authorization, Directory);
        Invoker = new ToolInvoker(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction);
    }

    public NamespacedToolName Echo => NamespacedToolName.Create(Slug, "echo");

    public ToolInvoker WithGuard(IContentGuard guard)
        => new(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction,
            timeProvider: null, logger: null, auditOptions: null, compression: null, guard: guard);

    public ToolInvoker WithApproval(IApprovalPolicy policy, IApprovalStore store)
        => new(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction,
            timeProvider: null, logger: null, auditOptions: null, compression: null,
            guard: null, guardOptions: null, approvalPolicy: policy, approvalStore: store);

    public IdentityId RegisterAgent(params Grant[] grants)
    {
        var role = new Role(RoleId.New(), "rolle", grants);
        Directory.UpsertRole(role);
        var id = IdentityId.New();
        Directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, [role.Id]));
        return id;
    }

    public IdentityId RegisterAdmin()
        => RegisterAgent(new Grant(
            new PermissionScope(null, null),
            [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt]));

    public ToolInvocationRequest Request(
        IdentityId caller, object? args = null, TimeSpan? timeout = null)
        => new(caller, CallOrigin.Mcp, Echo,
            args is null ? default : JsonSerializer.SerializeToElement(args), timeout);

    private static JsonElement Schema()
    {
        using var document = JsonDocument.Parse(
            """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""");
        return document.RootElement.Clone();
    }
}

/// <summary>Haelt jedes Audit-Ereignis fest — die Beobachtung, um die es hier geht.</summary>
public sealed class RecordingAuditSink : IAuditSink
{
    private readonly List<AuditEvent> _events = [];

    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Record(AuditEvent evt)
    {
        lock (_events)
        {
            _events.Add(evt);
        }
    }
}

public sealed class SwitchableRateLimiter : IRateLimiter
{
    public bool Allow { get; set; } = true;

    public bool TryAcquire(IdentityId identity) => Allow;
}

/// <summary>Steuerbarer Supervisor: Server, Inventar und Verbindung werden gesetzt, nicht gestartet.</summary>
public sealed class FakeSupervisor : IUpstreamSupervisor
{
    private readonly Dictionary<ServerId, (UpstreamStatus Status, UpstreamInventory? Inventory)> _servers = [];
    private readonly Dictionary<ServerId, IUpstreamConnection> _connections = [];

    public event EventHandler<UpstreamChangedEventArgs>? Changed;

    public IReadOnlyList<UpstreamStatus> Statuses => [.. _servers.Values.Select(entry => entry.Status)];

    public UpstreamStatus? GetStatus(ServerId id)
        => _servers.TryGetValue(id, out var entry) ? entry.Status : null;

    public UpstreamInventory? GetInventory(ServerId id)
        => _servers.TryGetValue(id, out var entry) ? entry.Inventory : null;

    public IUpstreamConnection? GetConnection(ServerId id) => _connections.GetValueOrDefault(id);

    public void SetConnection(ServerId id, IUpstreamConnection connection) => _connections[id] = connection;

    public ServerId SetServer(string slug, UpstreamInventory inventory)
    {
        var id = ServerId.New();
        _servers[id] = (
            new UpstreamStatus(id, slug, UpstreamState.Healthy, null, inventory.Tools.Count, DateTimeOffset.UtcNow),
            inventory);
        return id;
    }

    public void RaiseChanged(ServerId id)
        => Changed?.Invoke(this, new UpstreamChangedEventArgs
        {
            Server = id,
            Kind = UpstreamChangeKind.InventoryChanged,
            State = UpstreamState.Healthy,
        });

    public Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct)
        => throw new NotSupportedException("Die Welt setzt ihre Server direkt.");

    public Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct) => Task.CompletedTask;

    public Task SetEnabledAsync(ServerId id, bool enabled, CancellationToken ct) => Task.CompletedTask;

    public Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
        => Task.FromResult(new ConfigVersionId(1));

    public Task RollbackAsync(ServerId id, ConfigVersionId version, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Steuerbare Upstream-Verbindung: liefert eine Antwort, wirft, oder wartet ewig.</summary>
public sealed class FakeUpstreamConnection : IUpstreamConnection
{
    public ServerId Id { get; set; }

    /// <summary>Was der Upstream zurueckgibt. Vorgabe: ein leeres Objekt.</summary>
    public string ResponseJson { get; set; } = "{}";

    /// <summary>Gesetzt heisst: Der Aufruf endet in dieser Ausnahme.</summary>
    public Exception? Throw { get; set; }

    /// <summary>Gesetzt heisst: Der Aufruf antwortet nie und laeuft in sein Zeitlimit.</summary>
    public bool HangForever { get; set; }

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived;

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new UpstreamInventory([], [], []));

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        if (HangForever)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        using var document = JsonDocument.Parse(ResponseJson);
        return document.RootElement.Clone();
    }

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());

    public Task PingAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Nur damit das Ereignis benutzt wird — die Welt loest keine Benachrichtigungen aus.</summary>
    public void RaiseNotification()
        => NotificationReceived?.Invoke(
            this, new UpstreamNotificationEventArgs { Server = Id, Method = "notifications/probe" });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
