using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing.Providers;

/// <summary>
/// Der Parser für <c>~/.cursor/mcp.json</c>, geprüft an den versionierten Beispielkonfigurationen
/// unter <c>Importing/Fixtures/cursor</c>.
/// </summary>
public sealed class CursorImportProviderTests
{
    private static readonly CursorImportProvider Parser = new();

    // ── Erkennung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cursors_ersetzungsschreibweise_ist_der_dialektnachweis()
        => Parser.Recognize(ProviderWorld.Fixture("cursor", "01-lokal-stdio.json"))
            .Should().Be(CursorImportProvider.DialectConfidence);

    [Fact]
    public void Der_auth_block_wiegt_schwerer_als_der_dialekt()
        => Parser.Recognize(ProviderWorld.Fixture("cursor", "03-auth-und-envfile.json"))
            .Should().Be(CursorImportProvider.AuthConfidence);

    /// <summary>
    /// <b>Die ausdrücklich benannte Grenze.</b> Ohne Cursor-eigenes Merkmal ist die Datei
    /// zeichengleich mit einer generischen — dann meldet sich dieser Parser nicht.
    /// </summary>
    [Fact]
    public void Ohne_cursor_eigenes_merkmal_wird_nichts_beansprucht()
        => Parser.Recognize("""{"mcpServers":{"s":{"command":"npx","args":["-y","x"]}}}""")
            .Should().Be(0);

    [Theory]
    [InlineData("")]
    [InlineData("kein json")]
    [InlineData("[]")]
    [InlineData("""{"servers":{"s":{"command":"x","envFile":".env"}}}""")]
    public void Fremdes_und_kaputtes_wird_nicht_beansprucht(string document)
        => Parser.Recognize(document).Should().Be(0);

    // ── Abbildung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_lokaler_server_wird_abgebildet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "01-lokal-stdio.json"), null);

        plan.Source.Provider.Should().Be(CursorImportProvider.ProviderName);
        plan.Source.SchemaVersion.Should().Be("cursor/mcp.json");

        var config = plan.Candidates.Should().ContainSingle().Subject.Config;
        config.Kind.Should().Be(UpstreamTransportKind.Stdio);
        config.Stdio!.Command.Should().Be("python");
        config.Stdio.Arguments.Should().Equal("${workspaceFolder}/werkzeuge/mcp_server.py");
        config.Stdio.EnvironmentVariables!["API_KEY"].Should().Be("${env:API_KEY}");
    }

    /// <summary>
    /// Die Ersetzungen bleiben stehen: <c>${workspaceFolder}</c> zeigt auf den Arbeitsbereich des
    /// Editors, den es hier nicht gibt. Aufgelöst würde daraus ein Pfad, den niemand geprüft hat.
    /// </summary>
    [Fact]
    public void Ersetzungen_werden_nicht_aufgeloest_sondern_benannt()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "01-lokal-stdio.json"), null);

        plan.Everything().Should().Contain(f =>
            f.Code == ImportReason.Lossy && f.Summary.Contains("${env:"));
    }

    [Fact]
    public void Ein_entfernter_server_wird_abgebildet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "02-remote-http.json"), null);

        plan.Candidates.Should().HaveCount(2);
        var config = plan.Candidates.Single(c => c.SourceName == "entfernt").Config;
        config.Kind.Should().Be(UpstreamTransportKind.StreamableHttp);
        config.Http!.Endpoint.Should().Be(new Uri("https://mcp.example.test/mcp"));
        config.Http.Headers!["Authorization"].Should().Be("Bearer ${env:WERKSTATT_TOKEN}");
    }

    // ── Clientexklusive Felder ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("auth")]
    [InlineData("envFile")]
    [InlineData("disabled")]
    public void Clientexklusive_felder_ueberleben_als_befund(string field)
        => Parser.Plan(ProviderWorld.Fixture("cursor", "03-auth-und-envfile.json"), null)
            .Everything().Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Summary.Contains(field));

    /// <summary>
    /// <b>Der Kern dieses Providers.</b> <c>CLIENT_SECRET</c> steht an einer Stelle, die dieses
    /// Gateway nicht übernimmt — die zentrale Risikoprüfung sähe es also nie. Wird es hier nicht
    /// eingeordnet, verschwindet ein Zugangsdatum aus dem Blickfeld, obwohl es in der Quelldatei
    /// steht.
    /// </summary>
    [Fact]
    public void Das_client_secret_wird_eingeordnet_obwohl_es_nicht_uebernommen_wird()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "03-auth-und-envfile.json"), null);

        plan.Secrets().Should().Contain(s =>
            s.Location.Contains("CLIENT_SECRET") && s.ValuePresent);
        plan.Everything().Should().Contain(f =>
            f.Code == ImportReason.PlaintextSecret && f.Severity == ImportSeverity.Risk);
    }

    /// <summary>
    /// Die Umgebungsdatei wird <b>nicht geöffnet</b>. Ein Gateway, das den in einer fremden
    /// Konfiguration genannten Pfad selbst ausliest, wäre ein Weg, beliebige Dateien zu lesen.
    /// Gemeldet wird trotzdem, dass dort Werte liegen — sonst sähe der Import vollständig aus.
    /// </summary>
    [Fact]
    public void Die_umgebungsdatei_wird_gemeldet_und_nicht_gelesen()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "03-auth-und-envfile.json"), null);

        plan.Secrets().Should().Contain(s =>
            s.Location.Contains("envFile") && !s.ValuePresent);
        plan.Candidates.Single(c => c.SourceName == "mit-envfile").Config.Stdio!
            .EnvironmentVariables.Should().BeNull();
    }

    [Fact]
    public void Env_an_einem_entfernten_server_ist_ein_befund()
        => Parser.Plan(
                """{"mcpServers":{"s":{"url":"https://a.example.test/mcp","env":{"A":"${env:A}"}}}}""",
                null)
            .Everything().Should().Contain(f =>
                f.Code == ImportReason.ClientOnlyField && f.Path == "mcpServers/s/env");

    // ── Negativfälle ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Kaputtes_json_ergibt_genau_einen_fehler()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "90-kaputt.json"), null);

        plan.Candidates.Should().BeEmpty();
        plan.Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.NotJson);
    }

    [Fact]
    public void Doppelte_und_unbrauchbare_eintraege_werden_gemeldet()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "91-doppelt.json"), null);

        plan.Codes().Should().Contain(ImportReason.DuplicateServer);
        plan.CanApply.Should().BeFalse();
        plan.Candidates.Should().ContainSingle("der zweite Eintrag wird gemeldet, nicht uebernommen");
    }

    // ── Der Quellpfad ist eine Angabe, kein Leseauftrag ───────────────────────────────────────

    [Fact]
    public void Der_quellpfad_wird_genannt_und_nicht_gelesen()
    {
        var document = ProviderWorld.Fixture("cursor", "01-lokal-stdio.json");
        var ohne = Parser.Plan(document, null);
        var mit = Parser.Plan(document, "/gibt/es/nicht/mcp.json");

        mit.Source.OriginPath.Should().Be("/gibt/es/nicht/mcp.json");
        mit.Candidates.Should().BeEquivalentTo(ohne.Candidates);
    }

    // ── Nichts ist eingeschaltet ──────────────────────────────────────────────────────────────

    [Fact]
    public void Kein_kandidat_ist_eingeschaltet()
    {
        foreach (var name in ProviderWorld.Names("cursor"))
        {
            Parser.Plan(ProviderWorld.Fixture("cursor", name), null).Candidates
                .Should().OnlyContain(candidate => !candidate.Config.Enabled, "Datei {0}", name);
        }
    }

    /// <summary>
    /// Auch ein Eintrag, der in Cursor ausdrücklich <c>"disabled": false</c> trägt — also lief —,
    /// kommt hier abgeschaltet an.
    /// </summary>
    [Fact]
    public void Auch_ein_in_der_quelle_laufender_server_kommt_abgeschaltet_an()
        => Parser.Plan(
                """{"mcpServers":{"s":{"command":"/usr/bin/s","disabled":false,"envFile":".env"}}}""",
                null)
            .Candidates.Should().ContainSingle().Which.Config.Enabled.Should().BeFalse();

    // ── Rückweg ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_entfernter_server_geht_verlustfrei_zurueck()
    {
        var plan = Parser.Plan(ProviderWorld.Fixture("cursor", "02-remote-http.json"), null);
        var export = CursorImportProvider.Export(
            plan.Candidates.Single(c => c.SourceName == "entfernt"));

        export.Lossless.Should().BeTrue();
        export.Document.Should().Contain("\"url\"").And.Contain("mcp.example.test");
        Parser.Plan(export.Document, null).Candidates.Should().ContainSingle()
            .Which.Config.Http!.Endpoint.Should().Be(new Uri("https://mcp.example.test/mcp"));
    }

    [Fact]
    public void Ein_upstream_ohne_entsprechung_wird_beim_export_benannt()
    {
        var candidate = new ImportCandidate(
            "w",
            new UpstreamServerConfig("w", "w", UpstreamTransportKind.Wasi, Enabled: false),
            [],
            []);

        var export = CursorImportProvider.Export(candidate);

        export.Lossless.Should().BeFalse();
        export.Findings.Should().ContainSingle().Which.Severity.Should().Be(ImportSeverity.Error);
    }
}
