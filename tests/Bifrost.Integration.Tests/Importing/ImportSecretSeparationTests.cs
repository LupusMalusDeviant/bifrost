using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Security.Tests.Infrastructure;
using Bifrost.Server.Importing;

using Xunit;

namespace Bifrost.Integration.Tests.Importing;

/// <summary>
/// <b>Der Kern von WP4.3:</b> Die Zugangsdaten der Quelle sind vom normalisierten Vorschaumodell
/// getrennt.
///
/// <para>
/// WP4.1 hat diese Frage ausdrücklich offengelassen und weitergegeben:
/// <see cref="ImportCandidate.Config"/> trägt die <em>Klartextwerte</em>, weil ein Plan sonst beim
/// Anwenden nutzlos wäre. Über die Schnittstelle darf davon nichts gehen. Hier steht der Nachweis.
/// </para>
///
/// <para>
/// <b>Warum der Negativkorpus und keine Mustersuche.</b> Die Korpuswerte tragen absichtlich <em>kein</em>
/// erkennbares Geheimnismuster (siehe <see cref="SecretCorpus"/>): Ein Wert wie <c>sk-live-…</c>
/// würde von der Guardrail gefangen und ließe die Prüfung auch dort grün aussehen, wo gar keine
/// Trennung stattfindet. Was hier geprüft wird, ist die <b>Positivliste</b> — die Regel, dass ein
/// Konfigurationswert die Ausgabe erst gar nicht erreicht.
/// </para>
///
/// <para>
/// Gesucht wird zusätzlich nach <b>Bruchstücken von acht Zeichen</b>. Ein Formatierer, der kürzt,
/// erzeugt genau solche Reste — und acht Zeichen reichen, um einen Wert in einem Logarchiv
/// wiederzufinden.
/// </para>
/// </summary>
public class ImportSecretSeparationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Eine Konfiguration, in der <b>jedes wertetragende Feld</b> einen eigenen Korpuswert trägt —
    /// über alle sechs Transporte. Die Strukturangaben (Slug, Kommando, Zieladresse, Image) tragen
    /// bewusst harmlose Werte: Sie sind die Angaben, wegen derer ein Mensch die Vorschau überhaupt
    /// liest, und sie sind <em>keine</em> Werte im Sinne dieser Regel.
    /// </summary>
    public static TheoryData<string, UpstreamServerConfig> LoadedConfigurations()
    {
        var data = new TheoryData<string, UpstreamServerConfig>
        {
            {
                "Stdio: Argument und Umgebungswert",
                new UpstreamServerConfig(
                    "stdio-fall", "Stdio-Fall", UpstreamTransportKind.Stdio, false,
                    Stdio: new StdioTransportOptions(
                        "/usr/local/bin/server",
                        ["--api-key", SecretCorpus.ToolArgument],
                        new Dictionary<string, string>
                        {
                            ["GITHUB_TOKEN"] = SecretCorpus.StdioEnv,
                            ["HARMLOS"] = "produktion",
                        },
                        "/srv/arbeit",
                        new IsolationOptions(IsolationMode.Container, "ghcr.io/beispiel/server:1")))
            },
            {
                "HTTP: Headerwert, Query, Benutzerteil und OAuth-Secret",
                new UpstreamServerConfig(
                    "http-fall", "HTTP-Fall", UpstreamTransportKind.StreamableHttp, false,
                    Http: new HttpTransportOptions(
                        new Uri($"https://nutzer:{SecretCorpus.ApiKeyPlaintext}@api.example/mcp"
                            + $"?token={SecretCorpus.OAuthToken}#frag"),
                        new Dictionary<string, string>
                        {
                            ["Authorization"] = "Bearer " + SecretCorpus.HttpHeader,
                        },
                        OAuth: new UpstreamOAuthOptions("kunde-1", SecretCorpus.WebhookSecret)))
            },
            {
                "OpenAPI: Credential",
                new UpstreamServerConfig(
                    "openapi-fall", "OpenAPI-Fall", UpstreamTransportKind.OpenApi, false,
                    OpenApi: new OpenApiTransportOptions(
                        new Uri("https://api.example/openapi.json"),
                        new Uri("https://api.example/v1"),
                        OpenApiAuthKind.Bearer,
                        SecretCorpus.OpenApiCredential,
                        "X-Api-Key"))
            },
            {
                "OpenRPC: Credential",
                new UpstreamServerConfig(
                    "openrpc-fall", "OpenRPC-Fall", UpstreamTransportKind.OpenRpc, false,
                    OpenRpc: new OpenRpcTransportOptions(
                        new Uri("https://rpc.example/"),
                        new Uri("https://rpc.example/openrpc.json"),
                        OpenApiAuthKind.Bearer,
                        SecretCorpus.OpenRpcCredential))
            },
            {
                "CLI: Umgebungswert und festes Argument",
                new UpstreamServerConfig(
                    "cli-fall", "CLI-Fall", UpstreamTransportKind.Cli, false,
                    Cli: new CliTransportOptions(
                        "/usr/bin/werkzeug",
                        [new CliToolSpec("lauf", "Laeuft", [SecretCorpus.ConnectionString])],
                        "/srv/arbeit",
                        new Dictionary<string, string> { ["DB_PASSWORD"] = SecretCorpus.CliEnv }))
            },
            {
                "WASI: Secretwert",
                new UpstreamServerConfig(
                    "wasi-fall", "WASI-Fall", UpstreamTransportKind.Wasi, false,
                    Wasi: new WasiTransportOptions(
                        "/opt/host/bifrost-wasi",
                        "/opt/pakete/component.wasm",
                        "/opt/pakete/component.sig",
                        ["dGVzdA=="],
                        Secrets: new Dictionary<string, string>
                        {
                            ["API_TOKEN"] = SecretCorpus.WasiSecret,
                        }))
            },
        };

        return data;
    }

    /// <summary>
    /// Der Nachweis: Das Vorschaumodell trägt keinen einzigen Wert aus der Quelle — auch kein
    /// Bruchstück davon.
    /// </summary>
    [Theory]
    [MemberData(nameof(LoadedConfigurations))]
    public void The_preview_model_carries_no_value_from_the_source(
        string beschreibung, UpstreamServerConfig config)
    {
        var plan = new ImportPlan(
            new ImportSource("mcp", null, 0.6, "/heim/nutzer/.config/mcp.json"),
            [new ImportCandidate(
                "quellname",
                config,
                [],
                // Der Secretbefund traegt Ort und Begruendung, nie den Wert — hier steht der ORT
                // eines Feldes, dessen Wert im Korpus ist. Er muss durchgehen.
                [new ImportSecret("env/GITHUB_TOKEN", "Name nennt ein Zugangsdatum", true)])],
            []);

        var view = ImportPreviewProjection.From(plan, "handle-123", DateTimeOffset.UnixEpoch);
        var serialized = JsonSerializer.Serialize(view, Json);

        SecretCorpus.FirstLeakIn(serialized).Should().BeNull(
            $"das Vorschaumodell ({beschreibung}) ist eine Positivliste — ein Wert kann es nur "
            + "erreichen, wenn jemand ihn ausdruecklich hineingeschrieben hat. Ausgabe:\n"
            + serialized);
    }

    /// <summary>
    /// Die Gegenprobe. Ohne sie wäre der Test oben auch dann grün, wenn das Vorschaumodell schlicht
    /// leer wäre — „nichts ausgeben" besteht jede Leckprüfung und ist trotzdem unbrauchbar.
    /// </summary>
    [Fact]
    public void The_preview_model_still_says_what_would_run()
    {
        var config = new UpstreamServerConfig(
            "github", "GitHub", UpstreamTransportKind.Stdio, false,
            Stdio: new StdioTransportOptions(
                "npx",
                ["-y", "@modelcontextprotocol/server-github"],
                new Dictionary<string, string> { ["GITHUB_TOKEN"] = SecretCorpus.StdioEnv }));

        var view = ImportPreviewProjection.From(
            new ImportPlan(
                new ImportSource("mcp", null, 0.6, null),
                [new ImportCandidate("github", config, [], [])],
                []));

        var transport = view.Candidates.Single().Transport;
        transport.Program.Should().Be("npx", "welches Programm startet, ist die Angabe, wegen der "
            + "die Vorschau ueberhaupt gelesen wird");
        transport.ArgumentCount.Should().Be(2, "die Anzahl geht hinaus, die Werte nicht");
        transport.EnvironmentNames.Should().ContainSingle(
                "der NAME einer Umgebungsvariablen ist die Auskunft, der Wert ist es nicht")
            .Which.Should().Be("GITHUB_TOKEN");
        view.Candidates.Single().Slug.Should().Be("github");
    }

    /// <summary>
    /// Die Zieladresse geht ohne Query, Fragment und Benutzerteil hinaus — und dass etwas
    /// abgeschnitten wurde, steht dabei. Eine URL, aus der still ein Stück verschwindet, ist eine
    /// andere URL.
    /// </summary>
    [Fact]
    public void An_endpoint_travels_without_its_query_and_says_so()
    {
        var config = new UpstreamServerConfig(
            "remote", "Remote", UpstreamTransportKind.StreamableHttp, false,
            Http: new HttpTransportOptions(
                new Uri($"https://api.example/mcp?token={SecretCorpus.OAuthToken}")));

        var transport = ImportPreviewProjection.From(
                new ImportPlan(
                    new ImportSource("mcp", null, 0.6, null),
                    [new ImportCandidate("remote", config, [], [])],
                    []))
            .Candidates.Single().Transport;

        transport.Endpoint.Should().Be("https://api.example/mcp");
        transport.EndpointCarriedQuery.Should().BeTrue(
            "der Betreiber soll wissen, dass da etwas war — nur nicht was");
    }

    /// <summary>
    /// Die Architektursicherung: Im Typgraphen des Vorschaumodells kommt
    /// <see cref="UpstreamServerConfig"/> nicht vor.
    /// <para>
    /// <b>Warum das zusätzlich zum Korpus nötig ist.</b> Der Korpus prüft eine Ausgabe. Dieser Test
    /// prüft die <em>Bauart</em>: Wer morgen aus Bequemlichkeit ein Feld <c>Config</c> an
    /// <see cref="ImportCandidateView"/> hängt, damit die Oberfläche „nur noch schnell" das
    /// Arbeitsverzeichnis anzeigen kann, wird hier rot — und zwar auch dann, wenn im Test gerade
    /// kein Korpuswert an dieser Stelle steht.
    /// </para>
    /// </summary>
    [Fact]
    public void The_preview_type_graph_never_touches_the_configuration()
    {
        var visited = new HashSet<Type>();
        var pfad = FindConfig(typeof(ImportPreviewView), visited, []);

        pfad.Should().BeNull(
            "das Vorschaumodell ist die Trennlinie zwischen Plan und Ausgabe. Eine "
            + "UpstreamServerConfig darin traegt die Klartextwerte mit sich, egal wie sie heisst. "
            + "Gefunden ueber: " + pfad);

        visited.Should().Contain(typeof(ImportTransportView),
            "ohne die Transportsicht hat der Test den Graphen nicht abgelaufen und wuerde alles "
            + "durchlassen");
    }

    private static string? FindConfig(Type type, HashSet<Type> visited, IReadOnlyList<string> path)
    {
        if (type == typeof(UpstreamServerConfig))
        {
            return string.Join(" -> ", path);
        }

        if (type.IsPrimitive || type == typeof(string) || type.IsEnum || !visited.Add(type))
        {
            return null;
        }

        foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            var found = FindConfig(argument, visited, [.. path, $"{type.Name}<{argument.Name}>"]);
            if (found is not null)
            {
                return found;
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var found = FindConfig(
                property.PropertyType, visited, [.. path, $"{type.Name}.{property.Name}"]);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Der Wert-Entferner räumt eine Fremdmeldung um die Werte <em>dieser</em> Konfiguration auf —
    /// er rät nicht. Das ist die Sicherung für die eine Ausgabe, die nicht hier entsteht: die
    /// Fehlermeldung eines Verbindungstests.
    /// </summary>
    [Fact]
    public void A_foreign_error_message_loses_the_values_of_this_configuration()
    {
        var config = new UpstreamServerConfig(
            "github", "GitHub", UpstreamTransportKind.Stdio, false,
            Stdio: new StdioTransportOptions(
                "npx",
                ["--token", SecretCorpus.ToolArgument],
                new Dictionary<string, string> { ["GITHUB_TOKEN"] = SecretCorpus.StdioEnv }));

        var fremd = $"npx --token {SecretCorpus.ToolArgument} failed: "
            + $"env GITHUB_TOKEN={SecretCorpus.StdioEnv} rejected";

        var scrubbed = ImportValueScrubber.Scrub(fremd, config);

        SecretCorpus.FirstLeakIn(scrubbed).Should().BeNull(
            "eine Meldung aus einem fremden Prozess traegt gern die Kommandozeile mit sich");
        scrubbed.Should().Contain("npx", "die Meldung soll lesbar bleiben");
        scrubbed.Should().Contain("GITHUB_TOKEN", "der Name der Variablen ist die Auskunft");
    }
}
