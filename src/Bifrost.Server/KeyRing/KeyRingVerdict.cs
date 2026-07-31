using System.Globalization;

namespace Bifrost.Server.KeyRing;

/// <summary>Wie die Lage des Key-Rings beim Start zu bewerten ist.</summary>
public enum KeyRingVerdictKind
{
    /// <summary>Kein Ring, kein Zeuge, kein Geheimtext. Eine frische Instanz — der Start legt einen Ring an.</summary>
    FreshInstance = 0,

    /// <summary>Der Ring ist da und passt zu dem, was diese Instanz von ihm weiß.</summary>
    Established = 1,

    /// <summary>
    /// Der Ring ist da, aber es ist ein <b>anderer</b>: Kein einziger der zuletzt gesehenen
    /// Schlüssel liegt noch vor. Der Start läuft weiter — der vorhandene Ring könnte aus einer
    /// Wiederherstellung stammen —, aber er läuft nicht stillschweigend weiter.
    /// </summary>
    Replaced = 2,

    /// <summary>
    /// <b>Schlüsselmaterial fehlt, das vorhanden sein müsste.</b> Der Start bricht ab. Ein neuer Ring
    /// entsteht hier nicht: Er würde die Instanz als „bereit" melden, während jedes gespeicherte
    /// Geheimnis unlesbar ist — und der nächste Schreibvorgang überschriebe die letzte Spur davon.
    /// </summary>
    Lost = 3,

    /// <summary>
    /// Der Ring liegt vor, lässt sich mit der konfigurierten Zertifikatslage aber nicht lesen —
    /// typischerweise das falsche Zertifikat. Ebenfalls Abbruch: DataProtection legt bei einem
    /// unlesbaren Ring von sich aus einen <b>neuen Schlüssel</b> an, und ab da schreibt die Instanz
    /// Geheimtext, den das alte Zertifikat nie wieder öffnet.
    /// </summary>
    Unreadable = 4,
}

/// <summary>
/// Das Urteil über den Key-Ring beim Start — die Kernaussage von WP3.3.
/// </summary>
/// <param name="Kind">Die Bewertung.</param>
/// <param name="Summary">Ein Satz für Menschen.</param>
/// <param name="Remediation">Was zu tun ist. <c>null</c>, wenn nichts zu tun ist.</param>
public sealed record KeyRingVerdict(KeyRingVerdictKind Kind, string Summary, string? Remediation)
{
    /// <summary>Verhindert dieses Urteil den Start?</summary>
    public bool Blocks => Kind is KeyRingVerdictKind.Lost or KeyRingVerdictKind.Unreadable;
}

/// <summary>
/// Die Beweislage, aus der das Urteil entsteht. Sie steht getrennt vom Einsammeln, damit jede
/// Kombination prüfbar ist, ohne ein Volume zu verlieren.
/// </summary>
/// <param name="KeyFileCount">Zahl der Schlüsseldateien im Ring.</param>
/// <param name="PresentKeyIds">Die Ids der vorhandenen Schlüssel.</param>
/// <param name="Witness">Was beim letzten Start festgehalten wurde, oder <c>null</c>.</param>
/// <param name="WitnessUnreadable">
/// Es stand etwas da, war aber nicht lesbar. Ausdrücklich <b>nicht</b> dasselbe wie „nichts da".
/// </param>
/// <param name="EncryptedRowCount">
/// Zeilen mit Geheimtext in der Datenbank. Das ist der Beweis, der auch dann noch da ist, wenn der
/// Zeuge zusammen mit dem Volume verschwunden ist — der Fall, in dem die Datenbank in PostgreSQL
/// liegt und nur das Datenverzeichnis neu ist.
/// </param>
/// <param name="CiphertextKnown">
/// Ließ sich die Datenbank überhaupt befragen? Eine frische SQLite-Datei hat die Tabellen noch
/// nicht; das ist kein Beweis für „kein Geheimtext", sondern gar kein Beweis.
/// </param>
public sealed record KeyRingEvidence(
    int KeyFileCount,
    IReadOnlyList<string> PresentKeyIds,
    KeyRingWitnessRecord? Witness,
    bool WitnessUnreadable,
    long EncryptedRowCount,
    bool CiphertextKnown);

/// <summary>
/// Die Regel, nach der aus der Beweislage ein Urteil wird.
/// <para>
/// <b>Der Kern:</b> Ein leerer Ring ist genau dann harmlos, wenn nichts darauf hindeutet, dass es
/// je einen gab. Sobald etwas darauf hindeutet — ein Zeugeneintrag oder Geheimtext in der
/// Datenbank —, ist ein leerer Ring ein Datenverlust und kein Anlass, einen neuen anzulegen.
/// </para>
/// </summary>
public static class KeyRingJudgement
{
    public static KeyRingVerdict Judge(KeyRingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.KeyFileCount > 0)
        {
            return WithKeys(evidence);
        }

        // Ab hier: kein einziger Schlüssel im Ring.
        if (evidence.WitnessUnreadable)
        {
            return new KeyRingVerdict(
                KeyRingVerdictKind.Lost,
                "Das Schlüsselverzeichnis ist leer, und der Zeugeneintrag dieser Instanz ist "
                + "vorhanden, aber unlesbar. Ob hier je ein Key-Ring lag, lässt sich damit nicht "
                + "ausschließen.",
                "Den Zeugeneintrag prüfen. Ist er nur beschädigt und die Instanz nachweislich neu, "
                + "kann er entfernt werden. Steht die Datenbank dieser Instanz noch, gehört der "
                + "Key-Ring aus der Sicherung zurück (docs/operations.md, 'Key-Ring schützen').");
        }

        if (evidence.Witness is { } witness)
        {
            return new KeyRingVerdict(
                KeyRingVerdictKind.Lost,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Das Schlüsselverzeichnis ist leer, obwohl diese Instanz zuletzt "
                    + $"{witness.KeyCount} Schlüssel hatte (Stand {witness.WrittenAt:u}). Der "
                    + $"Key-Ring ist weg — sämtliche gespeicherten Upstream-Zugangsdaten, "
                    + $"OAuth-Token und Webhook-Secrets sind damit nicht mehr entschlüsselbar."),
                "Es wird KEIN neuer Ring angelegt: Er würde die Instanz als 'bereit' melden, "
                + "während nichts davon lesbar ist. Zuerst prüfen, ob das Datenverzeichnis "
                + "(BIFROST_DATA_DIR) auf das richtige Volume zeigt — ein umbenanntes Volume sieht "
                + "genau so aus. Andernfalls den Key-Ring aus der Sicherung zurückspielen "
                + "(ADR-0024: er liegt im Vollbackup unter 'keyring/').");
        }

        if (evidence.CiphertextKnown && evidence.EncryptedRowCount > 0)
        {
            return new KeyRingVerdict(
                KeyRingVerdictKind.Lost,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Das Schlüsselverzeichnis ist leer, aber die Datenbank enthält "
                    + $"{evidence.EncryptedRowCount} Datensätze mit Geheimtext. Dieser Geheimtext "
                    + $"kann nur mit einem Key-Ring entstanden sein, der jetzt fehlt."),
                "Es wird KEIN neuer Ring angelegt. Datenverzeichnis und Datenbank gehören "
                + "zusammen — hier passen sie nicht zueinander. Entweder zeigt BIFROST_DATA_DIR auf "
                + "das falsche Volume, oder es wurde eine Datenbank ohne den zugehörigen Key-Ring "
                + "zurückgespielt (ADR-0024 E3: beides gehört in dieselbe Sicherung).");
        }

        return new KeyRingVerdict(
            KeyRingVerdictKind.FreshInstance,
            "Kein Key-Ring vorhanden, und nichts deutet darauf hin, dass es je einen gab: keine "
            + "Zeugendatei, kein Geheimtext in der Datenbank. Der Start legt einen an.",
            null);
    }

    private static KeyRingVerdict WithKeys(KeyRingEvidence evidence)
    {
        var description = string.Create(
            CultureInfo.InvariantCulture, $"Der Key-Ring enthält {evidence.KeyFileCount} Schlüssel.");

        if (evidence.Witness is not { KeyIds.Count: > 0 } witness)
        {
            return new KeyRingVerdict(KeyRingVerdictKind.Established, description, null);
        }

        var present = evidence.PresentKeyIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (witness.KeyIds.Any(present.Contains))
        {
            return new KeyRingVerdict(KeyRingVerdictKind.Established, description, null);
        }

        // Kein Abbruch: Ein vollständig ausgetauschter Ring ist auch das Ergebnis einer legitimen
        // Wiederherstellung. Aber er ist ebenso das Ergebnis eines vertauschten Volumes, und
        // stillschweigend darf keiner der beiden Fälle durchgehen.
        return new KeyRingVerdict(
            KeyRingVerdictKind.Replaced,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{description} Keiner davon ist einer der {witness.KeyIds.Count} Schlüssel, die "
                + $"diese Instanz zuletzt gesehen hat (Stand {witness.WrittenAt:u}) — der Ring wurde "
                + $"vollständig ausgetauscht."),
            "War das eine Wiederherstellung, ist alles in Ordnung und dieser Hinweis erscheint "
            + "genau einmal. War es keine, zeigt das Datenverzeichnis auf ein fremdes Volume: Dann "
            + "ist mit diesem Ring kein bestehender Geheimtext dieser Instanz lesbar.");
    }
}
