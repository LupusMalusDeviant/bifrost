using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Rbac;
using Xunit;

namespace Bifrost.Core.Tests.Rbac;

/// <summary>
/// FR-21: Das Audit-Log trägt „Profil/Rolle" des Aufrufers im Klartext — die Id allein sagt beim
/// Nachvollziehen nichts.
/// </summary>
public class DescribeCallerTests
{
    private readonly InMemoryRbacDirectory _dir = new();
    private readonly AuthorizationService _auth;

    public DescribeCallerTests() => _auth = new AuthorizationService(_dir);

    private IdentityId Register(string name, Role? role = null, ToolProfile? profile = null)
    {
        if (role is not null) { _dir.UpsertRole(role); }
        if (profile is not null) { _dir.UpsertProfile(profile); }
        var id = IdentityId.New();
        _dir.UpsertIdentity(new Identity(
            id, name, IdentityKind.Agent,
            role is null ? [] : [role.Id],
            profile?.Id));
        return id;
    }

    [Fact]
    public void Description_carries_name_role_and_profile()
    {
        var role = new Role(RoleId.New(), "reader", []);
        var profile = new ToolProfile(ProfileId.New(), "readonly-profil", [], LazyToolsEnabled: true);
        var id = Register("ci-agent", role, profile);

        _auth.DescribeCaller(id).Should()
            .Contain("ci-agent").And.Contain("reader").And.Contain("readonly-profil");
    }

    [Fact]
    public void Missing_profile_is_stated_explicitly_not_left_blank()
    {
        var id = Register("agent-ohne-profil", new Role(RoleId.New(), "rolle", []));

        _auth.DescribeCaller(id).Should().Contain("kein Profil",
            "ein fehlendes Profil muss sichtbar sein, nicht als leeres Feld erscheinen");
    }

    [Fact]
    public void Roleless_identity_still_gets_a_description()
    {
        var id = Register("nackter-agent");

        _auth.DescribeCaller(id).Should().Contain("nackter-agent").And.Contain("ohne Rolle");
    }

    [Fact]
    public void Unregistered_identity_has_no_description()
    {
        _auth.DescribeCaller(IdentityId.New()).Should().BeNull();
    }
}
