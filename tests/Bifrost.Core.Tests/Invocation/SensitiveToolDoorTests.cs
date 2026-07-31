using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Xunit;

namespace Bifrost.Core.Tests.Invocation;

/// <summary>
/// Zwei Türen für denselben Aufruf: <c>invoke_tool</c> und <c>invoke_sensitive_tool</c> (ADR-0022).
/// <para>
/// <b>Warum es das gibt:</b> Ein MCP-Client kann seine Rückfrage nur je Tool<em>namen</em>
/// einstellen — er sieht <c>invoke_tool</c> und nicht, was dahintersteckt. Wer für
/// <c>invoke_tool</c> nachfragen lässt, wird also auch bei <c>list_servers</c> gefragt; wer es
/// abschaltet, bei <c>execute_command</c> nicht mehr. Mit zwei Namen fällt die Grenze des Clients
/// mit der Grenze der Gefahr zusammen.
/// </para>
/// </summary>
public class SensitiveToolDoorTests
{
    /// <summary>Markiert genau die Werkzeuge, die der Test scharf nennt.</summary>
    private sealed class Policy : IApprovalPolicy
    {
        private readonly Dictionary<NamespacedToolName, ApprovalEnforcement> _marked;

        public Policy(params (NamespacedToolName Tool, ApprovalEnforcement Mode)[] marked)
            => _marked = marked.ToDictionary(m => m.Tool, m => m.Mode);

        public bool RequiresApproval(NamespacedToolName tool)
            => _marked.TryGetValue(tool, out var mode) && mode is ApprovalEnforcement.Queue;

        public bool IsSensitive(NamespacedToolName tool) => _marked.ContainsKey(tool);

        public ApprovalEnforcement? EnforcementFor(NamespacedToolName tool)
            => _marked.TryGetValue(tool, out var mode) ? mode : null;

        public IReadOnlyCollection<NamespacedToolName> All => _marked.Keys;

        public Task SetAsync(NamespacedToolName tool, bool required, CancellationToken ct)
            => Task.CompletedTask;

        public Task SetAsync(NamespacedToolName tool, ApprovalEnforcement? enforcement, CancellationToken ct)
            => Task.CompletedTask;

        public ApprovalEnforcement DefaultEnforcement { get; set; } = ApprovalEnforcement.Queue;

        public ApprovalEnforcement? EffectiveFor(NamespacedToolName tool, bool declaredByCatalog)
            => EnforcementFor(tool) ?? (declaredByCatalog ? DefaultEnforcement : null);

        public Task SetDefaultEnforcementAsync(ApprovalEnforcement enforcement, CancellationToken ct)
        {
            DefaultEnforcement = enforcement;
            return Task.CompletedTask;
        }

        public event EventHandler? Changed { add { } remove { } }
    }

    private static Task<ToolInvocationResult> CallAsync(
        MetaToolService meta, IdentityId caller, string door, NamespacedToolName tool)
        => meta.ExecuteAsync(
            caller, CallOrigin.Mcp, door,
            JsonSerializer.SerializeToElement(new { name = tool.Value, arguments = new { message = "hi" } }),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Die offensichtliche Richtung: Ein scharfes Werkzeug darf nicht durch die harmlose Tür — sonst
    /// wäre die Aufteilung reine Zierde.
    /// </summary>
    [Fact]
    public async Task A_sensitive_tool_is_refused_at_the_plain_door()
    {
        var w = new InvokerTestWorld();
        var meta = w.WithApprovalPolicy(new Policy((w.Echo, ApprovalEnforcement.Client)));
        var admin = w.RegisterAdmin();

        var result = await CallAsync(meta, admin, MetaToolService.InvokeToolName, w.Echo);

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
        result.ErrorMessage.Should().Contain(MetaToolService.InvokeSensitiveToolName,
            "die Meldung muss die richtige Tuer NENNEN — sonst raet der Agent");
        w.Connection.LastToolName.Should().BeNull("abgewiesen heisst: der Upstream sieht den Aufruf nie");
    }

    /// <summary>Durch die richtige Tür läuft derselbe Aufruf ganz normal durch.</summary>
    [Fact]
    public async Task The_same_call_goes_through_the_sensitive_door()
    {
        var w = new InvokerTestWorld();
        var meta = w.WithApprovalPolicy(new Policy((w.Echo, ApprovalEnforcement.Client)));
        var admin = w.RegisterAdmin();

        var result = await CallAsync(meta, admin, MetaToolService.InvokeSensitiveToolName, w.Echo);

        result.Status.Should().Be(InvocationStatus.Success);
        w.Connection.LastToolName.Should().Be("echo");
    }

    /// <summary>
    /// Die Gegenrichtung — und die ist der eigentliche Grund für diesen Test.
    /// <para>
    /// Dürfte ein Agent Belangloses durch die scharfe Tür schicken, gewöhnte er den Menschen daran,
    /// den Dialog wegzuklicken. Ein Dialog, den man reflexhaft bestätigt, schützt nicht mehr — die
    /// Ermüdung ist hier der Angriff, nicht ein einzelner falscher Aufruf.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_harmless_tool_is_refused_at_the_sensitive_door()
    {
        var w = new InvokerTestWorld();
        var meta = w.WithApprovalPolicy(new Policy());
        var admin = w.RegisterAdmin();

        var result = await CallAsync(meta, admin, MetaToolService.InvokeSensitiveToolName, w.Echo);

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
        result.ErrorMessage.Should().Contain(MetaToolService.InvokeToolName);
        w.Connection.LastToolName.Should().BeNull();
    }

    /// <summary>
    /// Schärfe hat zwei Quellen: die Politik, die ein Mensch pflegt, und die Selbstauskunft eines
    /// Connector-Pakets. Zählte nur die erste, käme ein Paket-Werkzeug durch die harmlose Tür.
    /// </summary>
    [Fact]
    public async Task A_tool_that_declares_itself_sensitive_also_needs_the_sensitive_door()
    {
        var w = new InvokerTestWorld(echoRequiresApproval: true);
        var meta = w.WithApprovalPolicy(new Policy());
        var admin = w.RegisterAdmin();

        var result = await CallAsync(meta, admin, MetaToolService.InvokeToolName, w.Echo);

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
        result.ErrorMessage.Should().Contain(MetaToolService.InvokeSensitiveToolName);
    }

    /// <summary>
    /// Ein unbekannter Name darf hier NICHT beantwortet werden. Sonst verriete die Weiche über die
    /// Wahl der Meldung, ob es ein Werkzeug gibt, das der Aufrufer gar nicht sehen darf — die
    /// Sichtbarkeitsregel aus FR-29, an der Stelle ausgehebelt, an der niemand sie vermutet.
    /// </summary>
    [Fact]
    public async Task An_unknown_tool_is_answered_by_the_invoker_not_by_the_door()
    {
        var w = new InvokerTestWorld();
        var meta = w.WithApprovalPolicy(new Policy());
        var admin = w.RegisterAdmin();

        var result = await CallAsync(
            meta, admin, MetaToolService.InvokeToolName, NamespacedToolName.Create(w.Slug, "gibtsnicht"));

        result.Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    /// <summary>
    /// Ohne angebundene Politik gilt nur die Selbstauskunft des Katalogs — und alles Übrige läuft
    /// wie bisher. Eine Zusammenstellung ohne Politik ist gültig und darf nicht in eine Sperre
    /// laufen, die niemand auflösen kann.
    /// </summary>
    [Fact]
    public async Task Without_a_policy_nothing_is_locked_out()
    {
        var w = new InvokerTestWorld();
        var admin = w.RegisterAdmin();

        var result = await CallAsync(w.MetaTools, admin, MetaToolService.InvokeToolName, w.Echo);

        result.Status.Should().Be(InvocationStatus.Success);
    }

    /// <summary>
    /// <c>describe_tool</c> nennt die richtige Tür, bevor der Aufruf schiefgeht. Sonst lernte ein
    /// Agent die Aufteilung ausschließlich durch Fehlschläge — und ein vermeidbarer Fehlversuch je
    /// scharfem Werkzeug ist genau die Sorte Reibung, die diese Änderung beseitigen soll.
    /// </summary>
    [Fact]
    public async Task Describe_names_the_door_before_the_call_goes_wrong()
    {
        var w = new InvokerTestWorld();
        var meta = w.WithApprovalPolicy(new Policy((w.Echo, ApprovalEnforcement.Client)));
        var admin = w.RegisterAdmin();

        var described = await meta.ExecuteAsync(
            admin, CallOrigin.Mcp, MetaToolService.DescribeToolName,
            JsonSerializer.SerializeToElement(new { name = w.Echo.Value }),
            TestContext.Current.CancellationToken);

        described.Status.Should().Be(InvocationStatus.Success);
        described.Content!.Value.GetProperty("sensitive").GetBoolean().Should().BeTrue();
        described.Content!.Value.GetProperty("invokeWith").GetString()
            .Should().Be(MetaToolService.InvokeSensitiveToolName);
    }

    /// <summary>
    /// Die neue Tür reicht ein Upstream-Ergebnis durch — genau wie <c>invoke_tool</c>.
    /// <para>
    /// Das ist kein Detail: Genau diese Unterscheidung hat <c>read_skill</c> über einen echten
    /// MCP-Client bei jedem Aufruf scheitern lassen, während alle Tests grün waren. Wer ein
    /// Meta-Tool ergänzt und das hier vergisst, baut denselben Fehler noch einmal.
    /// </para>
    /// </summary>
    [Fact]
    public void The_sensitive_door_forwards_upstream_results_like_the_plain_one()
    {
        MetaToolService.ForwardsUpstreamResult(MetaToolService.InvokeSensitiveToolName)
            .Should().BeTrue();
        MetaToolService.IsMetaTool(MetaToolService.InvokeSensitiveToolName)
            .Should().BeTrue("sonst landet der Aufruf im normalen Invoker, der den Namen nicht kennt");
    }
}
