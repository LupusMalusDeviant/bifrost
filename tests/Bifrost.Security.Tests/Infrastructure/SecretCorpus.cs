namespace Bifrost.Security.Tests.Infrastructure;

/// <summary>
/// Der Negativkorpus: erfundene Zugangsdaten, die in <b>keiner</b> Ausgabe des Dienstes auftauchen
/// duerfen — weder in einer API-Antwort noch in einem Diagnosebericht, einem Export oder einer
/// Logzeile.
/// <para>
/// <b>Warum jeder Wert einmalig ist:</b> Ein Fehlschlag soll die schuldige Stelle benennen, nicht
/// nur „irgendetwas ist durchgerutscht". Die Werte tragen deshalb ihren Herkunftsnamen im Praefix
/// und dahinter genug Zufall, dass sie in keinem Fremdtext zufaellig vorkommen.
/// </para>
/// <para>
/// <b>Warum die Werte kein erkennbares Muster haben:</b> Ein Wert wie <c>sk-live-…</c> wuerde von
/// der Mustererkennung der Guardrail gefangen und liesse die Pruefung auch dort gruen aussehen, wo
/// gar keine Redaktion stattfindet. Der Korpus prueft die <em>Positivliste</em> — also die Regel,
/// dass ein Konfigurationswert die Ausgabe erst gar nicht erreicht.
/// </para>
/// </summary>
public static class SecretCorpus
{
    /// <summary>Praefix aller Korpuswerte. Ein Test darf danach in einer Ausgabe suchen.</summary>
    public const string Marker = "KORPUS";

    public const string StdioEnv = "KORPUS-stdio-QGgIVeMkKMhVwHXY";
    public const string HttpHeader = "KORPUS-http-lQeVzBWkjxHZmSvT";
    public const string OpenApiCredential = "KORPUS-openapi-YrTnDpUwOaKcFbLe";
    public const string CliEnv = "KORPUS-cli-MvXqZjRtNbGyHsPd";
    public const string WasiSecret = "KORPUS-wasi-CkWfUiToAeLdRnJm";
    public const string OpenRpcCredential = "KORPUS-openrpc-BzHpNsQxVkEaTgYw";
    public const string ToolArgument = "KORPUS-arg-DfKmSaWpLtZxCbNq";
    public const string WebhookSecret = "KORPUS-webhook-RgTyUiOpAsDfGhJk";
    public const string ApiKeyPlaintext = "KORPUS-apikey-ZxCvBnMqWeRtYuIo";
    public const string ConnectionString = "KORPUS-dbpass-PoIuYtReWqLkJhGf";
    public const string OAuthToken = "KORPUS-oauth-NmQaZwSxEdCrFvTg";

    /// <summary>
    /// Ein vorgelegtes Setup-Token (WP3.4). Der Einloesepfad ist der einzige unauthentifizierte
    /// Schreibweg des Dienstes; ein abgelehnter Versuch ist genau die Stelle, an der ein
    /// praesentiertes Geheimnis in einer Fehlerzeile mitreisen wuerde.
    /// </summary>
    public const string BootstrapToken = "KORPUS-bootstrap-HjLqWmZaEsRdTfYg";

    /// <summary>
    /// Alle Werte. Wer einen neuen Wert ergaenzt, ergaenzt ihn <b>hier</b> — die Tests laufen
    /// ausnahmslos ueber diese Liste, nie ueber einzelne Konstanten.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        StdioEnv, HttpHeader, OpenApiCredential, CliEnv, WasiSecret, OpenRpcCredential,
        ToolArgument, WebhookSecret, ApiKeyPlaintext, ConnectionString, OAuthToken, BootstrapToken,
    ];

    /// <summary>
    /// Bruchstuecke von acht Zeichen aus jedem Wert. Eine Ausgabe, die ein Geheimnis „nur zur
    /// Haelfte" mitschickt, ist kein halber Fehler — acht Zeichen reichen, um einen Wert in einem
    /// Logarchiv wiederzufinden, und ein truncierender Formatierer erzeugt genau solche Reste.
    /// </summary>
    public static IEnumerable<string> Fragments(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        for (var start = 0; start + 8 <= secret.Length; start += 4)
        {
            yield return secret.Substring(start, 8);
        }
    }

    /// <summary>
    /// Findet den ersten Korpuswert in einem Text — inklusive der Bruchstuecke des zufaelligen
    /// Teils. Der Praefix allein zaehlt nicht: <c>KORPUS-stdio-</c> ist der Name der Stelle, nicht
    /// das Geheimnis.
    /// </summary>
    public static string? FirstLeakIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var secret in All)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
            {
                return secret;
            }

            // Nur der Zufallsteil hinter dem zweiten Bindestrich — der Praefix ist beschreibend.
            var randomPart = secret[(secret.LastIndexOf('-') + 1)..];
            foreach (var fragment in Fragments(randomPart))
            {
                if (text.Contains(fragment, StringComparison.Ordinal))
                {
                    return $"{secret} (Bruchstueck '{fragment}')";
                }
            }
        }

        return null;
    }
}
