using AwesomeAssertions;

using Bifrost.Abstractions.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// Zugangsdaten werden <b>markiert</b>, nicht erraten und nicht weitergereicht.
/// <para>
/// Die härteste Zusicherung dieses Pakets steht am Ende der Datei: Kein Befund und kein
/// Secret-Eintrag trägt den Wert, um den es geht. Ein maskiertes Zugangsdatum ist ein Zugangsdatum
/// mit weniger Zeichen — wer es in einen Bericht schreibt, hat den Rest bereits verraten.
/// </para>
/// </summary>
public sealed class ImportSecretTests
{
    /// <summary>Ein Wert, der in keinem Bericht auftauchen darf. Er ist erfunden.</summary>
    private const string Klartext = "ghp_hV9zQ2mKp7Rt4Xw1Nb8Ls3Ye6Ud0Ac5Zi2q";

    [Fact]
    public void Ein_token_in_der_umgebung_wird_markiert()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", extra: $"\"env\": {{\"GITHUB_TOKEN\": \"{Klartext}\"}}"));

        var secret = plan.Candidates.Single().Secrets.Should().ContainSingle().Subject;
        secret.Location.Should().Be("Umgebungsvariable 'GITHUB_TOKEN'");
        secret.ValuePresent.Should().BeTrue();
        secret.Looked.Should().NotBeNullOrWhiteSpace("ohne Begruendung ist der Befund nicht beantwortbar");
        plan.AllCodes().Should().Contain(ImportReason.PlaintextSecret);
    }

    [Fact]
    public void Eine_harmlose_variable_wird_nicht_markiert()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", extra: "\"env\": {\"NODE_ENV\": \"production\"}"));

        plan.Candidates.Single().Secrets.Should().BeEmpty();
        plan.AllCodes().Should().NotContain(ImportReason.PlaintextSecret);
    }

    /// <summary>
    /// Der Pflichttest: Ein maskierter Wert wird als maskiert gemeldet — und nicht rekonstruiert.
    /// </summary>
    [Theory]
    [InlineData("***")]
    [InlineData("ghp_****************")]
    [InlineData("<dein-token-hier>")]
    [InlineData("${GITHUB_TOKEN}")]
    [InlineData("%GITHUB_TOKEN%")]
    [InlineData("REDACTED")]
    [InlineData("YOUR_TOKEN_HERE")]
    [InlineData("xxxxxxxxxxxx")]
    public void Ein_maskierter_wert_wird_als_maskiert_gemeldet(string maskiert)
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", extra: $"\"env\": {{\"GITHUB_TOKEN\": \"{maskiert}\"}}"));

        var secret = plan.Candidates.Single().Secrets.Should().ContainSingle().Subject;
        secret.ValuePresent.Should().BeFalse("aus einer Maske wird nichts rekonstruiert");

        plan.AllCodes().Should().Contain(ImportReason.MaskedValue);
        plan.AllCodes().Should().NotContain(
            ImportReason.PlaintextSecret,
            "ein maskierter Wert ist kein Klartext — die beiden Faelle verlangen verschiedene Handlungen");
    }

    /// <summary>
    /// Der maskierte Wert bleibt stehen, wie er dastand. Er wird weder ergänzt noch entfernt: Ein
    /// leergeräumtes Feld sähe aus wie ein vergessenes.
    /// </summary>
    [Fact]
    public void Der_maskierte_wert_wird_nicht_ersetzt()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", extra: "\"env\": {\"GITHUB_TOKEN\": \"${GITHUB_TOKEN}\"}"));

        plan.Candidates.Single().Config.Stdio!.EnvironmentVariables!["GITHUB_TOKEN"]
            .Should().Be("${GITHUB_TOKEN}");
    }

    [Fact]
    public void Ein_autorisierungsheader_wird_markiert()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Http(
            "s", "https://api.example.com/mcp", $"\"headers\": {{\"Authorization\": \"Bearer {Klartext}\"}}"));

        plan.Candidates.Single().Secrets.Should()
            .ContainSingle().Which.Location.Should().Be("HTTP-Header 'Authorization'");
        plan.AllCodes().Should().Contain(ImportReason.PlaintextSecret);
    }

    /// <summary>
    /// <b>Das Falschpositiv aus WP4.3.</b> Die Verweisform machte bis dahin nur dann einen
    /// maskierten Wert aus, wenn sie den <em>ganzen</em> Wert ausmachte — und
    /// <c>Bearer ${env:TOKEN}</c>, also die Form, in der ein Autorisierungsheader tatsächlich
    /// geschrieben wird, galt deshalb als Klartextgeheimnis. Der Irrtum ging in die sichere
    /// Richtung, war aber ein Falschpositiv: Falschpositive in einer Liste, die ein Mensch
    /// durchgehen soll, kosten genau die Aufmerksamkeit, die die echten Funde bräuchten.
    /// </summary>
    [Theory]
    [InlineData("Bearer ${env:TOKEN}")]
    [InlineData("Bearer ${GITHUB_TOKEN}")]
    [InlineData("Basic %CREDENTIALS%")]
    [InlineData("Token $API_TOKEN")]
    public void Eine_verweisform_im_header_ist_keine_klartextmeldung(string wert)
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Http(
            "s", "https://api.example.com/mcp", $"\"headers\": {{\"Authorization\": \"{wert}\"}}"));

        plan.Candidates.Single().Secrets.Should().ContainSingle()
            .Which.ValuePresent.Should().BeFalse("die Quelle traegt keinen benutzbaren Wert");

        plan.AllCodes().Should().Contain(ImportReason.MaskedValue);
        plan.AllCodes().Should().NotContain(
            ImportReason.PlaintextSecret,
            "hier steht ein Verweis auf ein Zugangsdatum, nicht das Zugangsdatum");
    }

    /// <summary>
    /// Die Grenze in die andere Richtung — und sie ist die wichtigere. Steht neben der Verweisform
    /// noch etwas, das ein Wert sein könnte, bleibt es ein Klartextfund. Ein halbes Geheimnis als
    /// „maskiert" abzustempeln wäre der Irrtum in die teure Richtung.
    /// </summary>
    [Theory]
    [InlineData("Bearer ghp_hV9zQ2mKp7Rt4Xw1Nb8Ls3Ye6Ud0${SUFFIX}")]
    [InlineData("${PREFIX}hV9zQ2mKp7Rt4Xw1Nb8Ls3Ye6Ud0Ac5Zi2q")]
    public void Ein_halbes_geheimnis_bleibt_ein_klartextfund(string wert)
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Http(
            "s", "https://api.example.com/mcp", $"\"headers\": {{\"Authorization\": \"{wert}\"}}"));

        plan.Candidates.Single().Secrets.Should().ContainSingle()
            .Which.ValuePresent.Should().BeTrue();
        plan.AllCodes().Should().Contain(ImportReason.PlaintextSecret);
    }

    [Fact]
    public void Ein_gewoehnlicher_header_wird_nicht_markiert()
        => ImportWorld.Permissive().Plan(ImportWorld.Http(
                "s", "https://api.example.com/mcp",
                "\"headers\": {\"Content-Type\": \"application/json\", \"Accept\": \"text/event-stream\"}"))
            .Candidates.Single().Secrets.Should().BeEmpty();

    /// <summary>
    /// Auch ohne sprechenden Namen: Die Form eines Zugangsdatums genügt. Der Name eines Feldes ist
    /// ein Hinweis, keine Bedingung.
    /// </summary>
    [Fact]
    public void Ein_zugangsdatum_ohne_sprechenden_namen_wird_an_der_form_erkannt()
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio(
                "s", "/usr/bin/server", extra: $"\"env\": {{\"WERT\": \"{Klartext}\"}}"))
            .Candidates.Single().Secrets.Should().ContainSingle();

    [Fact]
    public void Ein_token_auf_der_kommandozeile_wird_markiert()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", $"[\"--api-key={Klartext}\"]"));

        plan.Candidates.Single().Secrets.Should()
            .ContainSingle().Which.Location.Should().Contain("Position 0");
    }

    [Fact]
    public void Ein_gewoehnliches_argument_wird_nicht_markiert()
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio(
                "s", "/usr/bin/node", "[\"/opt/mcp/server.js\",\"--port\",\"8080\"]"))
            .Candidates.Single().Secrets.Should().BeEmpty();

    // ── Die harte Zusicherung ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Kein Wert verlässt die Analyse als Text.</b> Geprüft wird über den ganzen Plan: jeder
    /// Befund mit Zusammenfassung, Ort und Handlungsempfehlung, und jeder Secret-Eintrag.
    /// <para>
    /// Ausgenommen ist ausdrücklich <c>ImportCandidate.Config</c>: Dort <em>muss</em> der Wert
    /// stehen, sonst wäre der Plan beim Anwenden unbrauchbar. Er ist ein Wert im Arbeitsspeicher
    /// und wird nicht gespeichert; ihn von der Vorschau zu trennen, ist Aufgabe der API (WP4.3).
    /// </para>
    /// </summary>
    [Fact]
    public void Kein_befund_und_kein_secret_traegt_den_wert()
    {
        var document = $$"""
        {
          "mcpServers": {
            "stdio": {
              "command": "/usr/bin/server",
              "args": ["--api-key={{Klartext}}"],
              "env": { "GITHUB_TOKEN": "{{Klartext}}", "SLACK_TOKEN": "xoxb-{{Klartext}}" }
            },
            "http": {
              "type": "http",
              "url": "https://api.example.com/mcp",
              "headers": { "Authorization": "Bearer {{Klartext}}", "X-Api-Key": "{{Klartext}}" }
            }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        var berichtet = plan.AllFindings()
            .SelectMany(f => new[] { f.Code, f.Summary, f.Path, f.Remediation })
            .Concat(plan.Candidates.SelectMany(c => c.Secrets)
                .SelectMany(s => new[] { s.Location, s.Looked }))
            .Where(text => text is not null)
            .ToList();

        berichtet.Should().NotBeEmpty("sonst prueft dieser Test nichts");
        berichtet.Should().OnlyContain(text => !text!.Contains(Klartext, StringComparison.Ordinal));

        // Und auch kein nennenswertes Bruchstueck davon: Ein Praefix ist bereits eine Aussage ueber
        // den Wert (dieselbe Regel wie im ConfigurationSecretScrubber).
        foreach (var laenge in new[] { 8, 12, 16 })
        {
            var bruchstueck = Klartext[..laenge];
            berichtet.Should().OnlyContain(text => !text!.Contains(bruchstueck, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Vier Zugangsdaten in einem Dokument werden auch als vier gemeldet — jedes an seinem Ort. Ein
    /// zusammengefasster Befund „irgendwo stehen Geheimnisse" wäre keine Fundstelle.
    /// </summary>
    [Fact]
    public void Jedes_zugangsdatum_bekommt_seinen_eigenen_ort()
    {
        var document = $$"""
        {
          "mcpServers": {
            "stdio": {
              "command": "/usr/bin/server",
              "env": { "GITHUB_TOKEN": "{{Klartext}}", "SLACK_TOKEN": "xoxb-{{Klartext}}" }
            }
          }
        }
        """;

        var secrets = ImportWorld.Permissive().Plan(document).Candidates.Single().Secrets;

        secrets.Should().HaveCount(2);
        secrets.Select(s => s.Location).Should().OnlyHaveUniqueItems();
    }
}
