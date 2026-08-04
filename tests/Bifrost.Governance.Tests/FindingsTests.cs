using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Governance.Tests;

/// <param name="Owner">Bei einem offenen Medium-Befund Pflicht — sonst gehört er niemandem.</param>
/// <param name="Due">Bei einem offenen Medium-Befund Pflicht — sonst ist er unbefristet.</param>
/// <param name="Mitigation">
/// Bei einem offenen Medium-Befund Pflicht. Ein Befund ohne Mitigation ist ein Risiko, das man
/// kennt und trotzdem nicht abgesichert hat; das ist schlechter als einer, den man nicht kennt.
/// </param>
internal sealed record Finding(
    string Id,
    string Title,
    string Severity,
    string Status,
    string Source,
    string Detail,
    string Evidence,
    string? Owner = null,
    string? Due = null,
    string? Mitigation = null);

internal sealed record FindingsDocument(
    int SchemaVersion,
    string Note,
    FindingsCoverage Coverage,
    IReadOnlyList<Finding> Findings);

internal sealed record FindingsCoverage(string From, string Reason);

/// <summary>
/// M7/WP7.1: Die Finding-Liste ist ein <b>Artefakt mit Regeln</b>, keine Aufzählung.
/// <para>
/// <b>Critical und High blockieren den RC.</b> Es gibt bewusst kein Feld „akzeptiert": Wer einen
/// solchen Befund für tragbar hält, ändert den Schweregrad und begründet das im Datensatz — dann
/// steht die Entscheidung da, wo sie hingehört, statt in einem Ausnahmefeld zu verschwinden.
/// </para>
/// </summary>
public sealed class FindingsTests
{
    private static readonly string[] Severities = ["critical", "high", "medium", "low"];
    private static readonly string[] Stati = ["offen", "behoben"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ITestOutputHelper _output;

    public FindingsTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>Das RC-Tor.</b> Solange hier ein offener Critical- oder High-Befund steht, ist der Release
    /// Candidate keiner — und dieser Test sagt das, statt es einem Dokument zu überlassen.
    /// </summary>
    [Fact]
    public void No_open_critical_or_high_finding_blocks_the_release_candidate()
    {
        var blocking = Load().Findings
            .Where(f => f.Status == "offen" && f.Severity is "critical" or "high")
            .ToList();

        blocking.Should().BeEmpty(
            "ein offener Critical- oder High-Befund ist ein Releaseblocker (Pflichtenheft WP7.1):\n"
            + string.Join("\n", blocking.Select(f => $"{f.Id} [{f.Severity}] {f.Title}")));
    }

    /// <summary>
    /// Ein offener Medium-Befund braucht alle drei Angaben. Zwei von dreien sind kein Kompromiss,
    /// sondern ein Befund, der irgendwann niemandem mehr auffällt.
    /// </summary>
    [Fact]
    public void Every_open_medium_finding_has_an_owner_a_date_and_a_mitigation()
    {
        var incomplete = Load().Findings
            .Where(f => f.Status == "offen" && f.Severity == "medium")
            .Where(f => string.IsNullOrWhiteSpace(f.Owner)
                || string.IsNullOrWhiteSpace(f.Due)
                || string.IsNullOrWhiteSpace(f.Mitigation))
            .ToList();

        incomplete.Should().BeEmpty(
            "Owner, Termin und Mitigation sind bei offenen Medium-Befunden Pflicht:\n"
            + string.Join("\n", incomplete.Select(f => f.Id)));
    }

    [Fact]
    public void The_document_uses_only_the_agreed_vocabulary()
    {
        var document = Load();

        document.SchemaVersion.Should().Be(1);
        document.Findings.Should().NotBeEmpty();
        document.Findings.Select(f => f.Severity).Should().OnlyContain(s => Severities.Contains(s));
        document.Findings.Select(f => f.Status).Should().OnlyContain(s => Stati.Contains(s));
        document.Findings.Select(f => f.Id).Should().OnlyHaveUniqueItems();
        document.Findings.Should().OnlyContain(f => f.Id.StartsWith("BFR-F-", StringComparison.Ordinal));
        document.Findings.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Detail));
    }

    /// <summary>
    /// <b>Die härteste Regel des M7-Vertrags (§1), hier angewendet.</b> Ein Beleg, der wie ein
    /// Testname aussieht, muss einer sein. Ein Verweis auf einen gelöschten Test ist schlimmer als
    /// gar keiner — er sieht aus wie ein Nachweis.
    /// </summary>
    [Fact]
    public void Every_test_named_as_evidence_really_exists()
    {
        var suite = string.Join('\n', RepoTree.CSharpFiles("tests").Select(f => f.Content));

        var missing = Load().Findings
            .Select(f => (f.Id, Name: LastSegmentIfTestName(f.Evidence)))
            .Where(x => x.Name is not null)
            .Where(x => !suite.Contains(x.Name!, StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"Belege, die auf einen Test zeigen: {Load().Findings.Count(f => LastSegmentIfTestName(f.Evidence) is not null)}");

        missing.Should().BeEmpty(
            "ein Beleg, der auf einen nicht existierenden Test zeigt, ist kein Beleg:\n"
            + string.Join("\n", missing.Select(x => $"{x.Id} -> {x.Name}")));
    }

    /// <summary>
    /// Die Abdeckungsangabe ist Pflicht. Eine Liste, die nicht sagt, ab wann sie gilt, wird für
    /// vollständig gehalten — und genau das ist sie hier nicht.
    /// </summary>
    [Fact]
    public void The_list_states_from_where_it_is_complete()
    {
        var coverage = Load().Coverage;

        coverage.From.Should().NotBeNullOrWhiteSpace();
        coverage.Reason.Should().NotBeNullOrWhiteSpace(
            "eine Abdeckungsgrenze ohne Begruendung ist eine Ausrede");
    }

    /// <summary>
    /// Ein Beleg ist entweder ein Pfad im Repository oder ein Testname. Alles mit Schrägstrich oder
    /// Punkt-Endung gilt als Pfad; der Rest wird als Testname geprüft.
    /// </summary>
    private static string? LastSegmentIfTestName(string evidence)
        => evidence.Contains('/', StringComparison.Ordinal)
            ? null
            : evidence.Split('.')[^1];

    private static FindingsDocument Load()
        => JsonSerializer.Deserialize<FindingsDocument>(
            File.ReadAllText(Path.Combine(RepoTree.Root, "docs", "acceptance", "findings.json")),
            Json)
            ?? throw new InvalidOperationException("findings.json ist leer.");
}
