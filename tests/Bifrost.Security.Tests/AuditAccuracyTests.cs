using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Security.Tests.Infrastructure;
using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// <b>Invariante 6:</b> Das Audit haelt den <em>tatsaechlichen</em> Ausgang eines Aufrufs fest —
/// bei Erfolg, Ablehnung, Validierungsfehler, Freigabebedarf, Zeitueberschreitung und
/// zurueckgehaltenem Ergebnis.
/// <para>
/// <b>Warum das eine Sicherheitsinvariante ist und keine Fleissaufgabe:</b> Das Audit ist die
/// einzige Quelle, aus der sich hinterher beantworten laesst, was passiert ist. Ein Ausgang, der
/// falsch verbucht wird, ist schlimmer als einer, der fehlt — <c>GuardBlocked</c> als
/// <c>Denied</c> zu buchen behauptet, der Aufruf sei nicht gelaufen, obwohl der Seiteneffekt beim
/// Upstream bereits eingetreten ist. Genau dieser Unterschied steht als Begruendung an
/// <see cref="InvocationStatus.GuardBlocked"/>.
/// </para>
/// </summary>
public class AuditAccuracyTests
{
    private static AuditEvent SingleToolCall(RecordingAuditSink audit)
    {
        var calls = audit.Events.Where(evt => evt.Kind is AuditEventKind.ToolCall).ToArray();
        calls.Should().HaveCount(1, "ein Aufruf ist eine Zeile — nicht keine und nicht zwei");
        return calls[0];
    }

    [Fact]
    public async Task Success_is_audited_as_success()
    {
        var world = new InvokerWorld();
        world.Connection.ResponseJson = """{"content":[{"type":"text","text":"ok"}]}""";
        var caller = world.RegisterAdmin();

        var result = await world.Invoker.InvokeAsync(
            world.Request(caller, new { message = "hallo" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success);
        var entry = SingleToolCall(world.Audit);
        entry.Status.Should().Be(InvocationStatus.Success);
        entry.Tool.Should().Be(world.Echo.Value);
        entry.Caller.Should().Be(caller);
        entry.Duration.Should().NotBeNull("ohne Dauer ist die Zeile fuer eine Auswertung wertlos");
    }

    [Fact]
    public async Task A_denied_call_is_audited_as_denied()
    {
        var world = new InvokerWorld();
        // Eine Identitaet ohne jeden Grant: Default-Deny greift.
        var caller = world.RegisterAgent();

        var result = await world.Invoker.InvokeAsync(
            world.Request(caller, new { message = "hallo" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Denied);
        SingleToolCall(world.Audit).Status.Should().Be(InvocationStatus.Denied);
    }

    [Fact]
    public async Task A_schema_violation_is_audited_as_validation_failed()
    {
        var world = new InvokerWorld();
        var caller = world.RegisterAdmin();

        // 'message' ist im Schema als Pflichtfeld deklariert.
        var result = await world.Invoker.InvokeAsync(
            world.Request(caller, new { falsch = 1 }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
        var entry = SingleToolCall(world.Audit);
        entry.Status.Should().Be(InvocationStatus.ValidationFailed);
        entry.RedactedArguments.Should().NotBeNull(
            "gerade bei einem Validierungsfehler ist die Frage, WAS ankam — maskiert, aber da");
    }

    [Fact]
    public async Task A_call_that_waits_for_approval_is_audited_as_approval_required()
    {
        var world = new InvokerWorld();
        var caller = world.RegisterAdmin();
        var store = new QueueingApprovalStore();
        var invoker = world.WithApproval(new AlwaysApprovalPolicy(world.Echo), store);

        var result = await invoker.InvokeAsync(
            world.Request(caller, new { message = "hallo" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ApprovalRequired);
        SingleToolCall(world.Audit).Status.Should().Be(InvocationStatus.ApprovalRequired,
            "„darf nach Freigabe\" ist nicht dasselbe wie „darf nie\" — im Audit auch nicht");
        store.Enqueued.Should().Be(1);
    }

    [Fact]
    public async Task A_timeout_is_audited_as_timeout()
    {
        var world = new InvokerWorld();
        world.Connection.HangForever = true;
        var caller = world.RegisterAdmin();

        var result = await world.Invoker.InvokeAsync(
            world.Request(caller, new { message = "hallo" }, TimeSpan.FromMilliseconds(150)),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Timeout);
        SingleToolCall(world.Audit).Status.Should().Be(InvocationStatus.Timeout);
    }

    [Fact]
    public async Task A_withheld_result_is_audited_as_guard_blocked_and_not_as_denied()
    {
        var world = new InvokerWorld();
        world.Connection.ResponseJson =
            """{"content":[{"type":"text","text":"AKIAIOSFODNN7EXAMPLE"}]}""";
        var caller = world.RegisterAdmin();
        var invoker = world.WithGuard(new Bifrost.Core.Guardrails.SecretGuard(
            Bifrost.Core.Guardrails.BuiltInGuardRules.All));

        var result = await invoker.InvokeAsync(
            world.Request(caller, new { message = "hallo" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.GuardBlocked);
        SingleToolCall(world.Audit).Status.Should().Be(InvocationStatus.GuardBlocked,
            "der Upstream-Call ist gelaufen, der Seiteneffekt ist eingetreten — als Denied verbucht "
            + "behauptet die Zeile das Gegenteil");
    }

    [Fact]
    public async Task An_unknown_tool_is_audited_as_tool_not_found()
    {
        var world = new InvokerWorld();
        var caller = world.RegisterAdmin();

        var result = await world.Invoker.InvokeAsync(
            new ToolInvocationRequest(
                caller, CallOrigin.Mcp, new NamespacedToolName("gibtesnicht__tool"),
                JsonSerializer.SerializeToElement(new { }), null),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ToolNotFound);
        SingleToolCall(world.Audit).Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    [Fact]
    public async Task An_upstream_failure_is_audited_as_upstream_error()
    {
        var world = new InvokerWorld();
        world.Connection.Throw = new InvalidOperationException("Upstream kaputt");
        var caller = world.RegisterAdmin();

        var result = await world.Invoker.InvokeAsync(
            world.Request(caller, new { message = "hallo" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.UpstreamError);
        SingleToolCall(world.Audit).Status.Should().Be(InvocationStatus.UpstreamError);
    }

    /// <summary>
    /// <b>Der Waechter.</b> Er liest die Ausgaenge aus dem Aufzaehlungstyp und verlangt fuer jeden
    /// einen Test in dieser Datei — ueber den Methodennamen, nicht ueber eine Liste.
    /// <para>
    /// <b>Wie er bei einer neuen Stelle rot wird:</b> Ein neuer Wert in
    /// <see cref="InvocationStatus"/> — etwa „RateLimited" oder „PolicyBlocked" — hat hier keinen
    /// Test und macht den Waechter rot. Der Ausgang muss dann entweder geprueft oder ausdruecklich
    /// als nicht auditierbar begruendet werden. Genau das fehlte beim Redactor: die Stelle, an der
    /// das Vergessen auffaellt.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_invocation_outcome_has_a_test_in_this_file()
    {
        var testedNames = typeof(AuditAccuracyTests)
            .GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(FactAttribute), false).Length > 0)
            .Select(method => method.Name.Replace("_", string.Empty, StringComparison.Ordinal))
            .ToArray();

        var untested = Enum.GetNames<InvocationStatus>()
            .Where(status => !testedNames.Any(name =>
                name.Contains(status, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        untested.Should().BeEmpty(
            "jeder Ausgang eines Aufrufs muss im Audit richtig ankommen. Ohne Test fuer diesen "
            + "Ausgang ist nicht belegt, dass er ueberhaupt eine Zeile erzeugt. Offen: "
            + string.Join(", ", untested));
    }

    private sealed class AlwaysApprovalPolicy(NamespacedToolName tool) : IApprovalPolicy
    {
        public bool RequiresApproval(NamespacedToolName candidate) => candidate == tool;

        public bool IsSensitive(NamespacedToolName candidate) => candidate == tool;

        public ApprovalEnforcement? EnforcementFor(NamespacedToolName candidate)
            => candidate == tool ? ApprovalEnforcement.Queue : null;

        public ApprovalEnforcement DefaultEnforcement => ApprovalEnforcement.Queue;

        public ApprovalEnforcement? EffectiveFor(NamespacedToolName candidate, bool declaredByCatalog)
            => candidate == tool || declaredByCatalog ? ApprovalEnforcement.Queue : null;

        public IReadOnlyCollection<NamespacedToolName> All => [tool];

        public Task SetDefaultEnforcementAsync(ApprovalEnforcement enforcement, CancellationToken ct)
            => Task.CompletedTask;

        public Task SetAsync(NamespacedToolName candidate, bool required, CancellationToken ct)
            => Task.CompletedTask;

        public Task SetAsync(NamespacedToolName candidate, ApprovalEnforcement? enforcement, CancellationToken ct)
            => Task.CompletedTask;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class QueueingApprovalStore : IApprovalStore
    {
        public int Enqueued { get; private set; }

        public Task<Guid?> TryConsumeApprovalAsync(
            IdentityId caller, NamespacedToolName tool, string argumentFingerprint, CancellationToken ct)
            => Task.FromResult<Guid?>(null);

        public Task CompleteAsync(Guid taskId, TaskFailure? failure, CancellationToken ct)
            => Task.CompletedTask;

        public Task<Guid> EnqueueAsync(ApprovalRequest request, CancellationToken ct)
        {
            Enqueued++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ApprovalRequest>>([]);

        public Task DecideAsync(Guid requestId, bool approved, CancellationToken ct)
            => Task.CompletedTask;
    }
}
