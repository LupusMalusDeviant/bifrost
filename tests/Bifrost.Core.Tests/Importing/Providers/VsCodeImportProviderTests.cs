using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing.Providers;

/// <summary>
/// Der Parser für <c>.vscode/mcp.json</c> und den Block <c>mcp</c> in <c>settings.json</c>, geprüft
/// an den versionierten Beispielkonfigurationen unter <c>Importing/Fixtures/vscode</c>.
/// </summary>
public sealed class VsCodeImportProviderTests
{
    private static readonly VsCodeImportProvider Parser = new();

    // ── Erkennung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Servers_mit_inputs_wird_erkannt()
        => Parser.Recognize(ProviderWorld.Fixture("vscode", "01-mcp-json-inputs.json"))
            .Should().Be(VsCodeImportProvider.ServersConfidence);

    [Fact]
    public void Der_block_mcp_in_den_einstellungen_wiegt_schwerer()
        => Parser.Recognize(ProviderWorld.Fixture("vscode", "03-settings-json.json"))
            .Should().Be(VsCodeImportProvider.SettingsConfidence);

    /// <summary>
    /// <c>servers</c> allein genügt nicht: Das Wort steht in beliebigen Konfigurationsdateien. Ohne
    /// ein VS-Code-eigenes Merkmal übernimmt der generische Parser.
    /// </summary>
    [Fact]
    public void Servers_allein_genuegt_nicht()
        => Parser.Recognize("""{"servers":{"s":{"command":"/usr/bin/s"}}}""").Should().Be(0);

    [Theory]
    [InlineData("")]
    [InlineData("kein json")]
    [InlineData("[]")]
    [InlineData("""{"mcpServers":{"s":{"command":"x","envFile":".env"}}}""")]
    public void Fremdes_und_kaputtes_wird_nicht_beansprucht(string document)
        => Parser.Recognize(document).Should().Be(0);

    // ── Abbildung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stdio_mit_arbeitsverzeichnis_wird_abgebildet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("vscode", "02-http-und-sandbox.json"), null);

        plan.Source.SchemaVersion.Should().Be("vscode/mcp.json");
        var config = plan.Candidates.Single(c => c.SourceName == "eingehegt").Config;
        config.Stdio!.Command.Should().Be("/usr/local/bin/werkzeug-mcp");
        config.Stdio.WorkingDirectory.Should().Be("/srv/werkstatt",
            "VS Code ist das einzige der vier Formate mit einem dokumentierten 'cwd'");
    }

    [Fact]
    public void Http_mit_kopfzeilen_wird_abgebildet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("vscode", "02-http-und-sandbox.json"), null);
        var config = plan.Candidates.Single(c => c.SourceName == "github").Config;

        config.Kind.Should().Be(UpstreamTransportKind.StreamableHttp);
        config.Http!.Endpoint.Should().Be(new Uri("https://api.example.test/mcp"));
        config.Http.Headers!["Authorization"].Should().Be("Bearer ${input:github-token}");
    }

    [Fact]
    public void Der_block_mcp_in_den_einstellungen_wird_gelesen()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("vscode", "03-settings-json.json"), null);

        plan.Source.SchemaVersion.Should().Be("vscode/settings.json#mcp");
        plan.Candidates.Should().ContainSingle().Which.Config.Stdio!.Command.Should().Be("node");
        plan.Everything().Should().Contain(f => f.Path == "mcp/servers/werkstatt/dev");
    }

    // ── Eingabeaufforderungen ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Ein <c>${input:…}</c> ist kein Wert, sondern eine Frage.</b> VS Code stellt sie beim
    /// Start; dieses Gateway fragt niemanden. Der Wert fehlt also — und bei <c>password: true</c>
    /// fehlt ein Zugangsdatum. Beides wird gesagt, statt einen Platzhalter anzulegen, der aussieht
    /// wie ein Wert.
    /// </summary>
    [Fact]
    public void Ein_verweis_auf_eine_eingabeaufforderung_wird_als_fehlender_wert_gemeldet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("vscode", "01-mcp-json-inputs.json"), null);

        plan.Everything().Should().Contain(f =>
            f.Code == ImportReason.MaskedValue && f.Summary.Contains("perplexity-key"));
        plan.Secrets().Should().Contain(s =>
            s.Location.Contains("perplexity-key") && !s.ValuePresent);
        plan.Candidates.Single().Config.Stdio!.EnvironmentVariables!["PERPLEXITY_API_KEY"]
            .Should().Be("${input:perplexity-key}", "rekonstruiert wird hier nichts");
    }

    [Fact]
    public void Die_liste_der_eingabeaufforderungen_ueberlebt_als_befund()
        => Parser.Plan(ProviderWorld.Fixture("vscode", "01-mcp-json-inputs.json"), null)
            .Findings.Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Path == "inputs");

    // ── Die Sandbox reist nicht mit ───────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Der sicherheitsrelevante Verlust dieses Formats.</b> Die Quelle hat den Server auf Pfade
    /// und Domänen beschränkt; hier gibt es diese Grenze nicht. Das ist ein Risikobefund und
    /// verlangt eine Bestätigung — ein Import, der aus einem eingehegten Server einen freien macht,
    /// darf das nicht nebenbei tun.
    /// </summary>
    [Fact]
    public void Die_sandbox_der_quelle_wird_als_risiko_gemeldet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("vscode", "02-http-und-sandbox.json"), null);

        plan.Findings.Should().Contain(f =>
            f.Code == ImportReason.ClientOnlyField
            && f.Severity == ImportSeverity.Risk
            && f.Path == "sandbox");
        plan.RequiresConfirmation.Should().NotBeEmpty();
    }

    [Fact]
    public void Der_schalter_sandboxEnabled_wird_ebenfalls_gemeldet()
        => Parser.Plan(ProviderWorld.Fixture("vscode", "02-http-und-sandbox.json"), null)
            .Everything().Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Summary.Contains("sandboxEnabled"));

    // ── Weitere clientexklusive Felder ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("envFile")]
    [InlineData("dev")]
    public void Clientexklusive_felder_ueberleben_als_befund(string field)
        => Parser.Plan(ProviderWorld.Fixture("vscode", "03-settings-json.json"), null)
            .Everything().Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Summary.Contains(field));

    [Fact]
    public void Die_umgebungsdatei_wird_gemeldet_und_nicht_gelesen()
        => Parser.Plan(ProviderWorld.Fixture("vscode", "03-settings-json.json"), null)
            .Secrets().Should().Contain(s => s.Location.Contains("envFile") && !s.ValuePresent);

    // ── Negativfälle ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Servers_als_liste_ist_kein_serverdokument()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("vscode", "90-servers-ist-liste.json"), null);

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
        plan.Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.UnknownFormat);
    }

    [Fact]
    public void Kaputtes_json_ergibt_genau_einen_fehler()
        => Parser.Plan("""{"servers": {"a": }}""", null)
            .Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.NotJson);

    // ── Der Quellpfad ist eine Angabe, kein Leseauftrag ───────────────────────────────────────

    [Fact]
    public void Der_quellpfad_wird_genannt_und_nicht_gelesen()
    {
        var document = ProviderWorld.Fixture("vscode", "01-mcp-json-inputs.json");
        var mit = Parser.Plan(document, "/gibt/es/nicht/.vscode/mcp.json");

        mit.Source.OriginPath.Should().Be("/gibt/es/nicht/.vscode/mcp.json");
        mit.Candidates.Should().BeEquivalentTo(Parser.Plan(document, null).Candidates);
    }

    // ── Nichts ist eingeschaltet ──────────────────────────────────────────────────────────────

    [Fact]
    public void Kein_kandidat_ist_eingeschaltet()
    {
        foreach (var name in ProviderWorld.Names("vscode"))
        {
            Parser.Plan(ProviderWorld.Fixture("vscode", name), null).Candidates
                .Should().OnlyContain(candidate => !candidate.Config.Enabled, "Datei {0}", name);
        }
    }

    // ── Rückweg ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Der Rückweg nach VS Code verliert als einziger auch das Arbeitsverzeichnis nicht — dort ist
    /// <c>cwd</c> dokumentiert.
    /// </summary>
    [Fact]
    public void Ein_lokaler_server_geht_mit_arbeitsverzeichnis_verlustfrei_zurueck()
    {
        var candidate = new ImportCandidate(
            "werkstatt",
            new UpstreamServerConfig(
                "werkstatt", "Werkstatt", UpstreamTransportKind.Stdio, Enabled: false,
                Stdio: new StdioTransportOptions(
                    "/usr/local/bin/werkzeug-mcp", ["--modus", "lesen"], null, "/srv/werkstatt")),
            [],
            []);

        var export = VsCodeImportProvider.Export(candidate);

        export.Lossless.Should().BeTrue();
        export.Document.Should().Contain("\"cwd\"").And.Contain("/srv/werkstatt");
        Parser.Plan(export.Document, null).Candidates.Should().ContainSingle()
            .Which.Config.Stdio!.WorkingDirectory.Should().Be("/srv/werkstatt");
    }
}
