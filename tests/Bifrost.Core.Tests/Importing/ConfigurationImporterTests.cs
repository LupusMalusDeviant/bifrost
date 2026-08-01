using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;
using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// Die Formaterkennung und der Weg vom Dokument zum Plan: Was passiert bei Unklarheit, bei einer
/// kaputten Datei, bei doppelten Servern und bei Namen, die zusammenfallen?
/// </summary>
public sealed class ConfigurationImporterTests
{
    // ── Erkennung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_dokument_mit_mcpServers_wird_als_mcp_erkannt()
    {
        var source = ImportWorld.Permissive().Detect(ImportWorld.Stdio("github", "/usr/bin/server"));

        source.Provider.Should().Be(GenericMcpImportProvider.ProviderName);
        source.Confidence.Should().Be(GenericMcpImportProvider.McpServersConfidence);
    }

    [Fact]
    public void Ein_fremdes_json_wird_nicht_erkannt_statt_geraten()
        => ImportWorld.Permissive().Detect("""{"dependencies":{"left-pad":"1.0.0"}}""")
            .Provider.Should().Be(
                ConfigurationImporter.UnknownProvider,
                "ein Parser, der bei Unklarheit den naechstbesten nimmt, verschiebt den Fehler in "
                + "die Abbildung");

    [Fact]
    public void Ein_nicht_erkanntes_format_ergibt_einen_fehler_und_keinen_kandidaten()
    {
        var plan = ImportWorld.Permissive().Plan("""{"dependencies":{}}""");

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
        plan.AllCodes().Should().Contain(ImportReason.UnknownFormat);
    }

    /// <summary>
    /// Der Kern der Anforderung „bei Gleichstand ein Befund, kein Raten": Zwei Parser mit derselben
    /// Sicherheit führen zu einem Fehler mit beiden Namen, nicht zu einer Auswahl.
    /// </summary>
    [Fact]
    public void Bei_gleichstand_wird_kein_parser_gewaehlt()
    {
        var importer = new ConfigurationImporter(
            [new FixedProvider("alpha", 0.8), new FixedProvider("beta", 0.8)],
            ImportWorld.Allowing);

        var plan = importer.Plan("""{"mcpServers":{}}""");

        plan.Source.Provider.Should().Be(ConfigurationImporter.AmbiguousProvider);
        plan.CanApply.Should().BeFalse();
        plan.Findings.Should().ContainSingle()
            .Which.Summary.Should().Contain("alpha").And.Contain("beta");
    }

    /// <summary>
    /// Ein knapper Abstand ist auch ein Gleichstand. Ohne diese Regel entschiede die dritte
    /// Nachkommastelle einer Heuristik darüber, wie eine fremde Konfiguration gelesen wird.
    /// </summary>
    [Fact]
    public void Ein_knapper_vorsprung_gilt_als_gleichstand()
    {
        var importer = new ConfigurationImporter(
            [new FixedProvider("alpha", 0.80), new FixedProvider("beta", 0.75)],
            ImportWorld.Allowing);

        importer.Detect("{}").Provider.Should().Be(ConfigurationImporter.AmbiguousProvider);
    }

    [Fact]
    public void Ein_deutlicher_vorsprung_entscheidet()
    {
        var importer = new ConfigurationImporter(
            [new FixedProvider("alpha", 0.9), new FixedProvider("beta", 0.4)],
            ImportWorld.Allowing);

        importer.Detect("{}").Provider.Should().Be("alpha");
    }

    /// <summary>
    /// Ein schwach erkanntes Format wird verarbeitet <b>und</b> als geraten gemeldet — beides, weil
    /// nur eines davon entweder unehrlich oder unbrauchbar wäre.
    /// </summary>
    [Fact]
    public void Eine_schwache_erkennung_wird_als_solche_gemeldet()
    {
        var document = """
        {
          "servers": { "lokal": { "command": "/usr/local/bin/server" } }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.Source.Confidence.Should().BeLessThan(ConfigurationImporter.WeakRecognition);
        plan.Candidates.Should().ContainSingle();
        plan.Findings.Should().Contain(f =>
            f.Code == ImportReason.UnknownFormat && f.Severity == ImportSeverity.Warning);
        plan.CanApply.Should().BeTrue("eine schwache Erkennung ist eine Warnung, keine Absage");
    }

    [Fact]
    public void Ein_importer_ohne_parser_wird_nicht_gebaut()
        => FluentActions.Invoking(() => new ConfigurationImporter([], ImportWorld.Allowing))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Zwei_parser_mit_demselben_namen_werden_nicht_gebaut()
        => FluentActions.Invoking(() => new ConfigurationImporter(
                [new FixedProvider("mcp", 0.5), new FixedProvider("mcp", 0.9)], ImportWorld.Allowing))
            .Should().Throw<ArgumentException>();

    // ── Kaputte Dokumente ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kein json")]
    [InlineData("{\"mcpServers\": {")]
    [InlineData("{\"mcpServers\": {\"a\": {\"command\": \"x\"},}}}}")]
    public void Kaputtes_json_ergibt_genau_einen_fehler_und_keinen_kandidaten(string document)
    {
        var plan = ImportWorld.Permissive().Plan(document);

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
        plan.Findings.Should().ContainSingle().Which.Code.Should().Be(ImportReason.NotJson);
    }

    [Fact]
    public void Kommentare_und_nachlaufende_kommata_sind_kein_fehler()
    {
        var document = """
        {
          // Von Hand gepflegt.
          "mcpServers": {
            "lokal": { "command": "/usr/local/bin/server", },
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.AllCodes().Should().NotContain(ImportReason.NotJson);
        plan.Candidates.Should().ContainSingle();
    }

    [Fact]
    public void Eine_liste_auf_oberster_ebene_ist_kein_serverdokument()
    {
        var plan = ImportWorld.Permissive().Plan("""[{"command":"x"}]""");

        plan.CanApply.Should().BeFalse();
        plan.AllCodes().Should().Contain(ImportReason.UnknownFormat);
    }

    // ── Unbekannte Felder ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_unbekanntes_feld_verschwindet_nicht_still()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", extra: "\"zauberei\": true"));

        plan.AllFindings().Should().Contain(f =>
            f.Code == ImportReason.UnknownField && f.Summary.Contains("zauberei"));
    }

    [Fact]
    public void Ein_clientexklusives_feld_wird_als_solches_benannt()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", extra: "\"autoApprove\": [\"alles\"]"));

        plan.AllFindings().Should().Contain(f =>
            f.Code == ImportReason.ClientOnlyField && f.Summary.Contains("autoApprove"));
    }

    [Fact]
    public void Ein_unbekanntes_feld_auf_oberster_ebene_wird_gemeldet()
    {
        var document = """
        {
          "globalShortcut": "Alt+M",
          "mcpServers": { "s": { "command": "/usr/bin/server" } }
        }
        """;

        ImportWorld.Permissive().Plan(document).Findings.Should().Contain(f =>
            f.Code == ImportReason.UnknownField && f.Summary.Contains("globalShortcut"));
    }

    [Fact]
    public void Ein_eintrag_mit_command_und_url_wird_nicht_aufgeloest()
    {
        var document = """
        {
          "mcpServers": {
            "zwitter": { "command": "/usr/bin/server", "url": "https://example.test/mcp" }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void Ein_eintrag_ohne_command_und_ohne_url_ist_ein_fehler()
    {
        var plan = ImportWorld.Permissive().Plan("""{"mcpServers":{"leer":{"env":{}}}}""");

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
    }

    // ── Doppelte Server und Kollisionen ───────────────────────────────────────────────────────

    [Fact]
    public void Ein_zweimal_genannter_server_wird_nicht_stillschweigend_entschieden()
    {
        var document = """
        {
          "mcpServers": {
            "github": { "command": "/usr/bin/erster" },
            "github": { "command": "/usr/bin/zweiter" }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.AllCodes().Should().Contain(ImportReason.DuplicateServer);
        plan.CanApply.Should().BeFalse();
        plan.Candidates.Should().ContainSingle("der zweite Eintrag wird gemeldet, nicht uebernommen");
    }

    [Fact]
    public void Zwei_namen_die_auf_denselben_slug_fallen_sind_eine_kollision()
    {
        var document = """
        {
          "mcpServers": {
            "My Server": { "command": "/usr/bin/a" },
            "my.server": { "command": "/usr/bin/b" }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.Findings.Should().Contain(f => f.Code == ImportReason.NameCollision);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void Verschiedene_namen_ohne_kollision_gehen_durch()
    {
        var document = """
        {
          "mcpServers": {
            "alpha": { "command": "/usr/bin/a" },
            "beta": { "command": "/usr/bin/b" }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.AllCodes().Should().NotContain(ImportReason.NameCollision);
        plan.Candidates.Should().HaveCount(2);
        plan.CanApply.Should().BeTrue();
    }

    // ── Ein Plan ist keine Änderung ───────────────────────────────────────────────────────────

    /// <summary>
    /// Die DoD dieses Pakets in einer Zusicherung: Was aus dem Import kommt, ist abgeschaltet. Ein
    /// Plan, dessen Server bereits eingeschaltet wären, hätte den Unterschied zwischen „analysiert"
    /// und „angelegt" nur noch im Namen.
    /// </summary>
    [Fact]
    public void Kein_kandidat_ist_eingeschaltet()
    {
        var document = """
        {
          "mcpServers": {
            "a": { "command": "/usr/bin/a", "enabled": true },
            "b": { "type": "http", "url": "https://example.test/mcp" }
          }
        }
        """;

        ImportWorld.Permissive().Plan(document).Candidates
            .Should().OnlyContain(candidate => !candidate.Config.Enabled);
    }

    /// <summary>Auch der Parser allein — ohne den zentralen Weg — liefert nichts Eingeschaltetes.</summary>
    [Fact]
    public void Auch_der_parser_allein_liefert_nichts_eingeschaltetes()
        => new GenericMcpImportProvider()
            .Plan(ImportWorld.Stdio("a", "/usr/bin/a"), null).Candidates
            .Should().OnlyContain(candidate => !candidate.Config.Enabled);

    /// <summary>
    /// Ein Plan lässt sich prüfen, ohne dass etwas gespeichert wird — er ist ein Wert, kein Vorgang.
    /// Zweimal derselbe Aufruf liefert dasselbe Ergebnis.
    /// </summary>
    [Fact]
    public void Derselbe_plan_zweimal_ist_derselbe_plan()
    {
        var document = ImportWorld.Stdio("github", "npx", "[\"-y\",\"@scope/server\"]");
        var importer = ImportWorld.Permissive();

        var first = importer.Plan(document, "/home/u/.config/mcp.json");
        var second = importer.Plan(document, "/home/u/.config/mcp.json");

        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void Der_quellpfad_steht_im_plan()
        => ImportWorld.Permissive()
            .Plan(ImportWorld.Stdio("a", "/usr/bin/a"), "/home/u/.config/mcp.json")
            .Source.OriginPath.Should().Be("/home/u/.config/mcp.json");

    /// <summary>
    /// <see cref="ImportSeverity.Risk"/> blockiert nicht — das ist die Aussage des Vertrags, und sie
    /// gilt auch dann, wenn ein Server gleich mehrere Risiken auf einmal trägt.
    /// </summary>
    [Fact]
    public void Risiken_blockieren_nicht_sondern_verlangen_eine_bestaetigung()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("github", "npx", "[\"-y\",\"@scope/server\"]"));

        plan.CanApply.Should().BeTrue();
        plan.RequiresConfirmation.Should().NotBeEmpty();
        plan.RequiresConfirmation.Should().OnlyContain(f => f.Severity == ImportSeverity.Risk);
    }

    /// <summary>Jeder Befund nennt seinen Ort — sonst ist er über dreißig Servern eine Suchaufgabe.</summary>
    [Fact]
    public void Befunde_zu_einem_server_tragen_den_ort()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("github", "npx", "[\"-y\",\"@scope/server\"]"));

        plan.Candidates.Single().Findings.Should().OnlyContain(f =>
            f.Path != null && f.Path.StartsWith("mcpServers/github", StringComparison.Ordinal));
    }

    /// <summary>Ein Parser, der sich verrechnet, darf den Plan nicht kippen.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    [InlineData(17.0)]
    public void Eine_unsinnige_sicherheit_wird_zurechtgestutzt(double confidence)
    {
        var importer = new ConfigurationImporter([new FixedProvider("x", confidence)], ImportWorld.Allowing);

        importer.Detect("{}").Confidence.Should().BeInRange(0, 1);
    }

    /// <summary>Ein Parser für die Erkennungstests, der immer dieselbe Zahl sagt.</summary>
    private sealed class FixedProvider(string name, double confidence) : IImportProvider
    {
        public string Name => name;

        public double Recognize(string document) => confidence;

        public ImportPlan Plan(string document, string? originPath)
            => new(new ImportSource(name, null, confidence, originPath), [], []);
    }
}
