using System.Globalization;

namespace Bifrost.Compatibility.Tests.Matrix;

/// <summary>
/// Prüft eine Matrix gegen den eingefrorenen Vertrag (M5 §5). Der Validator ist das, was verhindert,
/// dass aus der Matrix eine Werbeaussage wird: Ohne ihn könnte jede Zeile ein <c>pass</c> tragen,
/// hinter dem nichts steht.
/// </summary>
public static class CompatibilityMatrixValidator
{
    public static IReadOnlyList<string> Validate(CompatibilityMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var errors = new List<string>();

        if (matrix.SchemaVersion != 1)
        {
            errors.Add($"schemaVersion {matrix.SchemaVersion} ist unbekannt; der Vertrag kennt 1.");
        }

        if (matrix.Entries.Count == 0)
        {
            errors.Add("Die Matrix hat keine Einträge. Eine leere Matrix belegt nichts.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in matrix.Entries)
        {
            ValidateEntry(entry, errors);

            if (!seen.Add(entry.Key))
            {
                errors.Add($"{entry.Key}: doppelte Zeile. Zwei Zeilen für denselben Lauf widersprechen sich irgendwann.");
            }
        }

        foreach (var client in CompatibilityVocabulary.MandatoryClients)
        {
            if (!matrix.Entries.Any(e => string.Equals(e.Client, client, StringComparison.Ordinal)))
            {
                errors.Add(
                    $"Pflichtclient '{client}' (FR-P061) fehlt vollständig. Ungeprüft heißt 'untested', nicht 'nicht erwähnt'.");
            }
        }

        return errors;
    }

    private static void ValidateEntry(CompatibilityEntry entry, List<string> errors)
    {
        var key = string.IsNullOrWhiteSpace(entry.Client) ? "(ohne client)" : entry.Key;

        RequireText(entry.Client, $"{key}: 'client' fehlt.", errors);
        RequireText(entry.ClientVersion, $"{key}: 'clientVersion' fehlt.", errors);
        RequireText(entry.ProtocolVersion, $"{key}: 'protocolVersion' fehlt.", errors);

        RequireVocabulary(entry.Os, CompatibilityVocabulary.OperatingSystems, $"{key}: 'os'", errors);
        RequireVocabulary(entry.Transport, CompatibilityVocabulary.Transports, $"{key}: 'transport'", errors);
        RequireVocabulary(entry.Mode, CompatibilityVocabulary.Modes, $"{key}: 'mode'", errors);

        foreach (var feature in CompatibilityFeatures.All)
        {
            if (!entry.Features.ContainsKey(feature))
            {
                errors.Add($"{key}: Feature '{feature}' fehlt. Alle acht Schlüssel sind Pflicht (M5 §5).");
            }
        }

        foreach (var (feature, value) in entry.Features)
        {
            if (!CompatibilityFeatures.All.Contains(feature, StringComparer.Ordinal))
            {
                errors.Add($"{key}: '{feature}' gehört nicht zum geschlossenen Feature-Wortschatz.");
            }

            if (!CompatibilityValues.All.Contains(value, StringComparer.Ordinal))
            {
                errors.Add(
                    $"{key}: Wert '{value}' für '{feature}' ist unzulässig ({string.Join(", ", CompatibilityValues.All)}).");
            }
        }

        ValidateEvidence(entry, key, errors);
    }

    /// <summary>
    /// Die Kernregel: Eine Zeile, die etwas behauptet, trägt einen Beleg — und eine Zeile, die nur
    /// eine Lücke benennt, trägt keinen. Ein Platzhalter mit Beleg wäre ein Widerspruch: belegt,
    /// dass nichts geprüft wurde.
    /// </summary>
    private static void ValidateEvidence(CompatibilityEntry entry, string key, List<string> errors)
    {
        if (entry.IsPlaceholder)
        {
            if (entry.Evidence is not null)
            {
                errors.Add($"{key}: Alle Features stehen auf 'untested', trotzdem hängt ein Beleg daran.");
            }

            return;
        }

        if (entry.Evidence is null)
        {
            errors.Add($"{key}: Zeile behauptet etwas, nennt aber keinen Beleg (M5 §5).");
            return;
        }

        var evidence = entry.Evidence;
        RequireVocabulary(evidence.Kind, CompatibilityVocabulary.EvidenceKinds, $"{key}: 'evidence.kind'", errors);
        RequireText(evidence.Ref, $"{key}: 'evidence.ref' fehlt (Lauf-URL oder Protokollpfad).", errors);

        if (!DateOnly.TryParseExact(evidence.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"{key}: 'evidence.date' ist kein ISO-Datum (yyyy-MM-dd): '{evidence.Date}'.");
        }

        if (string.Equals(evidence.Kind, "manual", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(evidence.Operator))
        {
            errors.Add($"{key}: Handprüfung ohne 'evidence.operator'. Ein Protokoll ohne Prüfer ist keins (M5 §1.4).");
        }

        if (string.Equals(evidence.Kind, "automated", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(evidence.Operator))
        {
            errors.Add($"{key}: Ein automatisierter Lauf hat keinen 'operator' — das verwischt den Unterschied zur Handprüfung.");
        }
    }

    private static void RequireText(string value, string message, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(message);
        }
    }

    private static void RequireVocabulary(
        string value, IReadOnlyList<string> allowed, string what, List<string> errors)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            errors.Add($"{what}: '{value}' ist unzulässig ({string.Join(" | ", allowed)}).");
        }
    }
}
