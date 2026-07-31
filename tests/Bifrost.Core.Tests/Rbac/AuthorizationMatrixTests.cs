using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Rbac;
using Xunit;

namespace Bifrost.Core.Tests.Rbac;

/// <summary>
/// RBAC-Abnahmematrix (PRD-Kriterium 4, WP2-DoD): 24 Erlaubt/Verboten-Kombinationen
/// über Wirkungsebenen (global/Server/Tool), Aktionen und Identitäts-Sonderfälle.
/// </summary>
public class AuthorizationMatrixTests
{
    private static readonly RbacTestWorld W = new();
    private static readonly AuthorizationService Service = W.CreateService();

    public static TheoryData<string, bool> Matrix()
    {
        var data = new TheoryData<string, bool>();
        foreach (var (key, _, _, _, expected) in Cases())
        {
            data.Add(key, expected);
        }

        return data;
    }

    private static IEnumerable<(string Key, IdentityId Identity, PermissionScope Scope, ToolAction Action, bool Expected)> Cases()
    {
        // 1-4: Tool-genauer Grant deckt genau dieses Tool, sonst nichts
        yield return ("01 list-only darf list_issues nutzen", W.ListOnlyAgent, RbacTestWorld.ToolScope(W.Github, W.GithubListIssues), ToolAction.UseTool, true);
        yield return ("02 list-only darf create_issue NICHT nutzen", W.ListOnlyAgent, RbacTestWorld.ToolScope(W.Github, W.GithubCreateIssue), ToolAction.UseTool, false);
        yield return ("03 list-only darf files__read_file NICHT nutzen", W.ListOnlyAgent, RbacTestWorld.ToolScope(W.Files, W.FilesReadFile), ToolAction.UseTool, false);
        yield return ("04 list-only darf github-Resources NICHT lesen", W.ListOnlyAgent, new PermissionScope(W.Github, W.GithubReadme), ToolAction.ReadResource, false);

        // 5-9: Server-Grant vererbt auf alle Tools/Aktionen des Servers (FR-28), aber nicht auf andere Server
        yield return ("05 github-admin darf list_issues nutzen", W.GithubAdmin, RbacTestWorld.ToolScope(W.Github, W.GithubListIssues), ToolAction.UseTool, true);
        yield return ("06 github-admin darf create_issue nutzen", W.GithubAdmin, RbacTestWorld.ToolScope(W.Github, W.GithubCreateIssue), ToolAction.UseTool, true);
        yield return ("07 github-admin darf github-Resources lesen", W.GithubAdmin, new PermissionScope(W.Github, W.GithubReadme), ToolAction.ReadResource, true);
        yield return ("08 github-admin darf github-Prompts nutzen", W.GithubAdmin, new PermissionScope(W.Github, W.GithubReadme), ToolAction.UsePrompt, true);
        yield return ("09 github-admin darf files__delete_file NICHT nutzen", W.GithubAdmin, RbacTestWorld.ToolScope(W.Files, W.FilesDeleteFile), ToolAction.UseTool, false);

        // 10-12: Globaler Grant deckt alles
        yield return ("10 global-admin darf create_issue nutzen", W.GlobalAdmin, RbacTestWorld.ToolScope(W.Github, W.GithubCreateIssue), ToolAction.UseTool, true);
        yield return ("11 global-admin darf files__delete_file nutzen", W.GlobalAdmin, RbacTestWorld.ToolScope(W.Files, W.FilesDeleteFile), ToolAction.UseTool, true);
        yield return ("12 global-admin darf files-Prompts nutzen", W.GlobalAdmin, new PermissionScope(W.Files, W.FilesSummarize), ToolAction.UsePrompt, true);

        // 13-16: Rollen-Vereinigung (mehrere Rollen addieren sich, FR-28)
        yield return ("13 mixed darf list_issues nutzen (Rolle 1)", W.MixedAgent, RbacTestWorld.ToolScope(W.Github, W.GithubListIssues), ToolAction.UseTool, true);
        yield return ("14 mixed darf files__read_file nutzen (Rolle 2)", W.MixedAgent, RbacTestWorld.ToolScope(W.Files, W.FilesReadFile), ToolAction.UseTool, true);
        yield return ("15 mixed darf files__delete_file nutzen (Server-Grant)", W.MixedAgent, RbacTestWorld.ToolScope(W.Files, W.FilesDeleteFile), ToolAction.UseTool, true);
        yield return ("16 mixed darf create_issue NICHT nutzen", W.MixedAgent, RbacTestWorld.ToolScope(W.Github, W.GithubCreateIssue), ToolAction.UseTool, false);

        // 17-19: Aktionstrennung — ReadResource-Grant gibt kein UseTool und umgekehrt (FR-28)
        yield return ("17 resource-only darf github-Resources lesen", W.ResourceOnlyAgent, new PermissionScope(W.Github, W.GithubReadme), ToolAction.ReadResource, true);
        yield return ("18 resource-only darf list_issues NICHT nutzen", W.ResourceOnlyAgent, RbacTestWorld.ToolScope(W.Github, W.GithubListIssues), ToolAction.UseTool, false);
        yield return ("19 files-user (mixed) darf files-Resources NICHT lesen", W.MixedAgent, new PermissionScope(W.Files, W.FilesSummarize), ToolAction.ReadResource, false);

        // 20-24: Default-Deny-Sonderfälle (FR-29)
        yield return ("20 Identität ohne Rollen darf nichts", W.NoRolesAgent, RbacTestWorld.ToolScope(W.Github, W.GithubListIssues), ToolAction.UseTool, false);
        yield return ("21 verwaiste Rollen-Referenz gewährt nichts", W.OrphanRoleAgent, RbacTestWorld.ToolScope(W.Github, W.GithubListIssues), ToolAction.UseTool, false);
        yield return ("22 unbekannte Identität darf nichts", W.UnknownAgent, RbacTestWorld.ToolScope(W.Github, W.GithubListIssues), ToolAction.UseTool, false);
        yield return ("23 unbekannte Identität darf auch global nichts", W.UnknownAgent, new PermissionScope(null, null), ToolAction.UseTool, false);
        yield return ("24 list-only: Server-Scope ohne Tool ist NICHT gedeckt", W.ListOnlyAgent, new PermissionScope(W.Github, null), ToolAction.UseTool, false);
        yield return ("25 no-roles: globaler Scope wird global-begründet verweigert", W.NoRolesAgent, new PermissionScope(null, null), ToolAction.UseTool, false);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Matrix_case_evaluates_as_expected(string caseKey, bool expected)
    {
        var (_, identity, scope, action, _) = Cases().Single(c => c.Key == caseKey);

        var decision = Service.Evaluate(identity, scope, action);

        decision.Allowed.Should().Be(expected, caseKey);
        if (!expected)
        {
            decision.DenyReason.Should().NotBeNullOrWhiteSpace("jeder Deny braucht einen auditierbaren Grund (FR-22)");
        }
    }

    [Fact]
    public void Matrix_covers_at_least_20_cases()
    {
        Cases().Count().Should().BeGreaterThanOrEqualTo(20, "PRD-Abnahmekriterium 4 verlangt ≥ 20 Kombinationen");
        Cases().Count(c => c.Expected).Should().BeGreaterThanOrEqualTo(8, "Matrix braucht substanzielle Allow-Fälle");
        Cases().Count(c => !c.Expected).Should().BeGreaterThanOrEqualTo(8, "Matrix braucht substanzielle Deny-Fälle");
    }

    [Fact]
    public void Grant_changes_take_effect_immediately()
    {
        var world = new RbacTestWorld();
        var service = world.CreateService();
        var scope = RbacTestWorld.ToolScope(world.Github, world.GithubCreateIssue);

        service.Evaluate(world.ListOnlyAgent, scope, ToolAction.UseTool).Allowed.Should().BeFalse();

        var extraRole = new Role(RoleId.New(), "nachtrag",
            [new Grant(new PermissionScope(null, world.GithubCreateIssue), [ToolAction.UseTool])]);
        world.Directory.UpsertRole(extraRole);
        var identity = world.Directory.GetIdentity(world.ListOnlyAgent)!;
        world.Directory.UpsertIdentity(identity with { Roles = [.. identity.Roles, extraRole.Id] });

        service.Evaluate(world.ListOnlyAgent, scope, ToolAction.UseTool).Allowed
            .Should().BeTrue("der Snapshot-Cache muss über Directory.Version invalidieren");

        world.Directory.RemoveIdentity(world.ListOnlyAgent);
        service.Evaluate(world.ListOnlyAgent, scope, ToolAction.UseTool).Allowed
            .Should().BeFalse("entfernte Identitäten verlieren sofort alle Rechte");
    }
}
