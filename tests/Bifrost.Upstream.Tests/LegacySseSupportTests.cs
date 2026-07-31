using AwesomeAssertions;
using Bifrost.Abstractions;
using ModelContextProtocol.Client;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// FR-02: Upstreams, die nur den abgelösten HTTP+SSE-Transport sprechen, müssen weiterhin
/// erreichbar sein. Das leistet der AutoDetect-Modus des SDK — eine Fähigkeit, die uns
/// „geschenkt" wird und deshalb genau dann unbemerkt verschwindet, wenn ein SDK-Upgrade den
/// Default ändert. Diese Tests halten die Annahme fest, statt ihr zu vertrauen.
/// </summary>
public class LegacySseSupportTests
{
    [Fact]
    public void Sdk_still_offers_an_sse_fallback_mode()
    {
        // Verschwindet HttpTransportMode.Sse im SDK, bricht dieser Test statt der Produktion.
        Enum.IsDefined(HttpTransportMode.Sse).Should().BeTrue(
            "ohne SSE-Modus im SDK ist FR-02 nicht mehr ohne eigenen Konnektor erfüllbar");
        Enum.IsDefined(HttpTransportMode.AutoDetect).Should().BeTrue();
    }

    [Fact]
    public void Gateway_defaults_to_allowing_the_legacy_transport()
    {
        // Transport-Heterogenität wegzukapseln ist Aufgabe des Gateways (FR-02) — der Rückfall
        // ist deshalb an, bis SSE aus dem Standard fällt.
        new HttpTransportOptions(new Uri("https://example.invalid/mcp"))
            .AllowLegacySse.Should().BeTrue();
    }

    [Fact]
    public void Legacy_fallback_can_be_switched_off_per_server()
    {
        var strict = new HttpTransportOptions(new Uri("https://example.invalid/mcp"), AllowLegacySse: false);

        strict.AllowLegacySse.Should().BeFalse(
            "wer nur moderne Upstreams zulassen will, muss den Rückfall abschalten können");
    }
}
