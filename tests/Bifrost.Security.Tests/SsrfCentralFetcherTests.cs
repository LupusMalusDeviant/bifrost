using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Bifrost.Security.Tests.Infrastructure;
using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// <b>Invariante 3:</b> Jedes dynamische Remoteziel geht durch den zentralen SSRF-Fetcher.
/// <para>
/// Ein Gateway, das eine konfigurierte Adresse abruft, ist ohne diese Pruefung ein Werkzeug, um
/// <b>interne</b> Dienste zu erreichen: Cloud-Metadaten unter <c>169.254.169.254</c>, ein
/// Admin-Port auf <c>127.0.0.1</c>, ein Nachbar im Firmennetz. Der Schutz liegt in
/// <c>RemoteSpecFetcher</c> — und er nuetzt nur, solange niemand daran vorbei greift.
/// </para>
/// </summary>
public class SsrfCentralFetcherTests
{
    private static readonly Type Fetcher =
        typeof(Bifrost.Upstream.StdioUpstreamConnector).Assembly
            .GetType("Bifrost.Upstream.Http.RemoteSpecFetcher", throwOnError: true)!;

    /// <summary>
    /// Adressen, die der Fetcher abweisen muss. Jede Zeile ist ein eigener Weg nach innen; die
    /// Liste deckt beide Adressfamilien und die drei Schreibweisen ab, mit denen eine
    /// Loopback-Adresse an einer naiven Textpruefung vorbeikaeme.
    /// </summary>
    public static TheoryData<string> InternalTargets() =>
    [
        "http://127.0.0.1:8080/spec.json",
        "http://127.42.7.9/spec.json",
        "http://localhost:5000/spec.json",
        "http://[::1]:8080/spec.json",
        "http://[::ffff:127.0.0.1]/spec.json",
        "http://169.254.169.254/latest/meta-data/",
        "http://10.1.2.3/spec.json",
        "http://172.16.0.5/spec.json",
        "http://172.31.255.254/spec.json",
        "http://192.168.178.61/spec.json",
        "http://100.64.0.1/spec.json",
        "http://0.0.0.0/spec.json",
        "http://[fe80::1]/spec.json",
        "http://[fd00::1]/spec.json",
    ];

    [Theory]
    [MemberData(nameof(InternalTargets))]
    public async Task The_fetcher_refuses_every_internal_target(string target)
    {
        var refusal = await CheckAsync(new Uri(target), allowPrivateTargets: false);

        refusal.Should().NotBeNull(
            $"'{target}' zeigt nach innen und darf ohne ausdrueckliche Freigabe nicht abgerufen werden");
        refusal!.Message.Should().Contain(new Uri(target).Host,
            "die Absage muss das Ziel benennen, sonst ist sie fuer den Betreiber ein Raetsel");
    }

    /// <summary>
    /// Die Gegenprobe zur Theorie: Ohne sie waere ein Fetcher, der <em>alles</em> abweist,
    /// vierzehnmal gruen und trotzdem kaputt.
    /// </summary>
    [Fact]
    public async Task A_public_target_passes_and_the_explicit_switch_lets_internals_through()
    {
        (await CheckAsync(new Uri("http://93.184.216.34/spec.json"), allowPrivateTargets: false))
            .Should().BeNull("eine oeffentliche Adresse ist erlaubt");

        (await CheckAsync(new Uri("http://127.0.0.1:8080/spec.json"), allowPrivateTargets: true))
            .Should().BeNull("wer interne Ziele braucht, setzt den Schalter ausdruecklich");
    }

    /// <summary>
    /// Wer eine eigene Verbindung nach aussen aufmacht. Jeder Eintrag ist eine Entscheidung mit
    /// Begruendung — nicht „ist halt so".
    /// </summary>
    private static readonly Dictionary<string, string> MayOpenItsOwnConnection = new(StringComparer.Ordinal)
    {
        ["src/Bifrost.Upstream/Http/RemoteSpecFetcher.cs"] =
            "der zentrale Fetcher selbst",
        ["src/Bifrost.Upstream/OpenApi/OpenApiUpstreamConnector.cs"] =
            "Zieladresse durch EnsureTargetAllowedAsync geprueft (Zeile 46), danach eigener Client",
        ["src/Bifrost.Upstream/OpenRpc/OpenRpcUpstreamConnector.cs"] =
            "Endpunkt durch SpecFetcher.EnsureTargetAllowedAsync geprueft (Zeile 33)",
        ["src/Bifrost.Upstream/OAuth/OAuthFlow.cs"] =
            "Token-Endpunkt durch EnsureTargetAllowedAsync geprueft (Zeile 166)",
        ["src/Bifrost.Server/UpstreamOAuthEndpoints.cs"] =
            "Sonde durch OAuthDiscovery.EnsureTargetAllowedAsync geprueft — DIESER Test hat die "
            + "fehlende Pruefung gefunden: Die Sonde ging ungeprueft gegen die vom Betreiber "
            + "genannte Adresse, drei Zeilen bevor derselbe Schalter an die Discovery ging",
        ["src/Bifrost.Server/Program.cs"] =
            "--healthcheck gegen die eigene, fest verdrahtete Adresse — kein dynamisches Ziel",
        ["src/Bifrost.Cli/Program.cs"] =
            "die CLI ist der Client dieses Dienstes, nicht der Dienst",
        ["src/Bifrost.Cli/GatewayCli.cs"] = "CLI-Client",
        ["src/Bifrost.Cli/OperationsCli.cs"] = "CLI-Client",
    };

    /// <summary>
    /// Der Waechter. Ein neuer Konnektor oder Endpunkt, der sich seinen eigenen
    /// <c>HttpClient</c> baut, taucht hier als unbekannte Datei auf.
    /// <para>
    /// <b>Warum nicht ueber die Aufrufe des Fetchers:</b> Zu pruefen, dass der Fetcher <em>oft
    /// genug</em> gerufen wird, geht nicht — es gibt keinen Nenner. Pruefbar ist die
    /// Gegenrichtung: wer ueberhaupt eine Verbindung aufbaut. Das ist eine endliche, sichtbare
    /// Menge, und jede Erweiterung faellt auf.
    /// </para>
    /// </summary>
    [Fact]
    public void No_new_place_opens_its_own_outbound_connection()
    {
        var pattern = new Regex(
            @"new\s+HttpClient\s*[({]|new\s+HttpClientHandler\b|new\s+SocketsHttpHandler\b|CreateClient\s*\(",
            RegexOptions.CultureInvariant);

        var unknown = RepositorySources.Find(pattern)
            .Where(hit => !MayOpenItsOwnConnection.ContainsKey(hit.File))
            .ToArray();

        unknown.Should().BeEmpty(
            "wer eine eigene Verbindung nach aussen aufmacht, muss das Ziel vorher durch "
            + "RemoteSpecFetcher.EnsureTargetAllowedAsync schicken — oder hier mit Begruendung "
            + "eingetragen werden. Gefunden:\n"
            + string.Join('\n', unknown.Select(hit => hit.ToString())));
    }

    /// <summary>
    /// Die Umkehrung des Waechters: Jede Datei in der Ausnahmeliste muss es noch geben. Sonst
    /// verrottet die Liste zu einer Sammlung von Namen, die niemand mehr nachprueft — und deckt
    /// irgendwann eine Stelle ab, die ganz woanders liegt.
    /// </summary>
    [Fact]
    public void The_exception_list_has_no_dead_entries()
    {
        var existing = RepositorySources.Production.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var dead = MayOpenItsOwnConnection.Keys.Where(path => !existing.Contains(path)).ToArray();

        dead.Should().BeEmpty("eine Ausnahme fuer eine Datei, die es nicht gibt, deckt nichts");
    }

    /// <summary>
    /// Jeder Transport, der eine vom Betreiber genannte Adresse abruft, muss den Schalter
    /// <c>AllowPrivateTargets</c> tragen — er ist die sichtbare Spur davon, dass die Zielpruefung
    /// diesen Transport ueberhaupt kennt.
    /// <para>
    /// <b>Wie er bei einer neuen Stelle rot wird:</b> Ein achter Transport mit einer
    /// <see cref="Uri"/>-Eigenschaft erscheint hier ohne Zutun und muss den Schalter mitbringen
    /// oder begruendet ausgenommen werden. Das ist dieselbe Bauart wie der Waechter im
    /// Redactor — und dieselbe Fehlerklasse: ein Feld, an das beim Nachziehen niemand dachte.
    /// </para>
    /// <para>
    /// <b>Dieser Test ist heute ROT.</b> <c>HttpTransportOptions</c> traegt den Schalter nicht,
    /// und <c>StreamableHttpUpstreamConnector.ConnectAsync</c> prueft sein Ziel nirgends. Der
    /// Befund steht in der Abgabe; behoben wird er nicht hier — <c>src/</c> ist fremde Zone.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_transport_with_a_remote_address_knows_the_private_target_switch()
    {
        var withoutSwitch = typeof(Bifrost.Abstractions.UpstreamServerConfig).Assembly
            .GetTypes()
            .Where(type => type.Name.EndsWith("TransportOptions", StringComparison.Ordinal))
            .Where(type => type.GetProperties().Any(p => p.PropertyType == typeof(Uri)))
            .Where(type => type.GetProperty("AllowPrivateTargets") is null)
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        withoutSwitch.Should().BeEmpty(
            "ein Transport mit einer konfigurierbaren Adresse und ohne AllowPrivateTargets hat "
            + "keine Zielpruefung — das Gateway ruft dann jede interne Adresse ab, die ein "
            + "Administrator eintraegt. Betroffen: " + string.Join(", ", withoutSwitch));
    }

    /// <summary>
    /// Ruft <c>RemoteSpecFetcher.EnsureTargetAllowedAsync</c> ueber Reflexion auf — der Typ ist
    /// <c>internal</c> zu <c>Bifrost.Upstream</c>, und das soll er bleiben. Gibt die Ausnahme
    /// zurueck, mit der die Pruefung abgelehnt hat, oder <c>null</c> bei Durchlass.
    /// </summary>
    private static async Task<Exception?> CheckAsync(Uri target, bool allowPrivateTargets)
    {
        var method = Fetcher.GetMethod(
            "EnsureTargetAllowedAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "RemoteSpecFetcher.EnsureTargetAllowedAsync gibt es nicht mehr — die Pruefung ist "
                + "umgezogen oder weg. Beides ist ein Befund, kein Testfehler.");

        Func<string, Exception> fail = message => new InvalidOperationException(message);
        var task = (Task)method.Invoke(
            null, [target, allowPrivateTargets, fail, TestContext.Current.CancellationToken])!;

        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
