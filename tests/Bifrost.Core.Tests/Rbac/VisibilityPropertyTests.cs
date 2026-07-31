using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Rbac;
using Bifrost.Core.Tests.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Rbac;

/// <summary>
/// WP2-DoD-Property-Test: über zufällige Grant-/Katalog-Kombinationen gilt immer
/// „sichtbar ⇔ erlaubt" — FilterVisible ist exakt die Menge der von Evaluate erlaubten Einträge.
/// Handgerollte Randomisierung mit festen Seeds (deterministisch, keine FsCheck-Abhängigkeit).
/// </summary>
public class VisibilityPropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(99991)]
    public void Visible_equals_allowed_for_random_worlds(int seed)
    {
        var random = new Random(seed);

        for (var iteration = 0; iteration < 50; iteration++)
        {
            var directory = new InMemoryRbacDirectory();
            var service = new AuthorizationService(directory);

            // Zufällige Welt: 1-4 Server mit je 1-6 Katalog-Einträgen
            var servers = Enumerable.Range(0, random.Next(1, 5))
                .Select(i => (Id: ServerId.New(), Slug: $"s{i}"))
                .ToList();
            var catalog = new List<CatalogEntry>();
            foreach (var (id, slug) in servers)
            {
                for (var t = 0; t < random.Next(1, 7); t++)
                {
                    var kind = (CatalogEntryKind)random.Next(0, 3);
                    catalog.Add(new CatalogEntry(
                        NamespacedToolName.Create(slug, $"tool{t}"), id, $"Beschreibung {slug} {t}",
                        TestData.EmptySchema(), kind, 10));
                }
            }

            // Zufällige Rollen: 0-4 Rollen mit je 0-3 zufälligen Grants
            var roles = new List<Role>();
            for (var r = 0; r < random.Next(0, 5); r++)
            {
                var grants = new List<Grant>();
                for (var g = 0; g < random.Next(0, 4); g++)
                {
                    var scopeKind = random.Next(0, 3);
                    var entry = catalog[random.Next(catalog.Count)];
                    PermissionScope scope = scopeKind switch
                    {
                        0 => new PermissionScope(null, null),
                        1 => new PermissionScope(entry.Server, null),
                        _ => new PermissionScope(null, entry.Name),
                    };
                    var actions = Enum.GetValues<ToolAction>().Where(_ => random.Next(2) == 0).ToList();
                    grants.Add(new Grant(scope, actions));
                }

                var role = new Role(RoleId.New(), $"rolle{r}", grants);
                roles.Add(role);
                directory.UpsertRole(role);
            }

            var identity = IdentityId.New();
            directory.UpsertIdentity(new Identity(
                identity, "prop", IdentityKind.Agent,
                [.. roles.Where(_ => random.Next(2) == 0).Select(r => r.Id)]));

            var visible = service.FilterVisible(identity, catalog);
            var expectedVisible = catalog.Where(e =>
                service.Evaluate(identity, new PermissionScope(e.Server, e.Name), ActionFor(e.Kind)).Allowed);

            visible.Should().BeEquivalentTo(
                expectedVisible,
                $"Seed {seed}, Iteration {iteration}: FilterVisible muss exakt der Evaluate-Erlaubnismenge entsprechen");

            // Unbekannte Identität sieht in derselben Welt nichts (Default-Deny)
            service.FilterVisible(IdentityId.New(), catalog).Should().BeEmpty();
        }
    }

    private static ToolAction ActionFor(CatalogEntryKind kind) => kind switch
    {
        CatalogEntryKind.Resource => ToolAction.ReadResource,
        CatalogEntryKind.Prompt => ToolAction.UsePrompt,
        _ => ToolAction.UseTool,
    };
}
