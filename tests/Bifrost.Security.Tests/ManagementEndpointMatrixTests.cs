using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using AwesomeAssertions;
using Bifrost.Security.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// <b>Invariante 2:</b> Jeder Managementendpunkt verlangt Authentifizierung <b>und</b> einen
/// Global-Grant.
/// <para>
/// <b>Der Kern dieses Pakets.</b> Die Endpunktliste wird nicht aufgezaehlt, sondern vom laufenden
/// Host erfragt (<see cref="EndpointDataSource"/>) — und die Erwartung ist <em>fail-closed</em>:
/// Ein Endpunkt gilt als Management, solange er nicht ausdruecklich in
/// <see cref="OpenToEveryAuthenticatedIdentity"/> steht. Wer einen neuen Endpunkt anlegt und den
/// Filter vergisst, bekommt hier ein Rot mit Route und Methode im Klartext.
/// </para>
/// <para>
/// <b>Warum das anders ist als der bisherige Test.</b>
/// <c>RestFacadeTests.Management_requires_global_grant_and_can_add_servers</c> prueft <em>eine</em>
/// Gruppe. Die Zusicherung im Namen gilt fuer alle — genau die Bauart, die beim Redactor sechs
/// Transporte behauptete und einen prueste. Elf Gruppen tragen den Filter heute per Copy-Paste;
/// nichts wird rot, wenn die zwoelfte ihn nicht bekommt.
/// </para>
/// </summary>
public class ManagementEndpointMatrixTests : IClassFixture<SecurityGatewayFixture>
{
    private readonly SecurityGatewayFixture _gateway;

    public ManagementEndpointMatrixTests(SecurityGatewayFixture gateway) => _gateway = gateway;

    /// <summary>
    /// Die <b>einzige</b> Liste in diesem Test, und sie zaehlt die Ausnahmen auf, nicht die Regel.
    /// Ein Eintrag hier ist eine Entscheidung: „dieser Endpunkt darf von jeder authentifizierten
    /// Identitaet benutzt werden, weil RBAC die Sichtbarkeit dahinter filtert."
    /// </summary>
    private static readonly string[] OpenToEveryAuthenticatedIdentity =
    [
        // Die Tool-Fassade. IAuthorizationService.FilterVisible entscheidet je Eintrag; ein
        // Global-Grant davor wuerde die Fassade fuer normale Agenten schliessen.
        "/api/v1/tools",
        "/api/v1/tools/{name}/invoke",
        "/api/v1/capabilities",
        "/api/v1/capabilities/{id}/invoke",
        "/api/v1/openapi.json",
        // Vorgaenge: sichtbar fuer den Eigentuemer, sonst nur mit Global-Grant. Die Pruefung
        // steht je Vorgang in MaySee — ein Gruppenfilter waere hier die falsche Antwort, weil er
        // dem Aufrufer den eigenen Vorgang verwehrte.
        "/api/v1/tasks",
        "/api/v1/tasks/{id}",
        "/api/v1/tasks/{id}/cancel",
    ];

    private Probe[] ManagementRoutes()
    {
        var routes = _gateway.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new Probe(
                Methods(endpoint),
                Normalize(endpoint.RoutePattern.RawText),
                RequiredQuery(endpoint)))
            .Where(entry => entry.Route.StartsWith("/api/", StringComparison.Ordinal))
            .DistinctBy(entry => (entry.Method, entry.Route))
            .OrderBy(entry => entry.Route, StringComparer.Ordinal)
            .ThenBy(entry => entry.Method, StringComparer.Ordinal)
            .ToArray();

        routes.Should().NotBeEmpty(
            "ohne Endpunkte prueft die Matrix nichts — genau der gruene Test, den es hier nicht "
            + "geben darf");
        return routes;
    }

    /// <param name="Query">Pflicht-Query-Parameter, aus der Signatur des Handlers gelesen.</param>
    private sealed record Probe(string Method, string Route, string Query);

    /// <summary>Die erste erlaubte Methode; ohne Angabe zaehlt der Endpunkt als GET.</summary>
    private static string Methods(RouteEndpoint endpoint)
    {
        var allowed = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return allowed is { Count: > 0 } ? allowed[0] : "GET";
    }

    /// <summary>
    /// Liest die Pflicht-Query-Parameter aus der Signatur des Handlers und baut eine Abfrage, die
    /// die Modellbindung besteht.
    /// <para>
    /// <b>Warum das noetig ist:</b> In Minimal APIs bindet ASP.NET die Parameter <em>vor</em> dem
    /// Endpunktfilter. Fehlt ein nicht-nullbarer Query-Parameter, endet die Anfrage in einem 400,
    /// bevor die Zugriffspruefung ueberhaupt laeuft — der Test saehe dann ein 400 statt eines 403
    /// und wuerde einen Endpunkt anklagen, der in Ordnung ist. Die Werte kommen aus der Signatur,
    /// nicht aus einer Liste: Ein neuer Endpunkt mit Pflichtparameter wird von selbst richtig
    /// angesprochen.
    /// </para>
    /// </summary>
    private static string RequiredQuery(RouteEndpoint endpoint)
    {
        var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
        if (handler is null)
        {
            return string.Empty;
        }

        var inRoute = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<string>();

        foreach (var parameter in handler.GetParameters())
        {
            if (parameter.Name is null || inRoute.Contains(parameter.Name))
            {
                continue;
            }

            var value = parameter.ParameterType switch
            {
                var t when t == typeof(bool) => "true",
                var t when t == typeof(int) || t == typeof(long) => "1",
                var t when t == typeof(Guid) => Guid.Empty.ToString(),
                _ => null,
            };

            if (value is not null)
            {
                pairs.Add($"{parameter.Name}={value}");
            }
        }

        return pairs.Count == 0 ? string.Empty : "?" + string.Join('&', pairs);
    }

    /// <summary>Vereinheitlicht <c>{id:guid}</c> zu <c>{id}</c> und entfernt den Schrägstrich am Ende.</summary>
    private static string Normalize(string? raw)
    {
        var route = "/" + (raw ?? string.Empty).Trim('/');
        route = System.Text.RegularExpressions.Regex.Replace(
            route, @"\{([^:}]+)(:[^}]*)?\}", "{$1}");
        return route;
    }

    /// <summary>
    /// Ohne Zugangsdaten gibt es <b>keinen</b> Endpunkt unter <c>/api/</c>, der antwortet. Das
    /// prueft die Middleware — und zugleich, dass ein neuer Endpunkt nicht ausserhalb ihres
    /// Pfadpraefixes landet.
    /// </summary>
    [Fact]
    public async Task No_api_endpoint_answers_without_credentials()
    {
        using var client = _gateway.CreateApiClient(apiKey: null);
        var leaks = new List<string>();

        foreach (var probe in ManagementRoutes())
        {
            var response = await Send(client, probe);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                leaks.Add($"{probe.Method} {probe.Route} → {(int)response.StatusCode}");
            }

            response.Dispose();
        }

        leaks.Should().BeEmpty(
            "ein Endpunkt ohne 401 haengt entweder nicht unter /api oder umgeht "
            + "ApiKeyAuthMiddleware. Gefunden:\n" + string.Join('\n', leaks));
    }

    /// <summary>
    /// Die eigentliche Matrix: Jeder Endpunkt, der nicht ausdruecklich offen ist, muss eine
    /// authentifizierte Identitaet ohne Global-Grant mit 403 abweisen.
    /// <para>
    /// <b>Wie er bei einer neuen Stelle rot wird:</b> Eine neu angelegte Gruppe ohne
    /// <c>RequireAdminAsync</c> antwortet dieser Identitaet mit 200, 400 oder 404 — jedenfalls
    /// nicht mit 403. Der Test nennt Methode und Route.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_management_endpoint_demands_the_global_grant()
    {
        var (_, plainKey) = await _gateway.SeedPlainAsync();
        using var client = _gateway.CreateApiClient(plainKey);

        var checkedRoutes = 0;
        var leaks = new List<string>();

        foreach (var probe in ManagementRoutes())
        {
            if (OpenToEveryAuthenticatedIdentity.Contains(probe.Route, StringComparer.Ordinal))
            {
                continue;
            }

            checkedRoutes++;
            var response = await Send(client, probe);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                leaks.Add($"{probe.Method} {probe.Route}{probe.Query} → {(int)response.StatusCode} statt 403");
            }

            response.Dispose();
        }

        // Erst der Nachweis, dass ueberhaupt etwas geprueft wurde. Ohne ihn waere der Test auch
        // dann gruen, wenn die Ausnahmeliste versehentlich alles abdeckte.
        checkedRoutes.Should().BeGreaterThan(30,
            "die Management-API hat heute rund 55 Endpunkte; deutlich weniger heisst, dass die "
            + "Aufzaehlung oder die Ausnahmeliste kaputt ist");

        leaks.Should().BeEmpty(
            "jeder dieser Endpunkte ist ohne Global-Grant erreichbar. Ist das gewollt, gehoert die "
            + "Route in OpenToEveryAuthenticatedIdentity — als Entscheidung, nicht als Versehen. "
            + "Gefunden:\n" + string.Join('\n', leaks));
    }

    /// <summary>
    /// Die Gegenprobe zur Matrix: Mit Global-Grant kommt <b>nicht</b> 403 zurueck. Ohne diesen
    /// Test waere die Matrix auch dann gruen, wenn die Management-API schlicht jeden abwiese —
    /// „alles verboten" besteht jede Zugriffspruefung und ist trotzdem kaputt.
    /// </summary>
    [Fact]
    public async Task The_global_grant_actually_opens_the_management_api()
    {
        var (_, adminKey) = await _gateway.SeedAdminAsync();
        using var client = _gateway.CreateApiClient(adminKey);

        using var servers = await client.GetAsync(
            "/api/v1/servers", TestContext.Current.CancellationToken);
        using var identities = await client.GetAsync(
            "/api/v1/rbac/identities", TestContext.Current.CancellationToken);

        servers.StatusCode.Should().Be(HttpStatusCode.OK);
        identities.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Ruft die Route mit ausgefuellten Platzhaltern auf. Der Inhalt spielt keine Rolle: Die
    /// Zugriffspruefung liegt <b>vor</b> Modellbindung und Fachlogik, ein 403 kommt also auch bei
    /// einer unsinnigen Id. Kaeme es erst danach, waere genau das der Befund.
    /// </summary>
    private static Task<HttpResponseMessage> Send(HttpClient client, Probe probe)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(
            probe.Route, @"\{[^}]+\}", Guid.Empty.ToString()) + probe.Query;
        var request = new HttpRequestMessage(new HttpMethod(probe.Method), path);
        if (probe.Method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(new { });
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
