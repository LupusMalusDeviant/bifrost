using System.Collections;

namespace Faolan.Abstractions;

/// <summary>
/// Die Generationen der Umgebungsvariablen: <c>MCPMCP_</c> → <c>BIFROST_</c> → <c>FAOLAN_</c>
/// (Vertrag <c>docs/contracts/umbenennung-faolan.md</c> §2.2).
/// <para>
/// <b>Die Kette steht an genau dieser Stelle.</b> Der Server, die Diagnose und die CLI lesen sie von
/// hier; die CLI hat keine Projektverweise und bindet deshalb diese Quelldatei selbst ein
/// (<c>&lt;Compile Include&gt;</c>, dort als <c>internal</c>). Eine dritte Umbenennung ist ein
/// weiterer Eintrag in <see cref="Generations"/>, keine neue Sonderregel.
/// </para>
/// <para>
/// <b>Der neueste gesetzte Name gewinnt.</b> Wer zwei Generationen gesetzt hat, ist gerade beim
/// Umstellen; der ältere Wert ist dann der zurückgelassene. Eine leer gesetzte Variable ist keine
/// Festlegung und zählt als nicht gesetzt.
/// </para>
/// </summary>
#if FAOLAN_CLI
internal
#else
public
#endif
static class EnvironmentNameChain
{
    /// <summary>Alle Präfixe, vom ältesten zum gültigen.</summary>
    public static IReadOnlyList<string> Generations { get; } = ["MCPMCP_", "BIFROST_", "FAOLAN_"];

    /// <summary>Der gültige Präfix.</summary>
    public static string CurrentPrefix => Generations[^1];

    /// <summary>Die abgelösten Präfixe, vom ältesten zum jüngsten.</summary>
    public static IReadOnlyList<string> LegacyPrefixes { get; } = [.. Generations.Take(Generations.Count - 1)];

    /// <summary>
    /// Liefert zu einem Namen der abgelösten Generationen den gültigen Namen, sonst <c>null</c>.
    /// </summary>
    public static string? CurrentNameOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var prefix in LegacyPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
            {
                return CurrentPrefix + name[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// Was übernommen werden müsste — als reine Rechnung über einer übergebenen Umgebung, damit sich
    /// die Regel prüfen lässt, ohne das Prozessumfeld eines Testlaufs anzufassen.
    /// <para>
    /// Je gültigem Namen höchstens ein Eintrag: der Wert der jüngsten gesetzten Generation. Sortiert
    /// nach dem alten Namen, damit die Meldung beim Start bei jedem Neustart gleich aussieht.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string OldName, string NewName, string Value)> PlanAdoption(IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Unter Windows sind Variablennamen unabhängig von Groß- und Kleinschreibung; ein gesetzter
        // neuer Name soll dort auch in anderer Schreibung als gesetzt gelten.
        var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && entry.Value is string { Length: > 0 } value)
            {
                present[key] = value;
            }
        }

        var plan = new List<(string OldName, string NewName, string Value)>();
        var planned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in present.Keys.Order(StringComparer.Ordinal))
        {
            var newName = CurrentNameOf(key);
            if (newName is null || present.ContainsKey(newName) || !planned.Add(newName))
            {
                continue;
            }

            // Die jüngste gesetzte Generation gewinnt, gleich unter welchem alten Namen der Fund war.
            var suffix = newName[CurrentPrefix.Length..];
            for (var generation = LegacyPrefixes.Count - 1; generation >= 0; generation--)
            {
                var candidate = LegacyPrefixes[generation] + suffix;
                if (present.TryGetValue(candidate, out var value))
                {
                    plan.Add((candidate, newName, value));
                    break;
                }
            }
        }

        plan.Sort((left, right) => string.CompareOrdinal(left.OldName, right.OldName));
        return plan;
    }

    /// <summary>
    /// Die Variable, in der das Image seine eigenen Vorgaben nennt: <c>NAME=Wert;NAME=Wert</c>.
    /// </summary>
    public const string ImageDefaultsVariable = "FAOLAN_IMAGE_DEFAULTS";

    /// <summary>
    /// Alte Einstellungen, die eine Vorgabe des Images still verdecken würde (Vertrag
    /// umbenennung-faolan §2.2, Nachtrag „Vorgaben des Images").
    /// <para>
    /// „Der neueste Name gewinnt" setzt voraus, dass der Betreiber beide Namen gesetzt hat. Setzt das
    /// Image den neuen Namen selbst (<c>FAOLAN_DATA_DIR=/data</c>), gewänne seine Vorgabe gegen das
    /// <c>BIFROST_DATA_DIR</c> des Betreibers — bis 0.12.0 war es umgekehrt. Der Gateway startete
    /// dann auf einem leeren Verzeichnis und meldete sich als bereit. Statt einer der beiden Seiten
    /// still recht zu geben, ist das ein Konflikt: Der Start bricht ab und nennt beide Variablen.
    /// </para>
    /// <para>
    /// Kein Konflikt ist, wenn der alte Wert der Vorgabe gleicht, und wenn der neue Name einen
    /// anderen Wert als die Vorgabe trägt — dann hat ihn der Betreiber gesetzt, und die Regel oben
    /// gilt.
    /// </para>
    /// <para>
    /// <b>Auch als Datei.</b> <c>BIFROST_DATA_DIR_FILE</c> ist dieselbe Einstellung wie
    /// <c>BIFROST_DATA_DIR</c>; ihr Inhalt wird gelesen und genauso verglichen. Eine Datei, die sich
    /// nicht lesen lässt, ist ebenfalls ein Abbruchgrund — raten wäre wieder die stille Vorgabe.
    /// </para>
    /// <para>
    /// <b>Werte stehen nie in der Meldung</b>, nur Namen: Eine Variable mit <c>_FILE</c> ist meist ein
    /// Geheimnis, und ein abgeleitetes Image kann auch für eine Verbindungszeichenfolge eine Vorgabe
    /// erklären. Eine unlesbare Deklaration bricht den Start ebenfalls ab, statt die Prüfung still zu
    /// übergehen.
    /// </para>
    /// </summary>
    /// <param name="readFile">Liest eine Datei; ersetzbar für Tests. Vorgabe: <see cref="File.ReadAllText(string)"/>.</param>
    /// <returns>Je Problem eine fertige Meldung mit Abhilfe; leer heißt: Start frei.</returns>
    public static IReadOnlyList<string> FindImageDefaultConflicts(
        IDictionary environment, Func<string, string>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        readFile ??= File.ReadAllText;

        var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && entry.Value is string { Length: > 0 } value)
            {
                present[key] = value;
            }
        }

        var problems = new List<string>();
        if (!present.TryGetValue(ImageDefaultsVariable, out var declared))
        {
            return problems;
        }

        foreach (var pair in declared.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var newName = separator > 0 ? pair[..separator] : string.Empty;
            if (separator <= 0
                || !newName.StartsWith(CurrentPrefix, StringComparison.Ordinal)
                || newName.Length == CurrentPrefix.Length)
            {
                problems.Add(
                    $"Start abgebrochen: {ImageDefaultsVariable} enthält einen Eintrag, der nicht die Form "
                    + $"'{CurrentPrefix}NAME=Wert' hat. Ohne lesbare Deklaration lässt sich nicht prüfen, ob eine "
                    + "Vorgabe des Images eine alte Einstellung verdeckt. Die Deklaration im Image berichtigen.");
                continue;
            }

            var imageValue = pair[(separator + 1)..];
            if (!present.TryGetValue(newName, out var current) || !string.Equals(current, imageValue, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = newName[CurrentPrefix.Length..];
            for (var generation = LegacyPrefixes.Count - 1; generation >= 0; generation--)
            {
                var oldName = LegacyPrefixes[generation] + suffix;
                string? oldValue;
                var shownName = oldName;
                if (present.TryGetValue(oldName, out var direct))
                {
                    oldValue = direct;
                }
                else if (present.TryGetValue(oldName + "_FILE", out var path))
                {
                    shownName = oldName + "_FILE";
                    try
                    {
                        oldValue = readFile(path).TrimEnd('\r', '\n');
                    }
#pragma warning disable CA1031 // Jeder Lesefehler führt zum selben Ergebnis: Abbruch mit Namen.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                        problems.Add(
                            $"Start abgebrochen: {shownName} ist gesetzt, die Datei lässt sich aber nicht lesen. "
                            + $"Das Image setzt {newName} selbst; ohne den alten Wert lässt sich nicht entscheiden, "
                            + $"welcher gemeint ist. {newName} (oder {newName}_FILE) setzen und {shownName} entfernen.");
                        break;
                    }
                }
                else
                {
                    continue;
                }

                if (!string.Equals(oldValue, imageValue, StringComparison.Ordinal))
                {
                    problems.Add(DescribeConflict(shownName, newName, oldValue));
                }

                break;
            }
        }

        return problems;
    }

    private static string DescribeConflict(string oldName, string newName, string oldValue)
    {
        var start = $"Start abgebrochen: {oldName} ist gesetzt und weicht von der Vorgabe ab, die das Image "
            + $"für {newName} mitbringt. Bis 0.12.0 galt der Wert des Betreibers, nach der Umbenennung gälte "
            + "still die Vorgabe des Images. ";

        // Der alte Wert nennt den Host, den das Image bis 0.12.0 mitbrachte. Ihn unter dem neuen Namen
        // zu setzen, zeigte auf ein Programm, das es im neuen Image nicht mehr gibt.
        if (newName.EndsWith("_WASI_HOST", StringComparison.Ordinal) && WasiHostNames.UsesLegacyName(oldValue))
        {
            return start + $"Der alte Wert nennt den bis 0.12.0 mitgelieferten Host: {oldName} entfernen, das "
                + "Image bringt den umbenannten Host selbst mit. Siehe docs/upgrade-faolan.md.";
        }

        return start + $"Ist der alte Wert gemeint, ihn unter {newName} setzen (bei einem Host: der Pfad des "
            + $"tatsächlich eingebundenen Programms); ist die Vorgabe gemeint, {oldName} entfernen. "
            + "Siehe docs/upgrade-faolan.md.";
    }

    /// <summary>
    /// Übernimmt alle noch alt benannten Variablen ins Prozessumfeld und liefert die Namen, unter
    /// denen sie gefunden wurden. Läuft vor dem Bau jeder Konfiguration.
    /// </summary>
    public static IReadOnlyList<string> Adopt()
    {
        var plan = PlanAdoption(Environment.GetEnvironmentVariables());
        foreach (var (_, newName, value) in plan)
        {
            Environment.SetEnvironmentVariable(newName, value);
        }

        return [.. plan.Select(entry => entry.OldName)];
    }
}
