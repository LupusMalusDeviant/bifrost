using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Xunit;

namespace Bifrost.Core.Tests.Invocation;

/// <summary>
/// FR-16: Kürzen darf das Ergebnis nicht unbrauchbar machen. Zwei Eigenschaften zählen —
/// es bleibt parsbar, und die Kürzung ist erkennbar.
/// </summary>
public class ResultCompressorTests
{
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static JsonElement ArrayOf(int count)
        => JsonSerializer.SerializeToElement(
            Enumerable.Range(0, count).Select(i => new { id = i, text = new string('x', 50) }));

    [Fact]
    public void Disabled_by_default_so_nobody_loses_data_unknowingly()
    {
        var big = ArrayOf(500);

        var (content, truncation) = ResultCompressor.Compress(big, new ResultCompressionOptions());

        truncation.Should().BeNull("ohne Konfiguration wird nichts gekürzt");
        content.GetRawText().Should().Be(big.GetRawText());
    }

    [Fact]
    public void Small_results_pass_through_untouched()
    {
        var small = Parse("""{"ok":true}""");

        var (content, truncation) = ResultCompressor.Compress(small, new ResultCompressionOptions(10_000));

        truncation.Should().BeNull();
        content.GetRawText().Should().Be(small.GetRawText());
    }

    [Fact]
    public void Truncated_array_stays_valid_json_and_reports_what_is_missing()
    {
        var (content, truncation) = ResultCompressor.Compress(ArrayOf(500), new ResultCompressionOptions(1000));

        truncation.Should().NotBeNull();
        truncation!.OriginalChars.Should().BeGreaterThan(truncation.TruncatedChars);

        // Der eigentliche Punkt: das Ergebnis ist weiterhin parsbar.
        var act = () => JsonDocument.Parse(content.GetRawText());
        act.Should().NotThrow("ein abgeschnittener Rohtext wäre für den Agenten wertlos");

        content.GetProperty(ResultTruncation.MarkerProperty).GetBoolean().Should().BeTrue();
        content.GetProperty("totalItems").GetInt32().Should().Be(500);
        content.GetProperty("returnedItems").GetInt32().Should().BeGreaterThan(0).And.BeLessThan(500);
        content.GetProperty("items").GetArrayLength()
            .Should().Be(content.GetProperty("returnedItems").GetInt32());
    }

    [Fact]
    public void Truncated_string_reports_the_omitted_length()
    {
        var long_ = JsonSerializer.SerializeToElement(new string('a', 5000));

        var (content, truncation) = ResultCompressor.Compress(long_, new ResultCompressionOptions(500));

        truncation.Should().NotBeNull();
        content.GetProperty("text").GetString()!.Length.Should().Be(500);
        content.GetProperty("omittedChars").GetInt32().Should().Be(4500);
    }

    [Fact]
    public void Truncated_object_says_plainly_that_the_excerpt_is_not_parsable()
    {
        var obj = JsonSerializer.SerializeToElement(new { data = new string('y', 5000) });

        var (content, _) = ResultCompressor.Compress(obj, new ResultCompressionOptions(300));

        content.GetProperty(ResultTruncation.MarkerProperty).GetBoolean().Should().BeTrue();
        content.GetProperty("note").GetString().Should().Contain("nicht parsbar",
            "wer den Ausschnitt weiterverarbeitet, muss wissen, dass er kein JSON ist");
    }

    [Fact]
    public void At_least_one_array_item_survives_even_below_the_limit()
    {
        // Ein einzelnes Element größer als die Grenze darf nicht zu einer leeren Liste führen —
        // sonst bekäme der Agent gar nichts und wüsste nicht warum.
        var oneBigItem = JsonSerializer.SerializeToElement(new[] { new { text = new string('z', 5000) } });

        var (content, _) = ResultCompressor.Compress(oneBigItem, new ResultCompressionOptions(300));

        content.GetProperty("returnedItems").GetInt32().Should().Be(1);
    }
}
