using McpMcp.Abstractions;
using McpMcp.Core.Guardrails;
using McpMcp.Core.Packaging;
using McpMcp.Core.Upstreams;
using McpMcp.Persistence;
using McpMcp.Persistence.Audit;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Server;

/// <summary>
/// Startreihenfolge des Gateways (WP4.2): Schema sicherstellen → RBAC hydratisieren →
/// Bootstrap-Admin (nur bei leerer DB) → persistierte Upstreams wiederherstellen.
/// Beim Stop werden die Upstreams gedraint (ADR-0005).
/// </summary>
public sealed partial class GatewayStartupService : IHostedService
{
    private const string UiInternalIdentityName = "ui-internal";

    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly DatabaseInitializer _databaseInitializer;
    private readonly PersistentRbacStore _rbacStore;
    private readonly ToolDescriptionOverrideStore _descriptionOverrides;
    private readonly RedactionRuleStore _redactionRules;
    private readonly GuardRuleStore _guardRules;
    private readonly ApprovalPolicyStore _approvalPolicy;
    private readonly PublisherTrustStore _publisherTrust;
    private readonly ConnectorPackageResolver _packages;
    private readonly ToolDefinitionPinStore _toolPins;
    private readonly IAuditSink _audit;
    private readonly IApiKeyService _apiKeys;
    private readonly IUiUserService _uiUsers;
    private readonly McpMcp.Web.UiInternalIdentity _uiInternal;
    private readonly IUpstreamConfigStore _configStore;
    private readonly UpstreamSupervisor _supervisor;
    private readonly ApprovalToTaskMigration _approvalMigration;
    private readonly ILogger<GatewayStartupService> _logger;

    public GatewayStartupService(
        IDbContextFactory<McpMcpDbContext> factory,
        DatabaseInitializer databaseInitializer,
        PersistentRbacStore rbacStore,
        ToolDescriptionOverrideStore descriptionOverrides,
        RedactionRuleStore redactionRules,
        GuardRuleStore guardRules,
        ApprovalPolicyStore approvalPolicy,
        PublisherTrustStore publisherTrust,
        ConnectorPackageResolver packages,
        ToolDefinitionPinStore toolPins,
        IAuditSink audit,
        IApiKeyService apiKeys,
        IUiUserService uiUsers,
        McpMcp.Web.UiInternalIdentity uiInternal,
        IUpstreamConfigStore configStore,
        UpstreamSupervisor supervisor,
        ApprovalToTaskMigration approvalMigration,
        ILogger<GatewayStartupService> logger)
    {
        _factory = factory;
        _databaseInitializer = databaseInitializer;
        _rbacStore = rbacStore;
        _descriptionOverrides = descriptionOverrides;
        _redactionRules = redactionRules;
        _guardRules = guardRules;
        _approvalPolicy = approvalPolicy;
        _publisherTrust = publisherTrust;
        _packages = packages;
        _toolPins = toolPins;
        _audit = audit;
        _apiKeys = apiKeys;
        _uiUsers = uiUsers;
        _uiInternal = uiInternal;
        _configStore = configStore;
        _supervisor = supervisor;
        _approvalMigration = approvalMigration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Ab v1.1 über EF-Migrationen; erkennt und stempelt v1.0-Schemata ohne Migrationshistorie.
        await _databaseInitializer.InitializeAsync(cancellationToken);

        await _rbacStore.LoadAsync(cancellationToken);
        await _descriptionOverrides.LoadAsync(cancellationToken);
        await _redactionRules.LoadAsync(cancellationToken);
        // Beim allerersten Start den kuratierten Regelsatz einsaeen; danach ist die DB massgeblich,
        // damit ein abgeschaltetes Muster abgeschaltet bleibt (ADR-0011).
        await _guardRules.LoadAsync(BuiltInGuardRules.All, cancellationToken);
        await _approvalPolicy.LoadAsync(cancellationToken);

        // ADR-0019: Wartende Freigaben einmalig in das Task-Modell uebernehmen, BEVOR die
        // Freigabe-Liste jemandem angezeigt wird — sonst sähe ein Operator nach dem Update eine
        // leere Queue und hielte offene Anfragen für erledigt. Idempotent, ein zweiter Start ist
        // harmlos.
        var migratedApprovals = await _approvalMigration.RunAsync(cancellationToken);
        if (migratedApprovals > 0)
        {
            Log.ApprovalsMigrated(_logger, migratedApprovals);
        }
        await _publisherTrust.LoadAsync(cancellationToken);

        // Die festgehaltenen Tool-Definitionen ebenfalls vor dem Start der Upstreams — sonst
        // liefe die erste Discovery gegen einen leeren Cache und nähme jede Änderung stillschweigend
        // als Erstsichtung an.
        await _toolPins.LoadAsync(cancellationToken);

        // ADR-0016: Die aktiven Paketversionen müssen stehen, BEVOR Upstreams starten — ein
        // Upstream, der auf ein Paket zeigt, findet sonst keine Dateien und bliebe unten, obwohl
        // alles installiert ist.
        await _packages.RefreshAsync(cancellationToken);

        await BootstrapAdminIfEmptyAsync(cancellationToken);
        await EnsureUiInternalIdentityAsync(cancellationToken);
        await BootstrapUiAdminIfEmptyAsync(cancellationToken);

        var persisted = await _configStore.GetAllLatestAsync(cancellationToken);

        // WP4: Schluessel aus alten WASI-Konfigurationen einmalig in den Trust-Store uebernehmen,
        // BEVOR Upstreams starten — sonst faende ein bestehender WASI-Upstream beim ersten Start
        // nach dem Update keinen gepinnten Publisher mehr und bliebe fail-closed unten.
        await ImportConfiguredPublishersAsync(persisted, cancellationToken);

        // Ein Entzug wirkt sofort (Plan 0003, festgelegte Entscheidung 2): betroffene Upstreams
        // werden gestoppt, nicht erst beim naechsten Laden.
        _publisherTrust.Revoked += OnPublisherRevoked;

        foreach (var (id, version) in persisted)
        {
            try
            {
                await _supervisor.RestoreAsync(id, version, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.RestoreFailed(_logger, ex, version.Config.Slug);
            }
        }

        Log.Started(_logger, persisted.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _publisherTrust.Revoked -= OnPublisherRevoked;
        await _supervisor.DisposeAsync();
    }

    /// <summary>
    /// Uebernimmt <c>Wasi.PinnedPublishers</c> aus vorhandenen Konfigurationen einmalig in den
    /// Trust-Store (WP4). Danach ist der Store die einzige Vertrauensquelle; das Config-Feld wird
    /// beim Laden nicht mehr gelesen. Nur tatsaechlich neu aufgenommene Schluessel werden
    /// auditiert — sonst stuende bei jedem Start dieselbe Zeile im Log.
    /// </summary>
    private async Task ImportConfiguredPublishersAsync(
        IReadOnlyDictionary<ServerId, UpstreamConfigVersion> persisted, CancellationToken ct)
    {
        var configured = persisted.Values
            .Where(version => version.Config.Wasi is not null)
            .SelectMany(version => (version.Config.Wasi!.PinnedPublishers ?? [])
                .Select(key => (PublicKeyBase64: key, Label: $"importiert aus '{version.Config.Slug}'")))
            .ToList();
        if (configured.Count == 0)
        {
            return;
        }

        var imported = await _publisherTrust.ImportAsync(configured, ct);
        foreach (var key in imported)
        {
            _audit.Record(new AuditEvent(
                DateTimeOffset.UtcNow, Caller: null, CallOrigin.System, AuditEventKind.ConfigChanged,
                Server: null, Tool: null, Status: null, RedactedArguments: null,
                RequestBytes: null, ResponseBytes: null, Duration: null,
                Detail: $"Publisher-Schluessel {key.KeyId} aus der Upstream-Konfiguration in den Trust-Store uebernommen ({key.Label})."));
            Log.PublisherImported(_logger, key.KeyId, key.Label);
        }
    }

    /// <summary>
    /// Stoppt jeden Upstream, dessen geladenes Component von dem entzogenen Publisher signiert
    /// war. Laeuft bewusst als Fire-and-Forget: Der Entzug selbst darf nicht daran haengen, wie
    /// schnell ein Kindprozess beendet ist.
    /// </summary>
    private void OnPublisherRevoked(object? sender, PublisherRevokedEventArgs args)
        => _ = Task.Run(async () =>
        {
            foreach (var status in _supervisor.Statuses)
            {
                if (_supervisor.GetConnection(status.Id) is not ISignedUpstreamConnection signed
                    || !string.Equals(signed.PublisherKeyId, args.KeyId, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    await _supervisor.RemoveAsync(status.Id, DrainPolicy.Immediate, CancellationToken.None);
                    _audit.Record(new AuditEvent(
                        DateTimeOffset.UtcNow, Caller: null, CallOrigin.System, AuditEventKind.ServerLifecycle,
                        Server: status.Id, Tool: null, Status: null, RedactedArguments: null,
                        RequestBytes: null, ResponseBytes: null, Duration: null,
                        Detail: $"Upstream '{status.Slug}' gestoppt: Publisher {args.KeyId} wurde entzogen."));
                    Log.RevokedUpstreamStopped(_logger, status.Slug, args.KeyId);
                }
                catch (Exception ex)
                {
                    Log.RevokeStopFailed(_logger, ex, status.Slug);
                }
            }
        });

    private async Task BootstrapAdminIfEmptyAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.Identities.AnyAsync(ct))
        {
            return;
        }

        var role = new Role(RoleId.New(), "bootstrap-admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        var identity = new Identity(IdentityId.New(), "bootstrap-admin", IdentityKind.Agent, [role.Id]);
        await _rbacStore.UpsertRoleAsync(role, ct);
        await _rbacStore.UpsertIdentityAsync(identity, ct);
        var key = await _apiKeys.IssueAsync(identity.Id, "bootstrap", expiresAt: null, ct);

        // Bewusste Ausnahme von DON'T Nr. 2: ohne diesen einmaligen Klartext-Key wäre eine
        // frische Instanz unbenutzbar (Henne-Ei). Der Key erscheint nur bei leerer DB, genau einmal.
        Log.BootstrapKey(_logger, key.PlaintextKey);
    }

    /// <summary>Sorgt für die Agenten-Identität, unter der UI-Test-Aufrufe laufen (Global-Grant), und cacht ihre Id.</summary>
    private async Task EnsureUiInternalIdentityAsync(CancellationToken ct)
    {
        var existing = (await _rbacStore.ListIdentitiesAsync(ct))
            .FirstOrDefault(i => i.Name == UiInternalIdentityName);
        if (existing is not null)
        {
            _uiInternal.Value = existing.Id;
            return;
        }

        var role = new Role(RoleId.New(), "ui-internal-admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        var identity = new Identity(IdentityId.New(), UiInternalIdentityName, IdentityKind.User, [role.Id]);
        await _rbacStore.UpsertRoleAsync(role, ct);
        await _rbacStore.UpsertIdentityAsync(identity, ct);
        _uiInternal.Value = identity.Id;
    }

    private async Task BootstrapUiAdminIfEmptyAsync(CancellationToken ct)
    {
        if (await _uiUsers.AnyExistAsync(ct))
        {
            return;
        }

        var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
            .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');
        await _uiUsers.CreateAsync("admin", password, UiRole.Admin, ct);

        // Wie beim Bootstrap-API-Key: ohne diese einmalige Klartext-Ausgabe käme niemand in die UI.
        Log.BootstrapUiPassword(_logger, password);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Gateway gestartet, {Count} persistierte Upstream-Server wiederhergestellt.")]
        public static partial void Started(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Restore von Upstream {Slug} fehlgeschlagen — Server bleibt inaktiv.")]
        public static partial void RestoreFailed(ILogger logger, Exception ex, string slug);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "{Count} Freigabe-Anfragen in das Task-Modell übernommen (ADR-0019).")]
        public static partial void ApprovalsMigrated(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "ERSTSTART: Bootstrap-Admin (Agent) angelegt. API-Key (wird NIE wieder angezeigt): {Key}")]
        public static partial void BootstrapKey(ILogger logger, string key);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "ERSTSTART: UI-Admin 'admin' angelegt. Passwort (wird NIE wieder angezeigt): {Password}")]
        public static partial void BootstrapUiPassword(ILogger logger, string password);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Publisher-Schlüssel {KeyId} aus der Upstream-Konfiguration übernommen ({Label}).")]
        public static partial void PublisherImported(ILogger logger, string keyId, string label);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug} gestoppt — Publisher {KeyId} wurde entzogen.")]
        public static partial void RevokedUpstreamStopped(ILogger logger, string slug, string keyId);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug} liess sich nach dem Entzug seines Publishers nicht stoppen.")]
        public static partial void RevokeStopFailed(ILogger logger, Exception ex, string slug);
    }
}

/// <summary>Betreibt den Audit-Batch-Writer; beim Stop wird der Channel vollständig gedraint (ADR-0007).</summary>
public sealed class AuditWriterService : BackgroundService
{
    private readonly AuditBatchWriter _writer;
    private readonly ChannelAuditSink _sink;

    public AuditWriterService(AuditBatchWriter writer, ChannelAuditSink sink)
    {
        _writer = writer;
        _sink = sink;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _writer.RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sink.Complete();
        await base.StopAsync(cancellationToken);
    }
}

public sealed class AuditRetentionService : BackgroundService
{
    private readonly AuditRetentionJob _job;

    public AuditRetentionService(AuditRetentionJob job) => _job = job;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _job.RunAsync(stoppingToken);
}

/// <summary>
/// Setzt faellige Vorgaenge periodisch auf `expired` (ADR-0019). Ohne diesen Dienst blieben sie in
/// der Liste als offen stehen, obwohl ihre Frist verstrichen ist.
/// </summary>
public sealed class TaskExpiryService : BackgroundService
{
    private readonly TaskExpiryJob _job;

    public TaskExpiryService(TaskExpiryJob job) => _job = job;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _job.RunAsync(stoppingToken);
}

/// <summary>Übersetzt Katalog-Änderungen (Server/Inventar/RBAC) in tools/list_changed an alle Sessions (FR-07).</summary>
public sealed class CatalogNotificationService : IHostedService
{
    private readonly IToolCatalog _catalog;
    private readonly McpSessionRegistry _registry;
    private EventHandler<CatalogChangedEventArgs>? _handler;

    public CatalogNotificationService(IToolCatalog catalog, McpSessionRegistry registry)
    {
        _catalog = catalog;
        _registry = registry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _handler = (_, _) => _ = _registry.NotifyToolListChangedAsync(CancellationToken.None);
        _catalog.Changed += _handler;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_handler is not null)
        {
            _catalog.Changed -= _handler;
        }

        return Task.CompletedTask;
    }
}
