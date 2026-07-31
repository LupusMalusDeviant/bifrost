using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Cli.Tests;

/// <summary>
/// Der Distributionsvertrag (docs/plans/m1-distribution-contract.md §2) verlangt, dass
/// <c>bifrost --version</c> SemVer <em>und</em> Commit-SHA nennt. Diese Tests halten die Zusage
/// fest: Sie prüfen nicht die Formatierung um ihrer selbst willen, sondern dass die
/// Build-Verdrahtung (VersionPrefix + SourceLink) den Commit überhaupt bis ins Programm trägt.
/// </summary>
public class CliVersionTests
{
    [Fact]
    public void Version_carries_semver_and_commit_from_the_build()
    {
        CliVersion.SemVer.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
        CliVersion.Commit.Should().NotBe(
            CliVersion.UnknownCommit,
            "das SDK füllt SourceRevisionId aus dem Arbeitsbaum; fehlt der Commit, "
            + "verliert das Artefakt seine Herkunft");
        CliVersion.Commit.Should().MatchRegex("^[0-9a-f]{7,40}$");
    }

    [Fact]
    public void Version_command_prints_both_values_to_stdout_and_succeeds()
    {
        var output = new StringWriter();

        var exit = GatewayCli.TryRunInfoCommand(["--version"], jsonOutput: false, output);

        exit.Should().Be(GatewayCli.Success);
        var text = output.ToString();
        text.Should().StartWith($"bifrost {CliVersion.SemVer}");
        text.Should().Contain(CliVersion.Commit);
    }

    [Fact]
    public void Version_command_has_a_machine_readable_form()
    {
        var output = new StringWriter();

        var exit = GatewayCli.TryRunInfoCommand(["--version"], jsonOutput: true, output);

        exit.Should().Be(GatewayCli.Success);
        using var document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("version").GetString().Should().Be(CliVersion.SemVer);
        document.RootElement.GetProperty("commit").GetString().Should().Be(CliVersion.Commit);
        document.RootElement.GetProperty("rid").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_goes_to_stdout_with_exit_code_zero(string argument)
    {
        var output = new StringWriter();

        var exit = GatewayCli.TryRunInfoCommand([argument], jsonOutput: false, output);

        exit.Should().Be(GatewayCli.Success);
        output.ToString().Should().Contain("tools invoke").And.Contain("--version");
    }

    [Fact]
    public async Task Wrong_usage_stays_an_error_on_stderr()
    {
        GatewayCli.TryRunInfoCommand(["quatsch"], jsonOutput: false, TextWriter.Null)
            .Should().BeNull("nur --version und --help laufen ohne Konfiguration");

        using var client = new HttpClient(new UnreachableHandler())
        {
            BaseAddress = new Uri("https://gateway.example/"),
        };
        var error = new StringWriter();
        var cli = new GatewayCli(
            client, TextReader.Null, TextWriter.Null, error, jsonOutput: false);

        var exit = await cli.RunAsync(["quatsch"], TestContext.Current.CancellationToken);

        exit.Should().Be(GatewayCli.UsageError);
        error.ToString().Should().Contain("Nutzung:");
    }

    [Fact]
    public void Parsing_survives_a_build_without_git()
    {
        // Regex statt Zeichenkettenvergleich, damit der Test auch dann etwas aussagt, wenn die
        // Version im Repository steigt: Die Ausgabe muss immer beide Angaben tragen.
        var human = CliVersion.Describe(jsonOutput: false);

        Regex.IsMatch(human, @"^bifrost \S+\r?\nCommit:\s+\S+\r?\nLaufzeit:\s+\S")
            .Should().BeTrue($"unerwartetes Format:{Environment.NewLine}{human}");
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Fehlbedienung darf das Gateway nicht anfassen.");
    }
}
