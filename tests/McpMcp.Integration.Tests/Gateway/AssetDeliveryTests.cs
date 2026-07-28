using AwesomeAssertions;
using McpMcp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// FR-40 / Keyfeature 7: zentral gepflegte Assets (Skills, Instructions) müssen bei den Agenten
/// ankommen — als MCP-Prompt **und** als MCP-Resource. Genau das fehlte, obwohl WP6.4 als erledigt
/// markiert war; diese Tests halten die Auslieferung jetzt fest.
/// </summary>
public sealed class AssetDeliveryTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public AssetDeliveryTests(GatewayFixture gw) => _gw = gw;

    private IAssetStore Assets => _gw.Services.GetRequiredService<IAssetStore>();

    [Fact]
    public async Task Asset_is_delivered_as_prompt_with_its_content()
    {
        var name = $"skill-prompt-{Guid.NewGuid():N}";
        await Assets.CreateAsync(name, "Ein zentral gepflegter Skill", "## Regeln\nImmer zuerst suchen.", metadata: null, TestContext.Current.CancellationToken);
        var (_, apiKey) = await _gw.SeedAdminAsync($"asset-prompt-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);

        var prompts = await client.ListPromptsAsync();
        var expectedName = AssetDelivery.PromptName(name);
        prompts.Should().Contain(p => p.Name == expectedName,
            "zentrale Assets erscheinen im Prompt-Verzeichnis des Agenten");

        var prompt = await client.GetPromptAsync(expectedName);
        prompt.Messages.Should().ContainSingle()
            .Which.Content.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("## Regeln\nImmer zuerst suchen.");
    }

    /// <summary>
    /// Assets sind für <b>jede</b> authentifizierte Identität sichtbar — auch für eine ohne jeden
    /// Grant. Das ist so entschieden (FR-40 kennt keine per-Asset-RBAC), und genau deshalb steht es
    /// als Test da: Ohne ihn wäre es eine Aussage in einem Kommentar, die jemand versehentlich
    /// ändert. Wer Zugriff einschränken will, ändert diesen Test bewusst mit.
    /// </summary>
    [Fact]
    public async Task Every_authenticated_identity_sees_the_same_assets()
    {
        var name = $"skill-offen-{Guid.NewGuid():N}";
        await Assets.CreateAsync(name, "Für alle", "Inhalt", metadata: null, TestContext.Current.CancellationToken);
        var expectedName = AssetDelivery.PromptName(name);

        var (_, adminKey) = await _gw.SeedAdminAsync($"asset-admin-{Guid.NewGuid():N}");
        var (_, ohneGrantKey) = await _gw.SeedIdentityAsync($"asset-ohne-grant-{Guid.NewGuid():N}", grants: []);

        await using var admin = await _gw.ConnectClientAsync(adminKey);
        await using var ohneGrant = await _gw.ConnectClientAsync(ohneGrantKey);

        (await admin.ListPromptsAsync()).Should().Contain(p => p.Name == expectedName);
        (await ohneGrant.ListPromptsAsync()).Should().Contain(p => p.Name == expectedName,
            "eine Identität ohne jeden Grant sieht Assets trotzdem — sie sind zentrale Instruktionen, "
            + "keine Zugriffe auf Fremdsysteme");

        var prompt = await ohneGrant.GetPromptAsync(expectedName);
        prompt.Messages.Should().ContainSingle()
            .Which.Content.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("Inhalt");
    }

    [Fact]
    public async Task Asset_is_readable_as_resource_and_serves_the_latest_version()
    {
        var name = $"skill-res-{Guid.NewGuid():N}";
        var id = await Assets.CreateAsync(name, null, "Version 1", metadata: null, TestContext.Current.CancellationToken);
        await Assets.PublishAsync(id, "Version 2 — aktualisiert", metadata: null, TestContext.Current.CancellationToken);
        var (_, apiKey) = await _gw.SeedAdminAsync($"asset-res-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);

        var uri = AssetDelivery.ResourceUri(name);
        var resources = await client.ListResourcesAsync();
        resources.Should().Contain(r => r.Uri == uri, "Assets sind auch als Resource adressierbar");

        var read = await client.ReadResourceAsync(uri);
        read.Contents.OfType<TextResourceContents>().Single().Text
            .Should().Be("Version 2 — aktualisiert", "ausgeliefert wird immer die neueste Version");
    }

    [Fact]
    public async Task Updating_an_asset_changes_what_agents_receive()
    {
        var name = $"skill-update-{Guid.NewGuid():N}";
        var id = await Assets.CreateAsync(name, null, "alter Stand", metadata: null, TestContext.Current.CancellationToken);
        var (_, apiKey) = await _gw.SeedAdminAsync($"asset-upd-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);
        var promptName = AssetDelivery.PromptName(name);

        var before = await client.GetPromptAsync(promptName);
        before.Messages[0].Content.Should().BeOfType<TextContentBlock>().Which.Text.Should().Be("alter Stand");

        // Der Kern von Keyfeature 7: zentral ändern → alle Agenten bekommen den neuen Stand.
        await Assets.PublishAsync(id, "neuer Stand", metadata: null, TestContext.Current.CancellationToken);

        var after = await client.GetPromptAsync(promptName);
        after.Messages[0].Content.Should().BeOfType<TextContentBlock>().Which.Text.Should().Be("neuer Stand");
    }

    [Fact]
    public async Task Unknown_asset_is_reported_as_missing()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync($"asset-missing-{Guid.NewGuid():N}");
        await using var client = await _gw.ConnectClientAsync(apiKey);

        var act = async () => await client.GetPromptAsync(AssetDelivery.PromptName("gibt-es-nicht"));

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Assets_slug_is_reserved_for_upstream_servers()
    {
        // Sonst könnte ein Upstream mit Slug "assets" die zentrale Auslieferung überschatten.
        var act = () => _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                AssetDelivery.Namespace, "Kollision", UpstreamTransportKind.Stdio, Enabled: true,
                Stdio: new StdioTransportOptions("egal", [])),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*reserviert*");
    }
}
