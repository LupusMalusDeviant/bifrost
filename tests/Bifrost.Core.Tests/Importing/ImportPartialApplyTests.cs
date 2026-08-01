using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;
using Bifrost.Core.Tests.Importing.Providers;

using Xunit;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// Der Teilimport: Ein Eintrag, der für sich stimmt, bleibt anwendbar — auch wenn ein anderer in
/// derselben Datei kaputt ist.
/// <para>
/// <b>Warum das eine eigene Testdatei verdient.</b> Bis WP4.3 galt <c>CanApply</c> planweit: Ein
/// einziger kaputter Eintrag machte eine Datei mit dreißig Servern unanwendbar. Für den geführten
/// Erstaufbau (WP4.4) ist das die Einschränkung, an der die Sache scheitert — wer dreißig Server
/// mitbringt, hat mit ziemlicher Sicherheit einen darunter, der nicht mehr stimmt.
/// </para>
/// <para>
/// <b>Und die Gegenrichtung, die genauso wichtig ist:</b> Ein Befund über das <em>Dokument</em>
/// darf durch diese Lockerung nicht zu einem Befund über einen Eintrag verharmlost werden. Die
/// Tests unten prüfen beide Richtungen; die zweite ist die, die man vergisst.
/// </para>
/// </summary>
public sealed class ImportPartialApplyTests
{
    /// <summary>Zwei stimmige Server, einer ohne Transport — die zwei bleiben anwendbar.</summary>
    [Fact]
    public void Ein_kaputter_eintrag_haelt_die_uebrigen_nicht_auf()
    {
        var document = """
        {
          "mcpServers": {
            "gut-eins": { "command": "/usr/bin/eins" },
            "kaputt":   { "beschreibung": "hier fehlt command und url" },
            "gut-zwei": { "command": "/usr/bin/zwei" }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.CanApply.Should().BeTrue(
            "ein Eintrag, der fuer sich stimmt, bleibt anwendbar");
        plan.ApplicableCandidates.Select(c => c.SourceName).Should().BeEquivalentTo(
            ["gut-eins", "gut-zwei"]);

        // Der kaputte Eintrag wird nicht zum Kandidaten — und der Befund darueber steht trotzdem
        // im Plan. Ein Import, der ihn verschweigt, sieht vollstaendig aus und ist es nicht.
        plan.Findings.Should().Contain(f =>
            f.Severity == ImportSeverity.Error && f.Path == "mcpServers/kaputt");
    }

    /// <summary>
    /// Ein Server, den die Ausführungs-Policy ablehnt, nimmt nur sich selbst heraus. Der Fall ist
    /// der praktisch häufigste: eine frische Instanz (ADR-0025 E2) und eine Quelldatei voller
    /// nativer Kommandos.
    /// </summary>
    [Fact]
    public void Ein_kandidat_mit_eigenem_fehler_nimmt_nur_sich_selbst_heraus()
    {
        var document = """
        {
          "mcpServers": {
            "nativ":  { "command": "/usr/bin/nativ" },
            "remote": { "type": "http", "url": "https://api.example.test/mcp" }
          }
        }
        """;

        // Strict: native Ausfuehrung ist verboten, der lokale Server traegt deshalb einen Fehler.
        var plan = ImportWorld.Strict().Plan(document);

        plan.Candidates.Single(c => c.SourceName == "nativ").CanApply.Should().BeFalse();
        plan.ApplicableCandidates.Select(c => c.SourceName).Should().Equal("remote");
        plan.CanApply.Should().BeTrue();
    }

    /// <summary>
    /// Ein doppelter Servername sperrt genau diesen Namen. Der Befund entsteht beim Parser und
    /// findet über den Pfad zu dem Kandidaten zurück, den er meint.
    /// </summary>
    [Fact]
    public void Ein_doppelter_name_sperrt_nur_diesen_namen()
    {
        var document = """
        {
          "mcpServers": {
            "doppelt": { "command": "/usr/bin/a" },
            "doppelt": { "command": "/usr/bin/b" },
            "einmalig": { "command": "/usr/bin/c" }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.Codes().Should().Contain(ImportReason.DuplicateServer);
        plan.ApplicableCandidates.Select(c => c.SourceName).Should().Equal("einmalig");
        plan.BlockersFor(plan.Candidates.Single(c => c.SourceName == "doppelt"))
            .Should().Contain(f => f.Code == ImportReason.DuplicateServer);
    }

    // ── Die Gegenrichtung: planweite Befunde bleiben planweit ─────────────────────────────────

    /// <summary>
    /// Ein Fehler über das Dokument hält alles an, auch die stimmigen Einträge. Der Plan wird hier
    /// von Hand gebaut, weil kein Parser diese Lage erzeugt — genau deshalb muss der Vertrag sie
    /// selbst abfangen.
    /// </summary>
    [Fact]
    public void Ein_planweiter_fehler_haelt_auch_stimmige_kandidaten_an()
    {
        var plan = new ImportPlan(
            new ImportSource("mcp", null, 1.0),
            [Stimmig("gut")],
            [
                new ImportFinding(
                    ImportReason.UnknownFormat,
                    ImportSeverity.Error,
                    "Das Format ist mehrdeutig."),
            ]);

        plan.BlockingFindings.Should().ContainSingle();
        plan.CanApply.Should().BeFalse("ein Dokumentbefund betrifft jeden Eintrag");
        plan.ApplicableCandidates.Should().BeEmpty();
        plan.BlockedCandidates.Should().ContainSingle();
    }

    /// <summary>
    /// <b>Der Wächter gegen die Verharmlosung.</b> Ein Fehler ohne ausdrückliche Bereichsangabe gilt
    /// für das ganze Dokument — auch dann, wenn er einen Pfad trägt und damit aussieht, als ginge es
    /// nur um eine Stelle. Wer einen neuen Einzelbefund einführt, muss das hinschreiben; wer es
    /// vergisst, blockiert zu viel statt zu wenig.
    /// </summary>
    [Fact]
    public void Ein_fehler_ohne_bereichsangabe_gilt_fuer_das_ganze_dokument()
    {
        var finding = new ImportFinding(
            ImportReason.UnknownField, ImportSeverity.Error, "Irgendwas", "mcpServers/gut");

        finding.Scope.Should().Be(
            ImportFindingScope.Document,
            "die Vorgabe ist die vorsichtige: ein Befund gilt fuer alles, bis jemand das Gegenteil "
            + "hinschreibt");

        var plan = new ImportPlan(new ImportSource("mcp", null, 1.0), [Stimmig("gut")], [finding]);

        plan.CanApply.Should().BeFalse();
    }

    /// <summary>
    /// Derselbe Befund, ausdrücklich als Einzelbefund gekennzeichnet, nimmt nur seinen Eintrag
    /// heraus — und findet ihn über den Pfad.
    /// </summary>
    [Fact]
    public void Ein_eintragsfehler_findet_ueber_den_pfad_zu_seinem_kandidaten()
    {
        var plan = new ImportPlan(
            new ImportSource("mcp", null, 1.0),
            [Stimmig("gut"), Stimmig("schlecht")],
            [
                new ImportFinding(
                    ImportReason.UnknownField,
                    ImportSeverity.Error,
                    "Nur dieser Eintrag.",
                    "mcpServers/schlecht/url",
                    null,
                    ImportFindingScope.Entry),
            ]);

        plan.BlockingFindings.Should().BeEmpty();
        plan.ApplicableCandidates.Select(c => c.SourceName).Should().Equal("gut");
        plan.IsApplicable(plan.Candidates[1]).Should().BeFalse();
    }

    /// <summary>
    /// Der Pfadvergleich trennt an der Grenze und nicht am Zeichen: <c>mcpServers/gut-2</c> ist
    /// nicht <c>mcpServers/gut</c>. Ohne diesen Test wäre ein Präfixvergleich, der zwei benachbarte
    /// Namen zusammenwirft, unbemerkt richtig aussehend.
    /// </summary>
    [Fact]
    public void Ein_benachbarter_name_wird_nicht_mitgesperrt()
    {
        var plan = new ImportPlan(
            new ImportSource("mcp", null, 1.0),
            [Stimmig("gut"), Stimmig("gut-2")],
            [
                new ImportFinding(
                    ImportReason.UnknownField,
                    ImportSeverity.Error,
                    "Nur 'gut'.",
                    "mcpServers/gut",
                    null,
                    ImportFindingScope.Entry),
            ]);

        plan.ApplicableCandidates.Select(c => c.SourceName).Should().Equal("gut-2");
    }

    // ── Bestätigungen gelten der Auswahl ──────────────────────────────────────────────────────

    /// <summary>
    /// Wer drei von dreißig Servern übernimmt, bestätigt die Risiken dieser drei. Eine Bestätigung,
    /// die pauschal für alles gilt, wird zur Formalie — und eine Formalie liest niemand.
    /// </summary>
    [Fact]
    public void Bestaetigt_wird_was_angelegt_wird()
    {
        var document = """
        {
          "mcpServers": {
            "harmlos":  { "type": "http", "url": "https://api.example.test/mcp" },
            "riskant":  { "command": "npx", "args": ["-y", "@scope/server"] }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);
        var harmlos = plan.Candidates.Single(c => c.SourceName == "harmlos");
        var riskant = plan.Candidates.Single(c => c.SourceName == "riskant");

        plan.ConfirmationsFor([riskant]).Should().Contain(f =>
            f.Code == ImportReason.FetchesCodeAtStart);
        plan.ConfirmationsFor([harmlos]).Should().NotContain(f =>
            f.Code == ImportReason.FetchesCodeAtStart);
    }

    /// <summary>
    /// Ein planweiter Risikobefund gilt für jede Auswahl. VS Codes <c>sandbox</c> auf oberster Ebene
    /// ist genau so einer: Die Quelle hatte alle Server eingehegt, hier ist keiner mehr eingehegt.
    /// </summary>
    [Fact]
    public void Ein_planweites_risiko_gilt_fuer_jede_auswahl()
    {
        var plan = ImportWorld.Permissive().Plan(
            ProviderWorld.Fixture("vscode", "02-http-und-sandbox.json"));

        var planweit = plan.Findings.Where(f => f.Severity == ImportSeverity.Risk).ToList();
        planweit.Should().NotBeEmpty("die Fixture traegt den sandbox-Risikobefund");

        plan.ConfirmationsFor([]).Should().BeEquivalentTo(planweit);
    }

    private static ImportCandidate Stimmig(string name) => new(
        name,
        new UpstreamServerConfig(
            name,
            name,
            UpstreamTransportKind.Stdio,
            Enabled: false,
            Stdio: new StdioTransportOptions("/usr/bin/server", [])),
        [],
        [],
        $"mcpServers/{name}");
}
