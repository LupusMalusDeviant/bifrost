using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Audit;
using Xunit;

namespace Bifrost.Core.Tests.Audit;

/// <summary>
/// M6/WP6.2: Die beiden Ausgabeformate (FR-P072).
/// <para>
/// Der wichtigste Test hier ist <see cref="A_field_that_looks_like_a_formula_is_defused"/>: Ein
/// Auditexport, der beim Öffnen in einer Tabellenkalkulation Code ausführt, ist die Umkehrung
/// seines Zwecks.
/// </para>
/// </summary>
public sealed class AuditExportWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Jsonl_writes_one_self_contained_object_per_line()
    {
        var line = AuditExportWriter.Line(Chained(), AuditExportFormat.Jsonl);

        line.Should().NotContain("\n", "eine Zeile je Eintrag ist der ganze Sinn von JSONL");
        var parsed = JsonDocument.Parse(line).RootElement;
        parsed.GetProperty("sequence").GetInt64().Should().Be(7);
        parsed.GetProperty("tool").GetString().Should().Be("github__read");
        parsed.GetProperty("redactedArguments").GetProperty("token").GetString().Should().Be("***");
    }

    /// <summary>
    /// Die Kettenglieder reisen mit — sonst ließe sich ein Export später nicht mehr nachrechnen,
    /// und die Kette wäre nur innerhalb dieser einen Instanz etwas wert.
    /// </summary>
    [Fact]
    public void Jsonl_carries_the_chain_so_an_export_stays_verifiable()
    {
        var parsed = JsonDocument.Parse(
            AuditExportWriter.Line(Chained(), AuditExportFormat.Jsonl)).RootElement;

        parsed.GetProperty("chainVersion").GetString().Should().Be(AuditChain.Version);
        parsed.GetProperty("chainIndex").GetInt64().Should().Be(3);
        parsed.GetProperty("entryHash").GetString().Should().NotBeNullOrWhiteSpace();
        parsed.GetProperty("previousHash").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_unchained_entry_says_so_by_leaving_the_fields_out()
    {
        var parsed = JsonDocument.Parse(AuditExportWriter.Line(
            new AuditExportEntry(1, Event()), AuditExportFormat.Jsonl)).RootElement;

        parsed.TryGetProperty("entryHash", out _).Should().BeFalse(
            "ein leerer Hash sähe aus wie ein Hash");
    }

    /// <summary>
    /// Die Spaltenfolge ist fest. Wer sie ändert, zerlegt jede Auswertung, die jemand darauf gebaut
    /// hat — deshalb steht sie hier als Erwartung und nicht nur im Code.
    /// </summary>
    [Fact]
    public void The_csv_column_order_is_pinned()
    {
        AuditExportWriter.Header(AuditExportFormat.Csv).Should().Be(
            "sequence,timestamp,kind,origin,caller,callerRoles,server,tool,status,durationMs,"
            + "requestBytes,responseBytes,correlationId,detail,chainId,chainIndex,previousHash,entryHash");
    }

    [Fact]
    public void Jsonl_has_no_header_line()
    {
        AuditExportWriter.Header(AuditExportFormat.Jsonl).Should().BeNull();
    }

    /// <summary>
    /// CSV trägt die maskierten Argumente <b>nicht</b>. Sie sind JSON und gehören nicht in eine
    /// Tabellenzelle; wer sie braucht, nimmt JSONL. Ein CSV, das sie mitschleppt, ist beim ersten
    /// Komma unlesbar.
    /// </summary>
    [Fact]
    public void Csv_leaves_the_arguments_to_jsonl()
    {
        AuditExportWriter.Line(Chained(), AuditExportFormat.Csv).Should().NotContain("***");
    }

    /// <summary>
    /// <b>CSV Injection.</b> Ein Auditfeld enthält Text aus fremder Quelle. Beginnt er mit
    /// <c>=</c>, <c>+</c>, <c>-</c> oder <c>@</c>, führt eine Tabellenkalkulation ihn beim Öffnen
    /// aus. Der Export wird gerade dann geöffnet, wenn jemand einem Vorfall nachgeht.
    /// </summary>
    [Theory]
    [InlineData("=CMD|'/c calc'!A1")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void A_field_that_looks_like_a_formula_is_defused(string boesartig)
    {
        var line = AuditExportWriter.Line(
            new AuditExportEntry(1, Event() with { Detail = boesartig }), AuditExportFormat.Csv);

        line.Should().Contain("'" + boesartig[0], "ein führendes Hochkomma macht daraus Text");
        line.Should().NotContain("," + boesartig[0], "unmaskiert stünde die Formel direkt in der Zelle");
    }

    [Fact]
    public void A_field_with_a_comma_or_a_quote_is_escaped_per_rfc_4180()
    {
        var line = AuditExportWriter.Line(
            new AuditExportEntry(1, Event() with { Detail = "a,b\"c" }), AuditExportFormat.Csv);

        line.Should().Contain("\"a,b\"\"c\"");
    }

    [Fact]
    public void The_content_type_matches_the_format()
    {
        AuditExportWriter.ContentType(AuditExportFormat.Csv).Should().StartWith("text/csv");
        AuditExportWriter.ContentType(AuditExportFormat.Jsonl).Should().StartWith("application/x-ndjson");
    }

    private static AuditExportEntry Chained()
    {
        var evt = Event();
        var previous = AuditChain.Link(AuditChain.Genesis, evt);
        return new AuditExportEntry(7, evt, Guid.NewGuid(), 3, previous, AuditChain.Link(previous, evt));
    }

    private static AuditEvent Event() => new(
        Now,
        new IdentityId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        CallOrigin.Mcp,
        AuditEventKind.ToolCall,
        new ServerId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        "github__read",
        InvocationStatus.Success,
        JsonDocument.Parse("""{"token":"***"}""").RootElement,
        128,
        256,
        TimeSpan.FromMilliseconds(12),
        "leser");
}
