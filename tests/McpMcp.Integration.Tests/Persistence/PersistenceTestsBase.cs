using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Audit;
using McpMcp.Core.Rbac;
using McpMcp.Persistence;
using McpMcp.Persistence.Audit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace McpMcp.Integration.Tests.Persistence;

internal sealed class TestDbContextFactory : IDbContextFactory<McpMcpDbContext>
{
    private readonly DbContextOptions<McpMcpDbContext> _options;

    public TestDbContextFactory(DbContextOptions<McpMcpDbContext> options) => _options = options;

    public McpMcpDbContext CreateDbContext() => new(_options);
}

/// <summary>
/// Provider-parametrisierte Persistenz-Tests (WP3-DoD): identische Suite läuft gegen
/// SQLite (Datei) und PostgreSQL (Testcontainer). Subklassen liefern die Optionen.
/// </summary>
public abstract class PersistenceTestsBase : IAsyncLifetime
{
    private const string Secret = "SUPERSECRET_VALUE_12345";

    internal IDbContextFactory<McpMcpDbContext> Factory { get; private set; } = null!;

    protected IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

    protected abstract Task<DbContextOptions<McpMcpDbContext>?> CreateOptionsAsync();

    /// <summary>Liefert eine zusätzliche, noch uninitialisierte Datenbank — für die Migrations-/Upgrade-Tests.</summary>
    protected abstract Task<DbContextOptions<McpMcpDbContext>> CreateFreshOptionsAsync(string name);

    protected abstract void MarkSkippedIfUnavailable();

    /// <summary>Die v1.0-Baseline des jeweiligen Providers — Ausgangspunkt des Legacy-Upgrade-Tests.</summary>
    protected abstract string InitialCreateMigration { get; }

    public async ValueTask InitializeAsync()
    {
        var options = await CreateOptionsAsync();
        if (options is null)
        {
            return; // Provider nicht verfügbar — Tests skippen einzeln
        }

        Factory = new TestDbContextFactory(options);
        // Tests nutzen denselben Migrationspfad wie der Host (v1.1), nicht mehr EnsureCreated.
        await new DatabaseInitializer(Factory).InitializeAsync(TestContext.Current.CancellationToken);
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected static UpstreamServerConfig ConfigWithSecret(string slug = "srv") => new(
        slug, $"Server {slug}", UpstreamTransportKind.Stdio, Enabled: true,
        Stdio: new StdioTransportOptions(
            "cmd", ["--arg"],
            new Dictionary<string, string> { ["API_TOKEN"] = Secret }));

    [Fact]
    public async Task ConfigStore_roundtrips_versions_and_encrypts_payload()
    {
        MarkSkippedIfUnavailable();
        var store = new EfUpstreamConfigStore(Factory, DataProtection);
        var id = ServerId.New();

        var v1 = await store.AppendVersionAsync(id, ConfigWithSecret("alt"), TestContext.Current.CancellationToken);
        var v2 = await store.AppendVersionAsync(id, ConfigWithSecret("neu"), TestContext.Current.CancellationToken);

        v1.Value.Should().Be(1);
        v2.Value.Should().Be(2);
        (await store.GetVersionAsync(id, v1, TestContext.Current.CancellationToken))!.Slug.Should().Be("alt");
        (await store.GetVersionAsync(id, v2, TestContext.Current.CancellationToken))!.Stdio!.EnvironmentVariables!["API_TOKEN"]
            .Should().Be(Secret, "die Entschlüsselung muss verlustfrei sein");
        (await store.GetHistoryAsync(id, TestContext.Current.CancellationToken)).Select(h => h.Version.Value).Should().Equal(1, 2);

        // NFR-04: der persistierte Blob darf das Secret nicht im Klartext enthalten
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var payloads = await db.ConfigVersions.AsNoTracking().Select(r => r.Payload).ToListAsync();
            var secretBytes = Encoding.UTF8.GetBytes(Secret);
            foreach (var payload in payloads)
            {
                ContainsSubsequence(payload, secretBytes).Should().BeFalse("Config-Blobs sind verschlüsselt (NFR-04)");
            }
        }

        await store.RemoveAsync(id, TestContext.Current.CancellationToken);
        (await store.GetHistoryAsync(id, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task RbacStore_persists_and_rehydrates_directory()
    {
        MarkSkippedIfUnavailable();
        var writeDirectory = new InMemoryRbacDirectory();
        var store = new PersistentRbacStore(Factory, writeDirectory);

        var role = new Role(RoleId.New(), "reader",
            [new Grant(new PermissionScope(ServerId.New(), null), [ToolAction.UseTool, ToolAction.ReadResource])],
            new RateLimit(42));
        var profile = new ToolProfile(ProfileId.New(), "profil",
            [NamespacedToolName.Create("srv", "tool")], LazyToolsEnabled: true);
        var identity = new Identity(IdentityId.New(), "agent-x", IdentityKind.Agent, [role.Id], profile.Id);

        await store.UpsertRoleAsync(role, TestContext.Current.CancellationToken);
        await store.UpsertProfileAsync(profile, TestContext.Current.CancellationToken);
        await store.UpsertIdentityAsync(identity, TestContext.Current.CancellationToken);

        // Frisches Directory aus der DB hydratisieren — muss inhaltsgleich sein
        var freshDirectory = new InMemoryRbacDirectory();
        await new PersistentRbacStore(Factory, freshDirectory).LoadAsync(TestContext.Current.CancellationToken);

        freshDirectory.GetRole(role.Id).Should().BeEquivalentTo(role);
        freshDirectory.GetProfile(profile.Id).Should().BeEquivalentTo(profile);
        freshDirectory.GetIdentity(identity.Id).Should().BeEquivalentTo(identity);

        await store.RemoveIdentityAsync(identity.Id, TestContext.Current.CancellationToken);
        await store.RemoveRoleAsync(role.Id, TestContext.Current.CancellationToken);
        await store.RemoveProfileAsync(profile.Id, TestContext.Current.CancellationToken);
        var emptied = new InMemoryRbacDirectory();
        await new PersistentRbacStore(Factory, emptied).LoadAsync(TestContext.Current.CancellationToken);
        emptied.GetIdentity(identity.Id).Should().BeNull();
        emptied.GetRole(role.Id).Should().BeNull();
    }

    [Fact]
    public async Task ApiKeys_issue_validate_revoke_and_expiry()
    {
        MarkSkippedIfUnavailable();
        var service = new ApiKeyService(Factory);
        var identity = IdentityId.New();

        var issued = await service.IssueAsync(identity, "test-key", expiresAt: null, TestContext.Current.CancellationToken);
        issued.PlaintextKey.Should().StartWith("mcpk_");

        (await service.ValidateAsync(issued.PlaintextKey, TestContext.Current.CancellationToken)).Should().Be(identity);
        (await service.ValidateAsync(issued.PlaintextKey + "x", TestContext.Current.CancellationToken)).Should().BeNull("manipuliertes Secret");
        (await service.ValidateAsync("mcpk_falschesformat", TestContext.Current.CancellationToken)).Should().BeNull();
        (await service.ValidateAsync("völlig-falsch", TestContext.Current.CancellationToken)).Should().BeNull();

        // Hash-Speicherung: Klartext-Secret darf nirgends in der Zeile stehen (NFR-04)
        var secretPart = issued.PlaintextKey.Split('_')[2];
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var row = await db.ApiKeys.AsNoTracking().SingleAsync(r => r.Id == issued.KeyId);
            row.Hash.Should().NotContain(secretPart);
        }

        var expired = await service.IssueAsync(identity, "abgelaufen", DateTimeOffset.UtcNow.AddHours(-1), TestContext.Current.CancellationToken);
        (await service.ValidateAsync(expired.PlaintextKey, TestContext.Current.CancellationToken)).Should().BeNull("Gültigkeitsfenster (FR-31)");

        await service.RevokeAsync(issued.KeyId, TestContext.Current.CancellationToken);
        (await service.ValidateAsync(issued.PlaintextKey, TestContext.Current.CancellationToken)).Should().BeNull("Widerruf wirkt sofort");

        var list = await service.ListAsync(identity, TestContext.Current.CancellationToken);
        list.Should().HaveCount(2);
        list.Single(k => k.KeyId == issued.KeyId).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Audit_1000_mixed_calls_yield_exactly_1000_attributed_redacted_rows()
    {
        MarkSkippedIfUnavailable();
        var sink = new ChannelAuditSink();
        var writer = new AuditBatchWriter(sink, Factory, new PersistenceOptions
        {
            AuditFlushInterval = TimeSpan.FromMilliseconds(100),
            AuditMaxBatchSize = 200,
        });
        using var cts = new CancellationTokenSource();
        var run = writer.RunAsync(cts.Token);

        var redaction = new RedactionService();
        var tool = NamespacedToolName.Create("srv", "login");
        var identities = new[] { IdentityId.New(), IdentityId.New(), IdentityId.New() };
        var statuses = new[] { InvocationStatus.Success, InvocationStatus.Denied, InvocationStatus.UpstreamError, InvocationStatus.Timeout };
        var args = JsonSerializer.SerializeToElement(new { user = "anna", password = "streng-geheim" });

        for (var i = 0; i < 1000; i++)
        {
            sink.Record(new AuditEvent(
                DateTimeOffset.UtcNow,
                identities[i % identities.Length],
                CallOrigin.Mcp,
                AuditEventKind.ToolCall,
                ServerId.New(),
                tool.Value,
                statuses[i % statuses.Length],
                redaction.RedactArguments(tool, args),
                RequestBytes: 100,
                ResponseBytes: 200,
                Duration: TimeSpan.FromMilliseconds(5)));
        }

        await WaitForRowCountAsync(1000);
        cts.Cancel();
        await run;

        await using var db = await Factory.CreateDbContextAsync();
        (await db.AuditEvents.CountAsync()).Should().Be(1000, "PRD-Kriterium 5: exakt 1000 Zeilen für 1000 Calls");
        (await db.AuditEvents.CountAsync(r => r.CallerId == identities[0].Value)).Should().Be(334);
        (await db.AuditEvents.CountAsync(r => r.Status == (int)InvocationStatus.Denied)).Should().Be(250);
        sink.DroppedCount.Should().Be(0);

        var anyRow = await db.AuditEvents.AsNoTracking().FirstAsync();
        anyRow.RedactedArgumentsJson.Should().Contain("anna").And.NotContain("streng-geheim", "Secrets sind maskiert (FR-24)");
        anyRow.RedactedArgumentsJson.Should().Contain(RedactionService.Mask);
    }

    /// <summary>
    /// Feste Uhr für die Vorgangs-Tests. Ohne sie prüfte <c>TaskStore</c> die Frist gegen die
    /// Wanduhr, während die Fixtures ein festes Datum trugen — die Tests waren dann nur bis zu
    /// diesem Zeitpunkt grün und schlugen danach fehl, ohne dass sich Code geändert hätte.
    /// </summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Der Bezugszeitpunkt aller Vorgangs-Fixtures.</summary>
    private static readonly DateTimeOffset TaskNow = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Ein Store, dessen Uhr zum Fixture passt.</summary>
    private TaskStore TaskStoreAtFixtureTime() => new(Factory, new FixedClock(TaskNow));

    /// <summary>
    /// Baut einen Vorgang mit den Feldern, die ADR-0019 verlangt. `state` ist der Ausgangszustand.
    /// </summary>
    private static TaskRecord NewTask(
        IdentityId owner,
        string tool = "srv__do_thing",
        string fingerprint = "fp-1",
        TaskState state = TaskState.InputRequired,
        DateTimeOffset? expiresAt = null)
    {
        var now = TaskNow;
        return new TaskRecord(
            Guid.NewGuid(), owner, "agent-1", new NamespacedToolName(tool), null, CallOrigin.Mcp,
            CorrelationId: Guid.NewGuid(),
            State: state,
            Revision: 0,
            Progress: null,
            InputFingerprint: fingerprint,
            RedactedInput: JsonSerializer.Deserialize<JsonElement>("""{"path":"***"}"""),
            RedactedResult: null,
            Failure: null,
            ExpectedInputSchema: null,
            Cancellation: TaskCancellation.None,
            ClaimedAt: null,
            CreatedAt: now,
            UpdatedAt: now,
            ExpiresAt: expiresAt ?? now.AddHours(1));
    }

    /// <summary>
    /// Grundzüge von TaskV1: anlegen, lesen, fortschreiben. Die Revision steigt monoton — sie ist
    /// die Grundlage der optimistischen Konkurrenzkontrolle.
    /// </summary>
    [Fact]
    public async Task TaskStore_persists_and_advances_a_task()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = TaskStoreAtFixtureTime();
        var owner = IdentityId.New();

        var created = await store.CreateOrGetAsync(NewTask(owner), ct);
        created.Revision.Should().Be(0);

        var advanced = await store.UpdateAsync(
            new TaskUpdate(created.Id, State: TaskState.Working, Progress: 40), created.Revision, ct);
        advanced.Should().Be(TaskUpdateOutcome.Applied);

        var reread = await store.GetAsync(created.Id, ct);
        reread!.State.Should().Be(TaskState.Working);
        reread.Progress.Should().Be(40);
        reread.Revision.Should().Be(1, "jede Fortschreibung erhöht die Revision");
        reread.RedactedInput!.Value.GetProperty("path").GetString()
            .Should().Be("***", "persistiert wird nur die redigierte Eingabe");
    }

    /// <summary>
    /// Ein Retry des Aufrufers darf keine zweite Warteschlangen-Zeile erzeugen — dasselbe Verhalten,
    /// das die Freigabe-Queue schon hatte und das im Task-Modell aufgeht.
    /// </summary>
    [Fact]
    public async Task TaskStore_does_not_duplicate_an_open_task_for_the_same_call()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = TaskStoreAtFixtureTime();
        var owner = IdentityId.New();

        var first = await store.CreateOrGetAsync(NewTask(owner), ct);
        var second = await store.CreateOrGetAsync(NewTask(owner), ct);

        second.Id.Should().Be(first.Id);
        (await store.ListAsync(new TaskFilter(Owner: owner), ct)).TotalCount.Should().Be(1);
    }

    /// <summary>
    /// Zwei Schreiber auf demselben Vorgang: Der zweite verliert, statt den ersten zu überschreiben.
    /// Ohne diese Prüfung ginge ein Ergebnis oder ein Abbruch still verloren.
    /// </summary>
    [Fact]
    public async Task TaskStore_rejects_a_stale_revision()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = TaskStoreAtFixtureTime();
        var created = await store.CreateOrGetAsync(NewTask(IdentityId.New()), ct);

        (await store.UpdateAsync(new TaskUpdate(created.Id, State: TaskState.Working), 0, ct))
            .Should().Be(TaskUpdateOutcome.Applied);
        (await store.UpdateAsync(new TaskUpdate(created.Id, Progress: 99), 0, ct))
            .Should().Be(TaskUpdateOutcome.RevisionMismatch, "die Revision 0 ist verbraucht");
    }

    /// <summary>
    /// Terminal heißt unveränderlich (ADR-0019). Ein spät eintreffender Schreiber darf ein
    /// abgeschlossenes Ergebnis nicht überschreiben.
    /// </summary>
    [Fact]
    public async Task TaskStore_freezes_a_terminal_task()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = TaskStoreAtFixtureTime();
        var created = await store.CreateOrGetAsync(NewTask(IdentityId.New()), ct);

        var result = JsonSerializer.Deserialize<JsonElement>("""{"ok":true}""");
        (await store.UpdateAsync(
            new TaskUpdate(created.Id, State: TaskState.Completed, RedactedResult: result),
            created.Revision, ct)).Should().Be(TaskUpdateOutcome.Applied);

        (await store.UpdateAsync(new TaskUpdate(created.Id, State: TaskState.Failed), 1, ct))
            .Should().Be(TaskUpdateOutcome.Terminal);
        (await store.GetAsync(created.Id, ct))!.State.Should().Be(TaskState.Completed);
    }

    /// <summary>
    /// Der heiße Pfad: Ein freigegebener Vorgang ist **einmalig** einlösbar. Der Zustandsautomat aus
    /// ADR-0019 kennt dafür keinen eigenen Zustand — deshalb der Claim-Zeitpunkt. Ohne ihn liefe ein
    /// zweiter identischer Call erneut durch, und eine Zustimmung wäre ein Dauerfreifahrtschein.
    /// </summary>
    [Fact]
    public async Task TaskStore_consumes_an_approved_task_exactly_once()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = TaskStoreAtFixtureTime();
        var owner = IdentityId.New();
        var tool = new NamespacedToolName("srv__do_thing");

        var created = await store.CreateOrGetAsync(NewTask(owner), ct);
        // Noch nicht freigegeben: nichts einzulösen.
        (await store.TryConsumeApprovedAsync(owner, tool, "fp-1", ct)).Should().BeFalse();

        // Freigabe = Übergang nach `working`.
        await store.UpdateAsync(new TaskUpdate(created.Id, State: TaskState.Working), created.Revision, ct);

        (await store.TryConsumeApprovedAsync(owner, tool, "fp-1", ct)).Should().BeTrue();
        (await store.TryConsumeApprovedAsync(owner, tool, "fp-1", ct))
            .Should().BeFalse("eine Freigabe gilt genau einmal");
        (await store.GetAsync(created.Id, ct))!.ClaimedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Die Bindung an den Argument-Fingerprint: Eine Freigabe für einen Aufruf deckt keinen anderen.
    /// </summary>
    [Fact]
    public async Task TaskStore_binds_an_approval_to_owner_tool_and_fingerprint()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = TaskStoreAtFixtureTime();
        var owner = IdentityId.New();
        var tool = new NamespacedToolName("srv__do_thing");

        var created = await store.CreateOrGetAsync(NewTask(owner, fingerprint: "fp-erlaubt"), ct);
        await store.UpdateAsync(new TaskUpdate(created.Id, State: TaskState.Working), created.Revision, ct);

        (await store.TryConsumeApprovedAsync(owner, tool, "fp-anders", ct))
            .Should().BeFalse("anderer Fingerprint");
        (await store.TryConsumeApprovedAsync(IdentityId.New(), tool, "fp-erlaubt", ct))
            .Should().BeFalse("anderer Aufrufer");
        (await store.TryConsumeApprovedAsync(owner, new NamespacedToolName("srv__other"), "fp-erlaubt", ct))
            .Should().BeFalse("anderes Tool");
        (await store.TryConsumeApprovedAsync(owner, tool, "fp-erlaubt", ct)).Should().BeTrue();
    }

    /// <summary>
    /// Ein abgelaufener Vorgang wird nicht still eingelöst und landet im Verfallslauf auf
    /// <c>expired</c> — nicht auf einem Terminalzustand, der ein Ergebnis behauptet.
    /// </summary>
    [Fact]
    public async Task TaskStore_expires_due_tasks_and_refuses_to_consume_them()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var past = TaskNow.AddHours(-1);
        var store = TaskStoreAtFixtureTime();
        var owner = IdentityId.New();
        var tool = new NamespacedToolName("srv__do_thing");

        var created = await store.CreateOrGetAsync(NewTask(owner, expiresAt: past), ct);
        await store.UpdateAsync(new TaskUpdate(created.Id, State: TaskState.Working), created.Revision, ct);

        (await store.TryConsumeApprovedAsync(owner, tool, "fp-1", ct))
            .Should().BeFalse("die Frist ist abgelaufen");

        (await store.ExpireDueAsync(past.AddMinutes(1), ct)).Should().Be(1);
        (await store.GetAsync(created.Id, ct))!.State.Should().Be(TaskState.Expired);
        (await store.ExpireDueAsync(past.AddMinutes(2), ct))
            .Should().Be(0, "ein terminaler Vorgang wird nicht erneut angefasst");
    }

    /// <summary>
    /// Die Audit-Correlation, die ADR-0019 voraussetzt und die es im Code bisher nicht gab: Zwei
    /// Ereignisse desselben Aufrufs tragen dieselbe Id und sind darüber wieder zusammenzuführen.
    /// </summary>
    [Fact]
    public async Task Audit_events_carry_a_correlation_id()
    {
        MarkSkippedIfUnavailable();
        var correlation = Guid.NewGuid();
        var caller = IdentityId.New();
        var tool = NamespacedToolName.Create("srv", "do_thing");
        var sink = new ChannelAuditSink();
        var writer = new AuditBatchWriter(sink, Factory, new PersistenceOptions
        {
            AuditFlushInterval = TimeSpan.FromMilliseconds(50),
        });
        using var cts = new CancellationTokenSource();
        var run = writer.RunAsync(cts.Token);

        // Zwei Zeilen zum selben Aufruf, eine ohne Bezug.
        sink.Record(new AuditEvent(
            DateTimeOffset.UtcNow, caller, CallOrigin.Mcp, AuditEventKind.ToolCall, null,
            tool.Value, InvocationStatus.ApprovalRequired, null, null, null, null,
            CorrelationId: correlation));
        sink.Record(new AuditEvent(
            DateTimeOffset.UtcNow, caller, CallOrigin.Mcp, AuditEventKind.ToolCall, null,
            tool.Value, InvocationStatus.Success, null, null, null, null,
            CorrelationId: correlation));
        sink.Record(new AuditEvent(
            DateTimeOffset.UtcNow, caller, CallOrigin.Mcp, AuditEventKind.ToolCall, null,
            tool.Value, InvocationStatus.Success, null, null, null, null));

        await WaitForRowCountAsync(3);
        cts.Cancel();
        await run;

        await using var db = await Factory.CreateDbContextAsync();
        var rows = await db.AuditEvents.AsNoTracking().ToListAsync();
        rows.Count(r => r.CorrelationId == correlation)
            .Should().Be(2, "beide Ereignisse gehören zum selben Aufruf");
        rows.Count(r => r.CorrelationId == null)
            .Should().Be(1, "ohne Aufrufbezug bleibt die Id leer");
    }

    /// <summary>
    /// Die Freigabe-Queue läuft auf der Task-Tabelle (ADR-0019, Entscheidung 1). Derselbe Vertrag,
    /// ein Unterbau — und die alte Tabelle bleibt dabei leer, weil nichts mehr dort landet.
    /// </summary>
    [Fact]
    public async Task Approvals_run_on_the_task_table()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var tasks = TaskStoreAtFixtureTime();
        var approvals = new TaskBackedApprovalStore(tasks);
        approvals.Should().BeAssignableTo<IApprovalStore>(
            "der Vertrag bleibt unverändert — Invoker, REST und UI merken vom Umbau nichts");
        var caller = IdentityId.New();
        var tool = NamespacedToolName.Create("srv", "delete_file");
        var now = DateTimeOffset.UtcNow;

        var id = await approvals.EnqueueAsync(new ApprovalRequest(
            Guid.NewGuid(), caller, "agent-1", tool, "fp-1",
            JsonSerializer.Deserialize<JsonElement>("""{"path":"***"}"""),
            ApprovalState.Pending, now, now.AddHours(1)), ct);

        // Der Vorgang liegt als Task, nicht in der alten Warteschlange.
        (await tasks.GetAsync(id, ct))!.State.Should().Be(TaskState.InputRequired);
        await using (var db = await Factory.CreateDbContextAsync(ct))
        {
            (await db.ApprovalRequests.CountAsync(ct))
                .Should().Be(0, "die alte Tabelle bekommt keine neuen Zeilen mehr");
        }

        // Sicht der UI: wartend.
        var pending = await approvals.ListAsync(ApprovalState.Pending, ct);
        pending.Should().ContainSingle(r => r.Id == id);
        pending[0].RedactedArguments!.Value.GetProperty("path").GetString().Should().Be("***");

        // Freigeben → einlösbar, genau einmal.
        await approvals.DecideAsync(id, approved: true, ct);
        (await approvals.ListAsync(ApprovalState.Approved, ct)).Should().ContainSingle(r => r.Id == id);
        (await approvals.TryConsumeApprovalAsync(caller, tool, "fp-1", ct)).Should().BeTrue();
        (await approvals.TryConsumeApprovalAsync(caller, tool, "fp-1", ct))
            .Should().BeFalse("eine Freigabe gilt einmalig (ADR-0012)");

        // Danach gilt sie als verbraucht — und nicht mehr als freigegeben.
        (await approvals.ListAsync(ApprovalState.Consumed, ct)).Should().ContainSingle(r => r.Id == id);
        (await approvals.ListAsync(ApprovalState.Approved, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Ablehnen ist kein stiller Zustand: Der Vorgang scheitert mit maschinenlesbarem Code, und die
    /// Freigabe-Sicht zeigt ihn als abgelehnt. Ein nachträgliches Einlösen ist ausgeschlossen.
    /// </summary>
    [Fact]
    public async Task A_denied_approval_fails_the_task_and_cannot_be_consumed()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var tasks = TaskStoreAtFixtureTime();
        var approvals = new TaskBackedApprovalStore(tasks);
        var caller = IdentityId.New();
        var tool = NamespacedToolName.Create("srv", "drop_table");
        var now = DateTimeOffset.UtcNow;

        var id = await approvals.EnqueueAsync(new ApprovalRequest(
            Guid.NewGuid(), caller, "agent-1", tool, "fp-2", null,
            ApprovalState.Pending, now, now.AddHours(1)), ct);

        await approvals.DecideAsync(id, approved: false, ct);

        var task = await tasks.GetAsync(id, ct);
        task!.State.Should().Be(TaskState.Failed);
        task.Failure!.Code.Should().Be(TaskBackedApprovalStore.DeniedCode);
        (await approvals.ListAsync(ApprovalState.Denied, ct)).Should().ContainSingle(r => r.Id == id);
        (await approvals.TryConsumeApprovalAsync(caller, tool, "fp-2", ct)).Should().BeFalse();

        // Idempotent: eine zweite Entscheidung ändert nichts mehr.
        await approvals.DecideAsync(id, approved: true, ct);
        (await tasks.GetAsync(id, ct))!.State.Should().Be(TaskState.Failed);
    }

    /// <summary>
    /// Bestehende Freigaben gehen beim Update nicht verloren (ADR-0019): Sie werden übernommen, die
    /// alte Tabelle bleibt stehen, und ein zweiter Start kopiert nichts doppelt.
    /// </summary>
    [Fact]
    public async Task Existing_approvals_are_migrated_once_and_keep_their_state()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var caller = IdentityId.New();
        var baseTicks = new DateTimeOffset(2026, 7, 26, 8, 0, 0, TimeSpan.Zero).UtcTicks;
        var pendingId = Guid.NewGuid();
        var consumedId = Guid.NewGuid();
        var deniedId = Guid.NewGuid();

        await using (var db = await Factory.CreateDbContextAsync(ct))
        {
            foreach (var (id, state) in new[]
            {
                (pendingId, ApprovalState.Pending),
                (consumedId, ApprovalState.Consumed),
                (deniedId, ApprovalState.Denied),
            })
            {
                db.ApprovalRequests.Add(new ApprovalRequestRow
                {
                    Id = id,
                    CallerId = caller.Value,
                    CallerDescription = "agent-alt",
                    Tool = "srv__legacy_tool",
                    Fingerprint = $"fp-{state}",
                    RedactedArgumentsJson = """{"secret":"***"}""",
                    State = (int)state,
                    RequestedAtTicks = baseTicks,
                    ExpiresAtTicks = baseTicks + TimeSpan.FromHours(2).Ticks,
                });
            }

            await db.SaveChangesAsync(ct);
        }

        var migration = new ApprovalToTaskMigration(Factory);
        (await migration.RunAsync(ct)).Should().Be(3);
        (await migration.RunAsync(ct)).Should().Be(0, "ein zweiter Start kopiert nichts doppelt");

        var tasks = TaskStoreAtFixtureTime();
        (await tasks.GetAsync(pendingId, ct))!.State.Should().Be(TaskState.InputRequired);
        (await tasks.GetAsync(deniedId, ct))!.Failure!.Code.Should().Be(TaskBackedApprovalStore.DeniedCode);

        // Der wichtigste Punkt der Übernahme: Eine bereits verbrauchte Freigabe darf nicht wieder
        // einlösbar werden, sonst würde aus einer verbrauchten Zustimmung eine zweite.
        var consumed = await tasks.GetAsync(consumedId, ct);
        consumed!.ClaimedAt.Should().NotBeNull();
        (await tasks.TryConsumeApprovedAsync(
            caller, new NamespacedToolName("srv__legacy_tool"), $"fp-{ApprovalState.Consumed}", ct))
            .Should().BeFalse();

        // Und die redigierten Argumente sind lesbar mitgekommen — nie die rohen.
        (await tasks.GetAsync(pendingId, ct))!.RedactedInput!.Value
            .GetProperty("secret").GetString().Should().Be("***");

        await using (var db = await Factory.CreateDbContextAsync(ct))
        {
            (await db.ApprovalRequests.CountAsync(ct))
                .Should().Be(3, "die alte Tabelle bleibt stehen — Löschen wäre unumkehrbar");
        }
    }

    [Fact]
    public async Task AuditQuery_filters_and_pages()
    {
        MarkSkippedIfUnavailable();
        var caller = IdentityId.New();
        var other = IdentityId.New();
        var baseTime = DateTimeOffset.UtcNow;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 30; i++)
            {
                db.AuditEvents.Add(new AuditEventRow
                {
                    Timestamp = baseTime.AddMinutes(-i),
                    CallerId = (i % 2 == 0 ? caller : other).Value,
                    Origin = (int)CallOrigin.Rest,
                    Kind = (int)AuditEventKind.ToolCall,
                    Tool = i < 10 ? "srv__a" : "srv__b",
                    Status = (int)(i % 3 == 0 ? InvocationStatus.Denied : InvocationStatus.Success),
                    CallerRoles = "agent [rolle]",
                });
            }

            await db.SaveChangesAsync();
        }

        var query = new EfAuditQuery(Factory);

        var byCaller = await query.QueryAsync(new AuditFilter(Caller: caller), TestContext.Current.CancellationToken);
        byCaller.TotalCount.Should().Be(15);
        byCaller.Items.Should().OnlyContain(e => e.Caller == caller);

        var denied = await query.QueryAsync(new AuditFilter(Status: InvocationStatus.Denied), TestContext.Current.CancellationToken);
        denied.TotalCount.Should().Be(10);

        var byTool = await query.QueryAsync(new AuditFilter(ToolPrefix: "srv__a"), TestContext.Current.CancellationToken);
        byTool.TotalCount.Should().Be(10);
        byTool.Items.Should().OnlyContain(e => e.CallerRoles == "agent [rolle]", "FR-21: Rolle wird mitgelesen");

        // FR-23: die UI sucht nach dem Server-Namespace, nicht nach dem vollen Tool-Namen —
        // mit exaktem Vergleich hätte das hier 0 statt 30 Treffer ergeben.
        var byPrefix = await query.QueryAsync(new AuditFilter(ToolPrefix: "srv__"), TestContext.Current.CancellationToken);
        byPrefix.TotalCount.Should().Be(30);

        var byOrigin = await query.QueryAsync(new AuditFilter(Origin: CallOrigin.Rest), TestContext.Current.CancellationToken);
        byOrigin.TotalCount.Should().Be(30);
        (await query.QueryAsync(new AuditFilter(Origin: CallOrigin.Mcp), TestContext.Current.CancellationToken))
            .TotalCount.Should().Be(0, "der Herkunft-Filter muss auch ausschließen");

        var page2 = await query.QueryAsync(new AuditFilter(Page: 2, PageSize: 12), TestContext.Current.CancellationToken);
        page2.Items.Should().HaveCount(12);
        page2.TotalCount.Should().Be(30);

        var window = await query.QueryAsync(
            new AuditFilter(From: baseTime.AddMinutes(-9.5), To: baseTime), TestContext.Current.CancellationToken);
        window.TotalCount.Should().Be(10);
        window.Items.Should().BeInDescendingOrder(e => e.Timestamp);
    }

    [Fact]
    public async Task Retention_removes_only_expired_events()
    {
        MarkSkippedIfUnavailable();
        var options = new PersistenceOptions { AuditRetention = TimeSpan.FromDays(7) };
        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.AuditEvents.Add(new AuditEventRow { Timestamp = DateTimeOffset.UtcNow.AddDays(-30), Kind = 0, Origin = 0 });
            db.AuditEvents.Add(new AuditEventRow { Timestamp = DateTimeOffset.UtcNow.AddDays(-8), Kind = 0, Origin = 0 });
            db.AuditEvents.Add(new AuditEventRow { Timestamp = DateTimeOffset.UtcNow.AddDays(-1), Kind = 0, Origin = 0 });
            await db.SaveChangesAsync();
        }

        var deleted = await new AuditRetentionJob(Factory, options).ExecuteOnceAsync(TestContext.Current.CancellationToken);

        deleted.Should().Be(2);
        await using var check = await Factory.CreateDbContextAsync();
        (await check.AuditEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UiUsers_create_validate_and_enforce_unique_username()
    {
        MarkSkippedIfUnavailable();
        var service = new UiUserService(Factory);
        var name = $"betreiber-{Guid.NewGuid():N}";

        (await service.AnyExistAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        var created = await service.CreateAsync(name, "geheimes-passwort", UiRole.Operator, TestContext.Current.CancellationToken);
        created.Role.Should().Be(UiRole.Operator);
        (await service.AnyExistAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        (await service.ValidateCredentialsAsync(name, "geheimes-passwort", TestContext.Current.CancellationToken))!.Id
            .Should().Be(created.Id);
        (await service.ValidateCredentialsAsync(name, "falsch", TestContext.Current.CancellationToken)).Should().BeNull();
        (await service.ValidateCredentialsAsync("gibtsnicht", "x", TestContext.Current.CancellationToken)).Should().BeNull();

        // Passwort-Hash liegt nie im Klartext (NFR-04)
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var row = await db.UiUsers.AsNoTracking().SingleAsync(u => u.Username == name);
            row.PasswordHash.Should().NotContain("geheimes-passwort");
        }

        var act = () => service.CreateAsync(name, "andere", UiRole.Admin, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*existiert bereits*");

        await service.SetPasswordAsync(created.Id, "neues-passwort", TestContext.Current.CancellationToken);
        (await service.ValidateCredentialsAsync(name, "neues-passwort", TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await service.ValidateCredentialsAsync(name, "geheimes-passwort", TestContext.Current.CancellationToken)).Should().BeNull();

        await service.DeleteAsync(created.Id, TestContext.Current.CancellationToken);
        (await service.ValidateCredentialsAsync(name, "neues-passwort", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Assets_version_and_retrieve()
    {
        MarkSkippedIfUnavailable();
        var store = new EfAssetStore(Factory);

        var id = await store.CreateAsync("mein-skill", "Ein Test-Skill", "Version-1-Inhalt", TestContext.Current.CancellationToken);
        var v2 = await store.PublishAsync(id, "Version-2-Inhalt", TestContext.Current.CancellationToken);
        v2.Value.Should().Be(2);

        (await store.GetAsync(id, null, TestContext.Current.CancellationToken)).Content.Should().Be("Version-2-Inhalt", "latest");
        (await store.GetAsync(id, new AssetVersion(1), TestContext.Current.CancellationToken)).Content.Should().Be("Version-1-Inhalt");

        var list = await store.ListAsync(TestContext.Current.CancellationToken);
        list.Should().ContainSingle(a => a.Id == id).Which.LatestVersion.Value.Should().Be(2);
    }

    /// <summary>
    /// v1.1 hebt PBKDF2 von 100k auf 600k Iterationen. Bestehende Hashes tragen ihre Iterationszahl
    /// im Format mit und müssen weiterhin verifizieren — sonst würde das Upgrade alle Logins sperren.
    /// </summary>
    [Fact]
    public async Task Password_hashed_with_legacy_iteration_count_still_verifies()
    {
        MarkSkippedIfUnavailable();
        const string password = "bestands-passwort";
        const int legacyIterations = 100_000;
        var username = $"altnutzer-{Guid.NewGuid():N}";

        // Hash im v1.0-Format (100k) von Hand erzeugen und direkt persistieren.
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, legacyIterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var legacyHash = $"{legacyIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";

        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.UiUsers.Add(new UiUserRow
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = legacyHash,
                Role = (int)UiRole.Admin,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = new UiUserService(Factory);
        (await service.ValidateCredentialsAsync(username, password, TestContext.Current.CancellationToken))
            .Should().NotBeNull("v1.0-Hashes müssen nach der Iterations-Erhöhung weiter funktionieren");
        (await service.ValidateCredentialsAsync(username, "falsch", TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    /// <summary>WP8.4: Die Recovery-Kommandos müssen aus dem „kein Zugang mehr"-Zustand herausführen.</summary>
    [Fact]
    public async Task Recovery_commands_restore_ui_and_agent_access()
    {
        MarkSkippedIfUnavailable();
        var users = new UiUserService(Factory);
        var username = $"recovery-{Guid.NewGuid():N}";

        // Fall 1: Nutzer existiert nicht → wird als Admin angelegt.
        var created = await McpMcp.Server.AdminCommands.ResetUiAdminAsync(users, username, TestContext.Current.CancellationToken);
        created.WasExisting.Should().BeFalse();
        created.Role.Should().Be(UiRole.Admin);
        (await users.ValidateCredentialsAsync(username, created.Password, TestContext.Current.CancellationToken))
            .Should().NotBeNull("das ausgegebene Passwort muss funktionieren");

        // Fall 2: Nutzer existiert → Passwort neu, Rolle unverändert, altes Passwort ungültig.
        var reset = await McpMcp.Server.AdminCommands.ResetUiAdminAsync(users, username, TestContext.Current.CancellationToken);
        reset.WasExisting.Should().BeTrue();
        reset.Password.Should().NotBe(created.Password);
        (await users.ValidateCredentialsAsync(username, reset.Password, TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await users.ValidateCredentialsAsync(username, created.Password, TestContext.Current.CancellationToken))
            .Should().BeNull("das alte Passwort darf nach dem Reset nicht mehr gelten");

        // Notfall-API-Key: neue Identität mit Global-Grant, Key ist sofort gültig.
        var directory = new InMemoryRbacDirectory();
        var rbac = new PersistentRbacStore(Factory, directory);
        var keys = new ApiKeyService(Factory);
        var recovery = await McpMcp.Server.AdminCommands.IssueBootstrapKeyAsync(rbac, keys, TestContext.Current.CancellationToken);

        recovery.ApiKey.Should().StartWith("mcpk_");
        var identityId = await keys.ValidateAsync(recovery.ApiKey, TestContext.Current.CancellationToken);
        identityId.Should().NotBeNull("der ausgegebene Key muss sofort validieren");

        var authorization = new AuthorizationService(directory);
        authorization.Evaluate(identityId!.Value, new PermissionScope(null, null), ToolAction.UseTool)
            .Allowed.Should().BeTrue("die Notfall-Identität trägt einen Global-Grant");
    }

    [Fact]
    public async Task Fresh_database_is_created_from_migrations()
    {
        MarkSkippedIfUnavailable();
        IDbContextFactory<McpMcpDbContext> factory = new TestDbContextFactory(await CreateFreshOptionsAsync("fresh"));

        var outcome = await new DatabaseInitializer(factory).InitializeAsync(TestContext.Current.CancellationToken);

        outcome.Should().Be(DatabaseInitOutcome.CreatedFromMigrations);
        await using var db = await factory.CreateDbContextAsync();
        // Bewusst nicht auf eine feste Anzahl prüfen — jede künftige Schemaänderung bringt eine weitere.
        (await db.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty("das Schema kommt aus Migrationen");
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Identities.CountAsync()).Should().Be(0, "das Schema ist nutzbar");
    }

    /// <summary>
    /// Der eigentliche v1.1-Upgrade-Nachweis: eine v1.0-Datenbank (Schema ohne Migrationshistorie)
    /// darf beim Start weder scheitern noch Daten verlieren — sie wird gestempelt und dann migriert.
    /// </summary>
    [Fact]
    public async Task Legacy_v1_database_is_baselined_without_data_loss()
    {
        MarkSkippedIfUnavailable();
        IDbContextFactory<McpMcpDbContext> factory = new TestDbContextFactory(await CreateFreshOptionsAsync("legacy"));
        var identityId = Guid.NewGuid();

        // v1.0-Zustand simulieren: Schema exakt im Stand der InitialCreate-Migration, danach die
        // Historie entfernen. Bewusst nicht EnsureCreated — das erzeugt das *heutige* Modell und
        // damit eine Datenbank, die schon Tabellen späterer Migrationen hätte.
        await using (var legacy = await factory.CreateDbContextAsync())
        {
            var script = legacy.GetService<IMigrator>().GenerateScript(toMigration: InitialCreateMigration);
            await legacy.Database.ExecuteSqlRawAsync(script);
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\"");

            legacy.Identities.Add(new IdentityRow
            {
                Id = identityId, Name = "bestandsagent", Kind = 0, RolesJson = "[]",
            });
            await legacy.SaveChangesAsync();
            (await legacy.Database.GetAppliedMigrationsAsync()).Should().BeEmpty("v1.0 kannte keine Migrationen");
        }

        var outcome = await new DatabaseInitializer(factory).InitializeAsync(TestContext.Current.CancellationToken);

        outcome.Should().Be(DatabaseInitOutcome.BaselinedLegacySchema);
        await using (var upgraded = await factory.CreateDbContextAsync())
        {
            (await upgraded.Identities.SingleAsync()).Name
                .Should().Be("bestandsagent", "das Upgrade darf keine Bestandsdaten anfassen");
            (await upgraded.Database.GetAppliedMigrationsAsync())
                .Should().NotBeEmpty("die Baseline ist als angewendet gestempelt");
            (await upgraded.Database.GetPendingMigrationsAsync())
                .Should().BeEmpty("nach dem Stempeln laufen die restlichen Migrationen durch");
        }

        // Zweiter Start derselben Instanz: normal migriert, nicht erneut gestempelt.
        (await new DatabaseInitializer(factory).InitializeAsync(TestContext.Current.CancellationToken))
            .Should().Be(DatabaseInitOutcome.Migrated, "Initialisierung ist idempotent");
    }

    /// <summary>
    /// Connector-Pakete (ADR-0016): Der Wechsel der aktiven Version muss auf <b>beiden</b> Providern
    /// atomar sein — zwei aktive Versionen desselben Pakets könnte kein Aufrufer auflösen.
    /// </summary>
    [Fact]
    public async Task ConnectorPackages_keep_exactly_one_active_version()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = new ConnectorPackageStore(Factory);
        var at = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

        await store.UpsertAsync(Package("1.0.0", at), ct);
        await store.ActivateAsync("com.example.paket", "1.0.0", at, ct);
        await store.UpsertAsync(Package("2.0.0", at), ct);
        await store.ActivateAsync("com.example.paket", "2.0.0", at.AddMinutes(5), ct);

        var versions = await store.GetVersionsAsync("com.example.paket", ct);
        versions.Should().HaveCount(2);
        versions.Count(v => v.State is PackageState.Active).Should().Be(1);
        (await store.GetActiveAsync("com.example.paket", ct))!.Version.Should().Be("2.0.0");
        versions.Single(v => v.Version == "1.0.0").State.Should().Be(PackageState.Superseded);

        // Zurückschalten ist derselbe Vorgang in die andere Richtung.
        await store.ActivateAsync("com.example.paket", "1.0.0", at.AddMinutes(9), ct);
        (await store.GetActiveAsync("com.example.paket", ct))!.Version.Should().Be("1.0.0");

        // Die erteilten Zugriffe überstehen den Weg durch die Datenbank — sie sind der Beleg,
        // wem das Gateway einmal was erlaubt hat.
        (await store.GetActiveAsync("com.example.paket", ct))!.GrantedCapabilities
            .Should().Equal("env:TOKEN", "fs-read:/daten");

        await store.RemoveAsync("com.example.paket", "2.0.0", ct);
        (await store.GetVersionsAsync("com.example.paket", ct)).Should().ContainSingle();
    }

    /// <summary>
    /// Bestehende Herausgeber dürfen durch die Migration nicht aufgewertet werden: Vorgabe ist
    /// ThirdParty, nicht Core.
    /// </summary>
    [Fact]
    public async Task An_existing_publisher_key_defaults_to_third_party()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var store = new PublisherTrustStore(Factory, TimeProvider.System);
        var key = await store.PinAsync(Convert.ToBase64String(new byte[32]), "vorhanden", ct);

        key.TrustLevel.Should().Be(ConnectorTrustLevel.ThirdParty);

        await store.SetTrustLevelAsync(key.KeyId, ConnectorTrustLevel.Official, ct);
        await store.LoadAsync(ct);
        store.All.Single(k => k.KeyId == key.KeyId).TrustLevel
            .Should().Be(ConnectorTrustLevel.Official);
    }

    private static InstalledConnectorPackage Package(string version, DateTimeOffset at) => new(
        "com.example.paket", version, "Beispiel", UpstreamTransportKind.Wasi,
        new string('a', 64), ConnectorTrustLevel.Official, new string('b', 64),
        Path.Combine("packages", "com.example.paket", version), PackageState.Quarantined, at, null,
        ["env:TOKEN", "fs-read:/daten"]);

    /// <summary>
    /// Die festgehaltenen Tool-Definitionen müssen einen Neustart überleben — sonst nähme die erste
    /// Discovery nach jedem Neustart jede Änderung stillschweigend als Erstsichtung an, und der
    /// Rug-Pull-Schutz wäre genau dann wirkungslos, wenn er gebraucht wird.
    /// </summary>
    [Fact]
    public async Task ToolDefinitionPins_survive_a_restart_and_track_pending_changes()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var server = ServerId.New();
        var store = new ToolDefinitionPinStore(Factory, TimeProvider.System);

        (await store.VerifyAsync(server, "read_file", new string('a', 64), ct))
            .Should().Be(ToolDefinitionVerdict.FirstSeen);
        (await store.VerifyAsync(server, "read_file", new string('a', 64), ct))
            .Should().Be(ToolDefinitionVerdict.Unchanged);
        (await store.VerifyAsync(server, "read_file", new string('b', 64), ct))
            .Should().Be(ToolDefinitionVerdict.Changed);

        // Ein frischer Store auf derselben Datenbank: genau der Neustart-Fall.
        var afterRestart = new ToolDefinitionPinStore(Factory, TimeProvider.System);
        await afterRestart.LoadAsync(ct);
        var pin = afterRestart.All.Single(p => p.Server == server && p.Tool == "read_file");
        pin.AcceptedHash.Should().Be(new string('a', 64));
        pin.HasPendingChange.Should().BeTrue("die Abweichung ist noch offen");

        await afterRestart.AcceptAsync(server, "read_file", ct);
        (await afterRestart.VerifyAsync(server, "read_file", new string('b', 64), ct))
            .Should().Be(ToolDefinitionVerdict.Unchanged, "die neue Fassung ist jetzt der Bezugspunkt");

        await afterRestart.ForgetServerAsync(server, ct);
        afterRestart.All.Should().NotContain(p => p.Server == server);
    }

    private async Task WaitForRowCountAsync(int expected, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await using var db = await Factory.CreateDbContextAsync();
            if (await db.AuditEvents.CountAsync() >= expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit-Zeilen erreichten nicht {expected} binnen {timeoutMs} ms.");
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;
}
