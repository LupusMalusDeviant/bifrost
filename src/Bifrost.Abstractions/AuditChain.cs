using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace Bifrost.Abstractions;

/// <summary>
/// Die <b>kanonische Fassung</b> eines Audit-Eintrags und die Verkettung darüber (M6/WP6.2,
/// FR-P073).
/// <para>
/// <b>Die Kanonisierung ist Teil des Vertrags, nicht der Umsetzung.</b> Wer sie ändert — Feldfolge,
/// Trennzeichen, Zahlen- oder Zeitformat —, entwertet jede bestehende Kette: Alte Einträge lassen
/// sich danach nicht mehr nachrechnen, und der Prüfer meldet Brüche, wo keine sind. Änderungen
/// gehören deshalb in eine neue <see cref="Version"/> mit eigener Kennung, nicht in diese hier.
/// </para>
/// <para>
/// <b>Warum hier und nicht im Kern</b> (Abweichung vom M6-Vertrag §3, dort steht
/// <c>Bifrost.Core/Audit/**</c>): Der Schreibpfad liegt in der Persistenz, der Prüfer im Kern —
/// beide brauchen dieselbe Rechnung. Sie im Kern zu lassen hieße, die Persistenz auf den Kern
/// zeigen zu lassen; das ist eine neue Kante im Abhängigkeitsgraphen für eine einzige statische
/// Klasse. Der Vertrag sagt selbst, die Kanonisierung sei Teil des Vertrags — dann steht sie auch
/// in der Vertragsassembly. Der <see cref="AuditChainVerifier"/> bleibt im Kern.
/// </para>
/// </summary>
public static class AuditChain
{
    /// <summary>
    /// Kennung des Kanonisierungsverfahrens. Sie steht im Export neben den Hashes, damit ein Prüfer
    /// von morgen weiß, nach welchen Regeln er rechnen muss.
    /// </summary>
    public const string Version = "audit-chain-v1";

    /// <summary>
    /// Der Vorgänger des ersten Eintrags eines Abschnitts. Bewusst ein sichtbarer Wert und nicht
    /// <c>null</c>: In der Ablage ist „kein Vorgänger" sonst von „Vorgänger fehlt" nicht zu
    /// unterscheiden.
    /// </summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Das Trennzeichen zwischen den Feldern — ein Zeichen, das in keinem Feld vorkommt.</summary>
    private const char Separator = '\u001f';

    /// <summary>
    /// Steht für ein <b>fehlendes</b> Feld. Ein fehlendes Feld ist etwas anderes als ein leeres:
    /// Ohne diese Unterscheidung hätten zwei verschiedene Ereignisse denselben Hash — und genau
    /// darauf zielte ein Fälscher.
    /// </summary>
    private const string NullMarker = "\u001e";

    /// <summary>
    /// Die kanonische Fassung eines Ereignisses: feste Feldfolge, festes Zeit- und Zahlenformat,
    /// keine Position und keine Id.
    /// <para>
    /// <b>Warum ohne die Zeilennummer:</b> Der Hash entsteht beim Schreiben, die Nummer vergibt die
    /// Datenbank beim Einfügen. Ein Hash über die Nummer wäre erst nach dem Schreiben zu bilden —
    /// und damit nachträglich, also genau das, was die Kette ausschließen soll. Die Reihenfolge ist
    /// trotzdem gebunden: Sie steckt im Vorgängerhash.
    /// </para>
    /// </summary>
    public static string Canonicalize(AuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var text = new StringBuilder(256);
        Append(text, Version);
        Append(text, evt.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(text, evt.Caller?.Value.ToString("D", CultureInfo.InvariantCulture));
        Append(text, ((int)evt.Origin).ToString(CultureInfo.InvariantCulture));
        Append(text, ((int)evt.Kind).ToString(CultureInfo.InvariantCulture));
        Append(text, evt.Server?.Value.ToString("D", CultureInfo.InvariantCulture));
        Append(text, evt.Tool);
        Append(text, evt.Status is { } status ? ((int)status).ToString(CultureInfo.InvariantCulture) : null);
        Append(text, evt.RedactedArguments?.GetRawText());
        Append(text, evt.RequestBytes?.ToString(CultureInfo.InvariantCulture));
        Append(text, evt.ResponseBytes?.ToString(CultureInfo.InvariantCulture));

        // Millisekunden mit fester Genauigkeit: Ein Double in Landessprache ("1,5") wäre auf einer
        // anderen Maschine ein anderer Hash — und der Prüfer meldete Manipulation.
        Append(text, evt.Duration?.TotalMilliseconds.ToString("F4", CultureInfo.InvariantCulture));
        Append(text, evt.CallerRoles);
        Append(text, evt.Detail);
        Append(text, evt.RedactedResponse?.GetRawText());
        Append(text, evt.CorrelationId?.ToString("D", CultureInfo.InvariantCulture));

        return text.ToString();
    }

    /// <summary>
    /// Das Kettenglied: SHA-256 über Vorgängerhash <b>und</b> kanonischer Fassung.
    /// <para>
    /// Ohne den Vorgänger wäre es eine Prüfsumme je Zeile — die schützt gegen ein verändertes Feld,
    /// aber nicht gegen das Entfernen oder Umsortieren ganzer Zeilen.
    /// </para>
    /// </summary>
    public static string Link(string previousHash, AuditEvent evt)
    {
        ArgumentException.ThrowIfNullOrEmpty(previousHash);

        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(previousHash + Separator + Canonicalize(evt))));
    }

    private static void Append(StringBuilder text, string? value)
    {
        text.Append(value ?? NullMarker).Append(Separator);
    }
}
