using McpMcp.Abstractions;

namespace McpMcp.Core.Packaging;

/// <summary>
/// Was ein Paket je Vertrauensstufe verlangen darf (ADR-0016).
/// <para>
/// Die Stufe entscheidet <b>nicht</b>, ob ein Connector läuft — dafür ist die Signatur zuständig —
/// sondern wie viel er ohne Rückfrage bekommt. Ein gepinnter Schlüssel heißt „dieser Herausgeber
/// ist echt", nicht „dieser Herausgeber darf ins Dateisystem".
/// </para>
/// </summary>
public static class ConnectorTrustPolicy
{
    /// <summary>
    /// Prüft die Anforderungen des Manifests gegen die Stufe und liefert die tatsächlich erteilten
    /// Zugriffe. Wirft, wenn etwas verlangt wird, dem niemand zugestimmt hat.
    /// </summary>
    /// <param name="acceptedGrants">
    /// Vom Administrator beim Installieren ausdrücklich zugestimmte Einträge in der Form
    /// <c>fs-read:/pfad</c> (siehe <see cref="ConnectorGrantRequest.Enumerate"/>). Die Zustimmung
    /// bezieht sich auf genau das, was im Manifest steht — eine pauschale „ja zu allem"-Angabe gibt
    /// es bewusst nicht, sonst wäre die Liste im Manifest bedeutungslos.
    /// </param>
    /// <param name="allowUntrusted">
    /// Freigabe des Pakets selbst. Nur für <see cref="ConnectorTrustLevel.Community"/> relevant.
    /// </param>
    public static IReadOnlyList<string> Evaluate(
        ConnectorManifest manifest,
        ConnectorTrustLevel level,
        IReadOnlyList<string>? acceptedGrants,
        bool allowUntrusted)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (level is ConnectorTrustLevel.Core)
        {
            // „Core" heißt: mit dem Produkt ausgeliefert und gleich versioniert. Ein nachträglich
            // installiertes Paket kann das nicht sein, und ihm die Stufe zu geben hiesse, jede
            // weitere Prüfung zu überspringen.
            throw new ConnectorPackageException(
                "Die Stufe 'Core' ist mit dem Produkt ausgelieferter Code und nicht installierbar. "
                + "Für ein Paket kommen 'Official', 'ThirdParty' oder 'Community' in Frage.");
        }

        if (level is ConnectorTrustLevel.Community && !allowUntrusted)
        {
            throw new ConnectorPackageException(
                $"Der Herausgeber von '{manifest.Id}' ist nicht eingestuft (Community). Die "
                + "Installation braucht eine ausdrückliche Freigabe — deny-by-default.");
        }

        var requested = manifest.GrantsOrNone.Enumerate();
        if (requested.Count == 0)
        {
            return [];
        }

        if (level is ConnectorTrustLevel.Official)
        {
            // Ein offizielles Paket bekommt, was im Manifest steht. Das Manifest ist signiert, also
            // ist die Liste selbst nicht manipulierbar — sie bleibt im Audit sichtbar.
            return requested;
        }

        var accepted = new HashSet<string>(acceptedGrants ?? [], StringComparer.Ordinal);
        var missing = requested.Where(r => !accepted.Contains(r)).ToList();
        if (missing.Count > 0)
        {
            throw new ConnectorPackageException(
                $"'{manifest.Id}' verlangt Zugriffe, denen niemand zugestimmt hat: "
                + $"{string.Join(", ", missing)}. Bei Stufe {level} ist jeder Zugriff nach außen "
                + "einzeln zu bestätigen.");
        }

        // Zugestimmt wird genau das Verlangte — eine Zustimmung zu etwas, das gar nicht im Manifest
        // steht, wird nicht zu einem Grant. Sonst wüchse die Berechtigung mit einem Tippfehler.
        return requested;
    }
}
