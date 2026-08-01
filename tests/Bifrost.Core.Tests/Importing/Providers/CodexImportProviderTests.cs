using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing.Providers;

/// <summary>
/// Der Parser für die MCP-Server der Codex-CLI, geprüft an den versionierten
/// Beispielkonfigurationen unter <c>Importing/Fixtures/codex</c>.
/// <para>
/// <b>Die Grenze steht unter Test, nicht nur in der Dokumentation:</b> Codex schreibt TOML, dieser
/// Importweg nimmt JSON. Was hier gelesen wird, ist die JSON-Umschrift — und jeder Plan sagt das.
/// </para>
/// </summary>
public sealed class CodexImportProviderTests
{
    private static readonly CodexImportProvider Parser = new();

    // ── Erkennung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Der_schlangenschrift_sammelname_wird_erkannt()
        => Parser.Recognize(ProviderWorld.Fixture("codex", "01-lokal.json"))
            .Should().Be(CodexImportProvider.SnakeCaseConfidence);

    [Theory]
    [InlineData("")]
    [InlineData("kein json")]
    [InlineData("[]")]
    [InlineData("""{"mcpServers":{"s":{"command":"x"}}}""")]
    [InlineData("""{"servers":{"s":{"command":"x"}}}""")]
    public void Fremdes_und_kaputtes_wird_nicht_beansprucht(string document)
        => Parser.Recognize(document).Should().Be(0);

    // ── Die Grenze: TOML gegen JSON ───────────────────────────────────────────────────────────

    /// <summary>
    /// Wer hier etwas importiert, hat <b>nicht</b> seine <c>config.toml</c> importiert, sondern
    /// deren Umschrift. Das steht in jedem Plan — nicht, weil es schön wäre, sondern weil der
    /// Unterschied sonst erst auffällt, wenn jemand die Datei sucht, aus der das hier kam.
    /// </summary>
    [Fact]
    public void Jeder_plan_nennt_die_umschrift_aus_toml()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("codex", "01-lokal.json"), null);

        plan.Source.SchemaVersion.Should().Be("codex/config.toml (als JSON umgeschrieben)");
        plan.Findings.Should().Contain(f =>
            f.Code == ImportReason.UnknownFormat
            && f.Severity == ImportSeverity.Warning
            && f.Summary.Contains("TOML"));
    }

    // ── Abbildung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_lokaler_server_wird_abgebildet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("codex", "01-lokal.json"), null);
        var config = plan.Candidates.Should().ContainSingle().Subject.Config;

        config.Kind.Should().Be(UpstreamTransportKind.Stdio);
        config.Stdio!.Command.Should().Be("/usr/local/bin/notizen-mcp");
        config.Stdio.Arguments.Should().Equal("--modus", "lesen");
        config.Stdio.EnvironmentVariables!["NOTIZEN_PFAD"].Should().Be("/home/anna/notizen");
        config.Stdio.WorkingDirectory.Should().Be("/srv/notizen");
    }

    [Fact]
    public void Ein_entfernter_server_wird_mit_http_headers_abgebildet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("codex", "02-remote.json"), null);
        var config = plan.Candidates.Should().ContainSingle().Subject.Config;

        config.Kind.Should().Be(UpstreamTransportKind.StreamableHttp);
        config.Http!.Endpoint.Should().Be(new Uri("https://mcp.example.test/mcp"));
        config.Http.Headers!["X-Werkstatt-Version"].Should().Be("3");
    }

    /// <summary>
    /// <c>tool_timeout_sec</c> hat hier eine Entsprechung und wird übernommen — dass es übernommen
    /// wurde, steht als Befund da: Ein Zeitlimit aus einer fremden Datei ist eine Abweichung von der
    /// Vorgabe dieser Instanz.
    /// </summary>
    [Fact]
    public void Das_werkzeugzeitlimit_wird_uebernommen_und_benannt()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("codex", "03-zeiten-und-schalter.json"), null);
        var langsam = plan.Candidates.Single(c => c.SourceName == "langsam");

        langsam.Config.CallTimeout.Should().Be(TimeSpan.FromSeconds(120));
        langsam.Findings.Should().Contain(f =>
            f.Code == ImportReason.Lossy && f.Path == "mcp_servers/langsam/tool_timeout_sec");
    }

    // ── Clientexklusive Felder ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("startup_timeout_sec")]
    [InlineData("enabled")]
    public void Clientexklusive_felder_ueberleben_als_befund(string field)
        => Parser.Plan(ProviderWorld.Fixture("codex", "03-zeiten-und-schalter.json"), null)
            .Everything().Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Summary.Contains(field));

    [Fact]
    public void Einstellungen_der_cli_neben_den_servern_verschwinden_nicht_still()
        => Parser.Plan(ProviderWorld.Fixture("codex", "03-zeiten-und-schalter.json"), null)
            .Findings.Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Summary.Contains("model"));

    /// <summary>
    /// <b>Der Kern dieses Providers.</b> <c>bearer_token_env_var</c> nennt nur den <em>Namen</em>
    /// einer Umgebungsvariablen. Der Wert steht nirgends — er wird als fehlend gemeldet und nicht
    /// erraten. Ein erratener Wert, der fast stimmt, ist schlimmer als ein fehlender.
    /// </summary>
    [Fact]
    public void Das_bearer_token_wird_als_fehlendes_zugangsdatum_gemeldet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("codex", "02-remote.json"), null);

        plan.Secrets().Should().ContainSingle().Which.Should().Match<ImportSecret>(s =>
            s.Location.Contains("WERKSTATT_TOKEN") && !s.ValuePresent);
        plan.Everything().Should().Contain(f =>
            f.Code == ImportReason.MaskedValue
            && f.Path == "mcp_servers/werkstatt/bearer_token_env_var");
        plan.Candidates.Single().Config.Http!.Headers.Should().NotContainKey("Authorization",
            "aus einem Variablennamen wird hier kein Wert erfunden");
    }

    // ── Negativfälle ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_eintrag_ohne_transport_ergibt_keinen_kandidaten()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("codex", "90-ohne-transport.json"), null);

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
        plan.Everything().Should().Contain(f =>
            f.Severity == ImportSeverity.Error && f.Path == "mcp_servers/leer");
        plan.Everything().Should().Contain(f =>
            f.Code == ImportReason.UnknownField && f.Path == "mcp_servers/leer/tool_timeout_sec",
            "ein Zeitlimit, das keine Zahl ist, wird gemeldet und nicht ausgelegt");
    }

    [Fact]
    public void Kaputtes_json_ergibt_genau_einen_fehler()
        => Parser.Plan("""{"mcp_servers": {""", null)
            .Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.NotJson);

    [Fact]
    public void Ein_dokument_ohne_mcp_servers_wird_abgewiesen()
    {
        var plan = Parser.Plan("""{"model":"gpt-5.1-codex"}""", null);

        plan.Candidates.Should().BeEmpty();
        plan.Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.UnknownFormat);
    }

    // ── Der Quellpfad ist eine Angabe, kein Leseauftrag ───────────────────────────────────────

    [Fact]
    public void Der_quellpfad_wird_genannt_und_nicht_gelesen()
    {
        var document = ProviderWorld.Fixture("codex", "01-lokal.json");
        var mit = Parser.Plan(document, "/gibt/es/nicht/.codex/config.toml");

        mit.Source.OriginPath.Should().Be("/gibt/es/nicht/.codex/config.toml");
        mit.Candidates.Should().BeEquivalentTo(Parser.Plan(document, null).Candidates);
    }

    // ── Nichts ist eingeschaltet ──────────────────────────────────────────────────────────────

    [Fact]
    public void Kein_kandidat_ist_eingeschaltet()
    {
        foreach (var name in ProviderWorld.Names("codex"))
        {
            Parser.Plan(ProviderWorld.Fixture("codex", name), null).Candidates
                .Should().OnlyContain(candidate => !candidate.Config.Enabled, "Datei {0}", name);
        }
    }

    /// <summary>
    /// Auch <c>"enabled": true</c> aus der Quelle ändert daran nichts. Der Schalter der Quelle ist
    /// eine Aussage über den Quellrechner, keine Erlaubnis für diesen.
    /// </summary>
    [Fact]
    public void Ein_eingeschalteter_server_der_quelle_kommt_abgeschaltet_an()
        => Parser.Plan("""{"mcp_servers":{"s":{"command":"/usr/bin/s","enabled":true}}}""", null)
            .Candidates.Should().ContainSingle().Which.Config.Enabled.Should().BeFalse();

    // ── Rückweg ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Der Rückweg schreibt <b>TOML</b>, weil das Codex' Format ist. Ein JSON-Ausschnitt sähe nur so
    /// aus, als hätte er geholfen — Codex lädt ihn nicht.
    /// </summary>
    [Fact]
    public void Der_rueckweg_schreibt_toml()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("codex", "01-lokal.json"), null);
        var export = CodexImportProvider.Export(plan.Candidates.Single());

        export.Lossless.Should().BeTrue();
        export.Document.Should().StartWith("[mcp_servers.notizen]");
        export.Document.Should().Contain("command = \"/usr/local/bin/notizen-mcp\"");
        export.Document.Should().Contain("args = [\"--modus\", \"lesen\"]");
        export.Document.Should().Contain("cwd = \"/srv/notizen\"");
        export.Document.Should().Contain("[mcp_servers.notizen.env]");
        export.Document.Should().Contain("NOTIZEN_PFAD = \"/home/anna/notizen\"");
    }

    [Fact]
    public void Anfuehrungszeichen_und_backslashes_werden_im_toml_maskiert()
    {
        var candidate = new ImportCandidate(
            "w",
            new UpstreamServerConfig(
                "w", "w", UpstreamTransportKind.Stdio, Enabled: false,
                Stdio: new StdioTransportOptions(@"C:\Programme\werkzeug.exe", ["--sagt=\"hallo\""])),
            [],
            []);

        var export = CodexImportProvider.Export(candidate);

        export.Document.Should().Contain(@"command = ""C:\\Programme\\werkzeug.exe""");
        export.Document.Should().Contain(@"args = [""--sagt=\""hallo\""""]");
    }

    [Fact]
    public void Ein_upstream_ohne_entsprechung_wird_beim_export_benannt()
    {
        var export = CodexImportProvider.Export(new ImportCandidate(
            "w",
            new UpstreamServerConfig("w", "w", UpstreamTransportKind.Wasi, Enabled: false),
            [],
            []));

        export.Document.Should().BeEmpty();
        export.Findings.Should().ContainSingle().Which.Severity.Should().Be(ImportSeverity.Error);
    }
}
