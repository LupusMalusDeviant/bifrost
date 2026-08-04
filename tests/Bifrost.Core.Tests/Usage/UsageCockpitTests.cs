using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Usage;
using Bifrost.Core.Usage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Usage;

/// <summary>
/// WP6.4: Das Cockpit rechnet, zeigt und simuliert — und ändert nichts.
/// <para>
/// Die Tests halten vor allem die Unterscheidung fest, an der dieses Paket hängt: <em>Exposure</em>
/// ist, was ein Profil einem Agenten zumutet; <em>Invocation</em> ist, was er davon benutzt hat.
/// Wer beides verrechnet, bekommt eine Zahl, die nichts bedeutet.
/// </para>
/// </summary>
public sealed class UsageCockpitTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly IdentityId _identity = IdentityId.New();

    private static UsageWindow LastWeek => new(Now.AddDays(-7), Now);

    [Fact]
    public async Task Exposure_and_invocation_are_reported_separately()
    {
        var catalog = new FakeCatalog(
            [Entry("github__issue", 400), Entry("github__pr", 250), Entry("jira__ticket", 100)]);
        var audit = new FakeAudit(
            [Call("github__issue", Now.AddDays(-1)), Call("github__issue", Now.AddDays(-2)),
             Call("jira__ticket", Now.AddHours(-3))]);

        var report = await new UsageCockpit(catalog, audit, _time)
            .AnalyseAsync(_identity, LastWeek, TestContext.Current.CancellationToken);

        report.ExposedTools.Should().Be(3);
        report.ExposedTokens.Should().Be(750, "das ist die Summe der Schemakosten, nicht der Aufrufe");
        report.TotalInvocations.Should().Be(3);

        report.Exposed.Should().BeInDescendingOrder(t => t.EstimatedSchemaTokens);
        report.Exposed.Single(t => t.Tool == "github__issue").Invocations.Should().Be(2);
        report.Exposed.Single(t => t.Tool == "github__pr").UnusedInWindow.Should().BeTrue();
    }

    /// <summary>
    /// Der Fall, für den es die zweite Liste gibt: Ein Werkzeug wird über den Lazy-Pfad benutzt,
    /// ohne im Profil zu stehen. Ohne sie sähe ein sparsames Profil aus, als arbeite niemand.
    /// </summary>
    [Fact]
    public async Task A_tool_used_outside_the_profile_shows_up_separately()
    {
        var catalog = new FakeCatalog([Entry("github__issue", 400)], extra: [Entry("jira__ticket", 100)]);
        var audit = new FakeAudit([Call("jira__ticket", Now.AddDays(-1))]);

        var report = await new UsageCockpit(catalog, audit, _time)
            .AnalyseAsync(_identity, LastWeek, TestContext.Current.CancellationToken);

        report.Exposed.Should().ContainSingle().Which.Tool.Should().Be("github__issue");
        report.UsedButNotExposed.Should().ContainSingle()
            .Which.Should().Match<ToolUsage>(t => t.Tool == "jira__ticket" && t.Invocations == 1 && t.StillInCatalog);
    }

    /// <summary>
    /// Ein Werkzeug, das es nicht mehr gibt, kostet nichts mehr — der Aufruf hat trotzdem
    /// stattgefunden. Verschwände er, sähe eine Instanz nach einem Upstream-Verlust aus wie eine,
    /// in der nie etwas benutzt wurde.
    /// </summary>
    [Fact]
    public async Task A_deleted_tool_keeps_its_invocations_and_costs_nothing()
    {
        var catalog = new FakeCatalog([Entry("github__issue", 400)]);
        var audit = new FakeAudit([Call("weg__tool", Now.AddDays(-2))]);

        var report = await new UsageCockpit(catalog, audit, _time)
            .AnalyseAsync(_identity, LastWeek, TestContext.Current.CancellationToken);

        var gone = report.UsedButNotExposed.Single();
        gone.Tool.Should().Be("weg__tool");
        gone.Invocations.Should().Be(1);
        gone.EstimatedSchemaTokens.Should().Be(0);
        gone.StillInCatalog.Should().BeFalse();
        report.Notes.Should().Contain(n => n.Contains("nicht mehr im Katalog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_calls_inside_the_window_are_counted()
    {
        var catalog = new FakeCatalog([Entry("github__issue", 400)]);
        var audit = new FakeAudit([Call("github__issue", Now.AddDays(-30))]);

        var report = await new UsageCockpit(catalog, audit, _time)
            .AnalyseAsync(_identity, LastWeek, TestContext.Current.CancellationToken);

        report.TotalInvocations.Should().Be(0);
        report.Exposed.Single().UnusedInWindow.Should().BeTrue();
        audit.LastFilter!.From.Should().Be(LastWeek.From, "das Fenster geht an die Abfrage, nicht in den Speicher");
    }

    [Fact]
    public async Task A_window_beyond_the_retention_is_shortened_and_says_so()
    {
        var catalog = new FakeCatalog([Entry("github__issue", 400)]);
        var cockpit = new UsageCockpit(catalog, new FakeAudit([]), _time, new UsageCockpitOptions
        {
            MaxWindow = TimeSpan.FromDays(30),
        });

        var report = await cockpit.AnalyseAsync(
            _identity, new UsageWindow(Now.AddDays(-365), Now), TestContext.Current.CancellationToken);

        report.Window.Duration.Should().Be(TimeSpan.FromDays(30));
        report.Notes.Should().Contain(n => n.Contains("gekürzt", StringComparison.Ordinal));
    }

    /// <summary>
    /// Auf einem großen Katalog darf der Bericht nicht selbst zum Problem werden — und die
    /// Kürzung muss dastehen. Eine Liste, die still bei 500 endet, liest sich wie eine
    /// vollständige.
    /// </summary>
    [Fact]
    public async Task A_large_catalog_is_capped_and_the_cap_is_named()
    {
        var many = Enumerable.Range(0, 1200).Select(i => Entry($"gross__tool{i:D4}", i)).ToList();
        var cockpit = new UsageCockpit(
            new FakeCatalog(many), new FakeAudit([]), _time, new UsageCockpitOptions { MaxToolsListed = 100 });

        var report = await cockpit.AnalyseAsync(_identity, LastWeek, TestContext.Current.CancellationToken);

        report.ExposedTools.Should().Be(1200, "die Zahl bleibt vollständig");
        report.Exposed.Should().HaveCount(100, "die Liste nicht");
        report.Notes.Should().Contain(n => n.Contains("1200 gefunden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_truncated_audit_read_is_reported_as_a_lower_bound()
    {
        var catalog = new FakeCatalog([Entry("github__issue", 400)]);
        var audit = new FakeAudit([.. Enumerable.Range(0, 40).Select(_ => Call("github__issue", Now.AddDays(-1)))]);
        var cockpit = new UsageCockpit(catalog, audit, _time, new UsageCockpitOptions
        {
            AuditPageSize = 10,
            MaxAuditPages = 2,
        });

        var report = await cockpit.AnalyseAsync(_identity, LastWeek, TestContext.Current.CancellationToken);

        report.TotalInvocations.Should().Be(20);
        report.Notes.Should().Contain(n => n.Contains("Untergrenzen", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Die DoD dieses Pakets:</b> Der Betreiber sieht Einsparung <em>und</em> Konsequenz, bevor
    /// er etwas übernimmt. Ein Cockpit, das nur die Einsparung zeigt, ist ein Verkaufsprospekt.
    /// </summary>
    [Fact]
    public async Task The_simulation_shows_the_saving_and_what_it_costs()
    {
        var catalog = new FakeCatalog(
            [Entry("github__issue", 400), Entry("github__pr", 250), Entry("jira__ticket", 100)]);
        var audit = new FakeAudit(
            [Call("github__pr", Now.AddDays(-1)), Call("github__pr", Now.AddDays(-2))]);

        var simulation = await new UsageCockpit(catalog, audit, _time).SimulateAsync(
            _identity, LastWeek, ["github__issue"], TestContext.Current.CancellationToken);

        simulation.TokensBefore.Should().Be(750);
        simulation.TokensAfter.Should().Be(400);
        simulation.TokensSaved.Should().Be(350);
        simulation.Removed.Should().BeEquivalentTo(["github__pr", "jira__ticket"]);

        // Und die Konsequenz: 'github__pr' wurde benutzt, 'jira__ticket' nicht.
        simulation.RemovedButUsed.Should().ContainSingle()
            .Which.Should().Match<ToolUsage>(t => t.Tool == "github__pr" && t.Invocations == 2);
        simulation.Notes.Should().Contain(n => n.Contains("Lazy-Pfad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_proposal_naming_unknown_tools_says_so_instead_of_pretending()
    {
        var catalog = new FakeCatalog([Entry("github__issue", 400)]);

        var simulation = await new UsageCockpit(catalog, new FakeAudit([]), _time).SimulateAsync(
            _identity, LastWeek, ["github__issue", "gibt__esnicht"], TestContext.Current.CancellationToken);

        simulation.Notes.Should().Contain(n => n.Contains("gibt__esnicht", StringComparison.Ordinal));
        simulation.TokensAfter.Should().Be(400, "ein unbekanntes Werkzeug kostet nichts");
    }

    /// <summary>
    /// Die Regel des Pakets, als Test: Weder Analyse noch Simulation fassen etwas an. Der Katalog
    /// meldet jeden Schreibversuch — es gibt keinen.
    /// </summary>
    [Fact]
    public async Task Neither_analysis_nor_simulation_changes_anything()
    {
        var catalog = new FakeCatalog([Entry("github__issue", 400)]);
        var cockpit = new UsageCockpit(catalog, new FakeAudit([]), _time);

        await cockpit.AnalyseAsync(_identity, LastWeek, TestContext.Current.CancellationToken);
        await cockpit.SimulateAsync(_identity, LastWeek, [], TestContext.Current.CancellationToken);

        catalog.WriteAttempts.Should().Be(0);
    }

    private static CatalogEntry Entry(string name, int tokens) => new(
        new NamespacedToolName(name),
        ServerId.New(),
        "Beschreibung",
        JsonDocument.Parse("""{"type":"object"}""").RootElement,
        CatalogEntryKind.Tool,
        tokens);

    private static AuditEvent Call(string tool, DateTimeOffset at) => new(
        at, null, CallOrigin.Mcp, AuditEventKind.ToolCall, null, tool, InvocationStatus.Success,
        null, null, null, null);

    private sealed class FakeCatalog(IReadOnlyList<CatalogEntry> pinned, IReadOnlyList<CatalogEntry>? extra = null)
        : IToolCatalog
    {
        private readonly List<CatalogEntry> _all = [.. pinned, .. extra ?? []];

        public int WriteAttempts { get; private set; }

        public IReadOnlyList<CatalogEntry> Snapshot => _all;

        public ProfileView GetViewFor(IdentityId identity) =>
            new(pinned, LazyToolsEnabled: true, pinned.Sum(p => p.EstimatedSchemaTokens));

        public IReadOnlyList<ToolSearchHit> Search(IdentityId identity, string query, int limit) => [];

        public CatalogEntry? Find(NamespacedToolName name) =>
            _all.FirstOrDefault(e => e.Name.Value == name.Value);

        public event EventHandler<CatalogChangedEventArgs>? Changed
        {
            add => WriteAttempts++;
            remove => WriteAttempts++;
        }
    }

    private sealed class FakeAudit(IReadOnlyList<AuditEvent> events) : IAuditQuery
    {
        public AuditFilter? LastFilter { get; private set; }

        public Task<PagedResult<AuditEvent>> QueryAsync(AuditFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            var inWindow = events
                .Where(e => (filter.From is null || e.Timestamp >= filter.From)
                    && (filter.To is null || e.Timestamp <= filter.To))
                .ToList();

            var page = inWindow.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
            return Task.FromResult(new PagedResult<AuditEvent>(page, inWindow.Count, filter.Page, filter.PageSize));
        }
    }
}
