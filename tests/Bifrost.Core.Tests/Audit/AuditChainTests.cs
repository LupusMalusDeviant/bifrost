using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Audit;
using Xunit;

namespace Bifrost.Core.Tests.Audit;

/// <summary>
/// M6/WP6.2: Die Integritätskette (FR-P073).
/// <para>
/// <b>Der Anspruch ist begrenzt, und die Tests halten beide Enden fest:</b> Was die Kette findet
/// (Änderung, Löschung, Umsortierung) und was sie ausdrücklich nicht findet. Ein Prüfer, dessen
/// Grenzen nur in der Dokumentation stehen, wird als Vollständigkeitsbeweis gelesen — und das ist
/// er nicht.
/// </para>
/// </summary>
public sealed class AuditChainTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    // ── Kanonisierung ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_event_always_canonicalizes_the_same_way()
    {
        var evt = Event(0);

        AuditChain.Canonicalize(evt).Should().Be(AuditChain.Canonicalize(evt with { }));
    }

    /// <summary>
    /// <b>Der Fall, der eine Kanonisierung wertlos macht.</b> Formatiert jemand die Dauer in
    /// Landessprache, ist derselbe Eintrag auf einem deutschen Rechner ein anderer Hash als auf
    /// einem englischen — und der Prüfer meldete Manipulation, wo nur eine andere
    /// Ländereinstellung war.
    /// <para>
    /// <b>Warum nicht mit umgeschalteter Kultur geprüft:</b> Dieser Testlauf läuft im
    /// Invariant-Mode, dort gibt es keine zweite Kultur zum Umschalten. Geprüft wird deshalb die
    /// Tatsache selbst — die feste, invariante Schreibweise im kanonischen Text.
    /// </para>
    /// </summary>
    [Fact]
    public void The_canonical_form_uses_the_invariant_number_format()
    {
        var canonical = AuditChain.Canonicalize(
            Event(0) with { Duration = TimeSpan.FromMilliseconds(1.5) });

        canonical.Should().Contain("1.5000").And.NotContain("1,5",
            "ein Komma hier hieße: derselbe Eintrag, anderer Rechner, anderer Hash");
    }

    [Fact]
    public void A_changed_field_changes_the_link()
    {
        var evt = Event(0);

        AuditChain.Link(AuditChain.Genesis, evt with { Tool = "github__write" })
            .Should().NotBe(AuditChain.Link(AuditChain.Genesis, evt));
    }

    /// <summary>
    /// Ein fehlendes Feld ist etwas anderes als ein leeres. Ohne diese Unterscheidung hätten zwei
    /// verschiedene Ereignisse denselben Hash — und genau darauf zielte ein Fälscher.
    /// </summary>
    [Fact]
    public void A_missing_field_is_not_an_empty_one()
    {
        var ohne = Event(0) with { Detail = null };
        var leer = Event(0) with { Detail = string.Empty };

        AuditChain.Link(AuditChain.Genesis, ohne)
            .Should().NotBe(AuditChain.Link(AuditChain.Genesis, leer));
    }

    [Fact]
    public void The_same_event_at_a_different_position_gets_a_different_link()
    {
        var evt = Event(0);
        var erster = AuditChain.Link(AuditChain.Genesis, evt);

        AuditChain.Link(erster, evt).Should().NotBe(erster,
            "der Vorgänger geht in den Hash ein — sonst wäre es eine Prüfsumme je Zeile");
    }

    // ── Der Prüfer ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_untouched_chain_has_no_break()
    {
        var report = await VerifyAsync(Chained(5));

        report.IsIntact.Should().BeTrue();
        report.Checked.Should().Be(5);
        report.Segments.Should().ContainSingle().Which.Count.Should().Be(5);
    }

    [Fact]
    public async Task A_modified_entry_is_named_with_its_line()
    {
        var eintraege = Chained(5);
        eintraege[2] = eintraege[2] with { Event = eintraege[2].Event with { Tool = "github__write" } };

        var report = await VerifyAsync(eintraege);

        report.FirstBreak.Should().NotBeNull();
        report.FirstBreak!.Sequence.Should().Be(eintraege[2].Sequence);
        report.FirstBreak.Reason.Should().Contain("geändert");
    }

    /// <summary>
    /// <b>Der Fall, den eine Prüfsumme je Zeile nicht findet.</b> Jede verbliebene Zeile rechnet für
    /// sich auf; erst der Vergleich mit dem tatsächlichen Vorgänger zeigt die Lücke.
    /// </summary>
    [Fact]
    public async Task A_deleted_entry_leaves_a_gap_that_is_found()
    {
        var eintraege = Chained(5);
        eintraege.RemoveAt(2);

        var report = await VerifyAsync(eintraege);

        report.FirstBreak.Should().NotBeNull();
        report.FirstBreak!.Sequence.Should().Be(eintraege[2].Sequence, "gemeldet wird die erste Zeile, die nicht mehr passt");
        report.FirstBreak.Reason.Should().Contain("fehlt");
    }

    [Fact]
    public async Task Reordered_entries_are_found()
    {
        var eintraege = Chained(5);
        (eintraege[1], eintraege[2]) = (eintraege[2], eintraege[1]);

        (await VerifyAsync(eintraege)).IsIntact.Should().BeFalse();
    }

    /// <summary>
    /// Gemeldet wird die <b>erste</b> gebrochene Stelle. „Irgendwo kaputt" lässt einen Betreiber mit
    /// einem Log von Millionen Zeilen allein.
    /// </summary>
    [Fact]
    public async Task The_first_break_is_reported_not_any_break()
    {
        var eintraege = Chained(6);
        eintraege[4] = eintraege[4] with { Event = eintraege[4].Event with { Detail = "spaeter" } };
        eintraege[1] = eintraege[1] with { Event = eintraege[1].Event with { Detail = "frueher" } };

        (await VerifyAsync(eintraege)).FirstBreak!.Sequence.Should().Be(eintraege[1].Sequence);
    }

    [Fact]
    public async Task Entries_from_before_the_chain_are_counted_and_not_a_finding()
    {
        var eintraege = new List<AuditExportEntry>
        {
            new(1, Event(0)),
            new(2, Event(1)),
        };
        eintraege.AddRange(Chained(2, startSequence: 3));

        var report = await VerifyAsync(eintraege);

        report.IsIntact.Should().BeTrue();
        report.Unchained.Should().Be(2);
        report.Checked.Should().Be(2);
    }

    /// <summary>
    /// Eine ungekettete Zeile <em>nach</em> dem Beginn der Kette ist kein Altbestand, sondern eine
    /// Zeile, die am Schreiber vorbeigekommen ist — also genau das, wonach gesucht wird.
    /// </summary>
    [Fact]
    public async Task An_unchained_entry_after_the_chain_started_is_a_finding()
    {
        var eintraege = Chained(3);
        eintraege.Add(new AuditExportEntry(99, Event(3)));

        var report = await VerifyAsync(eintraege);

        report.FirstBreak.Should().NotBeNull();
        report.FirstBreak!.Sequence.Should().Be(99);
    }

    [Fact]
    public async Task A_second_segment_is_normal_and_no_finding()
    {
        var eintraege = Chained(3);
        eintraege.AddRange(Chained(2, startSequence: 4));

        var report = await VerifyAsync(eintraege);

        report.IsIntact.Should().BeTrue("ein Neustart beginnt einen neuen Abschnitt, das ist kein Bruch");
        report.Segments.Should().HaveCount(2);
    }

    /// <summary>
    /// <b>Vertragspflicht aus M6 §3:</b> Die Grenze des Anspruchs steht in der Ausgabe des Prüfers,
    /// nicht nur in der Dokumentation.
    /// </summary>
    [Fact]
    public async Task Every_report_carries_the_limits_of_the_claim()
    {
        var report = await VerifyAsync(Chained(2));

        report.Limits.Should().NotBeEmpty();
        string.Join(" ", report.Limits).Should()
            .Contain("kein Beweis für Vollständigkeit")
            .And.Contain("übernommenen Wirt");
    }

    /// <summary>Baut eine echte Kette — dieselbe Rechnung wie im Schreibpfad.</summary>
    private static List<AuditExportEntry> Chained(int count, long startSequence = 1)
    {
        var chainId = Guid.NewGuid();
        var previous = AuditChain.Genesis;
        var entries = new List<AuditExportEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var evt = Event(i);
            var hash = AuditChain.Link(previous, evt);
            entries.Add(new AuditExportEntry(startSequence + i, evt, chainId, i, previous, hash));
            previous = hash;
        }

        return entries;
    }

    private static AuditEvent Event(int index) => new(
        Start.AddSeconds(index),
        new IdentityId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        CallOrigin.Mcp,
        AuditEventKind.ToolCall,
        new ServerId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        "github__read",
        InvocationStatus.Success,
        JsonDocument.Parse($$"""{"n":{{index}}}""").RootElement,
        128,
        256,
        TimeSpan.FromMilliseconds(12),
        "leser",
        null,
        null,
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static Task<AuditChainReport> VerifyAsync(IEnumerable<AuditExportEntry> entries)
        => AuditChainVerifier.VerifyAsync(entries.ToAsyncEnumerable(), TestContext.Current.CancellationToken);
}

/// <summary>Eine Liste als Datenstrom — der Prüfer nimmt ausschließlich einen solchen entgegen.</summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
