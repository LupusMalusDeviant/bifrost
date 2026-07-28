using AwesomeAssertions;
using McpMcp.Upstream;
using Xunit;

namespace McpMcp.Upstream.Tests;

/// <summary>
/// Was ein stdio-Kindprozess von der Umgebung des Gateways sieht (ADR-0005, Nachtrag 2026-07-28).
/// <para>
/// Bis dahin: alles. Ein stdio-Server las damit <c>MCPMCP_DB_CONNECTION</c> — bei Postgres samt
/// Passwort — und <c>MCPMCP_KEYRING_CERT_PASSWORD</c>, mit dem der Key-Ring entschlüsselt wird. Der
/// CLI-Transport räumt seine Umgebung seit ADR-0014 auf; beim ältesten Transport fehlte derselbe
/// Schritt.
/// </para>
/// </summary>
public sealed class StdioProcessEnvironmentTests
{
    /// <summary>Der Kernfall: Gateway-eigene Variablen erreichen den Kindprozess nicht.</summary>
    [Theory]
    [InlineData("MCPMCP_DB_CONNECTION")]
    [InlineData("MCPMCP_KEYRING_CERT_PASSWORD")]
    [InlineData("MCPMCP_PUBLIC_BASE_URL")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("GITHUB_TOKEN")]
    public void Gateway_secrets_do_not_reach_the_child(string name)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "streng-geheim-4711");
        try
        {
            var environment = StdioProcessEnvironment.Build(null);

            environment.Should().NotContainKey(name);
            environment.Values.Should().NotContain("streng-geheim-4711");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    /// <summary>
    /// Was ein <c>npx</c>-Server zum Starten braucht, bleibt drin. Eine leere Umgebung wäre kein
    /// Sicherheitsgewinn, sondern ein kaputter Transport — und die naheliegende Reaktion darauf
    /// wäre, die Härtung wieder abzuschalten.
    /// </summary>
    [Fact]
    public void What_npx_needs_survives()
    {
        var environment = StdioProcessEnvironment.Build(null);

        if (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 })
        {
            environment.Should().ContainKey("PATH");
        }

        environment.Should().ContainKey("TEMP").And.ContainKey("TMP");
        environment["TEMP"].Should().NotBeNullOrEmpty();
    }

    /// <summary>Was der Upstream konfiguriert, geht mit — dafür ist das Feld da.</summary>
    [Fact]
    public void Configured_values_are_passed_through()
    {
        var environment = StdioProcessEnvironment.Build(
            new Dictionary<string, string> { ["API_TOKEN"] = "vom-betreiber-gesetzt" });

        environment["API_TOKEN"].Should().Be("vom-betreiber-gesetzt");
    }

    /// <summary>
    /// Die Konfiguration gewinnt gegen die geerbte Variable: Wer <c>PATH</c> je Upstream setzt,
    /// meint das so.
    /// </summary>
    [Fact]
    public void Configured_values_win_over_inherited_ones()
    {
        var environment = StdioProcessEnvironment.Build(
            new Dictionary<string, string> { ["PATH"] = "/nur/hier" });

        environment["PATH"].Should().Be("/nur/hier");
    }

    /// <summary>
    /// Die Allowlist ist namentlich, nicht als Präfixregel formuliert — sonst liefe jede neue
    /// Variable stillschweigend durch.
    /// </summary>
    [Theory]
    [InlineData("IRGENDWAS_NEUES")]
    [InlineData("MCPMCP_WAS_AUCH_IMMER")]
    public void Unknown_names_are_withheld(string name)
        => StdioProcessEnvironment.IsWithheldByDefault(name).Should().BeTrue();
}
