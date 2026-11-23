using System.Security.Cryptography;
using System.Text;
using Faolan.Abstractions;

namespace Faolan.Core.Webhooks;

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
    public const string SignatureHeader = "X-Faolan-Signature";
    public const string TimestampHeader = "X-Faolan-Timestamp";

    /// <summary>
    /// Die Header-Namen vor der Umbenennung (Vertrag umbenennung-faolan §2.3). Eingehend werden sie
    /// weiter gelesen, ausgehend weiter geschrieben — ein Empfänger, der heute
    /// <c>X-Bifrost-Signature</c> prüft, bekäme sonst über Nacht unsignierte Nachrichten.
    /// </summary>
    public const string LegacySignatureHeader = "X-Bifrost-Signature";
    public const string LegacyTimestampHeader = "X-Bifrost-Timestamp";

    /// <summary>
    /// Welches Headerpaar eines eingehenden Requests geprüft wird.
    /// <para>
    /// <b>Liegt auch nur ein Header des neuen Paars vor, zählt ausschließlich das neue Paar.</b>
    /// Nie „eines von beiden passt": Sonst könnte ein Absender, der ein Paar nicht korrekt bilden
    /// kann, einfach das andere danebenlegen, und jede Schwäche des einen wäre eine des Ganzen.
    /// Ein gemischtes Paar (neue Signatur, alter Zeitstempel) gibt es deshalb ebenfalls nicht.
    /// </para>
    /// <para>
    /// „Vorliegen" heißt: Der Header ist da, auch leer. <paramref name="header"/> liefert
    /// <c>null</c> nur für einen fehlenden Header. Ein leeres <c>X-Faolan-Signature:</c> neben einem
    /// gültigen alten Paar wählt das neue Paar — und scheitert —, statt auf das alte auszuweichen.
    /// </para>
    /// </summary>
    public static (string? Signature, string? Timestamp) SelectHeaders(Func<string, string?> header)
    {
        ArgumentNullException.ThrowIfNull(header);

        var signature = header(SignatureHeader);
        var timestamp = header(TimestampHeader);
        return signature is not null || timestamp is not null
            ? (signature, timestamp)
            : (header(LegacySignatureHeader), header(LegacyTimestampHeader));
    }

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
