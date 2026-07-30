using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Core.Audit;
using McpMcp.Core.Catalog;
using McpMcp.Core.Invocation;
using McpMcp.Core.Rbac;
using McpMcp.Core.Tests.Catalog;
using McpMcp.Core.Tests.Upstreams;
using Microsoft.Extensions.Time.Testing;

namespace McpMcp.Core.Tests.Invocation;

internal sealed class FakeAuditSink : IAuditSink
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

internal sealed class FakeRateLimiter : IRateLimiter
{
    public bool Allow { get; set; } = true;

    public bool TryAcquire(IdentityId identity) => Allow;
}

/// <summary>Komplette Invoker-Welt: echter Katalog/RBAC/Redaction, fake Supervisor/Audit/RateLimit.</summary>
internal sealed class InvokerTestWorld
{
    public FakeTimeProvider Time { get; } = new();

    public FakeSupervisor Supervisor { get; } = new();

    public InMemoryRbacDirectory Directory { get; } = new();

    public AuthorizationService Authorization { get; }

    public ToolCatalog Catalog { get; }

    public FakeAuditSink Audit { get; } = new();

    public FakeRateLimiter RateLimiter { get; } = new();

    public RedactionService Redaction { get; } = new();

    public ToolInvoker Invoker { get; }

    public MetaToolService MetaTools { get; }

    public ServerId Server { get; }

    public FakeUpstreamConnection Connection { get; }

    /// <summary>
    /// Eigener Server-Slug je Welt: Metriken laufen über einen prozessweiten Meter, parallel laufende
    /// Tests würden sich sonst gegenseitig ihre Messungen in die Auswertung mischen.
    /// </summary>
    public string Slug { get; } = $"srv{Guid.NewGuid():N}"[..12];

    public InvokerTestWorld(
        bool echoRequiresApproval = false,
        bool echoSensitive = false)
    {
        Authorization = new AuthorizationService(Directory);
        Server = Supervisor.SetServer(Slug, new UpstreamInventory(
            [
                new ToolDescriptor(
                    "echo",
                    "Echoes a message back.",
                    StrictSchema(echoSensitive),
                    echoRequiresApproval ? CapabilityRisk.Destructive : CapabilityRisk.Read,
                    echoRequiresApproval),
                new ToolDescriptor("free", "Tool ohne verwertbares Schema.", BrokenSchema()),
            ],
            [], []));
        Connection = new FakeUpstreamConnection { Id = Server };
        Supervisor.SetConnection(Server, Connection);
        Catalog = new ToolCatalog(Supervisor, Authorization, Directory);
        Invoker = new ToolInvoker(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction, Time);
        MetaTools = new MetaToolService(Catalog, Authorization, Invoker, Audit, Redaction, Time);
    }

    public NamespacedToolName Echo => NamespacedToolName.Create(Slug, "echo");

    public NamespacedToolName Free => NamespacedToolName.Create(Slug, "free");

    /// <summary>
    /// Meta-Tools mit angebundener Skill-Ablage. Ohne sie ist <c>_assets</c> null — und genau das ist
    /// eine gültige Zusammenstellung, die ihren eigenen Test hat.
    /// </summary>
    public MetaToolService WithAssets(IAssetStore assets)
        => new(Catalog, Authorization, Invoker, Audit, Redaction, Time, assets);

    /// <summary>
    /// Meta-Tools, die wissen, welche Werkzeuge scharf sind — Voraussetzung für die Weiche zwischen
    /// <c>invoke_tool</c> und <c>invoke_sensitive_tool</c> (ADR-0022).
    /// </summary>
    public MetaToolService WithApprovalPolicy(IApprovalPolicy policy)
        => new(Catalog, Authorization, Invoker, Audit, Redaction, Time, assets: null, approvalPolicy: policy);

    /// <summary>Zweiter Invoker auf derselben Welt — für Schalter, die im Konstruktor festgelegt werden.</summary>
    public ToolInvoker WithAuditOptions(AuditOptions options)
        => new(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction, Time, logger: null, options);

    /// <summary>Invoker mit aktiver Inhaltsprüfung (ADR-0011).</summary>
    public ToolInvoker WithGuard(IContentGuard guard, GuardOptions? options = null)
        => new(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction, Time,
            logger: null, auditOptions: null, compression: null, guard: guard, guardOptions: options);

    /// <summary>Invoker mit aktiver Freigabe-Prüfung (ADR-0012).</summary>
    public ToolInvoker WithApproval(IApprovalPolicy policy, IApprovalStore store)
        => new(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction, Time,
            logger: null, auditOptions: null, compression: null, guard: null, guardOptions: null,
            approvalPolicy: policy, approvalStore: store);

    public IdentityId RegisterAgent(params Grant[] grants)
    {
        var role = new Role(RoleId.New(), "rolle", grants);
        Directory.UpsertRole(role);
        var id = IdentityId.New();
        Directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, [role.Id]));
        return id;
    }

    public IdentityId RegisterAdmin()
        => RegisterAgent(new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt]));

    public static ToolInvocationRequest Request(
        IdentityId caller, NamespacedToolName tool, object? args = null, TimeSpan? timeoutOverride = null)
        => new(
            caller,
            CallOrigin.Mcp,
            tool,
            args is null ? default : JsonSerializer.SerializeToElement(args),
            timeoutOverride);

    private static JsonElement StrictSchema(bool sensitive)
    {
        var schema = sensitive
            ? """{"type":"object","properties":{"message":{"type":"string","writeOnly":true}},"required":["message"]}"""
            : """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""";
        using var doc = JsonDocument.Parse(schema);
        return doc.RootElement.Clone();
    }

    private static JsonElement BrokenSchema()
    {
        // Syntaktisch JSON, aber kein auswertbares Schema (type-Wert ist Unsinn) — R3-Fallback-Pfad.
        using var doc = JsonDocument.Parse("""{"type":12345}""");
        return doc.RootElement.Clone();
    }
}
