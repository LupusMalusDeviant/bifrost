using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Webhooks;
using Xunit;

namespace Bifrost.Core.Tests.Webhooks;

/// <summary>
/// ADR-0013: Die Signaturprüfung ist der einzige Schutz am unauthentifizierten Webhook-Eingang.
/// Hier zählt, dass jede Manipulation erkannt wird — nicht nur, dass der Gutfall durchläuft.
/// </summary>
public class WebhookSignatureTests
{
    private const string Secret = "whsec_testgeheimnis_1234567890";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static long Ts(DateTimeOffset t) => t.ToUnixTimeSeconds();

    [Fact]
    public void Correctly_signed_request_is_valid()
    {
        var body = """{"ref":"main"}""";
        var sig = WebhookSignature.Compute(Secret, Ts(Now), body);

        WebhookSignature.Verify(Secret, sig, Ts(Now).ToString(), body, Now, Tolerance)
            .Should().Be(WebhookVerification.Valid);
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var sig = WebhookSignature.Compute(Secret, Ts(Now), """{"ref":"main"}""");

        // Angreifer ändert den Body, behält aber die Signatur.
        WebhookSignature.Verify(Secret, sig, Ts(Now).ToString(), """{"ref":"attacker"}""", Now, Tolerance)
            .Should().Be(WebhookVerification.InvalidSignature);
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var body = """{"ref":"main"}""";
        var sig = WebhookSignature.Compute("falsches-secret", Ts(Now), body);

        WebhookSignature.Verify(Secret, sig, Ts(Now).ToString(), body, Now, Tolerance)
            .Should().Be(WebhookVerification.InvalidSignature);
    }

    [Fact]
    public void Replayed_old_request_is_rejected_even_with_valid_signature()
    {
        // Der Request war zu seiner Zeit korrekt signiert …
        var past = Now - TimeSpan.FromMinutes(30);
        var body = """{"ref":"main"}""";
        var sig = WebhookSignature.Compute(Secret, Ts(past), body);

        // … wird aber 30 min später wiederholt.
        WebhookSignature.Verify(Secret, sig, Ts(past).ToString(), body, Now, Tolerance)
            .Should().Be(WebhookVerification.Stale, "Replay-Schutz greift vor dem HMAC");
    }

    [Fact]
    public void Future_timestamp_beyond_tolerance_is_rejected()
    {
        var future = Now + TimeSpan.FromMinutes(30);
        var body = "{}";
        var sig = WebhookSignature.Compute(Secret, Ts(future), body);

        WebhookSignature.Verify(Secret, sig, Ts(future).ToString(), body, Now, Tolerance)
            .Should().Be(WebhookVerification.Stale);
    }

    [Theory]
    [InlineData(null, "1800000000")]
    [InlineData("sha256=deadbeef", null)]
    [InlineData("", "1800000000")]
    [InlineData("kein-hex", "keine-zahl")]
    public void Missing_or_malformed_headers_are_rejected(string? sig, string? ts)
    {
        WebhookSignature.Verify(Secret, sig, ts, "{}", Now, Tolerance)
            .Should().NotBe(WebhookVerification.Valid);
    }

    [Fact]
    public void Signature_length_mismatch_does_not_throw()
    {
        // Ein zu kurzer/langer Signatur-Header darf nicht in eine Exception laufen.
        var act = () => WebhookSignature.Verify(Secret, "sha256=ab", Ts(Now).ToString(), "{}", Now, Tolerance);

        act.Should().NotThrow();
    }
}
