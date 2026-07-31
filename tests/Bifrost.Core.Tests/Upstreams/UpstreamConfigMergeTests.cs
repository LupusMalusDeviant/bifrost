using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

/// <summary>
/// FR-34: Beim Bearbeiten zeigt die UI bestehende Secrets nicht an. Ohne Übernahme würde jedes
/// Speichern sie stillschweigend löschen — ein Datenverlust, den niemand bemerkt, bis der
/// Upstream sich nicht mehr authentifizieren kann.
/// </summary>
public class UpstreamConfigMergeTests
{
    private static UpstreamServerConfig Stdio(IReadOnlyDictionary<string, string>? env) => new(
        "srv", "Server", UpstreamTransportKind.Stdio, Enabled: true,
        Stdio: new StdioTransportOptions("cmd", ["--x"], env));

    private static UpstreamServerConfig Cli(IReadOnlyDictionary<string, string>? env) => new(
        "cli", "CLI", UpstreamTransportKind.Cli, Enabled: true,
        Cli: new CliTransportOptions("cmd", [new CliToolSpec("run")], EnvironmentVariables: env));

    [Fact]
    public void Empty_env_keeps_the_previous_secrets()
    {
        var previous = Stdio(new Dictionary<string, string> { ["TOKEN"] = "geheim" });
        var edited = Stdio(null);

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.Stdio!.EnvironmentVariables.Should().ContainKey("TOKEN")
            .WhoseValue.Should().Be("geheim", "leere Secret-Felder bedeuten 'unverändert'");
    }

    [Fact]
    public void Provided_env_replaces_the_previous_secrets()
    {
        var previous = Stdio(new Dictionary<string, string> { ["TOKEN"] = "alt" });
        var edited = Stdio(new Dictionary<string, string> { ["TOKEN"] = "neu" });

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.Stdio!.EnvironmentVariables!["TOKEN"].Should().Be("neu",
            "wer etwas einträgt, will es auch ersetzen");
    }

    [Fact]
    public void Non_secret_changes_survive_the_merge()
    {
        var previous = Stdio(new Dictionary<string, string> { ["TOKEN"] = "geheim" });
        var edited = previous with { DisplayName = "Neuer Name", Stdio = previous.Stdio! with { EnvironmentVariables = null } };

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.DisplayName.Should().Be("Neuer Name");
        merged.Stdio!.EnvironmentVariables.Should().ContainKey("TOKEN");
    }

    [Fact]
    public void Http_headers_and_openapi_credentials_follow_the_same_rule()
    {
        var previousHttp = new UpstreamServerConfig(
            "h", "H", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                new Uri("https://a.invalid/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer x" }));
        var editedHttp = previousHttp with { Http = previousHttp.Http! with { Headers = null } };

        var previousApi = new UpstreamServerConfig(
            "a", "A", UpstreamTransportKind.OpenApi, true,
            OpenApi: new OpenApiTransportOptions(
                new Uri("https://a.invalid/spec.json"), AuthKind: OpenApiAuthKind.Bearer, Credential: "tok"));
        var editedApi = previousApi with { OpenApi = previousApi.OpenApi! with { Credential = null } };

        UpstreamConfigMerge.CarryOverSecrets(editedHttp, previousHttp)
            .Http!.Headers.Should().ContainKey("Authorization");
        UpstreamConfigMerge.CarryOverSecrets(editedApi, previousApi)
            .OpenApi!.Credential.Should().Be("tok");
    }

    [Fact]
    public void Cli_environment_is_carried_over_when_omitted()
    {
        var previous = Cli(new Dictionary<string, string> { ["TOKEN"] = "cli-secret" });

        var merged = UpstreamConfigMerge.CarryOverSecrets(Cli(null), previous);

        merged.Cli!.EnvironmentVariables.Should().ContainKey("TOKEN")
            .WhoseValue.Should().Be("cli-secret");
    }

    [Fact]
    public void Masked_values_keep_the_corresponding_secret_while_explicit_values_change()
    {
        var previous = Cli(new Dictionary<string, string>
        {
            ["TOKEN"] = "old-token",
            ["SECOND"] = "old-second",
        });
        var edited = Cli(new Dictionary<string, string>
        {
            ["TOKEN"] = UpstreamConfigRedactor.Mask,
            ["SECOND"] = "new-second",
        });

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.Cli!.EnvironmentVariables.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["TOKEN"] = "old-token",
            ["SECOND"] = "new-second",
        });
    }

    [Fact]
    public void Empty_cli_environment_explicitly_resets_all_secrets()
    {
        var previous = Cli(new Dictionary<string, string> { ["TOKEN"] = "old-token" });

        var merged = UpstreamConfigMerge.CarryOverSecrets(
            Cli(new Dictionary<string, string>()), previous);

        merged.Cli!.EnvironmentVariables.Should().BeEmpty();
    }

    [Fact]
    public void Empty_openapi_credential_explicitly_resets_the_secret()
    {
        var previous = new UpstreamServerConfig(
            "api", "API", UpstreamTransportKind.OpenApi, true,
            OpenApi: new OpenApiTransportOptions(
                new Uri("https://example.invalid/openapi.json"),
                AuthKind: OpenApiAuthKind.Bearer,
                Credential: "old-token"));
        var edited = previous with
        {
            OpenApi = previous.OpenApi! with { Credential = string.Empty },
        };

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.OpenApi!.Credential.Should().BeNull();
    }
}
