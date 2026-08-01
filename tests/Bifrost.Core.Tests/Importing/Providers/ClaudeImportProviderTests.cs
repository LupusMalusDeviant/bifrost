using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing.Providers;

/// <summary>
/// Der Parser für Claude Code und Claude Desktop, geprüft an den versionierten
/// Beispielkonfigurationen unter <c>Importing/Fixtures/claude</c>.
/// </summary>
public sealed class ClaudeImportProviderTests
{
    private static readonly ClaudeImportProvider Parser = new();

    // ── Erkennung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Die_ersetzungsform_mit_vorgabe_ist_der_dialektnachweis()
        => Parser.Recognize(ProviderWorld.Fixture("claude", "01-mcp-json-projekt.json"))
            .Should().Be(ClaudeImportProvider.DialectConfidence);

    [Fact]
    public void Ein_einstellungsschluessel_von_claude_code_wiegt_schwerer()
        => Parser.Recognize(ProviderWorld.Fixture("claude", "03-benutzerdatei-projects.json"))
            .Should().Be(ClaudeImportProvider.SettingsConfidence);

    /// <summary>
    /// <b>Die ausdrücklich benannte Grenze.</b> Eine Claude-Desktop-Datei ohne Claude-eigenen
    /// Schlüssel ist zeichengleich mit einer generischen MCP-Konfiguration. Dieser Parser meldet
    /// dann <c>0</c> — nicht, weil er sie nicht lesen könnte, sondern weil „aus Claude" eine
    /// Behauptung wäre, die das Dokument nicht hergibt.
    /// </summary>
    [Fact]
    public void Eine_datei_ohne_claude_eigenen_schluessel_wird_nicht_beansprucht()
        => Parser.Recognize(ProviderWorld.Fixture("claude", "02-desktop-config.json"))
            .Should().Be(0, "geraten wird hier nicht — dann uebernimmt der generische Parser");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kein json")]
    [InlineData("[]")]
    [InlineData("""{"mcpServers":{"a":{"command":"x"}}}""")]
    public void Fremdes_und_kaputtes_wird_nicht_beansprucht(string document)
        => Parser.Recognize(document).Should().Be(0);

    // ── Abbildung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stdio_und_http_werden_abgebildet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "01-mcp-json-projekt.json"), null);

        plan.Source.Provider.Should().Be(ClaudeImportProvider.ProviderName);
        plan.Source.SchemaVersion.Should().Be("claude-code/.mcp.json");
        plan.Candidates.Should().HaveCount(2);

        var stdio = plan.Candidates.Single(c => c.SourceName == "airtable").Config;
        stdio.Kind.Should().Be(UpstreamTransportKind.Stdio);
        stdio.Stdio!.Command.Should().Be("npx");
        stdio.Stdio.Arguments.Should().Equal("-y", "airtable-mcp-server");
        stdio.Stdio.EnvironmentVariables!["AIRTABLE_API_KEY"].Should().Be("${AIRTABLE_API_KEY}");

        var http = plan.Candidates.Single(c => c.SourceName == "werkstatt-api").Config;
        http.Kind.Should().Be(UpstreamTransportKind.StreamableHttp);
        http.Http!.Endpoint.Should().Be(new Uri("https://api.example.test/mcp"));
        http.Http.Headers!["Authorization"].Should().Be("Bearer ${WERKSTATT_TOKEN:-kein-token}");
    }

    /// <summary>
    /// Die Ersetzung wird <b>nicht aufgelöst</b> — und dass sie dasteht, wird gemeldet. Ein Wert,
    /// der auf der Quellmaschine etwas anderes bedeutet als hier, ist der stille Fehler, den dieses
    /// Paket abschaffen soll.
    /// </summary>
    [Fact]
    public void Ersetzungen_bleiben_stehen_und_werden_benannt()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "01-mcp-json-projekt.json"), null);

        plan.Everything().Should().Contain(f =>
            f.Code == ImportReason.Lossy
            && f.Severity == ImportSeverity.Warning
            && f.Summary.Contains("${VAR}"));
    }

    [Fact]
    public void Die_projektkarte_der_benutzerdatei_wird_gelesen()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "03-benutzerdatei-projects.json"), null);

        plan.Source.SchemaVersion.Should().Be("claude-code/~/.claude.json");
        plan.Candidates.Select(c => c.SourceName).Should().BeEquivalentTo(["notizen", "alt-sse"]);
        plan.Everything().Should().Contain(f =>
            f.Path != null && f.Path.StartsWith("projects/", StringComparison.Ordinal));
    }

    /// <summary>Der abgelöste SSE-Transport wird übernommen und die Abweichung benannt.</summary>
    [Fact]
    public void Sse_wird_uebernommen_und_als_verlust_benannt()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "03-benutzerdatei-projects.json"), null);
        var sse = plan.Candidates.Single(c => c.SourceName == "alt-sse");

        sse.Config.Http!.AllowLegacySse.Should().BeTrue();
        sse.Findings.Should().Contain(f =>
            f.Code == ImportReason.Lossy && f.Summary.Contains("SSE"));
    }

    // ── Clientexklusive Felder ────────────────────────────────────────────────────────────────

    [Fact]
    public void Die_freigabelisten_von_claude_code_ueberleben_als_befund()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "03-benutzerdatei-projects.json"), null);

        plan.Findings.Should().Contain(f =>
            f.Code == ImportReason.ClientOnlyField && f.Summary.Contains("enabledMcpjsonServers"));
        plan.Findings.Should().Contain(f =>
            f.Code == ImportReason.ClientOnlyField && f.Summary.Contains("disabledMcpjsonServers"));
    }

    [Fact]
    public void Eine_einstellung_des_quellclients_verschwindet_nicht_still()
        => Parser.Plan(ProviderWorld.Fixture("claude", "03-benutzerdatei-projects.json"), null)
            .Findings.Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Summary.Contains("theme"));

    /// <summary>
    /// <c>cwd</c> gehört nicht zum dokumentierten Claude-Schema. Es stillschweigend zu übernehmen
    /// hieße, den Server hier woanders zu starten als in der Quelle — gemeldet wird es deshalb.
    /// </summary>
    [Fact]
    public void Ein_arbeitsverzeichnis_wird_gemeldet_statt_geraten()
    {
        var plan = Parser.Plan(
            """{"enabledMcpjsonServers":[],"mcpServers":{"s":{"command":"/usr/bin/s","cwd":"/srv"}}}""",
            null);

        plan.Candidates.Single().Config.Stdio!.WorkingDirectory.Should().BeNull();
        plan.Everything().Should().Contain(f =>
            f.Code == ImportReason.ClientOnlyField && f.Summary.Contains("cwd"));
    }

    // ── Negativfälle ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Eine Adresse, die erst nach einer Ersetzung eine Adresse ist, wird abgewiesen. Die halbe
    /// Adresse anzulegen wäre schlimmer: Sie liefe durch jede Prüfung und scheiterte erst am Netz.
    /// </summary>
    [Fact]
    public void Eine_adresse_aus_einer_ersetzung_wird_abgewiesen()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "90-ersetzte-adresse.json"), null);

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
        plan.Everything().Should().Contain(f =>
            f.Severity == ImportSeverity.Error && f.Path != null && f.Path.Contains("api-server"));
    }

    [Fact]
    public void Widersprueche_werden_gemeldet_statt_aufgeloest()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "91-widerspruch.json"), null);

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
        plan.Everything().Count(f => f.Severity == ImportSeverity.Error).Should().Be(3);
    }

    [Fact]
    public void Kaputtes_json_ergibt_genau_einen_fehler()
    {
        var plan = Parser.Plan("{\"mcpServers\": {", null);

        plan.Candidates.Should().BeEmpty();
        plan.Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.NotJson);
    }

    // ── Der Quellpfad ist eine Angabe, kein Leseauftrag ───────────────────────────────────────

    /// <summary>
    /// Der Pfad landet in der Herkunftsangabe und wird nirgends geöffnet. Der Beleg: Ein Pfad, den
    /// es nicht gibt, ändert am Plan nichts — und ein Parser, der läse, käme hier nicht durch.
    /// </summary>
    [Fact]
    public void Der_quellpfad_wird_genannt_und_nicht_gelesen()
    {
        var document = ProviderWorld.Fixture("claude", "01-mcp-json-projekt.json");
        var ohne = Parser.Plan(document, null);
        var mit = Parser.Plan(document, "/gibt/es/nicht/.mcp.json");

        mit.Source.OriginPath.Should().Be("/gibt/es/nicht/.mcp.json");
        mit.Candidates.Should().BeEquivalentTo(ohne.Candidates);
        mit.Findings.Should().BeEquivalentTo(ohne.Findings);
    }

    // ── Nichts ist eingeschaltet ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Die DoD von WP4.1, hier je Parser nachgewiesen statt der zentralen Normalisierung
    /// überlassen: <b>Kein Kandidat kommt eingeschaltet aus diesem Parser.</b>
    /// </summary>
    [Fact]
    public void Kein_kandidat_ist_eingeschaltet()
    {
        foreach (var name in ProviderWorld.Names("claude"))
        {
            Parser.Plan(ProviderWorld.Fixture("claude", name), null).Candidates
                .Should().OnlyContain(candidate => !candidate.Config.Enabled, "Datei {0}", name);
        }
    }

    // ── Rückweg ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_lokaler_server_geht_verlustfrei_zurueck()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("claude", "01-mcp-json-projekt.json"), null);
        var export = ClaudeImportProvider.Export(
            plan.Candidates.Single(c => c.SourceName == "airtable"));

        export.Lossless.Should().BeTrue();
        export.Document.Should().Contain("\"mcpServers\"").And.Contain("airtable-mcp-server");

        // Der Beleg, dass der Rueckweg wirklich zurueckfuehrt: Das Ergebnis liest sich wieder ein.
        Parser.Plan(export.Document, null).Candidates.Should().ContainSingle()
            .Which.Config.Stdio!.Command.Should().Be("npx");
    }

    [Fact]
    public void Ein_arbeitsverzeichnis_geht_beim_export_verloren_und_wird_benannt()
    {
        var candidate = new ImportCandidate(
            "s",
            new UpstreamServerConfig(
                "s", "s", UpstreamTransportKind.Stdio, Enabled: false,
                Stdio: new StdioTransportOptions("/usr/bin/s", [], null, "/srv")),
            [],
            []);

        var export = ClaudeImportProvider.Export(candidate);

        export.Lossless.Should().BeFalse();
        export.Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.Lossy);
    }
}
