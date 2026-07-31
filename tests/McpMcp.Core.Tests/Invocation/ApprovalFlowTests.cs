using System.Collections.Concurrent;
using AwesomeAssertions;
using McpMcp.Abstractions;
using Xunit;

namespace McpMcp.Core.Tests.Invocation;

/// <summary>
/// FR-32 / ADR-0012 durch die Pipeline: blockieren → freigeben → derselbe Call läuft EINMALIG,
/// ein anderer nicht, und ohne Seiteneffekt beim Blockieren.
/// </summary>
public class ApprovalFlowTests
{
    private readonly InvokerTestWorld _w = new();

    private sealed class FakePolicy : IApprovalPolicy
    {
        private readonly HashSet<NamespacedToolName> _tools;
        public FakePolicy(params NamespacedToolName[] tools) => _tools = [.. tools];
        public bool RequiresApproval(NamespacedToolName tool) => _tools.Contains(tool);
        public bool IsSensitive(NamespacedToolName tool) => _tools.Contains(tool);
        public ApprovalEnforcement? EnforcementFor(NamespacedToolName tool)
            => _tools.Contains(tool) ? ApprovalEnforcement.Queue : null;
        public IReadOnlyCollection<NamespacedToolName> All => _tools;
        public Task SetAsync(NamespacedToolName tool, bool required, CancellationToken ct) => Task.CompletedTask;
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

    /// <summary>Minimaler In-Memory-Store: genau das Verhalten aus ADR-0012, ohne DB.</summary>
    private sealed class FakeStore : IApprovalStore
    {
        private readonly ConcurrentDictionary<string, ApprovalState> _byKey = new();
        private readonly ConcurrentDictionary<string, Guid> _ids = new();

        public int Enqueued { get; private set; }

        /// <summary>Vorgänge, die nach dem Aufruf abgeschlossen wurden — mit ihrem Ergebnis.</summary>
        public ConcurrentDictionary<Guid, TaskFailure?> Completed { get; } = new();

        private static string Key(IdentityId c, NamespacedToolName t, string fp) => $"{c.Value:N}|{t.Value}|{fp}";

        public Task<Guid?> TryConsumeApprovalAsync(IdentityId caller, NamespacedToolName tool, string fp, CancellationToken ct)
        {
            var key = Key(caller, tool, fp);
            if (_byKey.TryGetValue(key, out var s) && s == ApprovalState.Approved)
            {
                _byKey[key] = ApprovalState.Consumed; // einmalig
                return Task.FromResult<Guid?>(_ids.GetOrAdd(key, _ => Guid.NewGuid()));
            }

            return Task.FromResult<Guid?>(null);
        }

        public Task CompleteAsync(Guid taskId, TaskFailure? failure, CancellationToken ct)
        {
            Completed[taskId] = failure;
            return Task.CompletedTask;
        }

        public Task<Guid> EnqueueAsync(ApprovalRequest r, CancellationToken ct)
        {
            Enqueued++;
            var key = Key(r.Caller, r.Tool, r.ArgumentFingerprint);
            _byKey.TryAdd(key, ApprovalState.Pending);
            return Task.FromResult(_ids.GetOrAdd(key, _ => Guid.NewGuid()));
        }

        public void Approve(IdentityId caller, NamespacedToolName tool, string fp)
            => _byKey[Key(caller, tool, fp)] = ApprovalState.Approved;

        public Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ApprovalRequest>>([]);
        public Task DecideAsync(Guid id, bool approved, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Approval_required_tool_is_blocked_without_side_effect()
    {
        var admin = _w.RegisterAdmin();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), new FakeStore());

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ApprovalRequired);
        result.ErrorMessage.Should().Contain("Freigabe").And.Contain("erneut absetzen");
        _w.Connection.LastToolName.Should().BeNull("der Call darf nicht ausgeführt worden sein");
    }

    [Fact]
    public async Task After_approval_the_same_call_runs_exactly_once()
    {
        var admin = _w.RegisterAdmin();
        var store = new FakeStore();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), store);

        // 1) Blockiert, Anfrage in der Queue.
        var first = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);
        first.Status.Should().Be(InvocationStatus.ApprovalRequired);

        // 2) Mensch gibt genau diesen Aufruf frei (gleicher Fingerprint).
        var redacted = _w.Redaction.RedactArguments(
            _w.Echo, System.Text.Json.JsonSerializer.SerializeToElement(new { message = "hi" }));
        var fp = McpMcp.Core.Approvals.ApprovalFingerprint.Compute(admin, _w.Echo, redacted);
        store.Approve(admin, _w.Echo, fp);

        // 3) Retry desselben Calls läuft durch.
        var second = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);
        second.Status.Should().Be(InvocationStatus.Success);

        // 4) Ein weiterer identischer Call ist wieder blockiert — Freigabe war einmalig.
        var third = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);
        third.Status.Should().Be(InvocationStatus.ApprovalRequired);
    }

    /// <summary>
    /// Ein eingelöster Vorgang bekommt einen Abschluss. Vorher blieb er auf <c>Working</c> stehen
    /// und lief still in den Verfall — die Vorgangsliste zeigte für einen erfolgreichen Aufruf
    /// dauerhaft „läuft" und später „abgelaufen". <c>TaskState.Completed</c> hatte damit gar keinen
    /// Produzenten.
    /// </summary>
    [Fact]
    public async Task A_consumed_task_is_completed_after_the_call()
    {
        var admin = _w.RegisterAdmin();
        var store = new FakeStore();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), store);

        await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        var redacted = _w.Redaction.RedactArguments(
            _w.Echo, System.Text.Json.JsonSerializer.SerializeToElement(new { message = "hi" }));
        var fp = McpMcp.Core.Approvals.ApprovalFingerprint.Compute(admin, _w.Echo, redacted);
        store.Approve(admin, _w.Echo, fp);

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success);
        store.Completed.Should().ContainSingle()
            .Which.Value.Should().BeNull("ein erfolgreicher Aufruf schließt den Vorgang ohne Fehler ab");
    }

    /// <summary>
    /// Scheitert der Aufruf nach der Freigabe, wird der Vorgang als gescheitert abgeschlossen — mit
    /// dem Status als maschinenlesbarem Code. Ihn als „erledigt" zu führen wäre falsch, ihn offen zu
    /// lassen ebenso.
    /// </summary>
    [Fact]
    public async Task A_failing_call_fails_the_task_too()
    {
        var admin = _w.RegisterAdmin();
        var store = new FakeStore();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), store);
        await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        var redacted = _w.Redaction.RedactArguments(
            _w.Echo, System.Text.Json.JsonSerializer.SerializeToElement(new { message = "hi" }));
        var fp = McpMcp.Core.Approvals.ApprovalFingerprint.Compute(admin, _w.Echo, redacted);
        store.Approve(admin, _w.Echo, fp);
        _w.Connection.CallException = new IOException("Upstream weg (Test).");

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.UpstreamError);
        store.Completed.Should().ContainSingle()
            .Which.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Approval_does_not_transfer_to_a_different_argument()
    {
        var admin = _w.RegisterAdmin();
        var store = new FakeStore();
        var invoker = _w.WithApproval(new FakePolicy(_w.Echo), store);

        // Freigabe für message="a" …
        var redacted = _w.Redaction.RedactArguments(
            _w.Echo, System.Text.Json.JsonSerializer.SerializeToElement(new { message = "a" }));
        store.Approve(admin, _w.Echo, McpMcp.Core.Approvals.ApprovalFingerprint.Compute(admin, _w.Echo, redacted));

        // … deckt einen Call mit message="b" NICHT ab.
        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "b" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ApprovalRequired,
            "eine Freigabe bindet an die konkreten Argumente, nicht an das Tool");
    }

    [Fact]
    public async Task Tool_without_approval_policy_runs_normally()
    {
        var admin = _w.RegisterAdmin();
        var invoker = _w.WithApproval(new FakePolicy(/* leer */), new FakeStore());

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success);
    }

    [Fact]
    public async Task Manifest_risk_requires_approval_without_a_manually_configured_policy()
    {
        var world = new InvokerTestWorld(echoRequiresApproval: true);
        var admin = world.RegisterAdmin();
        var invoker = world.WithApproval(new FakePolicy(), new FakeStore());

        var result = await invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, world.Echo, new { message = "danger" }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ApprovalRequired);
        world.Connection.LastToolName.Should().BeNull();
    }
}
