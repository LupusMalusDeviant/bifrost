using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using McpMcp.Abstractions;
using McpMcp.Core.Audit;
using McpMcp.Core.Catalog;
using McpMcp.Core.Guardrails;
using McpMcp.Core.Invocation;
using McpMcp.Core.Rbac;
using McpMcp.Core.Upstreams;
using McpMcp.Persistence;
using McpMcp.Persistence.Audit;
using McpMcp.Server;
using McpMcp.Upstream;
using McpMcp.Web;
using McpMcp.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// Container-Healthcheck (chiseled-Image hat kein curl): als separater Prozess gegen den laufenden Server.
if (args.Contains("--healthcheck"))
{
    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(':').LastOrDefault()?.TrimEnd('/') ?? "8080";
        var resp = await probe.GetAsync($"http://localhost:{port}/healthz");
        return resp.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

// ── Logging (NFR-07: strukturierte Logs) ─────────────────────────────────────
// JSON ist der Default, damit Container-Logs ohne Zusatzkonfiguration von jedem
// Log-Aggregator geparst werden können. Für die lokale Entwicklung ist der lesbare
// Textformatter angenehmer — dort bleibt es beim Default von CreateBuilder.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        o.UseUtcTimestamp = true;
    });
}

// ── Konfiguration (NFR-05: Env-Vars + Volume) ────────────────────────────────
var dataDir = builder.Configuration["MCPMCP_DATA_DIR"] ?? "data";
Directory.CreateDirectory(dataDir);
var dbProvider = builder.Configuration["MCPMCP_DB_PROVIDER"] ?? "sqlite";
var connectionString = builder.Configuration["MCPMCP_DB_CONNECTION"]
    ?? $"Data Source={Path.Combine(dataDir, "mcpmcp.db")}";

// ── Persistenz & Schutz (ADR-0007, NFR-04) ───────────────────────────────────
// Der Key-Ring entschlüsselt die at-rest verschlüsselten Upstream-Credentials. Ohne Zusatzschutz
// liegt er im Klartext neben der DB (dokumentiertes Restrisiko). Optional per X509-Zertifikat
// schützen — bewusst zertifikatsbasiert statt Cloud-KMS, damit es self-hosted funktioniert (WP8.1).
var keyRing = builder.Services.AddDataProtection()
    .SetApplicationName("MCPMCP")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

var keyCertPath = builder.Configuration["MCPMCP_KEYRING_CERT_PATH"];
var keyRingProtected = false;
if (!string.IsNullOrWhiteSpace(keyCertPath))
{
    var certPassword = builder.Configuration["MCPMCP_KEYRING_CERT_PASSWORD"];
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(keyCertPath, certPassword);
    keyRing.ProtectKeysWithCertificate(certificate)
        // Für Zertifikatswechsel: mit dem alten Zertifikat verschlüsselte Keys bleiben lesbar,
        // solange es hier weiterhin angegeben wird.
        .UnprotectKeysWithAnyCertificate(certificate);
    keyRingProtected = true;
}
builder.Services.AddDbContextFactory<McpMcpDbContext>(options =>
    options.UseMcpMcpDatabase(dbProvider, connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
// FR-25: Aufbewahrungsdauer ist Betriebsentscheidung (Plattenbedarf vs. Nachvollziehbarkeit),
// darf also nicht im Code festgenagelt sein. Ungültige/fehlende Angabe fällt auf den Default zurück.
var retentionDays = int.TryParse(
    Environment.GetEnvironmentVariable("MCPMCP_AUDIT_RETENTION_DAYS"),
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDays) && parsedDays > 0
    ? parsedDays
    : 30;
var auditMode = string.Equals(
    Environment.GetEnvironmentVariable("MCPMCP_AUDIT_MODE"),
    "compliance",
    StringComparison.OrdinalIgnoreCase)
    ? AuditDeliveryMode.Compliance
    : AuditDeliveryMode.BestEffort;
builder.Services.AddSingleton(new PersistenceOptions
{
    AuditRetention = TimeSpan.FromDays(retentionDays),
    AuditMode = auditMode,
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GatewayIdentity>();

// ── RBAC (ADR-0006) ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<InMemoryRbacDirectory>();
builder.Services.AddSingleton<IMutableRbacDirectory>(sp => sp.GetRequiredService<InMemoryRbacDirectory>());
builder.Services.AddSingleton<IRbacDirectory>(sp => sp.GetRequiredService<InMemoryRbacDirectory>());
builder.Services.AddSingleton<IAuthorizationService, AuthorizationService>();
builder.Services.AddSingleton<IRateLimiter, TokenBucketRateLimiter>();
builder.Services.AddSingleton<PersistentRbacStore>();
builder.Services.AddSingleton<IRbacManagement>(sp => sp.GetRequiredService<PersistentRbacStore>());
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<IApiKeyService>(sp => sp.GetRequiredService<ApiKeyService>());
builder.Services.AddSingleton<IApiKeyValidator>(sp => sp.GetRequiredService<ApiKeyService>());
builder.Services.AddSingleton<IUiUserService, UiUserService>();
builder.Services.AddSingleton<IAssetStore, EfAssetStore>();
builder.Services.AddSingleton<McpMcp.Web.UiInternalIdentity>();

// ── Upstreams & Katalog (ADR-0005, WP2) ──────────────────────────────────────
builder.Services.AddSingleton<IUpstreamConnector, StdioUpstreamConnector>();
builder.Services.AddSingleton<IUpstreamConnector, StreamableHttpUpstreamConnector>();
builder.Services.AddSingleton<IUpstreamConnector, McpMcp.Upstream.OpenApi.OpenApiUpstreamConnector>();
builder.Services.AddSingleton<IUpstreamConnector, McpMcp.Upstream.Cli.CliUpstreamConnector>(); // ADR-0014
// Der WASI-Connector holt die gepinnten Publisher aus dem Trust-Store (WP4) und schreibt den
// Grant-Audit-Datensatz jedes Loads in den Audit-Pfad.
builder.Services.AddSingleton<IUpstreamConnector>(sp => new McpMcp.Upstream.Wasi.WasiRuntimeConnector(
    sp.GetRequiredService<IPublisherTrustStore>(), sp.GetRequiredService<IAuditSink>())); // ADR-0020
builder.Services.AddSingleton<IUpstreamConfigStore, EfUpstreamConfigStore>();
builder.Services.AddSingleton<IUpstreamConnectionTester, UpstreamConnectionTester>();
builder.Services.AddSingleton(new SupervisorOptions());
builder.Services.AddSingleton<UpstreamSupervisor>(sp => new UpstreamSupervisor(
    sp.GetServices<IUpstreamConnector>(),
    sp.GetRequiredService<IUpstreamConfigStore>(),
    sp.GetRequiredService<SupervisorOptions>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<UpstreamSupervisor>>(),
    sp.GetRequiredService<IAuditSink>()));
builder.Services.AddSingleton<IUpstreamSupervisor>(sp => sp.GetRequiredService<UpstreamSupervisor>());
builder.Services.AddSingleton<ToolDescriptionOverrideStore>();
builder.Services.AddSingleton<IToolDescriptionOverrides>(sp => sp.GetRequiredService<ToolDescriptionOverrideStore>());
builder.Services.AddSingleton<ToolCatalog>(sp => new ToolCatalog(
    sp.GetRequiredService<IUpstreamSupervisor>(),
    sp.GetRequiredService<IAuthorizationService>(),
    sp.GetRequiredService<IRbacDirectory>(),
    sp.GetRequiredService<IToolDescriptionOverrides>(),
    sp.GetRequiredService<ILogger<ToolCatalog>>()));
builder.Services.AddSingleton<IToolCatalog>(sp => sp.GetRequiredService<ToolCatalog>());

// ── Audit (ADR-0007) & Invocation (ADR-0008) ─────────────────────────────────
builder.Services.AddSingleton<ChannelAuditSink>(sp =>
{
    var options = sp.GetRequiredService<PersistenceOptions>();
    return new ChannelAuditSink(options.AuditChannelCapacity, options.AuditMode);
});
builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<ChannelAuditSink>());
builder.Services.AddSingleton<AuditBatchWriter>();
builder.Services.AddSingleton<IAuditQuery, EfAuditQuery>();
builder.Services.AddSingleton<AuditRetentionJob>();
// ── Guardrail: Secret-Erkennung (ADR-0011) ───────────────────────────────────
builder.Services.AddSingleton(new GuardOptions
{
    Enabled = Environment.GetEnvironmentVariable("MCPMCP_GUARD_ENABLED") is not ("0" or "false"),
    MaxScanChars = int.TryParse(
        Environment.GetEnvironmentVariable("MCPMCP_GUARD_MAX_SCAN_CHARS"),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var scanChars) && scanChars > 0
        ? scanChars
        : 256 * 1024,
    // Freitext-Regex ist eine Vertrauensentscheidung, keine technische Absicherung (ADR-0011, E2):
    // .NET bietet laut Microsoft keine Sicherheitsgrenze gegen bösartige Muster. Default aus.
    AllowCustomPatterns = Environment.GetEnvironmentVariable("MCPMCP_GUARD_ALLOW_CUSTOM_PATTERNS") is "1" or "true",
});
builder.Services.AddSingleton<GuardRuleStore>();
builder.Services.AddSingleton<IGuardRuleStore>(sp => sp.GetRequiredService<GuardRuleStore>());
builder.Services.AddSingleton(sp =>
{
    var store = sp.GetRequiredService<GuardRuleStore>();
    var guard = new SecretGuard(store.All, sp.GetRequiredService<GuardOptions>());
    // Hot-swappable: Regeländerungen bauen die Regex neu, ohne Neustart.
    store.Changed += (_, _) => guard.Reload(store.All);
    return guard;
});
builder.Services.AddSingleton<IContentGuard>(sp => sp.GetRequiredService<SecretGuard>());

// ── Publisher-Trust-Store für WASI-Components (Plan 0003/WP4, ADR-0020) ──────
builder.Services.AddSingleton<PublisherTrustStore>(sp => new PublisherTrustStore(
    sp.GetRequiredService<IDbContextFactory<McpMcpDbContext>>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IPublisherTrustStore>(sp => sp.GetRequiredService<PublisherTrustStore>());

// ── Freigabe-Flows (FR-32, ADR-0012) ─────────────────────────────────────────
builder.Services.AddSingleton<ApprovalPolicyStore>();
builder.Services.AddSingleton<IApprovalPolicy>(sp => sp.GetRequiredService<ApprovalPolicyStore>());

// ── Webhook-Trigger (FR-20, ADR-0013) ────────────────────────────────────────
builder.Services.AddSingleton<IWebhookStore>(sp => new WebhookStore(
    sp.GetRequiredService<IDbContextFactory<McpMcpDbContext>>(),
    sp.GetRequiredService<IDataProtectionProvider>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IApprovalStore>(sp => new ApprovalStore(
    sp.GetRequiredService<IDbContextFactory<McpMcpDbContext>>(), sp.GetRequiredService<TimeProvider>()));
// ADR-0019: Der Task-Store haelt langlaufende Vorgaenge. Die Freigabe-Queue geht darin auf; solange
// die Ablösung nicht vollzogen ist, stehen beide nebeneinander, und ApprovalStore bleibt der aktive
// Weg auf dem heissen Pfad.
builder.Services.AddSingleton<ITaskStore>(sp => new TaskStore(
    sp.GetRequiredService<IDbContextFactory<McpMcpDbContext>>(), sp.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton<RedactionRuleStore>();
builder.Services.AddSingleton<IRedactionRules>(sp => sp.GetRequiredService<RedactionRuleStore>());
builder.Services.AddSingleton<RedactionService>(sp => new RedactionService(sp.GetRequiredService<IRedactionRules>()));
builder.Services.AddSingleton<IRedactionService>(sp => sp.GetRequiredService<RedactionService>());

// FR-16: Kürzung übergroßer Ergebnisse. Default aus — sie ist verlustbehaftet, das soll niemand
// unbemerkt bekommen. Wer sie einschaltet, begrenzt damit den Token-Hunger einzelner Tools.
var maxResultChars = int.TryParse(
    Environment.GetEnvironmentVariable("MCPMCP_MAX_RESULT_CHARS"),
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedChars) && parsedChars > 0
    ? parsedChars
    : 0;
builder.Services.AddSingleton(new ResultCompressionOptions(maxResultChars));

// FR-24: Ergebnis-Payloads im Audit sind ausdrücklich zu aktivieren, nie Default (NFR-04).
builder.Services.AddSingleton(new AuditOptions(
    CaptureResponsePayloads: Environment.GetEnvironmentVariable("MCPMCP_AUDIT_DEBUG_PAYLOADS") is "1" or "true"));
builder.Services.AddSingleton<IToolInvoker, ToolInvoker>();
builder.Services.AddSingleton<MetaToolService>();

// ── MCP-Endpoint (WP4.2) + REST-Fassade (WP5) ────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<McpSessionRegistry>();
builder.Services.AddSingleton<IActiveSessionSource>(sp => sp.GetRequiredService<McpSessionRegistry>());
builder.Services.AddSingleton<OpenApiDocumentGenerator>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "mcp-mcp",
            Version = McpMcpProductInfo.Version,
        };
        options.ServerInstructions =
            "MCP-MCP gateway: aggregated tools from multiple upstream servers. " +
            "Use search_tools to discover capabilities, describe_tool for schemas, invoke_tool to call.";
    })
    .WithHttpTransport(options =>
    {
        // MCPEXP002: RunSessionHandler ist als experimentell markiert, aber der einzige
        // dokumentierte Hook für Session-Lifecycle (Registry für tools/list_changed, FR-07).
        // SDK ist per CPM auf 1.4.1 gepinnt — API-Drift trifft uns nur bei bewusstem Upgrade.
#pragma warning disable MCPEXP002
        options.RunSessionHandler = async (httpContext, server, ct) =>
        {
            var registry = httpContext.RequestServices.GetRequiredService<McpSessionRegistry>();
            var identity = (IdentityId)httpContext.Items[ApiKeyAuthMiddleware.IdentityItemKey]!;
            registry.Register(server, identity);
            try
            {
                await server.RunAsync(ct);
            }
            finally
            {
                registry.Unregister(server);
            }
        };
#pragma warning restore MCPEXP002
    })
    .WithListToolsHandler(GatewayMcpHandlers.ListToolsAsync)
    .WithCallToolHandler(GatewayMcpHandlers.CallToolAsync)
    .WithListResourcesHandler(GatewayMcpHandlers.ListResourcesAsync)
    .WithReadResourceHandler(GatewayMcpHandlers.ReadResourceAsync)
    .WithListPromptsHandler(GatewayMcpHandlers.ListPromptsAsync)
    .WithGetPromptHandler(GatewayMcpHandlers.GetPromptAsync);

// ── Metriken-Export (FR-26) ──────────────────────────────────────────────────
// Der Invoker misst Calls, Fehler und Latenzen; hier gehen sie nach draußen. Export nur, wenn ein
// OTLP-Ziel konfiguriert ist — sonst würde der Exporter dauerhaft gegen localhost:4317 laufen und
// Fehler loggen. Prometheus-Nutzer scrapen den OTel-Collector (eigener Prometheus-Exporter ist
// nicht stabil veröffentlicht).
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            "mcp-mcp", serviceVersion: McpMcpProductInfo.Version))
        .WithMetrics(metrics => metrics
            .AddMeter(ToolInvoker.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter());
}

// ── Web-UI (WP6, Blazor Interactive Server, ADR-0004) ────────────────────────
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "mcpmcp-ui";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Produktion (hinter TLS-Proxy, NFR-04): Cookie nur über HTTPS. Dev/Tests laufen über HTTP.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(UiPolicies.Authenticated, p => p.RequireAuthenticatedUser())
    .AddPolicy(UiPolicies.Operator, p => p.RequireClaim(
        UiPolicies.RoleClaim, nameof(UiRole.Operator), nameof(UiRole.Admin)))
    .AddPolicy(UiPolicies.Admin, p => p.RequireClaim(
        UiPolicies.RoleClaim, nameof(UiRole.Admin)));

// ── Lifecycle ────────────────────────────────────────────────────────────────
builder.Services.AddHostedService<GatewayStartupService>();
builder.Services.AddHostedService<AuditWriterService>();
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHostedService<CatalogNotificationService>();

var app = builder.Build();

if (!keyRingProtected)
{
    // CA1848: einmaliger Start-Log, LoggerMessage-Codegen brächte hier nichts.
#pragma warning disable CA1848
    app.Logger.LogWarning(
        "DataProtection-Key-Ring liegt ungeschützt unter {Path}. Er entschlüsselt die gespeicherten " +
        "Upstream-Credentials — Verzeichnis restriktiv halten oder MCPMCP_KEYRING_CERT_PATH setzen.",
        Path.Combine(dataDir, "keys"));
#pragma warning restore CA1848
}

// Recovery-Kommandos (WP8.4) laufen ohne Gateway-Start und beenden den Prozess.
if (AdminCommands.IsAdminCommand(args))
{
    return await AdminCommands.RunAsync(app, args);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.UseMiddleware<ApiKeyAuthMiddleware>();
app.MapMcp("/mcp");
app.MapGatewayApi();
app.MapAuthEndpoints();
app.MapWebhookEndpoint();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", async (
    IDbContextFactory<McpMcpDbContext> factory,
    IUpstreamSupervisor supervisor,
    ChannelAuditSink audit,
    CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var dbOk = await db.Database.CanConnectAsync(ct);
    var statuses = supervisor.Statuses;
    // Anonymer Endpoint: nur aggregierte Zahlen, keine Slugs/Topologie (Info-Disclosure vermeiden).
    var ready = dbOk && (audit.Mode != AuditDeliveryMode.Compliance || audit.IsHealthy);
    return ready
        ? Results.Ok(new
        {
            status = "ready",
            upstreamsTotal = statuses.Count,
            upstreamsHealthy = statuses.Count(s => s.State == UpstreamState.Healthy),
            auditMode = audit.Mode.ToString(),
            auditHealthy = audit.IsHealthy,
            auditDropped = audit.DroppedCount,
        })
        : Results.Json(new
        {
            status = dbOk ? "audit-unavailable" : "db-unreachable",
            auditMode = audit.Mode.ToString(),
            auditHealthy = audit.IsHealthy,
            auditDropped = audit.DroppedCount,
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
return 0;

/// <summary>Marker für WebApplicationFactory-basierte Integrationstests.</summary>
public partial class Program
{
}
