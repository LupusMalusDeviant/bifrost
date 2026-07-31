using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

/// <summary>
/// NFR-04: „Verbindung testen" zeigt fremde Fehlermeldungen in der UI. Die sind unkontrolliert —
/// ein HTTP-Client, der die angefragte URL mitliefert, oder ein Upstream, der seinen Header
/// zitiert, würde die Zugangsdaten sonst im Klartext anzeigen.
/// </summary>
public class ConnectionTesterScrubTests
{
    [Fact]
    public void Env_values_are_removed_from_error_messages()
    {
        var config = new UpstreamServerConfig(
            "srv", "Server", UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions(
                "cmd", [], new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_supergeheim123" }));

        var scrubbed = UpstreamConnectionTester.ScrubSecrets(
            "Prozess beendet: GITHUB_TOKEN=ghp_supergeheim123 ungültig", config);

        scrubbed.Should().NotContain("ghp_supergeheim123").And.Contain("***")
            .And.Contain("Prozess beendet", "die Meldung bleibt sonst brauchbar");
    }

    [Fact]
    public void Http_headers_and_openapi_credentials_are_removed_too()
    {
        var http = new UpstreamServerConfig(
            "h", "H", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                new Uri("https://a.invalid/mcp"),
                new Dictionary<string, string> { ["Authorization"] = "Bearer tok_abcdef123456" }));
        var api = new UpstreamServerConfig(
            "a", "A", UpstreamTransportKind.OpenApi, true,
            OpenApi: new OpenApiTransportOptions(
                new Uri("https://a.invalid/spec.json"), AuthKind: OpenApiAuthKind.Bearer, Credential: "cred_geheim99"));

        UpstreamConnectionTester.ScrubSecrets("401 mit Bearer tok_abcdef123456", http)
            .Should().NotContain("tok_abcdef123456");
        UpstreamConnectionTester.ScrubSecrets("Auth fehlgeschlagen: cred_geheim99", api)
            .Should().NotContain("cred_geheim99");
    }

    [Fact]
    public void Messages_without_secrets_stay_untouched()
    {
        var config = new UpstreamServerConfig(
            "srv", "Server", UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions("cmd", [], new Dictionary<string, string> { ["TOKEN"] = "geheim1234" }));

        UpstreamConnectionTester.ScrubSecrets("Verbindung abgelehnt (Port 1234)", config)
            .Should().Be("Verbindung abgelehnt (Port 1234)", "ohne Treffer wird nichts verändert");
    }

    [Fact]
    public void Very_short_values_do_not_shred_the_message()
    {
        // Ein Env-Wert wie "1" oder "on" ist kein Geheimnis, würde aber überall matchen und die
        // Meldung unlesbar machen — dann wäre die Fehlersuche kaputt statt sicherer.
        var config = new UpstreamServerConfig(
            "srv", "Server", UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions("cmd", [], new Dictionary<string, string> { ["DEBUG"] = "1" }));

        UpstreamConnectionTester.ScrubSecrets("Port 1234 nicht erreichbar", config)
            .Should().Be("Port 1234 nicht erreichbar");
    }

    [Fact]
    public void Cli_environment_values_are_removed_even_when_embedded()
    {
        const string secret = "cli_tok_supergeheim987";
        var config = new UpstreamServerConfig(
            "cli", "CLI", UpstreamTransportKind.Cli, true,
            Cli: new CliTransportOptions(
                "tool", [new CliToolSpec("run")],
                EnvironmentVariables: new Dictionary<string, string> { ["CLI_TOKEN"] = secret }));

        var scrubbed = UpstreamConnectionTester.ScrubSecrets(
            $"Start fehlgeschlagen: CLI_TOKEN=prefix-{secret}-suffix", config);

        scrubbed.Should().Be("Start fehlgeschlagen: CLI_TOKEN=prefix-***-suffix");
    }

    [Fact]
    public void Short_cli_environment_values_are_not_treated_as_secrets()
    {
        var config = new UpstreamServerConfig(
            "cli", "CLI", UpstreamTransportKind.Cli, true,
            Cli: new CliTransportOptions(
                "tool", [new CliToolSpec("run")],
                EnvironmentVariables: new Dictionary<string, string> { ["MODE"] = "on" }));

        UpstreamConnectionTester.ScrubSecrets("connection refused", config)
            .Should().Be("connection refused");
    }
}
