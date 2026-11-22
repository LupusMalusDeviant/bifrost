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
    /// </summary>
    public static IReadOnlyList<(string OldName, string OldValue, string NewName, string ImageValue)> FindImageDefaultConflicts(
        IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && entry.Value is string { Length: > 0 } value)
            {
                present[key] = value;
            }
        }

        var conflicts = new List<(string OldName, string OldValue, string NewName, string ImageValue)>();
        if (!present.TryGetValue(ImageDefaultsVariable, out var declared))
        {
            return conflicts;
        }

        foreach (var pair in declared.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var newName = pair[..separator];
            var imageValue = pair[(separator + 1)..];
            if (!newName.StartsWith(CurrentPrefix, StringComparison.Ordinal)
                || !present.TryGetValue(newName, out var current)
                || !string.Equals(current, imageValue, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = newName[CurrentPrefix.Length..];
            for (var generation = LegacyPrefixes.Count - 1; generation >= 0; generation--)
            {
                var oldName = LegacyPrefixes[generation] + suffix;
                if (present.TryGetValue(oldName, out var oldValue))
                {
                    if (!string.Equals(oldValue, imageValue, StringComparison.Ordinal))
                    {
                        conflicts.Add((oldName, oldValue, newName, imageValue));
                    }

                    break;
                }
            }
        }

        return conflicts;
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
