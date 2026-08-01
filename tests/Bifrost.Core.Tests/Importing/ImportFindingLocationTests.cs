using AwesomeAssertions;

using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;
using Bifrost.Core.Tests.Importing.Providers;

using Xunit;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// Der Ort, den ein Befund nennt, muss es in der Quelldatei geben.
/// <para>
/// <b>Der Fehler, den diese Datei festnagelt.</b> Die zentrale Nachbearbeitung setzte den Pfad aller
/// von ihr erzeugten Befunde fest auf <c>mcpServers/&lt;name&gt;</c>. Das stimmt bei einer schlichten
/// <c>.mcp.json</c> und bei drei der fünf Formate nicht: Claudes Benutzerdatei legt die Server unter
/// <c>projects/&lt;pfad&gt;/mcpServers</c>, VS Code unter <c>servers</c> beziehungsweise
/// <c>mcp/servers</c>, Codex unter <c>mcp_servers</c>. Und die zentralen Befunde sind die Mehrheit
/// aller Befunde — Risiko, Zugangsdaten, Normalisierung entstehen alle dort.
/// </para>
/// <para>
/// <b>Ein Ort, der nicht stimmt, ist schlechter als keiner:</b> Er schickt jemanden an die falsche
/// Zeile. Wer dort nichts findet, glaubt eher, den Befund missverstanden zu haben, als dass der Ort
/// falsch ist.
/// </para>
/// </summary>
public sealed class ImportFindingLocationTests
{
    /// <summary>
    /// Claudes Benutzerdatei: Die Server liegen unter der <c>projects</c>-Karte, und die zentralen
    /// Befunde sagen das auch.
    /// </summary>
    [Fact]
    public void Claudes_projects_karte_steht_im_ort_der_zentralen_befunde()
    {
        var plan = ImportWorld.Permissive().Plan(
            ProviderWorld.Fixture("claude", "03-benutzerdatei-projects.json"));

        plan.Candidates.Should().NotBeEmpty();
        plan.Candidates.Should().OnlyContain(c =>
            c.SourcePath != null && c.SourcePath.StartsWith("projects/", StringComparison.Ordinal));

        Orte(plan).Should().OnlyContain(
            path => path.StartsWith("projects/", StringComparison.Ordinal),
            "kein Befund darf auf 'mcpServers/…' zeigen — das gibt es in dieser Datei nicht");
    }

    /// <summary>VS Code: <c>servers</c>, nicht <c>mcpServers</c>.</summary>
    [Fact]
    public void Vs_code_nennt_servers()
    {
        var plan = ImportWorld.Permissive().Plan(
            ProviderWorld.Fixture("vscode", "01-mcp-json-inputs.json"));

        plan.Candidates.Should().NotBeEmpty();
        Orte(plan).Should().OnlyContain(path =>
            path.StartsWith("servers/", StringComparison.Ordinal));
    }

    /// <summary>VS Code in einer <c>settings.json</c>: der Block <c>mcp</c> davor.</summary>
    [Fact]
    public void Vs_code_in_settings_json_nennt_den_mcp_block()
    {
        var plan = ImportWorld.Permissive().Plan(
            ProviderWorld.Fixture("vscode", "03-settings-json.json"));

        plan.Candidates.Should().NotBeEmpty();
        Orte(plan).Should().OnlyContain(path =>
            path.StartsWith("mcp/servers/", StringComparison.Ordinal));
    }

    /// <summary>Codex: der Sammelname in Schlangenschrift.</summary>
    [Fact]
    public void Codex_nennt_mcp_servers_in_schlangenschrift()
    {
        var plan = ImportWorld.Permissive().Plan(ProviderWorld.Fixture("codex", "01-lokal.json"));

        plan.Candidates.Should().NotBeEmpty();
        Orte(plan).Should().OnlyContain(path =>
            path.StartsWith("mcp_servers/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Der Regelfall bleibt der Regelfall: Wo <c>mcpServers</c> dasteht, steht es auch im Befund.
    /// </summary>
    [Fact]
    public void Eine_schlichte_mcp_json_nennt_weiterhin_mcpServers()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("github", "npx", "[\"-y\",\"@scope/server\"]"));

        plan.Candidates.Single().SourcePath.Should().Be("mcpServers/github");
        Orte(plan).Should().OnlyContain(path =>
            path.StartsWith("mcpServers/github", StringComparison.Ordinal));
    }

    /// <summary>
    /// Der Querschnitt über alle Beispielkonfigurationen: Jeder Befund eines Kandidaten liegt im
    /// Ast dieses Kandidaten. Dieser Test fängt den nächsten Parser mit, der einen eigenen
    /// Sammelnamen mitbringt.
    /// </summary>
    [Fact]
    public void Jeder_kandidatenbefund_liegt_im_ast_seines_kandidaten()
    {
        foreach (var reference in ProviderWorld.All())
        {
            var plan = ImportWorld.Permissive().Plan(ProviderWorld.Load(reference));

            foreach (var candidate in plan.Candidates)
            {
                candidate.SourcePath.Should().NotBeNullOrWhiteSpace(
                    "Datei {0}, Server '{1}'", reference, candidate.SourceName);

                candidate.Findings
                    .Where(f => f.Path is { Length: > 0 })
                    .Should().OnlyContain(f =>
                        f.Path!.StartsWith(candidate.SourcePath!, StringComparison.Ordinal),
                        "Datei {0}, Server '{1}'", reference, candidate.SourceName);
            }
        }
    }

    private static IEnumerable<string> Orte(ImportPlan plan)
        => plan.Candidates
            .SelectMany(candidate => candidate.Findings)
            .Select(finding => finding.Path)
            .Where(path => path is { Length: > 0 })!;
}
