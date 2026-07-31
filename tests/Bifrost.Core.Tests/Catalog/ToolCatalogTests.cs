using System.Diagnostics;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Catalog;
using Bifrost.Core.Rbac;
using Bifrost.Core.Tests.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Catalog;

public class ToolCatalogTests
{
    private readonly FakeSupervisor _supervisor = new();
    private readonly InMemoryRbacDirectory _directory = new();
    private readonly AuthorizationService _authorization;

    public ToolCatalogTests()
    {
        _authorization = new AuthorizationService(_directory);
    }

    private ToolCatalog CreateCatalog() => new(_supervisor, _authorization, _directory);

    private IdentityId RegisterIdentity(IReadOnlyList<Grant> grants, ToolProfile? profile = null)
    {
        if (profile is not null)
        {
            _directory.UpsertProfile(profile);
        }

        var role = new Role(RoleId.New(), "rolle", grants);
        _directory.UpsertRole(role);
        var id = IdentityId.New();
        _directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, [role.Id], profile?.Id));
        return id;
    }

    private IdentityId RegisterGlobalAdmin(ToolProfile? profile = null)
        => RegisterIdentity(
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])],
            profile);

    [Fact]
    public void Snapshot_aggregates_and_namespaces_all_entry_kinds()
    {
        _supervisor.SetServer("github", new UpstreamInventory(
            [new ToolDescriptor("create_issue", "Erstellt ein Issue", TestData.EmptySchema())],
            [new ResourceDescriptor(new Uri("file:///readme"), "readme", "Das Readme", "text/plain")],
            [new PromptDescriptor("triage", "Triage-Prompt")]));
        _supervisor.SetServer("files", TestData.InventoryWithTools("read_file", "write_file"));

        var catalog = CreateCatalog();

        catalog.Snapshot.Should().HaveCount(5);
        catalog.Snapshot.Select(e => e.Name.Value).Should().BeEquivalentTo(
            "github__create_issue", "github__readme", "github__triage", "files__read_file", "files__write_file");
        catalog.Snapshot.Single(e => e.Name.Value == "github__readme").Kind.Should().Be(CatalogEntryKind.Resource);
        catalog.Snapshot.Single(e => e.Name.Value == "github__triage").Kind.Should().Be(CatalogEntryKind.Prompt);
        catalog.Snapshot.Single(e => e.Name.Value == "github__create_issue").EstimatedSchemaTokens
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void Duplicate_entries_from_one_server_keep_first_only()
    {
        _supervisor.SetServer("dup", new UpstreamInventory(
            [
                new ToolDescriptor("tool", "Erste Definition", TestData.EmptySchema()),
                new ToolDescriptor("tool", "Zweite Definition", TestData.EmptySchema()),
            ],
            [], []));

        var catalog = CreateCatalog();

        catalog.Snapshot.Should().ContainSingle()
            .Which.Description.Should().Be("Erste Definition");
    }

    [Fact]
    public void Inventory_change_rebuilds_snapshot_and_raises_mapped_event()
    {
        var serverId = _supervisor.SetServer("srv", TestData.InventoryWithTools("alt"));
        var catalog = CreateCatalog();
        var received = new List<CatalogChangedEventArgs>();
        catalog.Changed += (_, e) => received.Add(e);

        _supervisor.SetServer(serverId, "srv", TestData.InventoryWithTools("neu"));
        _supervisor.RaiseChanged(serverId, UpstreamChangeKind.InventoryChanged);

        catalog.Snapshot.Should().ContainSingle().Which.Name.Value.Should().Be("srv__neu");
        received.Should().ContainSingle().Which.Kind.Should().Be(CatalogChangeKind.InventoryChanged);
        received[0].AffectedServers.Should().Equal(serverId);
    }

    [Fact]
    public void State_changes_do_not_rebuild_or_raise()
    {
        var serverId = _supervisor.SetServer("srv", TestData.InventoryWithTools("echo"));
        var catalog = CreateCatalog();
        var received = new List<CatalogChangedEventArgs>();
        catalog.Changed += (_, e) => received.Add(e);

        _supervisor.RaiseChanged(serverId, UpstreamChangeKind.StateChanged, UpstreamState.Degraded);

        received.Should().BeEmpty("reine Statuswechsel ändern die Tool-Menge nicht (FR-07)");
    }

    [Fact]
    public void Rbac_directory_change_raises_permissions_changed()
    {
        _supervisor.SetServer("srv", TestData.InventoryWithTools("echo"));
        var catalog = CreateCatalog();
        var received = new List<CatalogChangedEventArgs>();
        catalog.Changed += (_, e) => received.Add(e);

        RegisterGlobalAdmin();

        received.Should().NotBeEmpty();
        received.Should().OnlyContain(e => e.Kind == CatalogChangeKind.PermissionsChanged);
    }

    [Fact]
    public void Token_estimate_uses_quarter_of_character_count()
    {
        var schema = TestData.EmptySchema(); // "{}" → 2 Zeichen

        ToolCatalog.EstimateTokens("abcd", "efgh", schema).Should().Be((4 + 4 + 2) / 4);
        ToolCatalog.EstimateTokens("a", null, default).Should().Be(1, "Untergrenze 1 Token, Undefined-Schema zählt 2");
    }

    [Fact]
    public void View_without_profile_is_lazy_with_meta_tools_only()
    {
        _supervisor.SetServer("srv", TestData.InventoryWithTools("echo"));
        var catalog = CreateCatalog();
        var admin = RegisterGlobalAdmin();

        var view = catalog.GetViewFor(admin);

        view.PinnedTools.Should().BeEmpty("ohne Profil ist nichts gepinnt (sparsamste Sicht)");
        view.LazyToolsEnabled.Should().BeTrue();
        view.EstimatedContextTokens.Should().Be(ToolCatalog.MetaToolTokenEstimate);
    }

    [Fact]
    public void View_with_profile_pins_visible_tools_and_sums_tokens()
    {
        _supervisor.SetServer("srv", TestData.InventoryWithTools("alpha", "beta", "gamma"));
        var catalog = CreateCatalog();
        var profile = new ToolProfile(ProfileId.New(), "pinned-zwei",
            [new NamespacedToolName("srv__alpha"), new NamespacedToolName("srv__beta")], LazyToolsEnabled: false);
        var admin = RegisterGlobalAdmin(profile);

        var view = catalog.GetViewFor(admin);

        view.PinnedTools.Select(e => e.Name.Value).Should().BeEquivalentTo("srv__alpha", "srv__beta");
        view.LazyToolsEnabled.Should().BeFalse();
        view.EstimatedContextTokens.Should().Be(
            view.PinnedTools.Sum(e => e.EstimatedSchemaTokens),
            "ohne Lazy-Modus zählen nur die Pinned-Schemas");
    }

    [Fact]
    public void Pinned_tools_without_grant_are_not_in_view()
    {
        var serverId = _supervisor.SetServer("srv", TestData.InventoryWithTools("erlaubt", "verboten"));
        _ = serverId;
        var catalog = CreateCatalog();
        var profile = new ToolProfile(ProfileId.New(), "beide-gepinnt",
            [new NamespacedToolName("srv__erlaubt"), new NamespacedToolName("srv__verboten")], LazyToolsEnabled: true);
        var restricted = RegisterIdentity(
            [new Grant(new PermissionScope(null, new NamespacedToolName("srv__erlaubt")), [ToolAction.UseTool])],
            profile);

        var view = catalog.GetViewFor(restricted);

        view.PinnedTools.Should().ContainSingle()
            .Which.Name.Value.Should().Be("srv__erlaubt", "Sichtbarkeit folgt Berechtigung — auch für Pins (FR-29)");
    }

    [Fact]
    public void Search_ranks_name_matches_above_description_matches_and_respects_rbac()
    {
        _supervisor.SetServer("git", new UpstreamInventory(
            [
                new ToolDescriptor("commit_changes", "Speichert Änderungen", TestData.EmptySchema()),
                new ToolDescriptor("push_branch", "Überträgt einen commit zum Remote", TestData.EmptySchema()),
                new ToolDescriptor("secret_tool", "Geheimes commit-Werkzeug", TestData.EmptySchema()),
            ],
            [], []));
        var catalog = CreateCatalog();
        var identity = RegisterIdentity(
        [
            new Grant(new PermissionScope(null, new NamespacedToolName("git__commit_changes")), [ToolAction.UseTool]),
            new Grant(new PermissionScope(null, new NamespacedToolName("git__push_branch")), [ToolAction.UseTool]),
        ]);

        var hits = catalog.Search(identity, "commit", 10);

        hits.Should().HaveCount(2, "secret_tool ist nicht gegrantet und darf nicht auftauchen (FR-29)");
        hits[0].Name.Value.Should().Be("git__commit_changes", "Name-Treffer wiegt schwerer als Beschreibungs-Treffer");
        hits[1].Name.Value.Should().Be("git__push_branch");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public void Search_respects_limit_and_rejects_empty_queries()
    {
        _supervisor.SetServer("srv", TestData.InventoryWithTools("a1", "a2", "a3"));
        var catalog = CreateCatalog();
        var admin = RegisterGlobalAdmin();

        catalog.Search(admin, "a", 2).Should().HaveCount(2);
        catalog.Search(admin, "   ", 10).Should().BeEmpty();
        catalog.Search(admin, "a", 0).Should().BeEmpty();
        catalog.Search(admin, "kein-treffer-xyz", 10).Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_tolerates_null_descriptions_and_missing_inventories()
    {
        var longDescription = new string('x', 200);
        _supervisor.SetServer("srv", new UpstreamInventory(
            [
                new ToolDescriptor("nodesc", null, TestData.EmptySchema()),
                new ToolDescriptor("longdesc", longDescription, TestData.EmptySchema()),
            ],
            [new ResourceDescriptor(new Uri("file:///r"), "res", null, null)],
            [new PromptDescriptor("prompt", null)]));
        _supervisor.SetServer("dead", inventory: null, UpstreamState.Failed);

        using var catalog = new Bifrost.Core.Catalog.ToolCatalog(
            _supervisor, _authorization, _directory, overrides: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Bifrost.Core.Catalog.ToolCatalog>.Instance);

        catalog.Snapshot.Should().HaveCount(4, "der Server ohne Inventar liefert keine Einträge");
        catalog.Snapshot.Should().OnlyContain(e => e.Description != null);

        var admin = RegisterGlobalAdmin();
        var hit = catalog.Search(admin, "xxx", 10).Should().ContainSingle().Subject;
        hit.ShortDescription.Should().HaveLength(158).And.EndWith("…", "lange Beschreibungen werden für Treffer gekürzt");
    }

    [Fact]
    public void Server_added_and_removed_events_map_to_catalog_change_kinds()
    {
        var catalog = CreateCatalog();
        var received = new List<CatalogChangedEventArgs>();
        catalog.Changed += (_, e) => received.Add(e);

        var id = _supervisor.SetServer("neu", TestData.InventoryWithTools("echo"));
        _supervisor.RaiseChanged(id, UpstreamChangeKind.Added);
        _supervisor.RemoveServer(id);
        _supervisor.RaiseChanged(id, UpstreamChangeKind.Removed);

        received.Select(e => e.Kind).Should().Equal(CatalogChangeKind.ServerAdded, CatalogChangeKind.ServerRemoved);
        catalog.Snapshot.Should().BeEmpty();
    }

    [Fact]
    public void Dangling_profile_reference_falls_back_to_default_view()
    {
        _supervisor.SetServer("srv", TestData.InventoryWithTools("echo"));
        var catalog = CreateCatalog();
        var role = new Role(RoleId.New(), "admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool])]);
        _directory.UpsertRole(role);
        var id = IdentityId.New();
        _directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, [role.Id], ProfileId.New()));

        var view = catalog.GetViewFor(id);

        view.PinnedTools.Should().BeEmpty("gelöschtes Profil fällt auf die Default-Sicht zurück");
        view.LazyToolsEnabled.Should().BeTrue();
    }

    [Fact]
    public void View_for_100_tools_stays_under_10ms()
    {
        _supervisor.SetServer("big", new UpstreamInventory(
            [.. Enumerable.Range(0, 100).Select(i =>
                new ToolDescriptor($"tool_{i:D3}", $"Beschreibung für Werkzeug Nummer {i}", TestData.EmptySchema()))],
            [], []));
        var catalog = CreateCatalog();
        var profile = new ToolProfile(ProfileId.New(), "big",
            [.. Enumerable.Range(0, 10).Select(i => new NamespacedToolName($"big__tool_{i:D3}"))], true);
        var admin = RegisterGlobalAdmin(profile);

        catalog.GetViewFor(admin); // Warmup: Snapshot-Kompilierung

        const int iterations = 50;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            catalog.GetViewFor(admin);
        }

        sw.Stop();

        var perCall = sw.Elapsed / iterations;
        perCall.Should().BeLessThan(TimeSpan.FromMilliseconds(10),
            "WP2-DoD: GetViewFor mit 100 Tools < 10 ms");
    }
}
