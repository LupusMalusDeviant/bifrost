using System.Net;
using System.Text;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// WP6-DoD: UI-Auth, Rollen-Enforcement, UI-Audit und der Referenz-Setup-Durchstich
/// (anlegen → Key → Tool testen → Log) über exakt die Application-Services, die die Blazor-Komponenten nutzen.
/// Volle Browser-E2E (Playwright) ist bewusst nicht in der CI (Browser-Binaries) — siehe Plan-Änderungslog.
/// </summary>
public sealed class WebUiTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public WebUiTests(GatewayFixture gw) => _gw = gw;

    [Fact]
    public async Task Unauthenticated_access_redirects_to_login()
    {
        using var client = _gw.CreateUiClient();

        var dashboard = await client.GetAsync("/");
        dashboard.StatusCode.Should().Be(HttpStatusCode.Redirect);
        dashboard.Headers.Location!.OriginalString.Should().Contain("/login");

        var login = await client.GetAsync("/login");
        login.StatusCode.Should().Be(HttpStatusCode.OK, "die Login-Seite ist anonym erreichbar");
    }

    [Fact]
    public async Task Wrong_credentials_are_rejected_and_audited()
    {
        await _gw.UiUsers.CreateAsync($"wrong-{Guid.NewGuid():N}", "richtig-geheim", UiRole.Admin, TestContext.Current.CancellationToken);
        using var client = _gw.CreateUiClient();

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "gibtsnicht",
            ["password"] = "falsch",
            ["returnUrl"] = "/",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("failed=true");

        await IntegrationSupport.WaitUntilAsync(async () =>
            (await _gw.AuditQuery.QueryAsync(
                new AuditFilter(Kind: AuditEventKind.Authentication, Status: InvocationStatus.Denied), TestContext.Current.CancellationToken))
                .TotalCount >= 1,
            because: "fehlgeschlagene Logins werden auditiert (FR-22)");
    }

    [Fact]
    public async Task Login_succeeds_sets_cookie_and_is_audited()
    {
        var name = $"admin-{Guid.NewGuid():N}";
        await _gw.UiUsers.CreateAsync(name, "passwort123", UiRole.Admin, TestContext.Current.CancellationToken);

        using var client = await _gw.LoginUiAsync(name, "passwort123");
        var dashboard = await client.GetAsync("/");
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK, "mit Cookie ist das Dashboard erreichbar");

        await IntegrationSupport.WaitUntilAsync(async () =>
            (await _gw.AuditQuery.QueryAsync(new AuditFilter(ToolPrefix: $"ui-login:{name}", Kind: AuditEventKind.Authentication), TestContext.Current.CancellationToken))
                .TotalCount >= 1,
            because: "erfolgreiche UI-Logins werden auditiert");
    }

    [Fact]
    public async Task Auditor_role_cannot_reach_admin_pages_but_can_read_logs()
    {
        var name = $"auditor-{Guid.NewGuid():N}";
        await _gw.UiUsers.CreateAsync(name, "nur-lesen", UiRole.Auditor, TestContext.Current.CancellationToken);
        using var client = await _gw.LoginUiAsync(name, "nur-lesen");

        // Admin-Seiten (RBAC/Profile/Nutzer) sind für Auditor gesperrt → Redirect (Policy NotAuthorized)
        (await client.GetAsync("/rbac")).StatusCode.Should().Be(HttpStatusCode.Redirect, "Auditor darf kein RBAC");
        (await client.GetAsync("/users")).StatusCode.Should().Be(HttpStatusCode.Redirect, "Auditor darf keine Nutzer");
        (await client.GetAsync("/servers")).StatusCode.Should().Be(HttpStatusCode.Redirect, "Auditor darf keine Server verwalten");

        // Lesende Seiten sind erlaubt
        (await client.GetAsync("/logs")).StatusCode.Should().Be(HttpStatusCode.OK, "Auditor darf Logs lesen");
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "Auditor darf das Dashboard sehen");
    }

    [Fact]
    public async Task Operator_can_manage_servers_but_not_rbac()
    {
        var name = $"operator-{Guid.NewGuid():N}";
        await _gw.UiUsers.CreateAsync(name, "betrieb", UiRole.Operator, TestContext.Current.CancellationToken);
        using var client = await _gw.LoginUiAsync(name, "betrieb");

        (await client.GetAsync("/servers")).StatusCode.Should().Be(HttpStatusCode.OK, "Operator darf Server verwalten");
        (await client.GetAsync("/rbac")).StatusCode.Should().Be(HttpStatusCode.Redirect, "Operator darf kein RBAC");
        // Vorgangsliste (ADR-0019): dieselbe Rolle wie die Freigaben, denn Freigaben SIND Vorgänge.
        (await client.GetAsync("/tasks")).StatusCode.Should().Be(HttpStatusCode.OK, "Operator darf Vorgänge sehen");
    }

    [Fact]
    public async Task Reference_setup_flow_through_ui_services_produces_ui_origin_audit()
    {
        // Der Durchstich aus PRD-Abnahmekriterium 1 / WP6-DoD über exakt die Services,
        // die Servers.razor, Rbac.razor und Tools.razor aufrufen — ohne je eine Config-Datei anzufassen.

        // 1. Server anlegen (Servers.razor → IUpstreamSupervisor.AddAsync)
        var serverId = await _gw.AddEchoUpstreamAsync("ui-ref");

        // 2. Identität + Key erzeugen (Rbac.razor → IRbacManagement + IApiKeyService)
        var rbac = _gw.Services.GetService(typeof(IRbacManagement)) as IRbacManagement;
        rbac.Should().NotBeNull();
        var role = new Role(RoleId.New(), "ui-ref-role",
            [new Grant(new PermissionScope(null, new NamespacedToolName("ui-ref__echo")), [ToolAction.UseTool])]);
        await rbac!.UpsertRoleAsync(role, TestContext.Current.CancellationToken);
        var identity = new Identity(IdentityId.New(), "ui-ref-agent", IdentityKind.Agent, [role.Id]);
        await rbac.UpsertIdentityAsync(identity, TestContext.Current.CancellationToken);
        var key = await _gw.ApiKeys.IssueAsync(identity.Id, "ui-ref-key", null, TestContext.Current.CancellationToken);
        key.PlaintextKey.Should().StartWith("mcpk_");

        // 3. Tool testweise aufrufen (Tools.razor → IToolInvoker mit Origin=Ui, interne UI-Identität)
        var uiInternal = (Bifrost.Web.UiInternalIdentity)_gw.Services.GetService(typeof(Bifrost.Web.UiInternalIdentity))!;
        var result = await _gw.Invoker.InvokeAsync(
            new ToolInvocationRequest(uiInternal.Value, CallOrigin.Ui, new NamespacedToolName("ui-ref__echo"),
                System.Text.Json.JsonSerializer.SerializeToElement(new { message = "aus der UI" }), null),
            TestContext.Current.CancellationToken);
        result.Status.Should().Be(InvocationStatus.Success);

        // 4. Im Log sichtbar (Logs.razor → IAuditQuery), mit Origin=Ui
        await IntegrationSupport.WaitUntilAsync(async () =>
            (await _gw.AuditQuery.QueryAsync(new AuditFilter(ToolPrefix: "ui-ref__echo"), TestContext.Current.CancellationToken))
                .Items.Any(e => e.Origin == CallOrigin.Ui && e.Status == InvocationStatus.Success),
            because: "WP6-DoD: der UI-Testaufruf erscheint mit Origin=Ui im Audit-Log");

        await _gw.Supervisor.RemoveAsync(serverId, DrainPolicy.Immediate, TestContext.Current.CancellationToken);
    }
}
