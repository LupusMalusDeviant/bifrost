using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Bifrost.Server.Importing;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Bifrost.Integration.Tests.Importing;

/// <summary>
/// <b>Die DoD dieses Pakets:</b> „Kein Setup-/Importendpoint ist nach Abschluss anonym erreichbar."
///
/// <para>
/// <b>Wie das belegt wird.</b> Die Endpunktliste steht nicht in diesem Test. Sie wird vom laufenden
/// Host erfragt (<see cref="EndpointDataSource"/>) und nach den beiden Präfixen gefiltert, um die es
/// hier geht. Vorbild ist <c>ManagementEndpointMatrixTests</c> in <c>Bifrost.Security.Tests</c>: Eine
/// Liste im Test wäre genau die Liste, gegen die dieses Paket antritt — sie kennt nur, was jemand
/// eingetragen hat, und ein neuer Endpunkt trägt sich nicht selbst ein.
/// </para>
///
/// <para>
/// <b>„Nach Abschluss" ist wörtlich zu nehmen.</b> Der Erstzugang wird vor der Prüfung eingelöst.
/// Vorher steht der Setup-Weg offen (mit Token und lokal) — das ist sein Zweck. Ein Test, der ihn im
/// ausstehenden Zustand prüfte, prüfte nicht die Zusage.
/// </para>
/// </summary>
public class ImportEndpointAccessTests : IClassFixture<ImportGatewayFixture>, IAsyncLifetime
{
    private readonly ImportGatewayFixture _gateway;

    public ImportEndpointAccessTests(ImportGatewayFixture gateway) => _gateway = gateway;

    public ValueTask InitializeAsync() => new(_gateway.CompleteBootstrapAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Die Routen, um die es geht — vom Host erfragt, nicht aufgezählt.</summary>
    private Probe[] ImportRoutes()
    {
        var routes = _gateway.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new Probe(Method(endpoint), Normalize(endpoint.RoutePattern.RawText)))
            // Die gesamte Import-API und ALLES unterhalb von /setup/. Der Schraegstrich ist
            // Absicht: Die Seite '/setup' selbst ist die Erstzugangs-Oberflaeche aus WP3.4 und
            // gehoert anonym erreichbar — sie ist der Weg, auf dem der Erstzugang ueberhaupt
            // stattfindet. Was UNTER ihr haengt, ist Schnittstelle und faellt unter die Zusage.
            .Where(entry =>
                entry.Route.StartsWith(ImportEndpoints.ApiBase, StringComparison.Ordinal)
                || entry.Route.StartsWith("/setup/", StringComparison.Ordinal))
            .DistinctBy(entry => (entry.Method, entry.Route))
            .OrderBy(entry => entry.Route, StringComparer.Ordinal)
            .ToArray();

        // Ohne diesen Nachweis waere der Test auch dann gruen, wenn der Filter nichts mehr findet —
        // etwa nach einer Umbenennung des Praefixes. Erwartet werden mindestens Vorschau, Probe,
        // Uebernahme und der Setup-Weg.
        routes.Should().HaveCountGreaterThanOrEqualTo(4,
            "gefunden wurden nur:\n" + string.Join('\n', routes.Select(r => $"{r.Method} {r.Route}")));
        routes.Select(r => r.Route).Should().Contain(ImportEndpoints.SetupPreviewPath,
            "der Setup-Weg gehoert zu den Endpunkten, ueber die die DoD spricht");
        return routes;
    }

    private sealed record Probe(string Method, string Route);

    /// <summary>
    /// <b>Der Nachweis.</b> Ohne Zugangsdaten antwortet keiner dieser Endpunkte mit einem Erfolg.
    /// <para>
    /// Zugelassen sind genau zwei Antworten, und beide sind eine Absage: <c>401</c> von der
    /// API-Middleware und <c>404</c> vom Setup-Weg, den es nach Abschluss nicht mehr gibt. Ein
    /// <c>403</c> wäre hier kein Trost: Es hieße, der Endpunkt sieht die Anfrage an.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_import_or_setup_endpoint_answers_anonymously_after_the_first_access_is_done()
    {
        using var client = _gateway.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        var leaks = new List<string>();
        foreach (var probe in ImportRoutes())
        {
            using var response = await Send(client, probe, local: false);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.NotFound))
            {
                leaks.Add($"{probe.Method} {probe.Route} -> {(int)response.StatusCode}");
            }
        }

        leaks.Should().BeEmpty(
            "ein Endpunkt, der anonym etwas anderes als eine Absage liefert, haengt entweder nicht "
            + "unter /api oder umgeht seine Zustandspruefung. Gefunden:\n" + string.Join('\n', leaks));
    }

    /// <summary>
    /// Dieselbe Runde, aber <b>vom Rechner selbst</b>. Der Setup-Weg darf nach dem Einlösen auch
    /// lokal nichts mehr tun — sonst wäre „nach Abschluss nicht mehr erreichbar" eine Aussage über
    /// die Netzwerktopologie und nicht über den Zustand.
    /// </summary>
    [Fact]
    public async Task Not_even_from_the_machine_itself_does_the_setup_path_still_answer()
    {
        using var client = _gateway.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        var leaks = new List<string>();
        foreach (var probe in ImportRoutes().Where(p => p.Route.StartsWith("/setup", StringComparison.Ordinal)))
        {
            using var response = await Send(client, probe, local: true);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.NotFound))
            {
                leaks.Add($"{probe.Method} {probe.Route} -> {(int)response.StatusCode}");
            }
        }

        leaks.Should().BeEmpty(string.Join('\n', leaks));
    }

    /// <summary>
    /// Die Gegenprobe zur Zugriffsprüfung: Mit Global-Grant antwortet die Vorschau. Ohne diesen Test
    /// wäre alles oben auch dann grün, wenn die Importendpunkte schlicht jeden abwiesen — „alles
    /// verboten" besteht jede Zugriffsprüfung und ist trotzdem kaputt.
    /// </summary>
    [Fact]
    public async Task With_the_global_grant_the_preview_answers()
    {
        var (_, apiKey) = await _gateway.SeedAdminAsync("wp43-gegenprobe");
        using var client = _gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        using var response = await client.PostAsync(
            ImportEndpoints.ApiBase + "/preview",
            new StringContent(
                """{"mcpServers":{"echo":{"command":"/usr/bin/echo","args":["hallo"]}}}""",
                Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Authentifiziert, aber ohne Global-Grant: 403 auf jedem Importendpunkt unter <c>/api</c>. Das
    /// prüft zusätzlich zur Matrix in <c>Bifrost.Security.Tests</c> — dort läuft dieselbe Aussage
    /// über alle Managementendpunkte, hier über genau diese, und zwar auch dann noch, wenn jemand
    /// die Route dort in die Ausnahmeliste schreibt.
    /// </summary>
    [Fact]
    public async Task An_identity_without_the_global_grant_is_refused_on_every_import_endpoint()
    {
        var (_, apiKey) = await _gateway.SeedIdentityAsync(
            "wp43-ohne-grant",
            [new Bifrost.Abstractions.Grant(
                new Bifrost.Abstractions.PermissionScope(Bifrost.Abstractions.ServerId.New(), null),
                [Bifrost.Abstractions.ToolAction.UseTool])]);
        using var client = _gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var leaks = new List<string>();
        foreach (var probe in ImportRoutes()
            .Where(p => p.Route.StartsWith(ImportEndpoints.ApiBase, StringComparison.Ordinal)))
        {
            using var response = await Send(client, probe, local: false);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                leaks.Add($"{probe.Method} {probe.Route} -> {(int)response.StatusCode} statt 403");
            }
        }

        leaks.Should().BeEmpty(string.Join('\n', leaks));
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, Probe probe, bool local)
    {
        var path = Regex.Replace(probe.Route, @"\{[^}]+\}", Guid.Empty.ToString());
        var request = new HttpRequestMessage(new HttpMethod(probe.Method), path)
        {
            Content = JsonContent.Create(new { }),
        };
        if (local)
        {
            request.Headers.Add(ImportGatewayFixture.RemoteAddressHeader, IPAddress.Loopback.ToString());
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Method(RouteEndpoint endpoint)
    {
        var allowed = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return allowed is { Count: > 0 } ? allowed[0] : "GET";
    }

    private static string Normalize(string? raw)
        => Regex.Replace("/" + (raw ?? string.Empty).Trim('/'), @"\{([^:}]+)(:[^}]*)?\}", "{$1}");
}
