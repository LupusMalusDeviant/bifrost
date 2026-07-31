using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// <c>invoke_sensitive_tool</c> über einen <b>echten MCP-Client</b> (ADR-0022).
/// <para>
/// Über den Service direkt zu prüfen reicht hier nicht: Ein neues Meta-Tool muss in
/// <c>tools/list</c> auftauchen, aufrufbar sein <em>und</em> sein Ergebnis durch die
/// Protokollschicht bringen. Genau an diesen drei Stellen ist <c>read_skill</c> im Betrieb
/// gescheitert, während die Unit-Tests grün waren.
/// </para>
/// </summary>
public sealed class SensitiveDoorOverMcpTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public SensitiveDoorOverMcpTests(GatewayFixture gw) => _gw = gw;

    private ApprovalPolicyStore Policy => _gw.Services.GetRequiredService<ApprovalPolicyStore>();

    private IApprovalStore Approvals => _gw.Services.GetRequiredService<IApprovalStore>();

    private async Task<NamespacedToolName> EchoToolAsync()
    {
        var slug = $"sd{Guid.NewGuid():N}"[..10];
        await _gw.AddEchoUpstreamAsync(slug);
        return new NamespacedToolName($"{slug}__echo");
    }

    /// <summary>Ohne Eintrag in <c>tools/list</c> existiert die Tür für einen Client nicht.</summary>
    [Fact]
    public async Task The_sensitive_door_is_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedAdminAsync($"sd-liste-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        tools.Select(t => t.Name).Should().Contain("invoke_sensitive_tool");
    }

    /// <summary>
    /// Der Kern des Entwurfs: Im Client-Modus hält das Gateway den Aufruf <b>nicht</b> mehr auf —
    /// es verlangt nur noch den anderen Weg. Nichts landet in der Warteschlange.
    /// </summary>
    [Fact]
    public async Task In_client_mode_the_call_runs_through_the_sensitive_door_without_a_queue_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, ApprovalEnforcement.Client, ct);
        try
        {
            var (_, apiKey) = await _gw.SeedAdminAsync($"sd-client-{Guid.NewGuid():N}");
            await using var client = await _gw.ConnectClientAsync(apiKey);

            var wrongDoor = await client.CallToolAsync(
                "invoke_tool",
                new Dictionary<string, object?>
                {
                    ["name"] = tool.Value,
                    ["arguments"] = new Dictionary<string, object?> { ["message"] = "hi" },
                },
                cancellationToken: ct);

            var rightDoor = await client.CallToolAsync(
                "invoke_sensitive_tool",
                new Dictionary<string, object?>
                {
                    ["name"] = tool.Value,
                    ["arguments"] = new Dictionary<string, object?> { ["message"] = "hi" },
                },
                cancellationToken: ct);

            wrongDoor.IsError.Should().BeTrue();
            wrongDoor.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("invoke_sensitive_tool");

            rightDoor.IsError.Should().NotBe(true,
                "im Client-Modus haelt das Gateway nichts mehr auf — es verlangt nur den Weg");
            (await Approvals.ListAsync(ApprovalState.Pending, ct))
                .Should().NotContain(r => r.Tool == tool,
                    "Client-Modus heisst: keine Warteschlange, sonst waere nichts gewonnen");
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// Der Warteschlangen-Modus bleibt, was er war — auch durch die neue Tür. Sonst wäre
    /// <c>invoke_sensitive_tool</c> ein Weg an der Freigabepflicht vorbei, also das Gegenteil
    /// dessen, wofür es gebaut ist.
    /// </summary>
    [Fact]
    public async Task The_sensitive_door_is_no_shortcut_past_the_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, ApprovalEnforcement.Queue, ct);
        try
        {
            // Client auf dem vorigen Stand ohne Formular-Handler: Geprueft wird der
            // WARTESCHLANGEN-Modus. Ein Client, der gefragt werden kann, bekaeme die Rueckfrage —
            // richtig so, aber dann pruefte dieser Test die Tuer nicht mehr gegen die Warteschlange.
            var (_, apiKey) = await _gw.SeedAdminAsync($"sd-queue-{Guid.NewGuid():N}");
            await using var client = await _gw.ConnectLegacyClientAsync(apiKey);

            var result = await client.CallToolAsync(
                "invoke_sensitive_tool",
                new Dictionary<string, object?>
                {
                    ["name"] = tool.Value,
                    ["arguments"] = new Dictionary<string, object?> { ["message"] = "hi" },
                },
                cancellationToken: ct);

            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("ApprovalRequired");
            (await Approvals.ListAsync(ApprovalState.Pending, ct))
                .Should().Contain(r => r.Tool == tool);
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// Der Weg zurück: Wer die Markierung entfernt, bekommt das Werkzeug wieder durch die harmlose
    /// Tür — und <b>nicht</b> mehr durch die scharfe. Ohne das bliebe eine einmal gesetzte
    /// Markierung faktisch unumkehrbar.
    /// </summary>
    [Fact]
    public async Task Clearing_the_mark_moves_the_tool_back_to_the_plain_door()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, ApprovalEnforcement.Client, ct);
        await Policy.SetAsync(tool, null, ct);

        var (_, apiKey) = await _gw.SeedAdminAsync($"sd-zurueck-{Guid.NewGuid():N}");
        await using var client = await _gw.ConnectClientAsync(apiKey);

        var plain = await client.CallToolAsync(
            "invoke_tool",
            new Dictionary<string, object?>
            {
                ["name"] = tool.Value,
                ["arguments"] = new Dictionary<string, object?> { ["message"] = "hi" },
            },
            cancellationToken: ct);

        plain.IsError.Should().NotBe(true);
        Policy.IsSensitive(tool).Should().BeFalse();
    }
}
