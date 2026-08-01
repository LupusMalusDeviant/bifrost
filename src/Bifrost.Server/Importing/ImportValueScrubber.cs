using Bifrost.Abstractions;
using Bifrost.Core.Execution;

namespace Bifrost.Server.Importing;

/// <summary>
/// Entfernt die Werte <b>einer bestimmten</b> Konfiguration aus einem Text.
///
/// <para>
/// <b>Wofür das gebraucht wird.</b> Das Vorschaumodell wird aufgebaut und kann deshalb nichts
/// durchlassen (siehe <see cref="ImportPreviewProjection"/>). Zwei Ausgaben entstehen aber nicht
/// hier, sondern woanders: die Fehlermeldung eines Verbindungstests und die Begründung einer
/// abgelehnten Übernahme. Beide sind Fremdtext — ein Prozess, der nicht startet, schreibt seine
/// Kommandozeile in die Meldung; ein HTTP-Ziel, das ablehnt, schickt gern die angefragte URL zurück.
/// </para>
///
/// <para>
/// <b>Warum das kein zweiter Mustererkenner ist.</b> Hier wird nicht geraten, was ein Geheimnis sein
/// könnte. Hier ist bekannt, welche Werte in dieser Konfiguration stehen — sie liegen einen
/// Methodenaufruf entfernt. Gesucht wird nach genau diesen Zeichenketten. Das ist enger als jede
/// Heuristik und irrt sich nicht in die gefährliche Richtung.
/// </para>
///
/// <para>
/// Sehr kurze Werte bleiben stehen (<see cref="MinimumLength"/>): <c>true</c> oder <c>8080</c> aus
/// einer Umgebungsvariablen aus einer Meldung zu streichen, machte sie unlesbar, ohne irgendetwas zu
/// schützen.
/// </para>
/// </summary>
public static class ImportValueScrubber
{
    /// <summary>Was an die Stelle eines Wertes tritt.</summary>
    public const string Mask = "***";

    /// <summary>
    /// Kürzere Werte werden nicht ersetzt.
    /// <para>
    /// Das ist keine Geheimnisgrenze, sondern eine Lesbarkeitsgrenze. Konfigurationen tragen Werte
    /// wie <c>true</c>, <c>node</c>, <c>http</c> oder <c>Container</c>; sie aus einer Meldung zu
    /// streichen machte sie unlesbar und schützte nichts. Zwölf Zeichen liegt oberhalb dieser Wörter
    /// und unterhalb jedes Zugangsdatums, das diesen Namen verdient.
    /// </para>
    /// </summary>
    public const int MinimumLength = 12;

    /// <summary>
    /// Der Text ohne die Werte aus <paramref name="config"/>. <c>null</c> bleibt <c>null</c>.
    /// </summary>
    [NoHostExecution(
        "Nimmt eine Konfiguration entgegen, um ihre WERTE aus einer fremden Meldung zu entfernen. "
        + "Startet nichts und gibt nichts zurueck ausser Text.")]
    public static string? Scrub(string? text, UpstreamServerConfig? config)
    {
        if (string.IsNullOrEmpty(text) || config is null)
        {
            return text;
        }

        var result = text;
        foreach (var value in ValuesOf(config)
            .Where(value => value.Length >= MinimumLength)
            // Von lang nach kurz: Sonst zerlegte ein kurzer Wert einen langen, der ihn enthaelt,
            // und der Rest des langen bliebe stehen.
            .OrderByDescending(value => value.Length))
        {
            result = result.Replace(value, Mask, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Alles, was in dieser Konfiguration ein <em>Wert</em> ist. Bewusst großzügig: Ein Pfad zu viel
    /// in dieser Menge kostet eine Sternchenfolge in einer Meldung, ein Wert zu wenig kostet ein
    /// Geheimnis.
    /// </summary>
    private static IEnumerable<string> ValuesOf(UpstreamServerConfig config)
    {
        if (config.Stdio is { } stdio)
        {
            foreach (var argument in stdio.Arguments ?? [])
            {
                yield return argument;
            }

            foreach (var value in Values(stdio.EnvironmentVariables))
            {
                yield return value;
            }
        }

        if (config.Http is { } http)
        {
            foreach (var value in Values(http.Headers))
            {
                yield return value;
            }

            if (http.Endpoint.IsAbsoluteUri)
            {
                if (!string.IsNullOrEmpty(http.Endpoint.Query))
                {
                    yield return http.Endpoint.Query.TrimStart('?');
                }

                if (!string.IsNullOrEmpty(http.Endpoint.UserInfo))
                {
                    yield return http.Endpoint.UserInfo;
                }
            }

            if (http.OAuth?.ClientSecret is { Length: > 0 } clientSecret)
            {
                yield return clientSecret;
            }
        }

        if (config.OpenApi?.Credential is { Length: > 0 } openApiCredential)
        {
            yield return openApiCredential;
        }

        if (config.OpenRpc?.Credential is { Length: > 0 } openRpcCredential)
        {
            yield return openRpcCredential;
        }

        if (config.Cli is { } cli)
        {
            foreach (var value in Values(cli.EnvironmentVariables))
            {
                yield return value;
            }

            foreach (var argument in cli.Tools?.SelectMany(tool => tool.FixedArguments ?? []) ?? [])
            {
                yield return argument;
            }
        }

        if (config.Wasi is { } wasi)
        {
            foreach (var value in Values(wasi.Secrets))
            {
                yield return value;
            }

            foreach (var argument in wasi.HostArguments ?? [])
            {
                yield return argument;
            }
        }
    }

    private static IEnumerable<string> Values(IReadOnlyDictionary<string, string>? map)
        => map?.Values ?? [];
}
