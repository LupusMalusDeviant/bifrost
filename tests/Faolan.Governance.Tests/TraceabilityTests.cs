using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Faolan.Governance.Tests;

internal sealed record Requirement(
    string Id,
    string Level,
    string Title,
    string WorkPackage,
    string Status,
    string? Test,
    string? Artifact,
    string? Note = null);

internal sealed record TraceabilityDocument(
    int SchemaVersion,
    string Note,
    string Source,
    IReadOnlyList<Requirement> Requirements);

/// <summary>
/// M7/WP7.1: Die Rückverfolgung Anforderung → Paket → <b>Test</b> → Artefakt.
/// <para>
/// <b>Die Regel aus dem M7-Vertrag §1 ist der Kern dieser Datei:</b> Eine Zeile zählt nur, wenn der
/// genannte Test wirklich existiert. Eine Matrix, die auf einen gelöschten Test zeigt, ist
/// schlimmer als keine — sie sieht aus wie ein Nachweis.
/// </para>
/// </summary>
public sealed class TraceabilityTests
{
    private static readonly string[] Stati = ["erfuellt", "teilweise", "offen", "entfaellt"];
    private static readonly string[] Levels = ["must", "should", "could"];

    /// <summary>Der versionierte Stellvertreter des unversionierten Lastenhefts, neben der Matrix.</summary>
    private const string IdListFile = "lastenheft-anforderungen.txt";

    /// <summary>So steht eine Anforderung im Lastenheft: <c>- **FR-P001 (M): …</c>.</summary>
    private static readonly Regex RequirementHeading =
        new(@"^- \*\*((?:FR|NFR)-P\d+) \([MSC]\):", RegexOptions.Multiline);

    private static readonly Regex RequirementId = new(@"^(?:FR|NFR)-P\d+$");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ITestOutputHelper _output;

    public TraceabilityTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>Keine weggelassene Anforderung.</b> Was nicht abgedeckt ist, steht als <c>offen</c> drin
    /// und sieht offen aus — der Unterschied zwischen einer Rückverfolgung und einer Werbeaussage.
    /// <para>
    /// Geprüft wird gegen die versionierte Id-Liste des Lastenhefts, nicht gegen das Lastenheft
    /// selbst: Das liegt bewusst nicht im Repository (<c>.gitignore</c>, <c>docs/produktreife/</c>)
    /// und fehlt deshalb in jedem CI-Checkout. Ein Test, der eine Datei liest, die es dort nie
    /// gibt, prüft dort nichts — er ist nur rot. Dass die Liste dem Lastenheft entspricht, hält
    /// <see cref="The_id_list_mirrors_the_lastenheft_wherever_it_is_present"/> fest.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_requirement_of_the_lastenheft_has_a_row()
    {
        var declared = Load().Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var listed = LoadIdList().ToHashSet(StringComparer.Ordinal);

        _output.WriteLine($"Id-Liste: {listed.Count} · Matrix: {declared.Count}");

        listed.Except(declared).Should().BeEmpty("jede Anforderung braucht eine Zeile — auch eine offene");
        declared.Except(listed).Should().BeEmpty("eine Zeile ohne Anforderung im Lastenheft ist erfunden");
    }

    /// <summary>
    /// Die Id-Liste ist ein Stellvertreter, und ein Stellvertreter mit Tippfehlern oder Dubletten
    /// täuscht eine Zahl vor. Leer darf sie auch nicht sein: Gegen eine leere Liste bestünde nur
    /// eine leere Matrix.
    /// </summary>
    [Fact]
    public void The_id_list_is_well_formed()
    {
        var listed = LoadIdList();

        listed.Should().NotBeEmpty();
        listed.Should().OnlyContain(id => RequirementId.IsMatch(id), "jede Zeile ist genau eine Anforderungs-Id");
        listed.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// <b>Die Liste entspricht dem Lastenheft</b> — überall dort, wo das Lastenheft liegt.
    /// <para>
    /// Das ist die Stelle, an der eine neue Anforderung auffällt: Wer sie im Lastenheft ergänzt,
    /// färbt diesen Lauf auf seinem Rechner rot, bis Liste und Matrix nachgezogen sind. Fehlt das
    /// Lastenheft (CI, frischer Klon, Worktree), gibt es nichts zu vergleichen; der Test besteht
    /// dann und sagt es in der Ausgabe. Ein Skip wäre ehrlicher gemeint, aber schlechter: Er
    /// stünde in jedem CI-Lauf, und ein Skip, der immer da ist, sieht niemand mehr.
    /// </para>
    /// </summary>
    [Fact]
    public void The_id_list_mirrors_the_lastenheft_wherever_it_is_present()
    {
        var source = Path.Combine(RepoTree.Root, Load().Source);
        if (!File.Exists(source))
        {
            _output.WriteLine(
                $"Lastenheft nicht vorhanden ({Load().Source} ist nicht versioniert) — "
                + "der Abgleich der Id-Liste mit dem Lastenheft ist in diesem Lauf NICHT gelaufen.");
            return;
        }

        var inSource = RequirementHeading
            .Matches(File.ReadAllText(source))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var listed = LoadIdList().ToHashSet(StringComparer.Ordinal);

        _output.WriteLine($"Lastenheft: {inSource.Count} · Id-Liste: {listed.Count}");

        inSource.Should().NotBeEmpty("ein Lastenheft ohne erkannte Anforderung heißt, dass das Muster nicht mehr passt");
        inSource.Except(listed).Should().BeEmpty(
            $"das Lastenheft kennt Anforderungen, die in {IdListFile} fehlen — Liste und Matrix nachziehen");
        listed.Except(inSource).Should().BeEmpty(
            $"{IdListFile} nennt Anforderungen, die das Lastenheft nicht (mehr) kennt");
    }

    /// <summary>
    /// <b>Die härteste Regel des Vertrags.</b> Ein genannter Test muss existieren; sonst zeigt die
    /// Matrix auf etwas, das es nicht gibt, und liest sich trotzdem wie ein Nachweis.
    /// </summary>
    [Fact]
    public void Every_named_test_really_exists()
    {
        var suite = string.Join('\n', RepoTree.CSharpFiles("tests").Select(f => f.Content));

        var missing = Load().Requirements
            .Where(r => !string.IsNullOrWhiteSpace(r.Test))
            .Where(r => !suite.Contains(r.Test!, StringComparison.Ordinal))
            .ToList();

        missing.Should().BeEmpty(
            "die Matrix zeigt auf Tests, die es nicht gibt:\n"
            + string.Join("\n", missing.Select(r => $"{r.Id} -> {r.Test}")));
    }

    /// <summary>
    /// Ein <c>erfuellt</c> ohne jeden Beleg ist eine Behauptung. Entweder ein Test oder ein
    /// benanntes Artefakt — irgendetwas, worauf jemand nachsehen kann.
    /// </summary>
    [Fact]
    public void Nothing_is_marked_done_without_evidence()
    {
        var bare = Load().Requirements
            .Where(r => r.Status == "erfuellt")
            .Where(r => string.IsNullOrWhiteSpace(r.Test) && string.IsNullOrWhiteSpace(r.Artifact))
            .ToList();

        bare.Should().BeEmpty(
            "erfuellt ohne Test und ohne Artefakt ist eine Behauptung:\n"
            + string.Join("\n", bare.Select(r => r.Id)));
    }

    /// <summary>
    /// Umgekehrt: Was <c>teilweise</c>, <c>offen</c> oder <c>entfaellt</c> ist, sagt warum. Ein
    /// Status ohne Grund lässt den nächsten Leser raten — und er rät zugunsten des Produkts.
    /// </summary>
    [Fact]
    public void Everything_short_of_done_says_why()
    {
        var silent = Load().Requirements
            .Where(r => r.Status is "teilweise" or "offen" or "entfaellt")
            .Where(r => string.IsNullOrWhiteSpace(r.Note))
            .ToList();

        silent.Should().BeEmpty(
            "ein Status unterhalb von erfuellt braucht eine Begruendung:\n"
            + string.Join("\n", silent.Select(r => $"{r.Id} [{r.Status}]")));
    }

    [Fact]
    public void The_document_uses_only_the_agreed_vocabulary()
    {
        var document = Load();

        document.SchemaVersion.Should().Be(1);
        document.Requirements.Should().NotBeEmpty();
        document.Requirements.Select(r => r.Status).Should().OnlyContain(s => Stati.Contains(s));
        document.Requirements.Select(r => r.Level).Should().OnlyContain(l => Levels.Contains(l));
        document.Requirements.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        document.Requirements.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.WorkPackage));
    }

    /// <summary>
    /// Die Zahl, auf die es beim RC ankommt: <b>Wie viele Must-Anforderungen sind noch nicht
    /// erfüllt?</b> Der Test verbietet sie nicht — er schreibt sie in die Ausgabe, damit sie in
    /// jedem Lauf sichtbar ist statt nur in einem Dokument.
    /// </summary>
    [Fact]
    public void The_open_must_requirements_are_named_in_every_run()
    {
        var open = Load().Requirements
            .Where(r => r.Level == "must" && r.Status != "erfuellt")
            .ToList();

        foreach (var requirement in open)
        {
            _output.WriteLine($"{requirement.Id} [{requirement.Status}] {requirement.WorkPackage}: {requirement.Title[..Math.Min(80, requirement.Title.Length)]}");
        }

        _output.WriteLine($"Must-Anforderungen ohne 'erfuellt': {open.Count}");
        open.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Note),
            "gerade die offenen Must-Anforderungen brauchen einen Grund");
    }

    /// <summary>Die Ids aus der versionierten Liste: eine je Zeile, <c>#</c> leitet einen Kommentar ein.</summary>
    private static List<string> LoadIdList()
        => [.. File.ReadAllLines(Path.Combine(RepoTree.Root, "docs", "acceptance", IdListFile))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))];

    private static TraceabilityDocument Load()
        => JsonSerializer.Deserialize<TraceabilityDocument>(
            File.ReadAllText(Path.Combine(RepoTree.Root, "docs", "acceptance", "traceability.json")),
            Json)
            ?? throw new InvalidOperationException("traceability.json ist leer.");
}
