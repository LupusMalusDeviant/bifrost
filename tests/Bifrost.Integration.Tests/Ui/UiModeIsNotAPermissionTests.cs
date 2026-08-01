using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Integration.Tests.Gateway;
using Bifrost.Web;
using AspNetAuthorization = Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Ui;

/// <summary>
/// WP4.5, der zentrale Test: <b>Rolle × Modus × Route.</b>
/// <para>
/// Der Basic-/Advanced-Umschalter ist Darstellung. Er darf nichts erreichbar machen, was die Rolle
/// nicht ohnehin erreicht — und ebenso wenig etwas verschließen, das sie erreichen darf. Beides
/// wird hier gegen den laufenden Server geprüft, nicht gegen ein Modell: Die Frage lautet ja
/// gerade, ob der Server den Modus irgendwo auswertet.
/// </para>
/// <para>
/// Der Modus liegt in einem Cookie, das der Browser setzt. Diese Tests schicken es mit — in beiden
/// Werten und einmal absichtlich verfälscht. Kommt bei allen dreien dieselbe Antwort heraus, wertet
/// der Server das Cookie nirgends aus. Das ist der Beleg, der zählt: nicht „wir lesen es nicht",
/// sondern „es macht nachweislich keinen Unterschied".
/// </para>
/// </summary>
public sealed class UiModeIsNotAPermissionTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public UiModeIsNotAPermissionTests(GatewayFixture gw) => _gw = gw;

    /// <summary>Alle Ziele der Navigation, je einmal — Sprungmarken zeigen auf dieselbe Route.</summary>
    public static TheoryData<string> NavigationRoutes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in UiNavigation.All.Select(e => e.Path).Distinct())
            {
                data.Add(path);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NavigationRoutes))]
    public async Task Mode_makes_no_difference_for_any_role_on_any_route(string route)
    {
        foreach (var role in Enum.GetValues<UiRole>())
        {
            var cookie = await LoginAsync(role);

            var basic = await GetAsync(route, cookie, "basic");
            var advanced = await GetAsync(route, cookie, "advanced");
            var none = await GetAsync(route, cookie, mode: null);
            // Ein Wert, den die Oberfläche nie schreibt: Wenn irgendwo eine Entscheidung am Cookie
            // hinge, wäre spätestens hier ein anderer Ausgang zu sehen.
            var nonsense = await GetAsync(route, cookie, "advanced-and-then-some");

            advanced.Should().Be(basic, $"{role} erreicht {route} unabhängig vom Modus");
            none.Should().Be(basic, $"ohne Modus-Cookie ändert sich für {role} auf {route} nichts");
            nonsense.Should().Be(basic, $"ein gefälschtes Modus-Cookie verschiebt für {role} keine Grenze");
        }
    }

    [Fact]
    public async Task Direct_link_to_an_advanced_page_works_in_basic_mode()
    {
        // Der Modus versteckt, er verbietet nicht: Wer den Link kennt oder ihn gespeichert hat,
        // kommt an — ohne umzuschalten.
        var operatorCookie = await LoginAsync(UiRole.Operator);
        (await GetAsync("/tasks", operatorCookie, "basic")).Should().Be(
            HttpStatusCode.OK, "Vorgänge sind Feinsteuerung, aber der Operator darf sie sehen");

        var adminCookie = await LoginAsync(UiRole.Admin);
        foreach (var route in UiNavigation.ForMode(UiMode.Advanced)
                     .Where(e => e.Section is UiNavSection.Advanced)
                     .Select(e => e.Path)
                     .Distinct())
        {
            (await GetAsync(route, adminCookie, "basic")).Should().Be(
                HttpStatusCode.OK, $"der Direktlink auf {route} gilt auch im Basic-Modus");
        }
    }

    [Fact]
    public async Task No_role_reaches_more_through_the_navigation_than_through_its_policies()
    {
        // Die andere Richtung derselben Aussage: Was das Menü anbietet, deckt sich mit dem, was die
        // Policy hergibt. Ein Menüpunkt, der bei jemandem erscheint, der die Seite nicht öffnen
        // kann, wäre ein toter Link; einer, der bei jemandem fehlt, der sie öffnen darf, wäre eine
        // versteckte Funktion.
        var authorization = _gw.Services.GetRequiredService<AspNetAuthorization.IAuthorizationService>();

        foreach (var role in Enum.GetValues<UiRole>())
        {
            var cookie = await LoginAsync(role);
            var user = Principal(role);

            foreach (var entry in UiNavigation.All)
            {
                var offered = (await authorization.AuthorizeAsync(user, resource: null, entry.Policy)).Succeeded;
                var reachable = await GetAsync(entry.Path, cookie, "basic") != HttpStatusCode.Redirect;

                offered.Should().Be(
                    reachable,
                    $"'{entry.Label}' wird {role} angeboten, genau wenn {entry.Path} für sie erreichbar ist");
            }
        }
    }

    [Fact]
    public async Task Group_headings_appear_exactly_when_the_group_has_content()
    {
        // Die beiden Überschriften tragen je eine Policy. Sie muss von genau den Rollen erfüllbar
        // sein, die in dieser Gruppe mindestens einen Eintrag sehen — sonst steht irgendwo eine
        // Überschrift ohne Inhalt oder ein Eintrag ohne Überschrift.
        var authorization = _gw.Services.GetRequiredService<AspNetAuthorization.IAuthorizationService>();

        foreach (var role in Enum.GetValues<UiRole>())
        {
            var user = Principal(role);

            foreach (var (section, heading) in new[]
                     {
                         (UiNavSection.Basic, UiNavigation.BasicHeadingPolicy),
                         (UiNavSection.Advanced, UiNavigation.AdvancedHeadingPolicy),
                     })
            {
                var headingShown = (await authorization.AuthorizeAsync(user, resource: null, heading)).Succeeded;

                var anyEntry = false;
                foreach (var entry in UiNavigation.ForSection(section))
                {
                    anyEntry |= (await authorization.AuthorizeAsync(user, resource: null, entry.Policy)).Succeeded;
                }

                headingShown.Should().Be(
                    anyEntry, $"die Überschrift der Gruppe {section} passt für {role} zu ihrem Inhalt");
            }
        }
    }

    /// <summary>
    /// Ein Prinzipal, wie ihn die Anmeldung ausstellt (siehe AuthEndpoints). Der Test baut ihn
    /// selbst, prüft ihn aber gegen den <c>IAuthorizationService</c> der laufenden Anwendung —
    /// die Policies stammen also aus Program.cs und nicht aus einer Kopie im Test.
    /// </summary>
    private static ClaimsPrincipal Principal(UiRole role)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, $"test-{role}"), new Claim(UiPolicies.RoleClaim, role.ToString())],
            authenticationType: "Test"));

    /// <summary>Legt einen UI-Nutzer der Rolle an, meldet ihn an und liefert sein Anmelde-Cookie.</summary>
    private async Task<string> LoginAsync(UiRole role)
    {
        var name = $"mode-{role}-{Guid.NewGuid():N}";
        const string password = "modus-ist-keine-berechtigung";
        await _gw.UiUsers.CreateAsync(name, password, role, TestContext.Current.CancellationToken);

        using var client = _gw.CreateDefaultClient();
        var response = await client.PostAsync(
            "/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = name,
                ["password"] = password,
                ["returnUrl"] = "/",
            }),
            TestContext.Current.CancellationToken);

        var setCookie = response.Headers.GetValues("Set-Cookie")
            .FirstOrDefault(v => v.StartsWith("bifrost-ui=", StringComparison.Ordinal));
        setCookie.Should().NotBeNull($"die Anmeldung als {role} muss ein Sitzungscookie setzen");

        return setCookie!.Split(';')[0];
    }

    /// <summary>
    /// Ein GET mit selbst gesetztem Cookie-Kopf. Bewusst ohne Cookie-Container: Nur so steht genau
    /// das im Kopf, was dieser Test aussagen will — Anmeldung plus (oder ohne) Modus.
    /// </summary>
    private async Task<HttpStatusCode> GetAsync(string route, string authCookie, string? mode)
    {
        using var client = _gw.CreateDefaultClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add(
            "Cookie",
            mode is null ? authCookie : $"{authCookie}; {UiNavigation.ModeCookieName}={mode}");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
