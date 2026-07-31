using System.Security.Cryptography;
using System.Text;
using Bifrost.Abstractions;

namespace Bifrost.Core.Webhooks;

/// <summary>
/// HMAC-SHA256-Signaturprüfung eingehender Webhooks (ADR-0013). Der einzige unauthentifizierte
/// Eingang des Gateways — deshalb zeitkonstanter Vergleich und Replay-Schutz.
///
/// Signiert wird über <c>{timestamp}.{body}</c>: Der Zeitstempel geht in die Signatur ein, damit
/// ein mitgeschnittener Request nicht außerhalb seines Zeitfensters wiederholt werden kann.
/// </summary>
public static class WebhookSignature
{
    /// <summary>Header-Namen — an gängigen Anbietern orientiert.</summary>
    public const string SignatureHeader = "X-Bifrost-Signature";
    public const string TimestampHeader = "X-Bifrost-Timestamp";

    /// <summary>Erzeugt die Signatur, die ein Absender mitschicken muss (Format <c>sha256=hex</c>).</summary>
    public static string Compute(string secret, long unixTimeSeconds, string body)
    {
        var material = $"{unixTimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}.{body}";
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(material));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Prüft eine eingehende Anfrage. <paramref name="now"/> wird übergeben, damit die Zeit im Test
    /// deterministisch ist.
    /// </summary>
    public static WebhookVerification Verify(
        string secret,
        string? signatureHeader,
        string? timestampHeader,
        string body,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(timestampHeader))
        {
            return WebhookVerification.InvalidSignature;
        }

        if (!long.TryParse(
                timestampHeader, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ts))
        {
            return WebhookVerification.InvalidSignature;
        }

        // Replay-Schutz VOR dem HMAC: ein alter (auch korrekt signierter) Request wird abgewiesen.
        var age = now - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age > tolerance || age < -tolerance)
        {
            return WebhookVerification.Stale;
        }

        var expected = Compute(secret, ts, body);

        // Zeitkonstanter Vergleich — sonst leakt die Vergleichsdauer Information über die Signatur.
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(signatureHeader);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b)
            ? WebhookVerification.Valid
            : WebhookVerification.InvalidSignature;
    }
}
