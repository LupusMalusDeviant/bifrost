using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Die Vorgabe für alles Scharfe <b>ohne</b> eigene Festlegung (ADR-0022).
/// <para>
/// Der eigentliche Anlass ist kein Komfort, sondern eine Lücke: Ein Werkzeug, das sich über sein
/// Connector-Paket selbst als scharf meldet (<c>ToolDescriptor.RequiresApproval</c>), hat gar
/// keine Politik-Zeile — und konnte deshalb <em>nur</em> in die Warteschlange. Ein anderer Weg war
/// für so ein Werkzeug schlicht nicht erreichbar.
/// </para>
/// <para>
/// <b>Vorgabe ist nicht Rückfall.</b> Was hier steht, ist eine Absicht. Die Rückfälle bei
/// Unklarheit — unbekannter Wert in der Spalte, Migration alter Zeilen — bleiben auf
/// <see cref="ApprovalEnforcement.Queue"/>, damit ein Tippfehler nicht dieselbe Wirkung hat wie
/// eine Entscheidung.
/// </para>
/// </summary>
public sealed class ApprovalDefaultModeTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public ApprovalDefaultModeTests(GatewayFixture gw) => _gw = gw;

    private ApprovalPolicyStore Policy => _gw.Services.GetRequiredService<ApprovalPolicyStore>();

    [Fact]
    public async Task The_shipped_default_is_the_stricter_way()
        => Policy.DefaultEnforcement.Should().Be(ApprovalEnforcement.Queue,
            "eine Instanz, die diese Einstellung nie gesehen hat, darf nicht schwaecher sein als vorher");

    /// <summary>
    /// Der Fall, für den es die Vorgabe gibt: ein Werkzeug ohne eigene Zeile, das sich selbst als
    /// scharf meldet.
    /// </summary>
    [Fact]
    public async Task A_catalog_declared_tool_follows_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new NamespacedToolName("paket__gefaehrlich");

        Policy.EffectiveFor(tool, declaredByCatalog: true).Should().Be(ApprovalEnforcement.Queue);

        await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Client, ct);
        try
        {
            Policy.EffectiveFor(tool, declaredByCatalog: true).Should().Be(ApprovalEnforcement.Client,
                "genau dafuer ist die Vorgabe da — sonst haette so ein Werkzeug nie einen anderen Weg");
        }
        finally
        {
            await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Queue, ct);
        }
    }

    /// <summary>
    /// Eine ausdrückliche Festlegung schlägt die Vorgabe — sonst würde ein Umstellen der Vorgabe
    /// stillschweigend Werkzeuge mitreißen, für die jemand bewusst etwas anderes gewählt hat.
    /// </summary>
    [Fact]
    public async Task An_explicit_setting_beats_the_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new NamespacedToolName("paket__ausdruecklich");
        await Policy.SetAsync(tool, ApprovalEnforcement.Queue, ct);
        await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Client, ct);
        try
        {
            Policy.EffectiveFor(tool, declaredByCatalog: true).Should().Be(ApprovalEnforcement.Queue);
        }
        finally
        {
            await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Queue, ct);
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// Ein Werkzeug, das <b>niemand</b> als scharf führt, bleibt harmlos — auch wenn die Vorgabe
    /// auf <c>Client</c> steht. Die Vorgabe ist der Weg für Scharfes, keine Markierung für alles.
    /// </summary>
    [Fact]
    public async Task The_default_does_not_make_anything_sensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Client, ct);
        try
        {
            Policy.EffectiveFor(new NamespacedToolName("srv__harmlos"), declaredByCatalog: false)
                .Should().BeNull();
        }
        finally
        {
            await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Queue, ct);
        }
    }

    /// <summary>
    /// Die Vorgabe überlebt einen Neustart — sie liegt in der Datenbank, nicht nur im Speicher.
    /// Eine Sicherheitseinstellung, die beim Neustart still zurückspringt, wäre schlimmer als
    /// keine: Niemand prüft sie ein zweites Mal.
    /// </summary>
    [Fact]
    public async Task The_default_survives_a_reload()
    {
        var ct = TestContext.Current.CancellationToken;
        await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Client, ct);
        try
        {
            await Policy.LoadAsync(ct);
            Policy.DefaultEnforcement.Should().Be(ApprovalEnforcement.Client);
        }
        finally
        {
            await Policy.SetDefaultEnforcementAsync(ApprovalEnforcement.Queue, ct);
        }
    }
}
