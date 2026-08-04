using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Governance.Tests;

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
    /// Geprüft wird gegen das Lastenheft selbst: Wer dort eine Anforderung ergänzt, färbt diesen
    /// Lauf rot, bis sie hier eine Zeile hat.
    /// </summary>
    [Fact]
    public void Every_requirement_of_the_lastenheft_has_a_row()
    {
        var document = Load();
        var declared = document.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        var inSource = new Regex(@"^- \*\*((?:FR|NFR)-P\d+) \([MSC]\):", RegexOptions.Multiline)
            .Matches(File.ReadAllText(Path.Combine(RepoTree.Root, document.Source)))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        _output.WriteLine($"Lastenheft: {inSource.Count} · Matrix: {declared.Count}");

        inSource.Except(declared).Should().BeEmpty("jede Anforderung braucht eine Zeile — auch eine offene");
        declared.Except(inSource).Should().BeEmpty("eine Zeile ohne Anforderung im Lastenheft ist erfunden");
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

    private static TraceabilityDocument Load()
        => JsonSerializer.Deserialize<TraceabilityDocument>(
            File.ReadAllText(Path.Combine(RepoTree.Root, "docs", "acceptance", "traceability.json")),
            Json)
            ?? throw new InvalidOperationException("traceability.json ist leer.");
}
