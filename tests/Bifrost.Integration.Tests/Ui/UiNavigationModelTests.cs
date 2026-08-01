using System.Reflection;
using AwesomeAssertions;
using Bifrost.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Bifrost.Integration.Tests.Ui;

/// <summary>
/// WP4.5: Die Zuordnung Basic/Advanced ist eine Aussage über die Oberfläche, und sie muss prüfbar
/// sein, ohne einen Browser zu starten. Deshalb liegt sie in <see cref="UiNavigation"/> und nicht
/// im Razor-Markup — und deshalb steht dieser Test hier.
/// <para>
/// Die zentrale Zusage des Pakets: <b>Der Modus ist Darstellung, keine Berechtigungsgrenze.</b>
/// Dieser Test hält die strukturelle Hälfte davon fest (die Mengen und die Policies der Seiten);
/// die andere Hälfte — dass der Server den Modus überhaupt nie sieht — steht in
/// <see cref="UiModeIsNotAPermissionTests"/>.
/// </para>
/// </summary>
public class UiNavigationModelTests
{
    [Fact]
    public void Basic_is_a_subset_of_advanced()
    {
        // Die Kernaussage in einer Zeile: Ein Moduswechsel kann nichts HINZUFÜGEN, was es in der
        // größeren Ansicht nicht ohnehin gibt. Wären es zwei verschiedene Mengen, könnte ein
        // Menüpunkt existieren, den nur ein bestimmter Modus zeigt — und genau daraus wird mit der
        // Zeit eine gefühlte Berechtigung.
        var basic = UiNavigation.ForMode(UiMode.Basic);
        var advanced = UiNavigation.ForMode(UiMode.Advanced);

        advanced.Should().Contain(basic, "Basic ist eine Teilmenge von Erweitert, keine andere Menge");
        advanced.Count.Should().BeGreaterThan(basic.Count, "sonst wäre der Modus wirkungslos");
    }

    [Fact]
    public void Every_entry_points_at_a_page_that_carries_the_declared_policy()
    {
        // Ein Menüpunkt, der eine andere Policy behauptet als seine Seite, ist entweder ein toter
        // Link (Menü strenger als Seite) oder ein Leck im Menü (Menü lockerer als Seite: der
        // Eintrag erscheint bei jemandem, der die Seite gar nicht öffnen kann).
        foreach (var entry in UiNavigation.All)
        {
            var page = RoutableComponents()
                .SingleOrDefault(t => t.GetCustomAttributes<RouteAttribute>().Any(r => r.Template == entry.Path));

            page.Should().NotBeNull($"der Menüpunkt '{entry.Label}' zeigt auf {entry.Path}");
            page!.GetCustomAttribute<AuthorizeAttribute>()?.Policy.Should().Be(
                entry.Policy,
                $"'{entry.Label}' verspricht {entry.Policy} — die Seite muss dieselbe Policy tragen");
        }
    }

    [Fact]
    public void Every_authorized_page_appears_in_the_navigation()
    {
        // Die Gegenrichtung: Eine Seite, die niemand im Menü findet, ist so gut wie nicht da.
        // Drei sind absichtlich nicht im Menü — man ist dort, bevor es ein Menü gibt. Der geführte
        // Erstaufbau (WP4.4) gehört dazu: Er legt den Zugang erst an, sein Weg dorthin führt über
        // /setup, /login und den leeren Zustand des Dashboards. Ein anonymer Eintrag zwischen lauter
        // rollengebundenen wäre die eine Zeile, die den Rollenfilter des Menüs aushebelt.
        string[] outsideTheShell = ["/login", "/setup", UiNavigation.SetupWizardRoute];

        var routed = RoutableComponents()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template)
            .Except(outsideTheShell);

        var linked = UiNavigation.All.Select(e => e.Path).Distinct();

        routed.Should().BeSubsetOf(linked, "jede Seite hinter der Anmeldung braucht einen Weg dorthin");
    }

    [Fact]
    public void Sub_entries_stay_inside_their_parent_page_and_section()
    {
        // Ein Untereintrag ist eine Sprungmarke in eine Seite, keine eigene Route. Läge er in einer
        // anderen Stufe als sein Elterneintrag, stünde im Basic-Modus ein Untereintrag ohne
        // Überschrift oder — schlimmer — ein Link in eine Seite, die die Ansicht gar nicht anbietet.
        foreach (var sub in UiNavigation.All.Where(e => e.IsSubEntry))
        {
            var parent = UiNavigation.All.SingleOrDefault(e => !e.IsSubEntry && e.Route == sub.Parent);

            parent.Should().NotBeNull($"'{sub.Label}' verweist auf den Elterneintrag {sub.Parent}");
            sub.Path.Should().Be(parent!.Route, "die Sprungmarke gehört in die Seite des Elterneintrags");
            sub.Section.Should().Be(
                UiNavSection.Advanced,
                "Untereintraege sind Feinsteuerung — sonst würde der Kern länger statt kürzer");
            sub.Policy.Should().Be(parent.Policy, "eine Sprungmarke kann nicht strenger sein als ihre Seite");
            sub.Route.Should().Contain("#", "ein Untereintrag ohne Marke wäre ein zweiter Link auf dieselbe Seite");
        }
    }

    [Fact]
    public void No_route_is_offered_twice()
    {
        var duplicates = UiNavigation.All
            .GroupBy(e => e.Route)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        duplicates.Should().BeEmpty("zwei Menüpunkte auf dasselbe Ziel sind ein Wartungsfehler");
    }

    [Fact]
    public void Basic_tasks_never_need_an_advanced_page()
    {
        // Das DoD des Pflichtenhefts, als Test: „Basisaufgaben benötigen keine Advanced-Seite."
        // Rutscht eine dieser Routen in die Feinsteuerung, wird der Lauf rot — und nicht erst der
        // erste Nutzer, der sie sucht.
        foreach (var (task, route) in UiNavigation.BasicTasks)
        {
            var entry = UiNavigation.All.SingleOrDefault(e => !e.IsSubEntry && e.Route == route);

            entry.Should().NotBeNull($"die Basisaufgabe '{task}' braucht die Route {route}");
            entry!.Section.Should().Be(
                UiNavSection.Basic,
                $"'{task}' ist eine Basisaufgabe und darf keine Advanced-Seite verlangen");
        }
    }

    private static IEnumerable<Type> RoutableComponents()
        => typeof(UiPolicies).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any());
}
