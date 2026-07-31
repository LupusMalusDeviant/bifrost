using Bifrost.Abstractions;
using Bifrost.Core.Rbac;

namespace Bifrost.Core.Tests.Rbac;

/// <summary>
/// Referenz-Welt für die RBAC-Abnahmematrix (PRD-Kriterium 4):
/// 2 Server (github, files), 4 Tools, 1 Resource, 1 Prompt, 6 Rollen, 8 Identitäten.
/// </summary>
internal sealed class RbacTestWorld
{
    public InMemoryRbacDirectory Directory { get; } = new();

    public ServerId Github { get; } = ServerId.New();

    public ServerId Files { get; } = ServerId.New();

    public NamespacedToolName GithubListIssues { get; } = NamespacedToolName.Create("github", "list_issues");

    public NamespacedToolName GithubCreateIssue { get; } = NamespacedToolName.Create("github", "create_issue");

    public NamespacedToolName FilesReadFile { get; } = NamespacedToolName.Create("files", "read_file");

    public NamespacedToolName FilesDeleteFile { get; } = NamespacedToolName.Create("files", "delete_file");

    public NamespacedToolName GithubReadme { get; } = NamespacedToolName.Create("github", "readme");

    public NamespacedToolName FilesSummarize { get; } = NamespacedToolName.Create("files", "summarize");

    // Identitäten
    public IdentityId ListOnlyAgent { get; } = IdentityId.New();       // nur github__list_issues

    public IdentityId GithubAdmin { get; } = IdentityId.New();         // ganzer github-Server, alle Aktionen

    public IdentityId GlobalAdmin { get; } = IdentityId.New();         // alles

    public IdentityId MixedAgent { get; } = IdentityId.New();          // list_issues + alle files-Tools

    public IdentityId ResourceOnlyAgent { get; } = IdentityId.New();   // nur github-Resources

    public IdentityId NoRolesAgent { get; } = IdentityId.New();        // registriert, keine Rollen

    public IdentityId OrphanRoleAgent { get; } = IdentityId.New();     // referenziert gelöschte Rolle

    public IdentityId UnknownAgent { get; } = IdentityId.New();        // NICHT registriert

    public RbacTestWorld()
    {
        var githubReader = new Role(RoleId.New(), "github-reader",
            [new Grant(new PermissionScope(null, GithubListIssues), [ToolAction.UseTool])]);
        var githubAdmin = new Role(RoleId.New(), "github-admin",
            [new Grant(new PermissionScope(Github, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        var globalAdmin = new Role(RoleId.New(), "global-admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        var filesUser = new Role(RoleId.New(), "files-user",
            [new Grant(new PermissionScope(Files, null), [ToolAction.UseTool])]);
        var resourceOnly = new Role(RoleId.New(), "github-resource-only",
            [new Grant(new PermissionScope(Github, null), [ToolAction.ReadResource])]);
        var orphan = new Role(RoleId.New(), "wird-geloescht", []);

        foreach (var role in new[] { githubReader, githubAdmin, globalAdmin, filesUser, resourceOnly })
        {
            Directory.UpsertRole(role);
        }

        Directory.UpsertIdentity(new Identity(ListOnlyAgent, "list-only", IdentityKind.Agent, [githubReader.Id]));
        Directory.UpsertIdentity(new Identity(GithubAdmin, "github-admin", IdentityKind.Agent, [githubAdmin.Id]));
        Directory.UpsertIdentity(new Identity(GlobalAdmin, "global-admin", IdentityKind.Agent, [globalAdmin.Id]));
        Directory.UpsertIdentity(new Identity(MixedAgent, "mixed", IdentityKind.Agent, [githubReader.Id, filesUser.Id]));
        Directory.UpsertIdentity(new Identity(ResourceOnlyAgent, "resource-only", IdentityKind.Agent, [resourceOnly.Id]));
        Directory.UpsertIdentity(new Identity(NoRolesAgent, "no-roles", IdentityKind.Agent, []));
        Directory.UpsertIdentity(new Identity(OrphanRoleAgent, "orphan", IdentityKind.Agent, [orphan.Id]));
        // orphan-Rolle absichtlich nie upserted — verwaiste Referenz
    }

    public AuthorizationService CreateService() => new(Directory);

    public static PermissionScope ToolScope(ServerId server, NamespacedToolName tool) => new(server, tool);
}
