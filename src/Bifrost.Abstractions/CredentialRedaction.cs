namespace Bifrost.Abstractions;

/// <summary>
/// Maskiert bekannte Geheimwerte in fremdem Fehlertext (Vertrag „Selbsttätige Wiederverbindung",
/// Abschnitte 6 und 10). Liegt hier und nicht im Core, weil auch ein Connector bereinigen muss: Was er
/// beim Verbindungsaufbau vorgelegt hat (ein OAuth-Token), kennt nach seinem Fehlschlag sonst niemand
/// mehr (Plan 0005 G1).
/// <para>
/// Bewusst exakter Wertabgleich statt Mustererkennung: Die konkreten Geheimnisse sind bekannt, raten
/// muss also niemand. <b>Auch Anfangsstücke werden maskiert:</b> Ein Upstream, der einen abgelehnten
/// Schlüssel „zur Hilfe" gekürzt zitiert, verrät damit genug, um ihn zuzuordnen.
/// </para>
/// </summary>
public static class CredentialRedaction
{
    /// <summary>Ab so vielen übereinstimmenden Anfangszeichen gilt ein Textstück als Teil eines Geheimwerts.</summary>
    public const int MinimumPrefixLength = 6;

    /// <summary>Unter diesem Schlüssel trägt eine bereinigte Ausnahme die Typnamen der ursprünglichen Kette.</summary>
    public const string OriginalTypesDataKey = "Bifrost.UpstreamFailure.OriginalTypes";

    /// <summary>Maskiert die Geheimwerte <paramref name="secrets"/> samt ihrer Anfangsstücke.</summary>
    public static string Scrub(string message, IEnumerable<string>? secrets)
        => Scrub(message, (secrets ?? []).Where(value => !string.IsNullOrEmpty(value)).SelectMany(Variants));

    /// <summary>
    /// Maskiert Kandidaten. <c>Prefixable</c> sagt, ob auch Anfangsstücke des Werts maskiert werden.
    /// </summary>
    public static string Scrub(string message, IEnumerable<(string Value, bool Prefixable)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        // Längste zuerst: Sonst zerlegt ein kurzer Wert einen längeren, der ihn enthält, und der
        // Rest des längeren bleibt stehen.
        var ordered = candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Value))
            .Distinct()
            .OrderByDescending(candidate => candidate.Value.Length)
            .ToList();

        foreach (var (value, _) in ordered)
        {
            // Sehr kurze Werte würden halbe Fehlermeldungen zerschießen und sind ohnehin keine Secrets.
            if (value.Length >= 4)
            {
                message = message.Replace(value, "***", StringComparison.Ordinal);
            }
        }

        foreach (var (value, prefixable) in ordered)
        {
            if (prefixable && value.Length > MinimumPrefixLength)
            {
                message = MaskPrefixes(message, value);
            }
        }

        return message;
    }

    /// <summary>
    /// Ein Kopfzeilenwert der Form „Bearer abc…" ist zweierlei: der ganze Wert und der Teil nach
    /// dem Schema. Das Anfangsstück wird nur am zweiten gemessen — sonst maskierte „Bearer" als
    /// Anfang von „Bearer abc…" das Wort in jeder Meldung, die es benutzt.
    /// </summary>
    public static IEnumerable<(string Value, bool Prefixable)> Variants(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var space = secret.IndexOf(' ', StringComparison.Ordinal);
        var hasScheme = space is > 0 and <= 10
            && space < secret.Length - 1
            && secret.AsSpan(0, space).IndexOfAnyExceptInRange('A', 'z') < 0;
        if (!hasScheme)
        {
            yield return (secret, true);
            yield break;
        }

        yield return (secret, false);
        yield return (secret[(space + 1)..].TrimStart(), true);
    }

    /// <summary>
    /// Trägt irgendeine Meldung der Ausnahmekette einen der Geheimwerte oder dessen Anfang? Nur dann
    /// muss eine Ausnahme ersetzt werden; sonst bleibt sie, wie sie ist — samt Typ und Statuscode.
    /// </summary>
    public static bool Exposes(Exception exception, IReadOnlyList<string>? secrets)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (secrets is not { Count: > 0 })
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.Equals(Scrub(current.Message, secrets), current.Message, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Die Typnamen der Ausnahmekette. Das geht ins Log anstelle des Ausnahmeobjekts — dessen
    /// Textform trüge die ungefilterte Meldung. Bei einer bereinigten Ausnahme stehen danach die
    /// Typnamen der ursprünglichen Kette (<see cref="OriginalTypesDataKey"/>).
    /// </summary>
    public static string TypeChain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var names = new List<string>();
        for (var current = exception; current is not null && names.Count < 6; current = current.InnerException)
        {
            names.Add(current.GetType().Name);
            if (current.Data[OriginalTypesDataKey] is string original)
            {
                names.Add(original);
                break;
            }
        }

        return string.Join(" → ", names);
    }

    private static string MaskPrefixes(string message, string secret)
    {
        var probe = secret[..MinimumPrefixLength];
        System.Text.StringBuilder? masked = null;
        var position = 0;
        while (position < message.Length)
        {
            var hit = message.IndexOf(probe, position, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            var length = MinimumPrefixLength;
            while (hit + length < message.Length
                && length < secret.Length
                && message[hit + length] == secret[length])
            {
                length++;
            }

            masked ??= new System.Text.StringBuilder(message.Length);
            masked.Append(message, position, hit - position).Append("***");
            position = hit + length;
        }

        if (masked is null)
        {
            return message;
        }

        return masked.Append(message, position, message.Length - position).ToString();
    }
}
