using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Integration.Tests.Gateway;
using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// WP6.4 am laufenden Gateway: Wer darf das Cockpit sehen — und ändert es wirklich nichts.
/// </summary>
public sealed class UsageCockpitApiTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;

    public UsageCockpitApiTests(GatewayFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Die Auswertung nennt, was eine <b>fremde</b> Identität benutzt hat. Das ist eine
    /// Verwaltungssicht — ein Cockpit, das jeder über jeden abfragen kann, wäre eine
    /// Nutzungsüberwachung durch die Hintertür.
    /// </summary>
    [Fact]
    public async Task Only_an_admin_may_look_at_the_cockpit()
    {
        var (identity, _) = await _fixture.SeedAdminAsync($"cockpit-ziel-{Guid.NewGuid():N}");
        var (_, ohneRechte) = await _fixture.SeedIdentityAsync($"cockpit-fremd-{Guid.NewGuid():N}", grants: []);

        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ohneRechte);

        var response = await client.GetAsync(
            $"/api/v1/usage/{identity.Value}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_report_separates_what_a_profile_costs_from_what_was_used()
    {
        var slug = $"cock{Guid.NewGuid():N}"[..10];
        await _fixture.AddEchoUpstreamAsync(slug);

        var pinned = new ToolProfile(
            ProfileId.New(), $"cockpit-{slug}", [NamespacedToolName.Create(slug, "echo")], LazyToolsEnabled: false);
        var (identity, apiKey) = await _fixture.SeedAdminAsync($"cockpit-{Guid.NewGuid():N}", pinned);

        // Einmal wirklich aufrufen — sonst prueft der Bericht eine Nutzung, die es nicht gab.
        await using (var mcp = await _fixture.ConnectClientAsync(apiKey))
        {
            await mcp.CallToolAsync(
                $"{slug}__echo", new Dictionary<string, object?> { ["message"] = "cockpit" },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var admin = _fixture.CreateDefaultClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // Der Auditschreiber leert gepuffert; bis der Aufruf in der Abfrage steht, kann ein Moment
        // vergehen. Ohne dieses Warten prueft der Test die Puffergroesse, nicht das Cockpit.
        await IntegrationSupport.WaitUntilAsync(
            async () =>
            {
                var body = await admin.GetStringAsync(
                    $"/api/v1/usage/{identity.Value}?days=1", TestContext.Current.CancellationToken);
                return body.Contains("\"totalInvocations\":1", StringComparison.Ordinal);
            },
            because: "der Aufruf muss im Bericht ankommen");

        var report = await admin.GetStringAsync(
            $"/api/v1/usage/{identity.Value}?days=1", TestContext.Current.CancellationToken);

        report.Should().Contain($"{slug}__echo");
        report.Should().Contain("\"exposedTools\":1");
    }

    /// <summary>
    /// Die Regel des Pakets am echten System: Nach einer Simulation steht das Profil unverändert da.
    /// </summary>
    [Fact]
    public async Task A_simulation_leaves_the_profile_untouched()
    {
        var slug = $"sim{Guid.NewGuid():N}"[..9];
        await _fixture.AddEchoUpstreamAsync(slug);

        var tool = NamespacedToolName.Create(slug, "echo");
        var profile = new ToolProfile(ProfileId.New(), $"sim-{slug}", [tool], LazyToolsEnabled: false);
        var (identity, apiKey) = await _fixture.SeedAdminAsync($"sim-{Guid.NewGuid():N}", profile);

        var admin = _fixture.CreateDefaultClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/usage/{identity.Value}/simulate",
            new { tools = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("\"tokensAfter\":0", "ohne Werkzeuge kostet das Profil nichts mehr");

        var stored = (await _fixture.RbacStore.ListProfilesAsync(TestContext.Current.CancellationToken))
            .Single(p => p.Id == profile.Id);
        stored.PinnedTools.Should().ContainSingle().Which.Value.Should().Be(tool.Value,
            "eine Simulation rechnet — sie aendert nichts");
    }
}
