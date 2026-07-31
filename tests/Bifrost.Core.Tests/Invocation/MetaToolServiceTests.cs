using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Xunit;

namespace Bifrost.Core.Tests.Invocation;

/// <summary>WP4.3: Meta-Tools sind RBAC-konsistent mit tools/list und laufen über die Invoker-Pipeline.</summary>
public class MetaToolServiceTests
{
    private readonly InvokerTestWorld _w = new();

    private Task<ToolInvocationResult> ExecuteAsync(IdentityId caller, string metaTool, object args)
        => _w.MetaTools.ExecuteAsync(
            caller, CallOrigin.Mcp, metaTool,
            System.Text.Json.JsonSerializer.SerializeToElement(args), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Search_returns_only_visible_tools()
    {
        var restricted = _w.RegisterAgent(
            new Grant(new PermissionScope(null, _w.Echo), [ToolAction.UseTool]));

        // "message" matcht die echo-Beschreibung, "Schema" die free-Beschreibung — beide Kandidaten treffen.
        var result = await ExecuteAsync(restricted, MetaToolService.SearchToolsName, new { query = "message Schema" });

        result.Status.Should().Be(InvocationStatus.Success);
        var names = result.Content!.Value.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        names.Should().Contain(_w.Echo.Value).And.NotContain(_w.Free.Value, "Sichtbarkeit folgt Berechtigung (FR-29)");
        _w.Audit.Events.Should().ContainSingle().Which.Tool.Should().Be("search_tools");
    }

    [Fact]
    public async Task Search_without_query_fails_validation()
    {
        var admin = _w.RegisterAdmin();

        var result = await ExecuteAsync(admin, MetaToolService.SearchToolsName, new { falsch = 1 });

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
    }

    [Fact]
    public async Task Describe_returns_schema_for_visible_tool_only()
    {
        var restricted = _w.RegisterAgent(
            new Grant(new PermissionScope(null, _w.Echo), [ToolAction.UseTool]));

        var visible = await ExecuteAsync(restricted, MetaToolService.DescribeToolName, new { name = _w.Echo.Value });
        var invisible = await ExecuteAsync(restricted, MetaToolService.DescribeToolName, new { name = _w.Free.Value });
        var missing = await ExecuteAsync(restricted, MetaToolService.DescribeToolName,
            new { name = NamespacedToolName.Create(_w.Slug, "nix").Value });

        visible.Status.Should().Be(InvocationStatus.Success);
        visible.Content!.Value.GetProperty("inputSchema").GetProperty("required")[0].GetString().Should().Be("message");
        invisible.Status.Should().Be(InvocationStatus.ToolNotFound,
            "nicht erlaubte Tools sind von nicht existierenden ununterscheidbar");
        missing.Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    [Fact]
    public async Task Invoke_routes_through_invoker_with_target_rbac()
    {
        var restricted = _w.RegisterAgent(
            new Grant(new PermissionScope(null, _w.Echo), [ToolAction.UseTool]));

        var allowed = await ExecuteAsync(restricted, MetaToolService.InvokeToolName,
            new { name = _w.Echo.Value, arguments = new { message = "hi" } });
        var denied = await ExecuteAsync(restricted, MetaToolService.InvokeToolName,
            new { name = _w.Free.Value, arguments = new { } });

        allowed.Status.Should().Be(InvocationStatus.Success);
        _w.Connection.LastToolName.Should().Be("echo");
        denied.Status.Should().Be(InvocationStatus.Denied, "Ziel-RBAC greift auch über invoke_tool");
        _w.Audit.Events.Should().HaveCount(2, "invoke_tool auditiert nur den inneren Ziel-Call — kein Doppel-Audit");
    }

    [Fact]
    public async Task Invoke_without_name_fails_validation_and_audits()
    {
        var admin = _w.RegisterAdmin();

        var result = await ExecuteAsync(admin, MetaToolService.InvokeToolName, new { arguments = new { } });

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
        _w.Audit.Events.Should().ContainSingle().Which.Tool.Should().Be("invoke_tool");
    }

    [Fact]
    public async Task Invoke_redacts_target_arguments_in_its_own_audit()
    {
        var admin = _w.RegisterAdmin();

        // Fehlschlagende Validierung: der Ziel-Call wird nie auditiert, nur der Meta-Call —
        // genau der Pfad, auf dem die Argumente früher ungefiltert in die DB gingen.
        await ExecuteAsync(admin, MetaToolService.InvokeToolName,
            new { arguments = new { password = "geheim123" } });

        var evt = _w.Audit.Events.Should().ContainSingle().Subject;
        evt.RedactedArguments!.Value.GetRawText()
            .Should().NotContain("geheim123", "auch der Meta-Pfad läuft durch die Redaction (NFR-04)");
    }

    [Fact]
    public void Definitions_expose_the_meta_tools_with_schemas()
    {
        // Fünf seit dem 2026-07-28: list_skills/read_skill sind dazugekommen, damit ein Agent den
        // Skill-Bestand selbst durchsehen kann. Über MCP-Prompts geht das nicht — die sind in den
        // meisten Clients nutzerinitiiert, das Modell sieht sie nicht.
        // Sechs seit dem 2026-07-30: invoke_sensitive_tool (ADR-0022). Der zweite Name ist der
        // ganze Zweck — ein Client kann seine Rückfrage nur je Tool-NAMEN einstellen und sieht
        // sonst nicht, ob hinter dem Aufruf 'uptime' oder 'rm -rf' steckt.
        MetaToolService.Definitions.Should().HaveCount(6);
        MetaToolService.Definitions.Select(d => d.Name).Should().BeEquivalentTo(
            "search_tools", "describe_tool", "invoke_tool", "invoke_sensitive_tool",
            "list_skills", "read_skill");
        MetaToolService.Definitions.Should().OnlyContain(
            d => d.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Object);
        MetaToolService.IsMetaTool("search_tools").Should().BeTrue();
        MetaToolService.IsMetaTool("srv__echo").Should().BeFalse();
    }
}
