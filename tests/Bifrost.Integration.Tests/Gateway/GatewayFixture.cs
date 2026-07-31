using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;
using Bifrost.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Xunit;

// 'Role' gibt es in beiden Welten (RBAC und Protokoll) — hier ist die RBAC-Rolle gemeint.
using Role = Bifrost.Abstractions.Role;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Startet den echten Gateway-Host (Program.cs-Komposition) in-memory und stellt
/// SDK-Clients mit API-Key-AuthN bereit — die Grundlage aller WP4-DoD-Tests.
/// </summary>
public class GatewayFixture : WebApplicationFactory<Program>
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"bifrost-e2e-{Guid.NewGuid():N}");

    /// <summary>
    /// Was der Gateway mitgeschrieben hat. Ohne das ist ein „die Rueckfrage kam nicht zustande"
    /// nicht von einem „der Client hat Nein gesagt" zu unterscheiden — und genau diese Verwechslung
    /// hat den Freigabe-Pfad zweimal falsch dastehen lassen.
    /// </summary>
    public CapturedLog Log { get; } = new();

    /// <summary>
    /// Zusaetzliche Konfiguration je Fixture — damit sich derselbe Host einmal mit und einmal ohne
    /// Sessions starten laesst, ohne die Komposition aus <c>Program.cs</c> nachzubauen.
    /// </summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> ExtraSettings => [];

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDir);
        builder.UseSetting("environment", "Development"); // Cookie SecurePolicy=SameAsRequest → Tests laufen über HTTP
        builder.UseSetting("BIFROST_DATA_DIR", _dataDir);
        builder.UseSetting("BIFROST_DB_CONNECTION", $"Data Source={Path.Combine(_dataDir, "e2e.db")}");
        foreach (var (key, value) in ExtraSettings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Log));
    }

    public UpstreamSupervisor Supervisor => Services.GetRequiredService<UpstreamSupervisor>();

    public PersistentRbacStore RbacStore => Services.GetRequiredService<PersistentRbacStore>();

    public IApiKeyService ApiKeys => Services.GetRequiredService<IApiKeyService>();

    public IAuditQuery AuditQuery => Services.GetRequiredService<IAuditQuery>();

    public IUiUserService UiUsers => Services.GetRequiredService<IUiUserService>();

    public IToolInvoker Invoker => Services.GetRequiredService<IToolInvoker>();

    /// <summary>HttpClient mit Cookie-Handling, der Redirects NICHT folgt (für Auth-/Authz-Prüfungen).</summary>
    public HttpClient CreateUiClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Meldet einen UI-Nutzer per Form-POST an; der zurückgegebene Client trägt das Auth-Cookie.</summary>
    public async Task<HttpClient> LoginUiAsync(string username, string password)
    {
        var client = CreateUiClient();
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["returnUrl"] = "/",
        }));
        if (response.StatusCode != System.Net.HttpStatusCode.Redirect
            || response.Headers.Location?.OriginalString == "/login?failed=true")
        {
            throw new InvalidOperationException($"UI-Login für '{username}' fehlgeschlagen ({response.StatusCode}).");
        }

        return client;
    }

    /// <summary>Identität + Rolle (+ optionales Profil) anlegen und einen API-Key ausstellen.</summary>
    public async Task<(IdentityId Identity, string ApiKey)> SeedIdentityAsync(
        string name, IReadOnlyList<Grant> grants, ToolProfile? profile = null)
    {
        if (profile is not null)
        {
            await RbacStore.UpsertProfileAsync(profile, TestContext.Current.CancellationToken);
        }

        var role = new Role(RoleId.New(), $"{name}-rolle", grants);
        await RbacStore.UpsertRoleAsync(role, TestContext.Current.CancellationToken);
        var identity = new Identity(IdentityId.New(), name, IdentityKind.Agent, [role.Id], profile?.Id);
        await RbacStore.UpsertIdentityAsync(identity, TestContext.Current.CancellationToken);
        var key = await ApiKeys.IssueAsync(identity.Id, $"{name}-key", null, TestContext.Current.CancellationToken);
        return (identity.Id, key.PlaintextKey);
    }

    public Task<(IdentityId Identity, string ApiKey)> SeedAdminAsync(string name = "e2e-admin", ToolProfile? profile = null)
        => SeedIdentityAsync(
            name,
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])],
            profile);

    /// <summary>
    /// Verbindet einen Testclient, wahlweise mit eigener Antwort auf Elicitation-Rueckfragen und
    /// mit eigenen Client-Optionen (etwa einer festgenagelten Protokollrevision).
    /// <para>
    /// <b>Diese Luecke hat drei Fehler durchgelassen.</b> Ohne Handler meldet der Client die
    /// Faehigkeit gar nicht erst an, und der Server nimmt jedes Mal den Warteschlangen-Pfad — die
    /// Rueckfrage selbst wurde also von keinem Test je ausgeloest. Gefunden hat sie jedes Mal erst
    /// der Betrieb. Wer hier einen Handler uebergibt, prueft den Pfad, den ein echter Client geht.
    /// </para>
    /// </summary>
    public async Task<McpClient> ConnectClientAsync(
        string apiKey,
        Func<ModelContextProtocol.Protocol.ElicitRequestParams?, CancellationToken,
            ValueTask<ModelContextProtocol.Protocol.ElicitResult>>? elicitationHandler = null,
        McpClientOptions? options = null)
    {
        var httpClient = CreateDefaultClient();
        httpClient.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "e2e",
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            },
            httpClient);

        if (elicitationHandler is not null)
        {
            options ??= new McpClientOptions();
            options.Handlers = new McpClientHandlers { ElicitationHandler = elicitationHandler };

            // Die Faehigkeit AUSDRUECKLICH anmelden, nicht nur den Handler setzen: Seit SDK 2.0
            // leitet der Client sie nicht mehr aus dem Handler ab, und der Server sieht sonst
            // "Elicitation: False" — genau der Zustand, in dem ein echter Client vor der Frage
            // steht, ob er gefragt werden darf.
            options.Capabilities ??= new ModelContextProtocol.Protocol.ClientCapabilities();
            options.Capabilities.Elicitation = new ModelContextProtocol.Protocol.ElicitationCapability();
        }

        return await McpClient.CreateAsync(transport, options);
    }

    /// <summary>
    /// Ein Client auf dem <b>vorigen</b> Stand (<c>2025-11-25</c>) ohne Formular-Handler — also
    /// einer, der nicht gefragt werden kann.
    /// <para>
    /// Den braucht es seit der Revision 2026-07-28 ausdruecklich: Dort meldet <em>kein</em> Client
    /// mehr eine Elicitation-Faehigkeit (sie ist in MRTR aufgegangen), und der Gateway fragt jeden,
    /// der MRTR spricht. „Kann nicht gefragt werden" gibt es nur noch auf dem alten Stand — und
    /// genau dort muss der Warteschlangen-Weg weiter stimmen.
    /// </para>
    /// </summary>
    public Task<McpClient> ConnectLegacyClientAsync(string apiKey)
        => ConnectClientAsync(apiKey, options: new McpClientOptions { ProtocolVersion = "2025-11-25" });

    public async Task<ServerId> AddEchoUpstreamAsync(string slug)
    {
        var id = await Supervisor.AddAsync(
            new UpstreamServerConfig(
                slug, $"Echo {slug}", UpstreamTransportKind.Stdio, Enabled: true,
                Stdio: new StdioTransportOptions(TestPaths.Executable("EchoServer"), [])),
            TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(
            () => Supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: $"EchoServer '{slug}' muss Healthy werden");
        return id;
    }
}
