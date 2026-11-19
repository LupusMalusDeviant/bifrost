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

        // Alle Treffer werden am unveränderten Text gesucht und erst danach gemeinsam ersetzt. Der
        // frühere Weg — ein Wert nach dem anderen — verlor Werte mit gemeinsamem Anfang: Maskierte der
        // eine nur das gemeinsame Stück („sk-ant-…"), fehlte dem anderen danach der Anker, und sein
        // Rest blieb stehen. Überlappende und aneinanderstoßende Treffer werden zu einem.
        var ranges = new List<(int Start, int End)>();
        foreach (var (value, prefixable) in candidates
                     .Where(candidate => !string.IsNullOrEmpty(candidate.Value))
                     .Distinct())
        {
            // Sehr kurze Werte würden halbe Fehlermeldungen zerschießen und sind ohnehin keine Secrets.
            if (value.Length >= 4)
            {
                AddOccurrences(message, value, ranges);
            }

            if (prefixable && value.Length > MinimumPrefixLength)
            {
                AddPrefixes(message, value, ranges);
            }
        }

        if (ranges.Count == 0)
        {
            return message;
        }

        ranges.Sort();
        var masked = new System.Text.StringBuilder(message.Length);
        var position = 0;
        var index = 0;
        while (index < ranges.Count)
        {
            var (start, end) = ranges[index];
            while (++index < ranges.Count && ranges[index].Start <= end)
            {
                end = Math.Max(end, ranges[index].End);
            }

            masked.Append(message, position, start - position).Append("***");
            position = end;
        }

        return masked.Append(message, position, message.Length - position).ToString();
    }

    private static void AddOccurrences(string message, string value, List<(int Start, int End)> ranges)
    {
        for (var hit = message.IndexOf(value, StringComparison.Ordinal);
             hit >= 0;
             hit = message.IndexOf(value, hit + 1, StringComparison.Ordinal))
        {
            ranges.Add((hit, hit + value.Length));
        }
    }

    /// <summary>Jedes Textstück, das mit mindestens <see cref="MinimumPrefixLength"/> Zeichen des Werts beginnt.</summary>
    private static void AddPrefixes(string message, string secret, List<(int Start, int End)> ranges)
    {
        var probe = secret[..MinimumPrefixLength];
        for (var hit = message.IndexOf(probe, StringComparison.Ordinal);
             hit >= 0;
             hit = message.IndexOf(probe, hit + 1, StringComparison.Ordinal))
        {
            var length = MinimumPrefixLength;
            while (hit + length < message.Length
                && length < secret.Length
                && message[hit + length] == secret[length])
            {
                length++;
            }

            ranges.Add((hit, hit + length));
        }
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
}
