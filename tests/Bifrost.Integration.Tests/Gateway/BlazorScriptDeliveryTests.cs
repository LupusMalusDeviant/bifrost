using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Der dritte Fund aus dem echten Betrieb (Badwolf, 2026-07-29) — und der folgenreichste: In der
/// gesamten Admin-Oberfläche tat <b>kein einziger Knopf</b> etwas.
/// <para>
/// Die UI ist bewusst selbstenthaltend (CSS inline in <c>App.razor</c>, Favicon als Data-URI, „keine
/// externen Ressourcen"). Genau <b>eine</b> Datei lässt sich nicht inlinen:
/// <c>_framework/blazor.web.js</c>. Sie fehlte, weil der SDK das Paket mit den Blazor-Assets nur
/// einbindet, wenn das Projekt <b>selbst</b> <c>.razor</c>-Dateien enthält — unsere liegen alle in
/// der Razor-Klassenbibliothek (ADR-0004). Ohne die Datei startet der Circuit nie, und
/// <c>@onclick</c>/<c>@bind</c> sind tot, während die Seiten serverseitig gerendert werden und
/// deshalb <em>benutzbar aussehen</em>.
/// </para>
/// <para>
/// Warum kein Test das gefunden hat: Die Integrationstests sprechen MCP und REST, nie die Seite.
/// Dieser hier hält deshalb nicht das Verhalten fest, sondern die <b>Lieferbedingung</b>.
/// </para>
/// </summary>
public class BlazorScriptDeliveryTests
{
    /// <summary>
    /// Das Manifest der statischen Assets muss den Blazor-Einstiegspunkt kennen. Es entsteht beim
    /// Bau aus <c>RequiresAspNetWebAssets</c>; fällt die Eigenschaft weg, ist dieses Manifest leer
    /// (53 Byte) — und genau daran hing der Ausfall.
    /// </summary>
    [Fact]
    public void The_static_asset_manifest_contains_the_blazor_entry_point()
    {
        var manifest = FindManifest();
        manifest.Should().NotBeNull(
            "ohne Manifest liefert MapStaticAssets nichts aus — dann ist die Oberfläche tot");

        using var document = JsonDocument.Parse(File.ReadAllText(manifest!));
        var routes = document.RootElement.GetProperty("Endpoints").EnumerateArray()
            .Select(e => e.GetProperty("Route").GetString())
            .ToList();

        routes.Should().Contain("_framework/blazor.web.js",
            "die Seite fordert genau diesen Pfad an (App.razor); fehlt er, startet der Blazor-Circuit "
            + "nie und in der GESAMTEN Oberfläche tut kein Knopf etwas");
    }

    /// <summary>
    /// Der Endpunkt allein genügt nicht — die Datei muss auch eine Länge haben. Beim ersten Versuch
    /// stand der Endpunkt im Manifest, und der Server antwortete trotzdem mit
    /// <c>Content-Length: 0</c>.
    /// </summary>
    [Fact]
    public void The_blazor_entry_point_is_not_empty()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindManifest()!));
        var endpoint = document.RootElement.GetProperty("Endpoints").EnumerateArray()
            .First(e => e.GetProperty("Route").GetString() == "_framework/blazor.web.js");

        var length = endpoint.GetProperty("ResponseHeaders").EnumerateArray()
            .Where(h => h.GetProperty("Name").GetString() == "Content-Length")
            .Select(h => long.Parse(h.GetProperty("Value").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .Max();

        length.Should().BeGreaterThan(50_000,
            "blazor.web.js ist rund 200 KB; ein winziger Wert hieße, dass etwas anderes ausgeliefert wird");
    }

    /// <summary>
    /// Das Manifest liegt neben der Server-Assembly. Gesucht wird von dort aus, damit der Test
    /// unabhängig vom Arbeitsverzeichnis des Testlaufs funktioniert.
    /// </summary>
    private static string? FindManifest()
    {
        var directory = Path.GetDirectoryName(typeof(Bifrost.Server.ForwardedProxyOptions).Assembly.Location);
        return directory is null
            ? null
            : Directory.EnumerateFiles(directory, "*.staticwebassets.endpoints.json")
                .FirstOrDefault(f => Path.GetFileName(f).StartsWith("Bifrost.Server.", StringComparison.Ordinal));
    }
}
