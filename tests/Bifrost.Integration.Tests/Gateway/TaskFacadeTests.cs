using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Die REST-Fläche der Vorgänge (ADR-0019, Darstellung) samt Eigentümerprüfung. Der Vertrag ist
/// Polling — geprüft wird also, dass jemand seinen Stand <em>holen</em> kann, und dass er dabei
/// nichts sieht, was ihm nicht gehört.
/// </summary>
public sealed class TaskFacadeTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public TaskFacadeTests(GatewayFixture gw) => _gw = gw;

    private HttpClient CreateApiClient(string apiKey)
    {
        var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return client;
    }

    private ITaskStore Tasks => _gw.Services.GetRequiredService<ITaskStore>();

    private async Task<TaskRecord> SeedTaskAsync(IdentityId owner, string tool = "srv__long_job")
    {
        var now = DateTimeOffset.UtcNow;
        return await Tasks.CreateOrGetAsync(
            new TaskRecord(
                Guid.NewGuid(), owner, "agent", new NamespacedToolName(tool), null, CallOrigin.Rest,
                CorrelationId: Guid.NewGuid(),
                State: TaskState.Working,
                Revision: 0,
                Progress: 10,
                InputFingerprint: Guid.NewGuid().ToString("N"),
                RedactedInput: JsonSerializer.Deserialize<JsonElement>("""{"arg":"***"}"""),
                RedactedResult: null,
                Failure: null,
                ExpectedInputSchema: null,
                Cancellation: TaskCancellation.None,
                ClaimedAt: null,
                CreatedAt: now,
                UpdatedAt: now,
                ExpiresAt: now.AddHours(1)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Task_list_works_without_paging_parameters()
    {
        var (identity, apiKey) = await _gw.SeedAdminAsync("task-admin");
        await SeedTaskAsync(identity);
        using var client = CreateApiClient(apiKey);

        var response = await client.GetAsync("/api/v1/tasks");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "ein simples GET /tasks muss ohne Query gehen");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("page").GetInt32().Should().Be(1);
        body.GetProperty("pageSize").GetInt32().Should().Be(100);
        body.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Vorgänge sind keine global sichtbaren Nebenprodukte (ADR-0019): Ein fremder Vorgang ist für
    /// einen einfachen Aufrufer <b>nicht gefunden</b> — nicht „verboten". Sonst liesse sich über den
    /// Statuscode abfragen, welche Ids existieren.
    /// </summary>
    [Fact]
    public async Task A_caller_sees_only_its_own_tasks()
    {
        var (admin, adminKey) = await _gw.SeedAdminAsync("task-owner-admin");
        var (_, plainKey) = await _gw.SeedIdentityAsync(
            "task-plain", [new Grant(new PermissionScope(new ServerId(Guid.NewGuid()), null), [ToolAction.UseTool])]);
        var task = await SeedTaskAsync(admin, "srv__admin_job");

        using var plain = CreateApiClient(plainKey);
        var foreignGet = await plain.GetAsync($"/api/v1/tasks/{task.Id}");
        var ownList = await plain.GetAsync("/api/v1/tasks");

        foreignGet.StatusCode.Should().Be(HttpStatusCode.NotFound, "ein fremder Vorgang existiert für ihn nicht");
        var listed = await ownList.Content.ReadFromJsonAsync<JsonElement>();
        listed.GetProperty("items").EnumerateArray()
            .Should().BeEmpty("er hat selbst keine Vorgänge");

        // Der Eigentümer (hier mit Global-Grant) sieht ihn.
        using var owner = CreateApiClient(adminKey);
        (await owner.GetAsync($"/api/v1/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Abbruch über REST ist <b>endgültig</b>, solange nichts läuft.
    /// <para>
    /// Dieser Test stand bis zum 2026-07-27 andersherum da: Er hielt fest, dass der Abbruch nur
    /// „verlangt" wird und der Vorgang auf <c>Working</c> stehen bleibt. Das war die Beschreibung
    /// eines Defekts, nicht einer Entscheidung — niemand las das Feld je aus, und eine widerrufene
    /// Freigabe blieb einlösbar. ADR-0019 verlangt eine Bestätigung nur dort, wo ein Ausführender
    /// sie geben kann; bei einem Vorgang, bei dem nichts läuft, ist der Abbruch sofort belegbar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Cancel_is_final_when_nothing_is_running()
    {
        var (identity, apiKey) = await _gw.SeedAdminAsync("task-cancel-admin");
        var task = await SeedTaskAsync(identity, "srv__cancel_job");
        using var client = CreateApiClient(apiKey);

        var response = await client.PostAsync($"/api/v1/tasks/{task.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "endgültig, nicht bloß angenommen");
        var reread = await Tasks.GetAsync(task.Id, TestContext.Current.CancellationToken);
        reread!.State.Should().Be(TaskState.Cancelled);
        reread.Cancellation.Should().Be(TaskCancellation.Confirmed);
        reread.IsTerminal.Should().BeTrue();
    }

    /// <summary>Ein abgeschlossener Vorgang lässt sich nicht mehr abbrechen — Terminal ist terminal.</summary>
    [Fact]
    public async Task Cancelling_a_finished_task_conflicts()
    {
        var (identity, apiKey) = await _gw.SeedAdminAsync("task-done-admin");
        var task = await SeedTaskAsync(identity, "srv__done_job");
        await Tasks.UpdateAsync(
            new TaskUpdate(task.Id, State: TaskState.Completed), task.Revision,
            TestContext.Current.CancellationToken);
        using var client = CreateApiClient(apiKey);

        var response = await client.PostAsync($"/api/v1/tasks/{task.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Der Verfallslauf macht eine verstrichene Frist sichtbar. Ohne ihn stünde der Vorgang weiter
    /// als offen in der Liste, obwohl niemand ihn mehr einlösen kann.
    /// </summary>
    [Fact]
    public async Task The_expiry_job_marks_overdue_tasks()
    {
        var (identity, _) = await _gw.SeedAdminAsync("task-expiry-admin");
        var past = DateTimeOffset.UtcNow.AddHours(-2);
        var stale = await Tasks.CreateOrGetAsync(
            new TaskRecord(
                Guid.NewGuid(), identity, "agent", new NamespacedToolName("srv__stale_job"), null,
                CallOrigin.Rest, Guid.NewGuid(), TaskState.Working, 0, null,
                Guid.NewGuid().ToString("N"), null, null, null, null,
                TaskCancellation.None, null, past, past, past.AddMinutes(1)),
            TestContext.Current.CancellationToken);

        var job = new Bifrost.Persistence.TaskExpiryJob(Tasks);
        var expired = await job.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        expired.Should().BeGreaterThan(0);
        (await Tasks.GetAsync(stale.Id, TestContext.Current.CancellationToken))!
            .State.Should().Be(TaskState.Expired);
    }
}
