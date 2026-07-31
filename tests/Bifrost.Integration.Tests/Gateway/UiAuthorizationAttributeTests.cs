using System.Reflection;
using AwesomeAssertions;
using Bifrost.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// FR-30/36: Bei Blazor Interactive Server laufen Mutationen über den Circuit, nicht über
/// HTTP-POST — die Autorisierung hängt also am <see cref="AuthorizeAttribute"/> der Seite.
/// Die darunterliegenden Services (<c>IRbacManagement</c>, <c>IUpstreamSupervisor</c>) prüfen
/// bewusst keine UI-Rollen; fällt ein Attribut weg, gibt es keine zweite Verteidigungslinie.
///
/// Dieser Test hält deshalb fest, welche Route welche Policy tragen muss. Er ersetzt keinen
/// End-to-End-Klick, schließt aber genau die Regression, die sonst unbemerkt bliebe.
/// </summary>
public class UiAuthorizationAttributeTests
{
    public static TheoryData<string, string> ExpectedPolicies => new()
    {
        { "/servers", UiPolicies.Operator },
        { "/tools", UiPolicies.Authenticated },
        { "/rbac", UiPolicies.Admin },
        { "/profiles", UiPolicies.Admin },
        { "/users", UiPolicies.Admin },
        { "/assets", UiPolicies.Admin },
        { "/guardrails", UiPolicies.Admin },
        { "/approvals", UiPolicies.Operator },
        { "/webhooks", UiPolicies.Admin },
        { "/logs", UiPolicies.Authenticated },
        { "/", UiPolicies.Authenticated },
    };

    [Theory]
    [MemberData(nameof(ExpectedPolicies))]
    public void Routable_page_carries_the_expected_policy(string route, string expectedPolicy)
    {
        var page = RoutableComponents()
            .SingleOrDefault(t => t.GetCustomAttributes<RouteAttribute>().Any(r => r.Template == route));

        page.Should().NotBeNull($"die Route {route} muss von einer Komponente bedient werden");

        var authorize = page!.GetCustomAttribute<AuthorizeAttribute>();
        authorize.Should().NotBeNull($"{page.Name} ist ohne [Authorize] öffentlich erreichbar");
        authorize!.Policy.Should().Be(expectedPolicy);
    }

    /// <summary>Seiten, die absichtlich ohne Anmeldung erreichbar sind — sonst käme niemand hinein.</summary>
    private static readonly string[] PublicByDesign = ["Login"];

    [Fact]
    public void No_routable_page_is_left_unauthorized()
    {
        // Fängt neue Seiten ab, die jemand ohne Attribut hinzufügt — die Tabelle oben kennt sie ja noch nicht.
        var unprotected = RoutableComponents()
            .Where(t => t.GetCustomAttribute<AuthorizeAttribute>() is null)
            .Select(t => t.Name)
            .Except(PublicByDesign)
            .ToList();

        unprotected.Should().BeEmpty("jede routbare Seite braucht eine Policy");
    }

    private static IEnumerable<Type> RoutableComponents()
        => typeof(UiPolicies).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any());
}
