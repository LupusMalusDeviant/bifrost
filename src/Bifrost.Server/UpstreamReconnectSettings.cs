using System.Globalization;
using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;

namespace Bifrost.Server;

/// <summary>
/// Liest die drei Betriebsoptionen der Wiederverbindung (Vertrag „Selbsttätige Wiederverbindung",
/// Abschnitt 3.2) und baut daraus die <see cref="SupervisorOptions"/>.
/// <para>
/// <b>Ein ungültiger Wert hält den Start nicht auf.</b> Er setzt die Vorgabe und erzeugt eine
/// Warnung. Ein Gateway, das wegen eines Tippfehlers in einer Wartefrist nicht hochkommt, wäre der
/// größere Schaden — die Vorgabe ist immer ein brauchbarer Wert.
/// </para>
/// </summary>
public static class UpstreamReconnectSettings
{
    public const string InitialSecondsKey = "BIFROST_UPSTREAM_RECONNECT_INITIAL_SECONDS";
    public const string MaxSecondsKey = "BIFROST_UPSTREAM_RECONNECT_MAX_SECONDS";
    public const string AuthSecondsKey = "BIFROST_UPSTREAM_RECONNECT_AUTH_SECONDS";

    private const int InitialDefault = 5;
    private const int InitialUpperBound = 300;
    private const int MaxDefault = 300;
    private const int MaxUpperBound = 3600;
    private const int AuthDefault = 900;
    private const int AuthUpperBound = 86400;

    /// <param name="read">Liefert den Rohwert eines Schlüssels oder <c>null</c>.</param>
    public static (SupervisorOptions Options, IReadOnlyList<string> Warnings) Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var warnings = new List<string>();

        var initial = Value(read, InitialSecondsKey, InitialDefault, 1, InitialUpperBound, warnings);

        // Die Vorgaben der abhängigen Werte rücken nach, wenn der Wert davor sie überholt: Wer nur
        // die Anfangsfrist hochsetzt, hat damit keinen ungültigen Deckel konfiguriert.
        var max = Value(read, MaxSecondsKey, Math.Max(MaxDefault, initial), initial, MaxUpperBound, warnings);
        var auth = Value(read, AuthSecondsKey, Math.Max(AuthDefault, max), max, AuthUpperBound, warnings);

        var options = new SupervisorOptions
        {
            DefaultRestartPolicy = RestartPolicy.Default with
            {
                InitialBackoff = TimeSpan.FromSeconds(initial),
                MaxBackoff = TimeSpan.FromSeconds(max),
            },
            AuthenticationRetryInterval = TimeSpan.FromSeconds(auth),
        };
        return (options, warnings);
    }

    private static int Value(
        Func<string, string?> read, string key, int fallback, int lower, int upper, List<string> warnings)
    {
        var raw = read(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= lower && parsed <= upper)
        {
            return parsed;
        }

        // Der Rohwert steht absichtlich nicht in der Warnung: Was in einer Umgebungsvariable
        // steht, die jemand vertauscht hat, weiß hier niemand.
        warnings.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"{key} ist ungültig (erlaubt: ganze Zahl von {lower} bis {upper}). Es gilt {fallback}."));
        return fallback;
    }
}
