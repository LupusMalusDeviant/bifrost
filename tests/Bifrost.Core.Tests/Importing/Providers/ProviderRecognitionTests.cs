using System.Globalization;
using System.Reflection;

using AwesomeAssertions;

using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing.Providers;

/// <summary>
/// Die Erkennung über alle Parser hinweg — die Frage, die dieses Paket entscheidet: <b>Wer bekommt
/// ein Dokument, und wann bekommt es niemand?</b>
/// <para>
/// Der Importer meldet einen Fehler, wenn der Abstand zwischen dem besten und dem zweitbesten
/// Treffer unter <see cref="ConfigurationImporter.AmbiguityMargin"/> liegt. Ein Clientparser, der
/// sich meldet, muss den generischen deshalb <em>deutlich</em> überstimmen — sonst hätte ein neuer
/// Parser die vorhandenen unbrauchbar gemacht, und zwar nicht mit einer falschen Antwort, sondern
/// mit gar keiner.
/// </para>
/// </summary>
public sealed class ProviderRecognitionTests
{
    private static ConfigurationImporter Importer()
        => ConfigurationImporter.CreateDefault(ImportWorld.Allowing);

    // ── Die Erkennungswerte selbst ────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Die Regel als Test über die Zahlen, nicht über Beispiele.</b> Jeder öffentlich erklärte
    /// Erkennungswert eines Clientparsers liegt mindestens einen Gleichstandsabstand über dem
    /// stärksten Wert des generischen Parsers. Ein neuer Wert unterhalb dieser Schwelle fällt hier
    /// auf — und nicht erst an einem Dokument, das zufällig niemand als Beispiel hat.
    /// </summary>
    [Fact]
    public void Jeder_erkennungswert_ueberstimmt_den_generischen_parser_deutlich()
    {
        var schwelle = GenericMcpImportProvider.McpServersConfidence
            + ConfigurationImporter.AmbiguityMargin;

        var zuSchwach = Confidences()
            .Where(entry => entry.Value < schwelle - 1e-9)
            .Select(entry => $"{entry.Owner}.{entry.Name} = "
                + entry.Value.ToString("0.00", CultureInfo.InvariantCulture))
            .ToList();

        zuSchwach.Should().BeEmpty(
            "ein Wert unter {0} faellt gegenueber dem generischen Parser in den Gleichstandsbereich; "
            + "der Importer meldet dann einen Fehler, statt zu waehlen. Zu schwach: {1}",
            schwelle.ToString("0.00", CultureInfo.InvariantCulture),
            string.Join(", ", zuSchwach));
    }

    /// <summary>Findet die Suche keine Werte, prüft der Test darüber nichts.</summary>
    [Fact]
    public void Die_suche_findet_ueberhaupt_erkennungswerte()
        => Confidences().Should().HaveCountGreaterThanOrEqualTo(7);

    // ── Wer bekommt welches Dokument? ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude/01-mcp-json-projekt.json", ClaudeImportProvider.ProviderName)]
    [InlineData("claude/03-benutzerdatei-projects.json", ClaudeImportProvider.ProviderName)]
    [InlineData("claude/90-ersetzte-adresse.json", ClaudeImportProvider.ProviderName)]
    [InlineData("claude/91-widerspruch.json", ClaudeImportProvider.ProviderName)]
    [InlineData("cursor/01-lokal-stdio.json", CursorImportProvider.ProviderName)]
    [InlineData("cursor/02-remote-http.json", CursorImportProvider.ProviderName)]
    [InlineData("cursor/03-auth-und-envfile.json", CursorImportProvider.ProviderName)]
    [InlineData("cursor/91-doppelt.json", CursorImportProvider.ProviderName)]
    [InlineData("vscode/01-mcp-json-inputs.json", VsCodeImportProvider.ProviderName)]
    [InlineData("vscode/02-http-und-sandbox.json", VsCodeImportProvider.ProviderName)]
    [InlineData("vscode/03-settings-json.json", VsCodeImportProvider.ProviderName)]
    [InlineData("codex/01-lokal.json", CodexImportProvider.ProviderName)]
    [InlineData("codex/02-remote.json", CodexImportProvider.ProviderName)]
    [InlineData("codex/03-zeiten-und-schalter.json", CodexImportProvider.ProviderName)]
    [InlineData("codex/90-ohne-transport.json", CodexImportProvider.ProviderName)]
    public void Jede_beispielkonfiguration_geht_an_ihren_parser(string reference, string expected)
        => Importer().Detect(ProviderWorld.Load(reference)).Provider.Should().Be(expected);

    /// <summary>
    /// <b>Die ausdrücklich benannte Grenze, hier über den ganzen Weg.</b> Eine Claude-Desktop-Datei
    /// ohne Claude-eigenen Schlüssel geht an den generischen Parser. Das ist kein Versäumnis: Sie
    /// ist zeichengleich mit dem, was Cursor und jeder generische Client schreiben, und „aus Claude"
    /// wäre eine Behauptung, die das Dokument nicht hergibt.
    /// </summary>
    [Fact]
    public void Eine_zeichengleiche_datei_bleibt_beim_generischen_parser()
        => Importer().Detect(ProviderWorld.Fixture("claude", "02-desktop-config.json"))
            .Provider.Should().Be(GenericMcpImportProvider.ProviderName);

    [Fact]
    public void Keine_beispielkonfiguration_landet_im_gleichstand()
    {
        foreach (var reference in ProviderWorld.All())
        {
            var document = ProviderWorld.Load(reference);
            if (document.Contains("args\": [\"server.py\"", StringComparison.Ordinal))
            {
                // Die kaputte Datei ist kein Erkennungsfall; sie scheitert vorher am JSON.
                continue;
            }

            Importer().Detect(document).Provider.Should().NotBe(
                ConfigurationImporter.AmbiguousProvider, "Datei {0}", reference);
        }
    }

    /// <summary>
    /// <b>Der Beleg, dass der Gleichstand wirklich greift</b> und nicht bloß nie eintritt: Ein
    /// Dokument, das die Merkmale zweier Clients zugleich trägt, wird <em>nicht</em> zugeordnet. Ein
    /// geratenes Format verschiebt den Fehler nur in die Abbildung, wo er wie ein Datenfehler
    /// aussieht.
    /// </summary>
    [Fact]
    public void Ein_dokument_mit_zwei_dialekten_wird_nicht_geraten()
    {
        var mischform = """
        {
          "mcpServers": {
            "a": { "command": "/usr/bin/a", "env": { "P": "${HEIM:-/tmp}" } },
            "b": { "command": "/usr/bin/b", "env": { "Q": "${env:HEIM}" } }
          }
        }
        """;

        var plan = Importer().Plan(mischform);

        plan.Source.Provider.Should().Be(ConfigurationImporter.AmbiguousProvider);
        plan.CanApply.Should().BeFalse();
        plan.Findings.Should().ContainSingle().Which.Summary
            .Should().Contain(ClaudeImportProvider.ProviderName)
            .And.Contain(CursorImportProvider.ProviderName);
    }

    // ── Die Registrierung ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Der_vorgabe_importer_kennt_alle_fuenf_formate()
        => Importer().Providers.Select(p => p.Name).Should().BeEquivalentTo(
            [
                GenericMcpImportProvider.ProviderName,
                ClaudeImportProvider.ProviderName,
                CursorImportProvider.ProviderName,
                VsCodeImportProvider.ProviderName,
                CodexImportProvider.ProviderName,
            ]);

    // ── Der ganze Weg ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Die DoD von WP4.1 über alle Beispielkonfigurationen:</b> Was aus dem Import kommt, ist
    /// abgeschaltet — über den zentralen Weg wie über jeden Parser einzeln.
    /// </summary>
    [Fact]
    public void Kein_kandidat_irgendeiner_beispielkonfiguration_ist_eingeschaltet()
    {
        foreach (var reference in ProviderWorld.All())
        {
            Importer().Plan(ProviderWorld.Load(reference), reference).Candidates
                .Should().OnlyContain(candidate => !candidate.Config.Enabled, "Datei {0}", reference);
        }
    }

    /// <summary>
    /// Der zentrale Weg ordnet Zugangsdaten in Kopfzeilen und Umgebungsvariablen ein — die Parser
    /// bauen diese Erkennung nicht nach, sie überlassen sie der vorhandenen. Der Beleg: Ein
    /// Klartext-Schlüssel in einer Cursor-Datei taucht als Zugangsdatum auf.
    /// </summary>
    [Fact]
    public void Kopfzeilen_geheimnisse_werden_auf_dem_zentralen_weg_eingeordnet()
    {
        var plan = Importer().Plan(ProviderWorld.Fixture("cursor", "02-remote-http.json"));

        plan.Secrets().Should().Contain(s =>
            s.Location.Contains("X-Api-Key") && s.ValuePresent);
        plan.Codes().Should().Contain(ImportReason.PlaintextSecret);

        // Und die Ersetzung daneben bleibt eine Ersetzung: Der Parser meldet sie als solche,
        // aufgeloest wird sie nicht. (Die zentrale Erkennung haelt 'Bearer ${env:…}' fuer einen
        // Klartextwert, weil ihre Maskenform den GANZEN Wert betrifft — beobachtet, nicht
        // geaendert: ein Header, der zu viel meldet, ist der richtige Irrtum.)
        plan.Codes().Should().Contain(ImportReason.Lossy);
        plan.Everything().Should().Contain(f => f.Summary.Contains("${env:"));
    }

    /// <summary>
    /// Ein Plan ist ein Wert, kein Vorgang: Zweimal derselbe Aufruf liefert dasselbe Ergebnis — auch
    /// über die neuen Parser.
    /// </summary>
    [Fact]
    public void Derselbe_plan_zweimal_ist_derselbe_plan()
    {
        foreach (var reference in ProviderWorld.All())
        {
            var document = ProviderWorld.Load(reference);
            var importer = Importer();

            importer.Plan(document, reference).Should().BeEquivalentTo(
                importer.Plan(document, reference), "Datei {0}", reference);
        }
    }

    /// <summary>Die öffentlich erklärten Erkennungswerte der vier Clientparser.</summary>
    private static IReadOnlyList<(string Owner, string Name, double Value)> Confidences()
        => [.. new[]
            {
                typeof(ClaudeImportProvider), typeof(CursorImportProvider),
                typeof(VsCodeImportProvider), typeof(CodexImportProvider),
            }
            .SelectMany(type => type
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(field => field.IsLiteral && field.FieldType == typeof(double))
                .Select(field => (type.Name, field.Name, (double)field.GetRawConstantValue()!)))];
}
