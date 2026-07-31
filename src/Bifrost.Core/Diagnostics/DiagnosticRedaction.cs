using System.Text.RegularExpressions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Guardrails;

namespace Bifrost.Core.Diagnostics;

/// <summary>
/// Der eine Redaktionsschritt, durch den <b>jeder</b> Befund läuft, bevor er den Dienst verlässt
/// (M2-Vertrag §6, Invariante 2).
/// <para>
/// <b>Warum zentral und nicht je Check:</b> Eine Maskierung, die an siebzehn Stellen steht, ist an
/// siebzehn Stellen vergessbar. Der Dienst schickt deshalb ausnahmslos jeden Befund hier durch —
/// auch die, die er selbst für einen Zeitüberlauf oder eine geworfene Ausnahme erzeugt. Gerade die
/// sind gefährlich: Eine Datenbankausnahme trägt gern die vollständige Verbindungszeichenfolge im
/// Text.
/// </para>
/// <para>
/// <b>Das hier ist die zweite Linie, nicht die erste.</b> Die erste ist eine Regel für die Checks:
/// Sie schreiben Namen, Pfade, Zahlen und Ja/Nein in <c>SafeDetails</c> — nie einen
/// Konfigurationswert. Das ist eine Positivliste und deshalb belastbar. Muster erkennen nur, was
/// ein Muster hat; ein zufälliges Passwort mitten in einem Fremdtext hat keins (dieselbe Grenze wie
/// bei der Guardrail, ADR-0011).
/// </para>
/// <para>
/// Bewusst <b>nicht</b> maskiert wird das nackte Wort <c>key</c>: Sonst verschwände jeder Pfad zum
/// Key-Ring aus der Ausgabe — also genau die Angabe, wegen der ein Betreiber die Diagnose aufruft.
/// Die Schlüssel selbst liegen in Dateien, nicht in der Konfiguration.
/// </para>
/// </summary>
public static partial class DiagnosticRedaction
{
    public const string Mask = "***";

    /// <summary>
    /// Obergrenze je Textfeld. Ein Befund ist ein Satz, kein Protokollauszug — und ein sehr langer
    /// Fremdtext ist die Stelle, an der ein Wert ohne Muster mitreist.
    /// </summary>
    public const int MaxLength = 2000;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Benannte Werte: <c>Password=…</c>, <c>token: …</c>, <c>?access_token=…</c>,
    /// <c>Authorization: Bearer …</c>. Ohne führendes <c>\b</c>, weil der Wortanker an
    /// <c>BIFROST_KEYRING_CERT_PASSWORD</c> genau nicht greift — der Unterstrich ist ein
    /// Wortzeichen. Ein vorangestelltes <c>Bearer</c>/<c>Basic</c> wird mitmaskiert: Der Befund ist
    /// ein Satz für Menschen, kein wiederverwendbarer Header.
    /// </summary>
    [GeneratedRegex(
        """(?i)(?:pass(?:word|wort|phrase)?|pwd|token|secret|credential|api[_-]?key|apikey|authorization|client[_-]?secret|connection[_-]?string)\s*[:=]\s*((?:(?:bearer|basic)\s+)?(?:"[^"]*"|'[^']*'|[^\s;,&"']+))""",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamedValue();

    /// <summary>Zugangsdaten im Autoritätsteil einer URL: <c>https://benutzer:geheim@host/…</c>.</summary>
    [GeneratedRegex(
        @"(?i)([a-z][a-z0-9+.\-]*://)[^\s/@:]+:[^\s/@]+@",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    /// <summary>Der API-Key-Präfix dieses Produkts. Er steht in der Doku und ist damit suchbar.</summary>
    [GeneratedRegex(@"\bmcpk_[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex GatewayApiKey();

    /// <summary>
    /// Die Muster der Guardrail, hier zum Maskieren statt zum Blockieren. Sie werden mitgepflegt,
    /// weil sie an einer anderen Stelle gebraucht werden — eine zweite Sammlung derselben Regeln
    /// wäre die, die veraltet.
    /// </summary>
    private static readonly Regex[] KnownTokenShapes =
    [
        .. BuiltInGuardRules.All.Select(rule => new Regex(
            rule.Pattern,
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
            MatchTimeout)),
    ];

    /// <summary>
    /// Maskiert einen einzelnen Text. Läuft ein Muster in sein Zeitlimit, wird der <b>ganze</b> Text
    /// verworfen — fail-closed. Ein halb geprüfter Text ist ein ungeprüfter Text.
    /// </summary>
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            var result = NamedValue().Replace(text, MaskTrailingValue);
            result = UrlUserInfo().Replace(result, $"$1{Mask}:{Mask}@");
            result = GatewayApiKey().Replace(result, Mask);
            foreach (var shape in KnownTokenShapes)
            {
                result = shape.Replace(result, Mask);
            }

            return result.Length > MaxLength
                ? string.Concat(result.AsSpan(0, MaxLength), " … (gekürzt)")
                : result;
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }
    }

    /// <summary>Maskiert Schlüssel <b>und</b> Werte einer Detailtabelle.</summary>
    public static IReadOnlyDictionary<string, string>? Scrub(IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
        {
            return details;
        }

        var scrubbed = new Dictionary<string, string>(details.Count, StringComparer.Ordinal);
        foreach (var (key, value) in details)
        {
            // Auch der Schlüssel: Er kommt in einem Fall von außen (Upstream-Slug, Variablenname).
            scrubbed[Scrub(key) ?? string.Empty] = Scrub(value) ?? string.Empty;
        }

        return scrubbed;
    }

    /// <summary>Der Aufruf, den der Dienst auf jeden Befund anwendet.</summary>
    public static DiagnosticCheck Scrub(DiagnosticCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        return check with
        {
            Summary = Scrub(check.Summary) ?? string.Empty,
            Remediation = Scrub(check.Remediation),
            SafeDetails = Scrub(check.SafeDetails),
        };
    }

    /// <summary>
    /// Ersetzt den Wert und behält alles davor — inklusive eines Präfixes wie <c>BIFROST_KEYRING_CERT_</c>,
    /// das erst den Namen vollständig macht.
    /// </summary>
    private static string MaskTrailingValue(Match match)
    {
        var valueOffset = match.Groups[1].Index - match.Index;
        return string.Concat(match.Value.AsSpan(0, valueOffset), Mask);
    }
}
