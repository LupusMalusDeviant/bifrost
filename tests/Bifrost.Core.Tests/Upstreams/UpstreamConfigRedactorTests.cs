using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

public class UpstreamConfigRedactorTests
{
    /// <summary>
    /// Erfundene Zugangsdaten, je Transport ein eigener Wert. Keiner davon darf die Maskierung
    /// überleben — und weil jeder Wert einmalig ist, benennt ein Fehlschlag den schuldigen
    /// Transport, statt nur „irgendetwas ist durchgerutscht" zu sagen.
    /// </summary>
    private static readonly string[] Corpus =
    [
        "stdio-QGgIVeMkKMhVwHXY", "http-lQeVzBWkjxHZmSvT", "openapi-YrTnDpUwOaKcFbLe",
        "cli-MvXqZjRtNbGyHsPd", "wasi-CkWfUiToAeLdRnJm", "openrpc-BzHpNsQxVkEaTgYw",
    ];

    private static UpstreamServerConfig FullyLoadedConfig() => new(
        "alle", "Alle Transporte", UpstreamTransportKind.Stdio, true,
        Stdio: new StdioTransportOptions(
            "server", ["--serve"],
            new Dictionary<string, string> { ["TOKEN"] = Corpus[0] }),
        Http: new HttpTransportOptions(
            new Uri("https://example.invalid/mcp"),
            Headers: new Dictionary<string, string> { ["Authorization"] = Corpus[1] }),
        OpenApi: new OpenApiTransportOptions(
            new Uri("https://example.invalid/openapi.json"),
            Credential: Corpus[2]),
        Cli: new CliTransportOptions(
            "tool",
            [new CliToolSpec("run")],
            EnvironmentVariables: new Dictionary<string, string> { ["PASSWORD"] = Corpus[3] }),
        Wasi: new WasiTransportOptions(
            "bifrost-wasi-host", "component.wasm", "component.sig", ["cHVibGlzaGVy"],
            Secrets: new Dictionary<string, string> { ["API_KEY"] = Corpus[4] }),
        OpenRpc: new OpenRpcTransportOptions(
            new Uri("https://example.invalid/rpc"),
            Credential: Corpus[5]));

    [Fact]
    public void No_transport_secret_survives_redaction()
    {
        var config = FullyLoadedConfig();

        // Erst der Nachweis, dass hier überhaupt etwas zu maskieren ist. Ohne ihn wäre der Test
        // auch dann grün, wenn ein Transport gar nicht serialisiert würde — er prüfte dann nichts.
        var plain = JsonSerializer.Serialize(config);
        foreach (var secret in Corpus)
        {
            plain.Should().Contain(secret,
                "sonst prüft der folgende Vergleich einen Wert, der ohnehin nie ausgegeben wird");
        }

        var json = JsonSerializer.Serialize(UpstreamConfigRedactor.Redact(config));

        foreach (var secret in Corpus)
        {
            json.Should().NotContain(secret,
                $"'{secret}' geht über ApiEndpoints an die Oberfläche");
        }
    }

    [Fact]
    public void Redaction_does_not_touch_the_persisted_configuration()
    {
        var config = FullyLoadedConfig();

        UpstreamConfigRedactor.Redact(config);

        config.Cli!.EnvironmentVariables!["PASSWORD"].Should().Be(Corpus[3]);
        config.OpenRpc!.Credential.Should().Be(Corpus[5]);
    }

    /// <summary>
    /// Der eigentliche Wächter. <see cref="OpenRpcTransportOptions"/> trug sein Credential im
    /// Klartext in die API-Ausgabe, weil beim Nachziehen eines neuen Transports niemand an den
    /// Redactor dachte — und kein Test das merkte, denn er prüfte nur die bekannten Fälle.
    /// Dieser hier wird rot, sobald ein Transport dazukommt, und zwingt zur Entscheidung.
    /// </summary>
    [Fact]
    public void A_new_transport_forces_a_decision_about_its_secrets()
    {
        var known = new[] { "Stdio", "Http", "OpenApi", "Cli", "Wasi", "OpenRpc" };

        var transports = typeof(UpstreamServerConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.Name.EndsWith("TransportOptions", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();

        transports.Should().BeEquivalentTo(known,
            "ein neuer Transport gehört in Corpus, FullyLoadedConfig, UpstreamConfigRedactor "
            + "UND UpstreamConfigMerge — sonst leckt sein Secret oder die Maske wird gespeichert");
    }
}
