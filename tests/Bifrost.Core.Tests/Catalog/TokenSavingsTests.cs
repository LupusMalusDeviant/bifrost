using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Catalog;
using Bifrost.Core.Rbac;
using Xunit;

namespace Bifrost.Core.Tests.Catalog;

/// <summary>
/// WP7.1 / PRD-Abnahmekriterium 2: Referenz-Setup mit 10 Servern à 10 Tools (100 Tools).
/// Belegt ≥ 80 % Schema-Token-Ersparnis eines Lazy-Profils gegenüber der Voll-Exposition (Z-2).
/// </summary>
public class TokenSavingsTests
{
    private const int Servers = 10;
    private const int ToolsPerServer = 10;

    private readonly ITestOutputHelper _output;

    public TokenSavingsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Lazy_profile_saves_at_least_80_percent_versus_full_exposure()
    {
        var (catalog, directory) = BuildReferenceSetup();

        // Voll-Exposition: ein Agent, der alle 100 Tools mit vollem Schema sähe.
        var fullTokens = catalog.Snapshot.Where(e => e.Kind == CatalogEntryKind.Tool)
            .Sum(e => e.EstimatedSchemaTokens);

        // Lazy-Profil: keine gepinnten Tools, nur die drei Meta-Tools.
        var lazyProfile = new ToolProfile(ProfileId.New(), "lazy", [], LazyToolsEnabled: true);
        var lazyAgent = RegisterAgent(directory, lazyProfile);
        var lazyTokens = catalog.GetViewFor(lazyAgent).EstimatedContextTokens;

        // Hybrid: 5 gepinnte Tools voll + Lazy für den Rest.
        var pinned = catalog.Snapshot.Where(e => e.Kind == CatalogEntryKind.Tool).Take(5).Select(e => e.Name).ToList();
        var hybridProfile = new ToolProfile(ProfileId.New(), "hybrid", pinned, LazyToolsEnabled: true);
        var hybridAgent = RegisterAgent(directory, hybridProfile);
        var hybridTokens = catalog.GetViewFor(hybridAgent).EstimatedContextTokens;

        var lazySavings = 100.0 * (fullTokens - lazyTokens) / fullTokens;
        var hybridSavings = 100.0 * (fullTokens - hybridTokens) / fullTokens;

        _output.WriteLine($"Referenz-Setup: {Servers} Server × {ToolsPerServer} Tools = {Servers * ToolsPerServer} Tools");
        _output.WriteLine($"Voll-Exposition:  {fullTokens,6} Tokens");
        _output.WriteLine($"Lazy-Profil:      {lazyTokens,6} Tokens  → {lazySavings:0.0}% Ersparnis");
        _output.WriteLine($"Hybrid (5 pinned):{hybridTokens,6} Tokens  → {hybridSavings:0.0}% Ersparnis");

        fullTokens.Should().BeGreaterThan(5000, "100 realistische Tool-Schemas kosten spürbar Kontext");
        lazySavings.Should().BeGreaterThanOrEqualTo(80.0, "PRD-Kriterium 2 / Z-2: Lazy-Profil ≥ 80 % Ersparnis");
        hybridSavings.Should().BeGreaterThanOrEqualTo(80.0, "auch das Hybrid-Profil mit 5 Pins bleibt ≥ 80 %");
    }

    private static (ToolCatalog Catalog, InMemoryRbacDirectory Directory) BuildReferenceSetup()
    {
        var supervisor = new FakeSupervisor();
        for (var s = 0; s < Servers; s++)
        {
            var tools = Enumerable.Range(0, ToolsPerServer)
                .Select(t => new ToolDescriptor(
                    $"operation_{t:D2}",
                    $"Performs operation {t} on the {s} subsystem, validating inputs and returning a structured result.",
                    RealisticSchema()))
                .ToList();
            supervisor.SetServer($"server{s:D2}", new UpstreamInventory(tools, [], []));
        }

        var directory = new InMemoryRbacDirectory();
        var authorization = new AuthorizationService(directory);
        var catalog = new ToolCatalog(supervisor, authorization, directory);
        return (catalog, directory);
    }

    private static IdentityId RegisterAgent(InMemoryRbacDirectory directory, ToolProfile profile)
    {
        directory.UpsertProfile(profile);
        var role = new Role(RoleId.New(), "admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        directory.UpsertRole(role);
        var id = IdentityId.New();
        directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, [role.Id], profile.Id));
        return id;
    }

    /// <summary>Ein realistisch großes Tool-Schema (mehrere typisierte Parameter mit Beschreibungen).</summary>
    private static JsonElement RealisticSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "target": { "type": "string", "description": "The identifier of the target resource to operate on." },
                "options": { "type": "object", "description": "Additional options controlling the operation behaviour.",
                  "properties": {
                    "recursive": { "type": "boolean", "description": "Whether to apply recursively to children." },
                    "limit": { "type": "integer", "description": "Maximum number of items to process." }
                  }
                },
                "filters": { "type": "array", "description": "Filter expressions applied before processing.",
                  "items": { "type": "string" } },
                "dryRun": { "type": "boolean", "description": "If true, validate but do not persist changes." }
              },
              "required": ["target"]
            }
            """);
        return doc.RootElement.Clone();
    }
}
