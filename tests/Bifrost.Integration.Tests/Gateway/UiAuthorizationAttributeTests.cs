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
        // Betrieb: Von hier aus entsteht ein Vollbackup — es enthaelt den Key-Ring und ist damit so
        // schuetzenswert wie die Instanz selbst (ADR-0024 E3). Operator reicht dafuer nicht.
        { "/operations", UiPolicies.Admin },
        { "/logs", UiPolicies.Authenticated },
        { "/", UiPolicies.Authenticated },
        // Beide standen bisher nicht in dieser Tabelle: Sie kamen nach ihr dazu und wurden nur vom
        // Auffangtest unten gehalten, der lediglich „irgendeine Policy" verlangt. Mit der
        // Basic-/Advanced-Navigation (WP4.5) stehen sie jetzt im Menü und damit im Blickfeld.
        { "/tasks", UiPolicies.Operator },
        { "/packages", UiPolicies.Admin },
    };

    /// <summary>
    /// WP4.5: Was das Menü verspricht, muss die Seite halten. Ein Menüpunkt mit lockererer Policy
    /// als seine Seite erscheint bei jemandem, der die Seite gar nicht öffnen kann — ein toter Link
    /// mit dem Beigeschmack einer versteckten Funktion. Umgekehrt wäre die Seite unauffindbar.
    /// </summary>
    [Fact]
    public void Navigation_entries_and_pages_agree_on_the_policy()
    {
        foreach (var entry in UiNavigation.All)
        {
            var page = RoutableComponents()
                .SingleOrDefault(t => t.GetCustomAttributes<RouteAttribute>().Any(r => r.Template == entry.Path));

            page.Should().NotBeNull($"der Menüpunkt '{entry.Label}' zeigt auf {entry.Path}");
            page!.GetCustomAttribute<AuthorizeAttribute>()?.Policy.Should().Be(entry.Policy);
        }
    }

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

    /// <summary>
    /// Seiten, die absichtlich ohne Anmeldung erreichbar sind — sonst käme niemand hinein.
    /// <para>
    /// <c>Setup</c> ist der Erstzugang (WP3.4). Sie ist eine reine Eingabemaske: Sie verrät nicht,
    /// ob gerade ein Token aussteht, und sie kann nichts anlegen. Die Entscheidung fällt im Server,
    /// gegen den gespeicherten Hash — eine Anmeldung davorzuhängen wäre ein Henne-Ei.
    /// </para>
    /// <para>
    /// <c>SetupWizard</c> ist der geführte Erstaufbau (WP4.4) und steht aus demselben Grund hier:
    /// Sein Schritt 2 <em>legt den Zugang an</em>. Was die Seite ohne Anmeldung zeigt, ist der
    /// Zustand der Einrichtung und dasselbe Token-Formular wie <c>/setup</c>; alles, was etwas
    /// anlegt, hängt an den Diensten dahinter, und die prüfen ihre Rollen selbst. Der Vorgang
    /// bekommt beim ersten angemeldeten Aufruf einen Eigentümer und wird danach keinem anderen
    /// mehr herausgegeben.
    /// </para>
    /// </summary>
    private static readonly string[] PublicByDesign = ["Login", "Setup", "SetupWizard"];

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
