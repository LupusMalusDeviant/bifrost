using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Core.Audit;
using Bifrost.Core.Execution;
using Bifrost.Core.Catalog;
using Bifrost.Core.Guardrails;
using Bifrost.Core.Invocation;
using Bifrost.Core.Rbac;
using Bifrost.Core.Upstreams;
using Bifrost.Core.Packaging;
using Bifrost.Persistence;
using Bifrost.Persistence.Audit;
using Bifrost.Server;
using Bifrost.Server.Execution;
using Bifrost.Server.Operations;
using Bifrost.Upstream;
using Bifrost.Web;
using Bifrost.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

// Alt benannte Umgebungsvariablen uebernehmen, BEVOR die Konfiguration gebaut wird — sonst
// startet eine bestehende Installation nach der Umbenennung lautlos auf Vorgabewerten
// (siehe LegacyEnvironment). Die Meldung dazu kommt weiter unten, sobald es einen Logger gibt.
var adoptedLegacyVariables = LegacyEnvironment.Adopt();

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
var dataDir = builder.Configuration["BIFROST_DATA_DIR"] ?? "data";
Directory.CreateDirectory(dataDir);
var dbProvider = builder.Configuration["BIFROST_DB_PROVIDER"] ?? "sqlite";
// Liegt im Datenverzeichnis noch eine Datenbank unter dem alten Namen, gewinnt sie — sonst
// entstuende daneben eine leere neue, und der Gateway meldete "bereit" ohne einen einzigen Server.
var connectionString = builder.Configuration["BIFROST_DB_CONNECTION"]
    ?? $"Data Source={LegacyEnvironment.ResolveSqliteFile(dataDir)}";

// config/instance.json: die stabile Kennung dieser Installation (M2, WP2.7). Sie entsteht hier und
// nicht im Backup — eine Sicherung veraendert die Instanz nicht, die sie sichert. Ohne die Datei
// traegt jedes Archivmanifest eine leere instanceId, und ein Restore kann nicht sagen, ob das
// Archiv zu dieser Installation gehoert.
var instanceId = InstanceIdentityFile.EnsureCreated(dataDir);

// ── Persistenz & Schutz (ADR-0007, NFR-04) ───────────────────────────────────
// Der Key-Ring entschlüsselt die at-rest verschlüsselten Upstream-Credentials. Ohne Zusatzschutz
// liegt er im Klartext neben der DB (dokumentiertes Restrisiko). Optional per X509-Zertifikat
// schützen — bewusst zertifikatsbasiert statt Cloud-KMS, damit es self-hosted funktioniert (WP8.1).
var keyRing = builder.Services.AddDataProtection()
    // ── NICHT UMBENENNEN ─────────────────────────────────────────────────────────────────────
    // Der Anwendungsname geht in die Schluesselableitung ein. Er heisst weiterhin "MCPMCP",
    // obwohl das Produkt seit dem 2026-07-31 B.I.F.R.O.S.T heisst — ein neuer Name hier wuerde
    // JEDEN gespeicherten Geheimtext unlesbar machen: Upstream-Zugangsdaten, OAuth-Token,
    // Webhook-Secrets. Eine Umbenennung waere kein Schoenheitsfehler, sondern Datenverlust auf
    // jeder bestehenden Installation.
    //
    // Das ist der Preis dafuer, dass ein kryptografischer Bezeichner kein Markenname ist. Wer ihn
    // dennoch aendern will, braucht einen Migrationslauf, der alles entschluesselt und neu
    // verschluesselt — nicht eine Textersetzung.
    .SetApplicationName(CryptographicNames.DataProtectionApplication)
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

var keyCertPath = builder.Configuration["BIFROST_KEYRING_CERT_PATH"];
var keyRingProtected = false;
if (!string.IsNullOrWhiteSpace(keyCertPath))
{
    var certPassword = builder.Configuration["BIFROST_KEYRING_CERT_PASSWORD"];
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(keyCertPath, certPassword);
    keyRing.ProtectKeysWithCertificate(certificate)
        // Für Zertifikatswechsel: mit dem alten Zertifikat verschlüsselte Keys bleiben lesbar,
        // solange es hier weiterhin angegeben wird.
        .UnprotectKeysWithAnyCertificate(certificate);
    keyRingProtected = true;
}
builder.Services.AddDbContextFactory<BifrostDbContext>(options =>
    options.UseBifrostDatabase(dbProvider, connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
// FR-25: Aufbewahrungsdauer ist Betriebsentscheidung (Plattenbedarf vs. Nachvollziehbarkeit),
// darf also nicht im Code festgenagelt sein. Ungültige/fehlende Angabe fällt auf den Default zurück.
var retentionDays = int.TryParse(
    Environment.GetEnvironmentVariable("BIFROST_AUDIT_RETENTION_DAYS"),
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDays) && parsedDays > 0
    ? parsedDays
    : 30;
var auditMode = string.Equals(
    Environment.GetEnvironmentVariable("BIFROST_AUDIT_MODE"),
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
// Prueft deklarierte Skill-Verweise gegen den Bestand und die vorausgesetzten Tools gegen den
// Katalog — Letzteres kann nur der Gateway, weil nur er den Katalog kennt.
builder.Services.AddSingleton<ISkillValidator, SkillValidator>();
builder.Services.AddSingleton<Bifrost.Web.UiInternalIdentity>();

// ── Upstreams & Katalog (ADR-0005, WP2) ──────────────────────────────────────
builder.Services.AddSingleton<IUpstreamConnector, StdioUpstreamConnector>();
// ── Gateway als OAuth-Resource-Server (MCP-Autorisierung, Stufe 1) ──────────
// Nur aktiv, wenn ein Issuer konfiguriert ist. Ohne ihn bleibt alles wie bisher — API-Keys sind
// dann der einzige Weg, und der Standard nennt Autorisierung ausdruecklich optional.
var oauthResourceServer = OAuthResourceServerOptions.FromConfiguration(builder.Configuration);
if (oauthResourceServer is not null)
{
    builder.Services.AddSingleton(oauthResourceServer);
    builder.Services.AddSingleton<IOAuthTokenValidator>(sp => new OAuthTokenValidator(
        oauthResourceServer, sp.GetRequiredService<IRbacManagement>()));
}

// Upstream-OAuth: Token-Ablage verschluesselt wie jedes andere Credential (NFR-04).
builder.Services.AddSingleton<IUpstreamOAuthTokenStore, UpstreamOAuthTokenStore>();
builder.Services.AddSingleton<IUpstreamConnector>(sp => new StreamableHttpUpstreamConnector(
    sp.GetService<GatewayIdentity>(),
    sp.GetRequiredService<IUpstreamOAuthTokenStore>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IUpstreamConnector, Bifrost.Upstream.OpenApi.OpenApiUpstreamConnector>();
builder.Services.AddSingleton<IUpstreamConnector, Bifrost.Upstream.Cli.CliUpstreamConnector>(); // ADR-0014
builder.Services.AddSingleton<IUpstreamConnector, Bifrost.Upstream.OpenRpc.OpenRpcUpstreamConnector>(); // Roadmap Phase 8
// Der WASI-Connector holt die gepinnten Publisher aus dem Trust-Store (WP4) und schreibt den
// Grant-Audit-Datensatz jedes Loads in den Audit-Pfad.
builder.Services.AddSingleton<IUpstreamConnector>(sp => new Bifrost.Upstream.Wasi.WasiRuntimeConnector(
    sp.GetRequiredService<IPublisherTrustStore>(),
    sp.GetRequiredService<IAuditSink>(),
    sp.GetRequiredService<IConnectorPackageResolver>())); // ADR-0020, Pakete nach ADR-0016
builder.Services.AddSingleton<IUpstreamConfigStore, EfUpstreamConfigStore>();
// ── Ausfuehrungs-Policy (ADR-0025, WP3.1) ────────────────────────────────────
// Eine Stelle entscheidet, ob ein fremdes Programm nativ auf dem Host starten darf; jeder Startweg
// fragt dieselbe. Die Einstellung wird hier gelesen und nicht im Kern, damit die Regel pruefbar
// bleibt, ohne das Prozessumfeld eines Testlaufs anzufassen.
builder.Services.AddBifrostHostExecution(dataDir, builder.Configuration[HostExecutionSwitch.Name]);
builder.Services.AddSingleton<IUpstreamConnectionTester>(sp => new UpstreamConnectionTester(
    sp.GetServices<IUpstreamConnector>(),
    sp.GetRequiredService<IHostExecutionPolicy>()));
builder.Services.AddSingleton(new SupervisorOptions());
// Rug-Pull-Schutz: festgehaltene Tool-Definitionen. Der Supervisor prueft jede Discovery dagegen.
builder.Services.AddSingleton<ToolDefinitionPinStore>(sp => new ToolDefinitionPinStore(
    sp.GetRequiredService<IDbContextFactory<BifrostDbContext>>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IToolDefinitionPinStore>(sp => sp.GetRequiredService<ToolDefinitionPinStore>());
builder.Services.AddSingleton<UpstreamSupervisor>(sp => new UpstreamSupervisor(
    sp.GetServices<IUpstreamConnector>(),
    sp.GetRequiredService<IUpstreamConfigStore>(),
    sp.GetRequiredService<SupervisorOptions>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<UpstreamSupervisor>>(),
    sp.GetRequiredService<IAuditSink>(),
    sp.GetRequiredService<IToolDefinitionPinStore>(),
    sp.GetRequiredService<IHostExecutionPolicy>()));
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
    Enabled = Environment.GetEnvironmentVariable("BIFROST_GUARD_ENABLED") is not ("0" or "false"),
    MaxScanChars = int.TryParse(
        Environment.GetEnvironmentVariable("BIFROST_GUARD_MAX_SCAN_CHARS"),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var scanChars) && scanChars > 0
        ? scanChars
        : 256 * 1024,
    // Freitext-Regex ist eine Vertrauensentscheidung, keine technische Absicherung (ADR-0011, E2):
    // .NET bietet laut Microsoft keine Sicherheitsgrenze gegen bösartige Muster. Default aus.
    AllowCustomPatterns = Environment.GetEnvironmentVariable("BIFROST_GUARD_ALLOW_CUSTOM_PATTERNS") is "1" or "true",
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
    sp.GetRequiredService<IDbContextFactory<BifrostDbContext>>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IPublisherTrustStore>(sp => sp.GetRequiredService<PublisherTrustStore>());

// ── Connector-Pakete (ADR-0016) ──────────────────────────────────────────────
builder.Services.AddSingleton<IConnectorPackageStore, ConnectorPackageStore>();
builder.Services.AddSingleton<ConnectorPackageResolver>();
builder.Services.AddSingleton<IConnectorPackageResolver>(
    sp => sp.GetRequiredService<ConnectorPackageResolver>());
builder.Services.AddSingleton(sp => new ConnectorPackageInstaller(
    Path.Combine(dataDir, "packages"),
    sp.GetRequiredService<IConnectorPackageStore>(),
    sp.GetRequiredService<IPublisherTrustStore>(),
    // Die Probe startet den Connector wirklich — aus der Quarantäne, mit denselben Dateien, die
    // gleich aktiv werden. Ein Paket, das hier nicht antwortet, wird nie aktiv.
    WasiPackageProbe.Create(sp),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IAuditSink>(),
    sp.GetRequiredService<ILogger<ConnectorPackageInstaller>>(),
    // Damit ein Paket die Skills mitbringen kann, die erklären, wie man seinen Konnektor benutzt
    // (Material 0021-EM, Option B).
    sp.GetRequiredService<IAssetStore>()));

// ── Freigabe-Flows (FR-32, ADR-0012) ─────────────────────────────────────────
builder.Services.AddSingleton<ApprovalPolicyStore>();
builder.Services.AddSingleton<IApprovalPolicy>(sp => sp.GetRequiredService<ApprovalPolicyStore>());

// ── Webhook-Trigger (FR-20, ADR-0013) ────────────────────────────────────────
builder.Services.AddSingleton<IWebhookStore>(sp => new WebhookStore(
    sp.GetRequiredService<IDbContextFactory<BifrostDbContext>>(),
    sp.GetRequiredService<IDataProtectionProvider>(),
    sp.GetRequiredService<TimeProvider>()));
// ADR-0019: Der Task-Store haelt langlaufende Vorgaenge.
builder.Services.AddSingleton<ITaskStore>(sp => new TaskStore(
    sp.GetRequiredService<IDbContextFactory<BifrostDbContext>>(), sp.GetRequiredService<TimeProvider>()));
// Die Freigabe-Queue geht darin auf (ADR-0019, Entscheidung 1): IApprovalStore bleibt als Vertrag
// fuer Invoker, REST und UI, laeuft aber auf der Task-Tabelle. Eine Tabelle, eine Liste.
builder.Services.AddSingleton<IApprovalStore>(sp => new TaskBackedApprovalStore(
    sp.GetRequiredService<ITaskStore>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ApprovalToTaskMigration>(sp => new ApprovalToTaskMigration(
    sp.GetRequiredService<IDbContextFactory<BifrostDbContext>>()));
builder.Services.AddSingleton<TaskExpiryJob>(sp => new TaskExpiryJob(
    sp.GetRequiredService<ITaskStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<TaskExpiryJob>>()));

builder.Services.AddSingleton<RedactionRuleStore>();
builder.Services.AddSingleton<IRedactionRules>(sp => sp.GetRequiredService<RedactionRuleStore>());
builder.Services.AddSingleton<RedactionService>(sp => new RedactionService(sp.GetRequiredService<IRedactionRules>()));
builder.Services.AddSingleton<IRedactionService>(sp => sp.GetRequiredService<RedactionService>());

// FR-16: Kürzung übergroßer Ergebnisse. Default aus — sie ist verlustbehaftet, das soll niemand
// unbemerkt bekommen. Wer sie einschaltet, begrenzt damit den Token-Hunger einzelner Tools.
var maxResultChars = int.TryParse(
    Environment.GetEnvironmentVariable("BIFROST_MAX_RESULT_CHARS"),
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedChars) && parsedChars > 0
    ? parsedChars
    : 0;
builder.Services.AddSingleton(new ResultCompressionOptions(maxResultChars));

// FR-24: Ergebnis-Payloads im Audit sind ausdrücklich zu aktivieren, nie Default (NFR-04).
builder.Services.AddSingleton(new AuditOptions(
    CaptureResponsePayloads: Environment.GetEnvironmentVariable("BIFROST_AUDIT_DEBUG_PAYLOADS") is "1" or "true"));
builder.Services.AddSingleton<IToolInvoker, ToolInvoker>();
// Explizit statt per Konvention: Der Asset-Store ist ein OPTIONALER Konstruktorparameter, und
// den fuellt der Container nicht von selbst — ohne diese Zeile gaebe es list_skills/read_skill,
// die immer "keine Skill-Ausliefering eingebunden" antworten.
builder.Services.AddSingleton(sp => new MetaToolService(
    sp.GetRequiredService<IToolCatalog>(),
    sp.GetRequiredService<IAuthorizationService>(),
    sp.GetRequiredService<IToolInvoker>(),
    sp.GetRequiredService<IAuditSink>(),
    sp.GetRequiredService<IRedactionService>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IAssetStore>(),
    // Ohne die Politik wuesste invoke_sensitive_tool nicht, WELCHE Werkzeuge scharf sind — die
    // Weiche fiele auf "nichts ist scharf" zurueck und beide Tueren staenden fuer alles offen.
    sp.GetRequiredService<IApprovalPolicy>()));

// ── MCP-Endpoint (WP4.2) + REST-Fassade (WP5) ────────────────────────────────
builder.Services.AddHttpContextAccessor();

// ── Sessionlos oder mit Sitzung? (Spec-Revision 2026-07-28, SEP-2567) ────────
// Die Revision hat den Initialize-Handshake und 'Mcp-Session-Id' ersatzlos gestrichen: Jede Anfrage
// steht fuer sich. Das SDK laesst beides zu, aber NICHT gleichzeitig, und die Wahl ist folgenreich:
//
//   Stateless (Vorgabe): Wir sprechen 2026-07-28 wirklich. Aeltere Clients laufen weiter — das SDK
//       bedient ihren Initialize-Handshake unveraendert. Sie verlieren allerdings zwei Dinge, die
//       eine stehende Sitzung voraussetzen: die Rueckfrage im laufenden Aufruf (fuer sie bleibt die
//       Freigabe-Warteschlange) und den 'tools/list_changed'-Anstoss (dafuer traegt jede Liste jetzt
//       eine Cache-Frist).
//   Stateful: Alles bleibt wie bisher — aber ein Client mit 2026-07-28 wird vom SDK mit
//       '-32022 UnsupportedProtocolVersion' abgewiesen und handelt daraufhin 2025-11-25 aus. Wir
//       liefen dann auf dem neuen SDK und sprächen weiter die alte Revision.
//
// Der Schalter ist da, weil ein Betreiber mit ausschliesslich alten Clients die zweite Wahl treffen
// koennen muss, ohne auf ein SDK von gestern zurueckzugehen.
var statelessMcp = builder.Configuration["BIFROST_MCP_STATELESS"] is not ("0" or "false");

// Cache-Frist der Listen (SEP-2549). Im stateless Betrieb ist sie der einzige Weg, auf dem ein
// angeschlossener Agent ueberhaupt von einem neuen Werkzeug erfaehrt.
var listTtlSeconds = int.TryParse(
    builder.Configuration["BIFROST_MCP_LIST_TTL_SECONDS"],
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTtl) && parsedTtl >= 0
    ? parsedTtl
    : (int)McpCacheOptions.Default.ListTimeToLive.TotalSeconds;
builder.Services.AddSingleton(new McpCacheOptions(TimeSpan.FromSeconds(listTtlSeconds)));

builder.Services.AddSingleton(sp => new McpSessionRegistry(
    sp.GetRequiredService<TimeProvider>(), statelessMcp));
builder.Services.AddSingleton<IActiveSessionSource>(sp => sp.GetRequiredService<McpSessionRegistry>());
builder.Services.AddSingleton<OpenApiDocumentGenerator>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "bifrost",
            Version = BifrostProductInfo.Version,
        };
        // Der einzige Text, den jeder angeschlossene Agent einmal je Sitzung sieht. Kurz halten:
        // Er kostet Kontext in JEDER Sitzung, und lang macht ihn nicht wirksamer.
        options.ServerInstructions =
            "B.I.F.R.O.S.T gateway: aggregated tools from multiple upstream servers. " +
            "Use search_tools to discover capabilities, describe_tool for schemas, invoke_tool to call. " +
            "This gateway also publishes shared skills — instructions, playbooks and conventions " +
            "maintained centrally for all agents. Call list_skills when a task might have an " +
            "established procedure here; it returns names only, read_skill fetches one.";
    })
    .WithHttpTransport(options =>
    {
        // Ausdruecklich gesetzt, nicht dem Default ueberlassen: Der Default hat sich mit SDK 2.0
        // von 'false' auf 'true' gedreht. Eine so folgenreiche Umstellung darf nicht daran haengen,
        // welche Paketversion gerade aufgeloest wurde.
        options.Stateless = statelessMcp;

        // MCPEXP002: RunSessionHandler ist als experimentell markiert, aber der einzige
        // dokumentierte Hook für Session-Lifecycle (Registry für tools/list_changed, FR-07).
        // Im stateless Betrieb ruft das SDK ihn JE ANFRAGE auf — die Registry weiss das und zaehlt
        // dann Lebenszeichen statt Sitzungen.
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

// ── Telemetrie-Export (FR-26) ────────────────────────────────────────────────
// Der Invoker misst Calls, Fehler und Latenzen und öffnet je Aufruf einen Span; hier geht beides
// nach draußen. Export nur, wenn ein OTLP-Ziel konfiguriert ist — sonst würde der Exporter dauerhaft
// gegen localhost:4317 laufen und Fehler loggen. Prometheus-Nutzer scrapen den OTel-Collector
// (eigener Prometheus-Exporter ist nicht stabil veröffentlicht).
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            "bifrost", serviceVersion: BifrostProductInfo.Version))
        .WithMetrics(metrics => metrics
            .AddMeter(ToolInvoker.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter())
        // Traces zeigen, was die Metriken nur aggregiert beantworten: wo die Zeit eines einzelnen
        // Aufrufs geblieben ist. Der Kind-Span um den Upstream-Aufruf trennt Gateway-Anteil vom
        // Fremdanteil. Spans tragen bewusst KEINE Argumente oder Ergebnisse — das Audit-Log ist
        // redigiert, ein Telemetrie-Backend ist es nicht.
        .WithTracing(tracing => tracing
            .AddSource(ToolInvoker.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter());

    // Health- und Readiness-Probes laufen im Sekundentakt und sagen über einen Tool-Aufruf nichts
    // aus; ohne Filter fluten sie den Trace-Strom und machen ihn unbrauchbar.
    builder.Services.Configure<OpenTelemetry.Instrumentation.AspNetCore.AspNetCoreTraceInstrumentationOptions>(
        options => options.Filter = context =>
            !context.Request.Path.StartsWithSegments("/healthz")
            && !context.Request.Path.StartsWithSegments("/readyz"));
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
        options.Cookie.Name = "bifrost-ui";
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

// ── Betriebsdienste: Sicherung, Wiederherstellung, Diagnose, Konfigurationsexport ────────────
// (M2/ADR-0024, WP2.7). Erst diese Zeile macht die in M2 gebauten Dienste erreichbar — und sie
// setzt den Haken aus ADR-0024 E7: vor einer schemaaendernden Migration entsteht bei SQLite
// automatisch eine Sicherung, ohne die nicht migriert wird.
builder.Services.AddBifrostOperations(dataDir, dbProvider, connectionString);

// ── Lifecycle ────────────────────────────────────────────────────────────────
builder.Services.AddHostedService<GatewayStartupService>();
builder.Services.AddHostedService<AuditWriterService>();
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHostedService<TaskExpiryService>();
builder.Services.AddHostedService<CatalogNotificationService>();

var app = builder.Build();

if (adoptedLegacyVariables.Count > 0)
{
    // Eine Warnung, keine stille Uebernahme: Sonst laeuft eine Installation jahrelang auf Namen,
    // die in keiner Doku mehr stehen, und der naechste Mensch sucht die Einstellung vergeblich.
#pragma warning disable CA1848
    app.Logger.LogWarning(
        "Umgebungsvariablen mit altem Namen uebernommen: {Variables}. Der Praefix heisst seit der "
        + "Umbenennung BIFROST_ (frueher MCPMCP_); bitte umstellen. Die alten Namen werden noch "
        + "gelesen, sind aber nicht mehr dokumentiert.",
        string.Join(", ", adoptedLegacyVariables));
#pragma warning restore CA1848
}

// Die Instanz-Id einmal beim Start nennen: Sie steht im Manifest jeder Sicherung, und beim
// Zurueckspielen ist sie die einzige Angabe, an der sich "gehoert dieses Archiv hierher?" ablesen laesst.
if (app.Logger.IsEnabled(LogLevel.Information))
{
    var instanceFile = InstanceIdentityFile.PathFor(dataDir);
#pragma warning disable CA1848
    app.Logger.LogInformation("Instanz-Id {InstanceId} (aus {Path}).", instanceId, instanceFile);
#pragma warning restore CA1848
}

if (!keyRingProtected)
{
    // CA1848: einmaliger Start-Log, LoggerMessage-Codegen brächte hier nichts.
#pragma warning disable CA1848
    app.Logger.LogWarning(
        "DataProtection-Key-Ring liegt ungeschützt unter {Path}. Er entschlüsselt die gespeicherten " +
        "Upstream-Credentials — Verzeichnis restriktiv halten oder BIFROST_KEYRING_CERT_PATH setzen.",
        Path.Combine(dataDir, "keys"));
#pragma warning restore CA1848
}

// Recovery-Kommandos (WP8.4) laufen ohne Gateway-Start und beenden den Prozess.
if (AdminCommands.IsAdminCommand(args))
{
    return await AdminCommands.RunAsync(app, args);
}

// Das Sitzungs-Cookie der Web-UI trägt außerhalb von Development immer 'Secure' (NFR-04). Ein
// Browser verwirft ein solches Cookie über Klartext-HTTP **stillschweigend**: Die Anmeldung geht
// durch, der nächste Seitenaufruf ist wieder anonym, und nirgends steht ein Grund. Genau das ist
// beim ersten echten Betrieb passiert.
//
// Beim Start lässt sich NICHT entscheiden, ob davor ein TLS-Proxy steht — deshalb ist diese Zeile
// eine Bedingung, keine Fehlermeldung. Die eindeutige Aussage kommt beim Login-Versuch selbst
// (siehe AuthEndpoints); erst dort ist die Frage beantwortbar.
if (!app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetService<IServer>()?.Features
            .Get<IServerAddressesFeature>()?.Addresses ?? [];
        if (addresses.Count > 0 && addresses.All(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
#pragma warning disable CA1848
            app.Logger.LogWarning(
                "Der Gateway lauscht nur auf HTTP ({Addresses}). Das Sitzungs-Cookie der Web-UI ist " +
                "'Secure' — steht davor kein TLS-Proxy, verwirft der Browser es und die Anmeldung " +
                "hält nicht über den nächsten Seitenaufruf hinaus. Mit TLS davor ist alles in Ordnung.",
                string.Join(", ", addresses));
#pragma warning restore CA1848
        }
    });
}

// Hinter einem TLS-Proxy (der vorgesehene Produktionsaufbau, NFR-04) sieht der Gateway selbst nur
// HTTP. Ohne Auswertung der Forwarded-Header baut er absolute Adressen aus dem, was er sieht — und
// schickt einen abgemeldeten Besucher von einer https-Seite auf eine http-Adresse. Beim ersten
// echten Betrieb war das ein „400 The plain HTTP request was sent to HTTPS port".
//
// Bewusst OPT-IN: Wer X-Forwarded-Proto von jedem Absender glaubt, laesst sich von jedem Client
// erzaehlen, die Verbindung sei sicher. Deshalb muss ein Betreiber sagen, WEM er glaubt.
if (ForwardedProxyOptions.TryCreate(builder.Configuration["BIFROST_TRUSTED_PROXIES"], out var forwarded))
{
    app.UseForwardedHeaders(forwarded);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.UseMiddleware<ApiKeyAuthMiddleware>();
app.MapMcp("/mcp");
app.MapGatewayApi();
app.MapAuthEndpoints();
app.MapUpstreamOAuth();

// Protected Resource Metadata (RFC 9728). Bewusst anonym: Sie ist der Weg, auf dem ein Client
// ueberhaupt erst erfaehrt, wo er sich ein Token holt — hinter Authentifizierung waere sie nutzlos.
if (oauthResourceServer is not null)
{
    // Einmal gebaut statt bei jedem Abruf: Das Dokument ist unveraenderlich, solange die
    // Konfiguration steht.
    var protectedResourceMetadata = new
    {
        resource = oauthResourceServer.Audience,
        authorization_servers = new[] { oauthResourceServer.Issuer },
        bearer_methods_supported = new[] { "header" },
    };
    app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(protectedResourceMetadata));
}
app.MapWebhookEndpoint();

// Die UI ist selbstenthaltend (CSS inline in App.razor, Favicon als Data-URI) — deshalb fehlte
// hier lange jede statische Auslieferung. Genau EINE Datei laesst sich aber nicht inlinen:
// '_framework/blazor.web.js'. Ohne sie startet der Blazor-Circuit nie, und dann tut in der
// gesamten Oberflaeche kein @onclick und kein @bind etwas — waehrend die Seiten weiterhin
// serverseitig gerendert werden und deshalb benutzbar AUSSEHEN. Genau so ist es beim ersten
// echten Betrieb aufgefallen.
app.MapStaticAssets();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", async (
    IDbContextFactory<BifrostDbContext> factory,
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
