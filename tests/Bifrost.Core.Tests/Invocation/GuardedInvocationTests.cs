using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Guardrails;
using Xunit;

namespace Bifrost.Core.Tests.Invocation;

/// <summary>
/// ADR-0011 im Zusammenspiel mit der Pipeline. Der wichtigste Punkt hier ist nicht, dass
/// blockiert wird — sondern dass die beiden Richtungen unterscheidbar bleiben, weil in der
/// Ergebnis-Richtung der Seiteneffekt bereits eingetreten ist.
/// </summary>
public class GuardedInvocationTests
{
    private readonly InvokerTestWorld _w = new();

    private static SecretGuard GuardFor(GuardDirection direction, GuardMode mode = GuardMode.Block)
        => new([new GuardRule("test", "Test-Zugangsdaten", @"\bSEKRET-\d{4}\b", "SEKRET-", direction, mode)]);

    [Fact]
    public async Task Secret_in_arguments_is_blocked_before_the_upstream_is_contacted()
    {
        var admin = _w.RegisterAdmin();
        var invoker = _w.WithGuard(GuardFor(GuardDirection.Outbound));

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "SEKRET-1234" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Denied);
        _w.Connection.LastToolName.Should().BeNull(
            "in der ausgehenden Richtung darf der Seiteneffekt gar nicht erst entstehen");
        result.ErrorMessage.Should().Contain("nicht kontaktiert");
    }

    [Fact]
    public async Task Secret_in_result_is_withheld_but_the_call_already_happened()
    {
        // Der Kern der Entscheidung E1: Hier IST der Call gelaufen. Sagt die Meldung das nicht,
        // wiederholt ein Agent den Aufruf — und legt das Issue ein zweites Mal an.
        var admin = _w.RegisterAdmin();
        _w.Connection.ResultOverride = """{"config":"token=SEKRET-1234"}""";
        var invoker = _w.WithGuard(GuardFor(GuardDirection.Inbound));

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.GuardBlocked,
            "der Status muss von Denied unterscheidbar sein — der Upstream wurde aufgerufen");
        _w.Connection.LastToolName.Should().Be("echo", "der Call hat stattgefunden");
        result.Content.Should().BeNull("das Ergebnis wird zurückgehalten");
        result.ErrorMessage.Should().Contain("ausgeführt").And.Contain("NICHT wiederholen");
    }

    [Fact]
    public async Task Blocked_call_is_audited_so_the_side_effect_is_traceable()
    {
        var admin = _w.RegisterAdmin();
        _w.Connection.ResultOverride = """{"config":"token=SEKRET-1234"}""";
        var invoker = _w.WithGuard(GuardFor(GuardDirection.Inbound));

        await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        var evt = _w.Audit.Events.Should().ContainSingle().Subject;
        evt.Status.Should().Be(InvocationStatus.GuardBlocked,
            "sonst fehlt später genau die Zeile, die erklärt, warum eine Aktion ausgeführt wurde");
        evt.Tool.Should().Be(_w.Echo.Value);
    }

    [Fact]
    public async Task Observe_mode_lets_the_call_through()
    {
        var admin = _w.RegisterAdmin();
        _w.Connection.ResultOverride = """{"config":"token=SEKRET-1234"}""";
        var invoker = _w.WithGuard(GuardFor(GuardDirection.Inbound, GuardMode.Observe));

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success, "Probelauf zählt nur mit");
        result.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task Payload_above_the_scan_limit_is_not_let_through()
    {
        // ADR-0011 E4: Ungeprüft heißt blockiert — sonst ist die Größengrenze der blinde Fleck,
        // den man ansteuert, um an der Prüfung vorbeizukommen.
        var admin = _w.RegisterAdmin();
        _w.Connection.ResultOverride = $$"""{"blob":"{{new string('x', 5000)}}"}""";
        var invoker = _w.WithGuard(
            GuardFor(GuardDirection.Inbound), new GuardOptions { MaxScanChars = 1000 });

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.GuardBlocked);
        result.ErrorMessage.Should().Contain("Prüfgrenze").And.Contain("BIFROST_MAX_RESULT_CHARS",
            "die Meldung muss den Ausweg nennen, statt den Betreiber raten zu lassen");
    }

    [Fact]
    public async Task Without_a_guard_nothing_changes()
    {
        var admin = _w.RegisterAdmin();
        _w.Connection.ResultOverride = """{"config":"token=SEKRET-1234"}""";

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "SEKRET-1234" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success, "ohne konfigurierte Guardrail bleibt alles wie bisher");
    }
}
