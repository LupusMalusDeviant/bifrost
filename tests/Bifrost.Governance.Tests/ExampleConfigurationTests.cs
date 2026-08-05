using System.Text.RegularExpressions;

using AwesomeAssertions;

using Xunit;

namespace Bifrost.Governance.Tests;

/// <summary>
/// <b>Die Beispielkonfiguration ist ausgeliefertes Produkt, kein Beiwerk.</b>
/// <para>
/// `.env.example` ist die Datei, die jeder Betreiber im ersten Schritt kopiert und dann bearbeitet
/// — der Standardweg beginnt mit <c>cp .env.example .env</c>. Was dort steht, wird geglaubt und
/// nicht gegengeprüft.
/// </para>
/// <para>
/// <b>Warum es diese Tests gibt</b> (BFR-F-0050, BFR-F-0051, Pilot 1): Die Datei behauptete, der
/// Gateway könne das Passwort des Key-Rings „heute NICHT aus einer Datei lesen" — dabei liest er
/// jede <c>BIFROST_*</c>-Einstellung auch aus <c>&lt;NAME&gt;_FILE</c> (FR-P048), die Compose-Datei
/// zeigt es im Secret-Block, und <c>bifrost doctor</c> empfiehlt es wörtlich. Die falsche Zeile
/// drängte damit zum Klartextpasswort in <c>.env</c> — also genau dorthin, wo es in
/// <c>docker inspect</c> auftaucht. Und die eine Entscheidung, die das Produkt ausdrücklich
/// verlangt, fehlte ganz.
/// </para>
/// <para>
/// Beides sind Textfehler in einer Datei, die niemand testet. Deshalb hier.
/// </para>
/// </summary>
public sealed class ExampleConfigurationTests
{
    private const string ExampleFile = ".env.example";

    /// <summary>
    /// Die Einstellung, deren Fehlen der Start anmahnt und <c>bifrost doctor</c> als
    /// <c>BFR-KEY-0002</c> meldet. Als Zeichenkette und nicht als Verweis auf
    /// <c>KeyRingChecks.ProtectionSetting</c>: Dieses Projekt hält bewusst keine Referenz auf die
    /// Serverwelt — es prüft sie von außen, so wie ein Betreiber sie sieht.
    /// </summary>
    private const string ProtectionSetting = "BIFROST_KEYRING_PROTECTION";

    private readonly ITestOutputHelper _output;

    public ExampleConfigurationTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>Wo ein Geheimnis eingetragen werden kann, steht auch der Weg über eine Datei.</b>
    /// „Nicht gesetzt" ist bei einem Secret kein neutraler Zustand: Wer nur die Wertform sieht,
    /// nimmt die Wertform.
    /// </summary>
    [Fact]
    public void Every_secret_setting_in_the_example_shows_its_file_variant()
    {
        var text = ReadExample();
        var names = Settings(text);

        // Was ein Geheimnis traegt, erkennt man am Namen — dieselbe Regel, nach der die
        // Maskierung im Produkt arbeitet.
        var secrets = names
            .Where(n => n.EndsWith("_PASSWORD", StringComparison.Ordinal)
                || n.EndsWith("_SECRET", StringComparison.Ordinal)
                || n.EndsWith("_TOKEN", StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"Einstellungen in {ExampleFile}: {names.Count} · davon geheimnistragend: "
            + string.Join(", ", secrets));

        secrets.Should().NotBeEmpty(
            "sonst prueft dieser Test nichts — und das faellt nicht auf");

        var withoutFileVariant = secrets
            .Where(n => !names.Contains(n + "_FILE", StringComparer.Ordinal))
            .ToList();

        withoutFileVariant.Should().BeEmpty(
            "der Gateway liest jede BIFROST_*-Einstellung auch aus <NAME>_FILE (FR-P048). Steht "
            + "nur die Wertform in der Beispieldatei, landet das Geheimnis im Klartext in .env und "
            + "damit in 'docker inspect':\n"
            + string.Join("\n", withoutFileVariant));
    }

    /// <summary>
    /// <b>Die eine Entscheidung, die das Produkt verlangt, steht in der Datei, in der man
    /// Entscheidungen einträgt.</b> Sie nur in der Dokumentation zu führen heißt: Der Start warnt,
    /// und der Betreiber sucht die Zeile in einer Datei, in der sie nicht vorkommt.
    /// </summary>
    [Fact]
    public void The_example_names_the_key_ring_decision_the_product_demands()
    {
        var text = ReadExample();

        Settings(text).Should().Contain(
            ProtectionSetting,
            "ohne diese Angabe warnt der Start und 'bifrost doctor' meldet BFR-KEY-0002 — die "
            + "Beispielkonfiguration darf die verlangte Wahl nicht verschweigen");

        // Die drei zulaessigen Werte gehoeren dazu. Eine Variable ohne ihre Werte ist eine Frage
        // ohne Antwortmoeglichkeiten.
        foreach (var mode in new[] { "certificate", "file-secret", "none" })
        {
            text.Should().Contain(mode);
        }
    }

    private static string ReadExample()
    {
        var path = Path.Combine(RepoTree.Root, ExampleFile);
        File.Exists(path).Should().BeTrue($"{ExampleFile} ist der Standardweg der Installation");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Alle <c>BIFROST_*</c>-Zuweisungen — auch die auskommentierten. Eine auskommentierte Zeile ist
    /// in dieser Datei die übliche Form: Sie zeigt den Namen und überlässt den Wert dem Betreiber.
    /// </summary>
    private static HashSet<string> Settings(string text)
        => new(
            Regex.Matches(text, @"^#?\s*(BIFROST_[A-Z0-9_]+)\s*=", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);
}
