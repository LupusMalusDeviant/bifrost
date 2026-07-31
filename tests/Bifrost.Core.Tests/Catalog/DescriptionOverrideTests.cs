using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Catalog;
using Bifrost.Core.Rbac;
using Bifrost.Core.Tests.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Catalog;

/// <summary>
/// FR-14: serverseitiger Description-Override. Zweck ist Token-Sparen, deshalb muss er nicht nur
/// die Anzeige, sondern auch die Token-Schätzung und die Suche beeinflussen.
/// </summary>
public class DescriptionOverrideTests
{
    private sealed class FakeOverrides : IToolDescriptionOverrides
    {
        private readonly Dictionary<NamespacedToolName, string> _values = [];

        public event EventHandler? Changed;

        public IReadOnlyDictionary<NamespacedToolName, string> All => _values;

        public string? GetOverride(NamespacedToolName tool) => _values.GetValueOrDefault(tool);

        public Task SetAsync(NamespacedToolName tool, string? description, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                _values.Remove(tool);
            }
            else
            {
                _values[tool] = description;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private static readonly string Verbose =
        "Dieses Werkzeug " + new string('x', 800) + " und erklaert sich in epischer Breite.";

    private readonly FakeSupervisor _supervisor = new();
    private readonly InMemoryRbacDirectory _directory = new();
    private readonly FakeOverrides _overrides = new();

    private ToolCatalog CreateCatalog()
    {
        _supervisor.SetServer("srv", new UpstreamInventory(
            [new ToolDescriptor("chatty", Verbose, TestData.EmptySchema())], [], []));
        return new ToolCatalog(_supervisor, new AuthorizationService(_directory), _directory, _overrides);
    }

    private IdentityId RegisterAdmin()
    {
        var role = new Role(RoleId.New(), "admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool])]);
        _directory.UpsertRole(role);
        var id = IdentityId.New();
        _directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, [role.Id]));
        return id;
    }

    [Fact]
    public async Task Override_replaces_description_and_lowers_token_estimate()
    {
        using var catalog = CreateCatalog();
        var tool = new NamespacedToolName("srv__chatty");

        var before = catalog.Find(tool)!;
        before.Description.Should().Be(Verbose);

        await _overrides.SetAsync(tool, "Kurz und knapp.", TestContext.Current.CancellationToken);

        var after = catalog.Find(tool)!;
        after.Description.Should().Be("Kurz und knapp.", "der Override schlägt die Upstream-Beschreibung");
        after.EstimatedSchemaTokens.Should().BeLessThan(before.EstimatedSchemaTokens,
            "genau darum geht es bei FR-14: weniger Kontext-Tokens");
    }

    [Fact]
    public async Task Override_change_raises_catalog_changed_for_list_changed()
    {
        using var catalog = CreateCatalog();
        var raised = 0;
        catalog.Changed += (_, _) => raised++;

        await _overrides.SetAsync(new NamespacedToolName("srv__chatty"), "Neu.", TestContext.Current.CancellationToken);

        raised.Should().BeGreaterThan(0, "Agenten müssen die neue Beschreibung über list_changed erfahren");
    }

    [Fact]
    public async Task Search_matches_the_override_not_the_original()
    {
        using var catalog = CreateCatalog();
        var admin = RegisterAdmin();
        var tool = new NamespacedToolName("srv__chatty");

        await _overrides.SetAsync(tool, "Rechnungen exportieren.", TestContext.Current.CancellationToken);

        catalog.Search(admin, "Rechnungen", 10).Should().ContainSingle()
            .Which.Name.Should().Be(tool);
        catalog.Search(admin, "epischer", 10).Should().BeEmpty(
            "die überschriebene Beschreibung ersetzt die alte auch in der Suche");
    }

    [Fact]
    public async Task Clearing_the_override_restores_the_upstream_description()
    {
        using var catalog = CreateCatalog();
        var tool = new NamespacedToolName("srv__chatty");
        await _overrides.SetAsync(tool, "Kurz.", TestContext.Current.CancellationToken);

        await _overrides.SetAsync(tool, null, TestContext.Current.CancellationToken);

        catalog.Find(tool)!.Description.Should().Be(Verbose);
    }
}
