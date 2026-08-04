using Bifrost.Abstractions;

namespace Bifrost.Core.Audit;

/// <param name="Sequence">Die Zeile, an der es zuerst nicht mehr aufgeht.</param>
/// <param name="Reason">Im Klartext, was nicht stimmt — nicht „ungültig".</param>
public sealed record AuditChainBreak(long Sequence, Guid? ChainId, string Reason);

/// <param name="Checked">Wie viele verkettete Einträge nachgerechnet wurden.</param>
/// <param name="Unchained">
/// Einträge ohne Kettenglied. Das sind die aus der Zeit vor der Kette — sie sind kein Befund,
/// aber auch nicht gedeckt, und deshalb stehen sie da.
/// </param>
/// <param name="FirstBreak">
/// Die <b>erste</b> gebrochene Stelle, nicht irgendeine. Ein Prüfer, der nur „ungültig" sagt, lässt
/// einen Betreiber mit einem Log von Millionen Zeilen allein.
/// </param>
/// <param name="Limits">
/// Was diese Prüfung <b>nicht</b> zeigt. Steht im Ergebnis und nicht nur in der Dokumentation:
/// Ein „Kette intakt", das man für „nichts fehlt" hält, ist gefährlicher als gar keine Kette.
/// </param>
public sealed record AuditChainReport(
    long Checked,
    long Unchained,
    IReadOnlyList<AuditChainSegment> Segments,
    AuditChainBreak? FirstBreak,
    IReadOnlyList<string> Limits)
{
    public bool IsIntact => FirstBreak is null;
}

/// <summary>
/// Rechnet die Integritätskette nach (M6/WP6.2, FR-P073).
/// <para>
/// <b>Zwei verschiedene Prüfungen, und beide werden gebraucht:</b> Der Eintrag wird gegen seinen
/// eigenen gespeicherten Vorgängerhash nachgerechnet — das findet ein <em>geändertes Feld</em>. Und
/// der gespeicherte Vorgängerhash wird gegen den tatsächlichen Vorgänger gehalten — das findet eine
/// <em>entfernte oder umsortierte Zeile</em>. Nur die erste Prüfung wäre eine Prüfsumme je Zeile:
/// Wer eine Zeile löscht, käme damit durch.
/// </para>
/// </summary>
public static class AuditChainVerifier
{
    /// <summary>
    /// Der Anspruch, ausgeschrieben. Er gehört in jede Ausgabe des Prüfers — der M6-Vertrag
    /// verlangt das ausdrücklich, und der Grund ist, dass „Kette intakt" sonst als „nichts fehlt"
    /// gelesen wird.
    /// </summary>
    public static IReadOnlyList<string> ClaimLimits { get; } =
    [
        "Die Kette erschwert nachträgliche Änderungen an dieser Ablage. Sie ist kein Beweis für "
        + "Vollständigkeit: Wer die letzten Einträge eines Abschnitts entfernt, hinterlässt keine "
        + "Lücke, die von hier aus sichtbar wäre.",
        "Gegen einen übernommenen Wirt schützt sie nicht. Wer auf dem Rechner schreiben darf, kann "
        + "die Kette neu rechnen — dann stimmt sie wieder und sagt nichts mehr aus.",
        "Ein Abschnitt entspricht einem Schreiberlauf. Ein Neustart beginnt einen neuen; dass ein "
        + "ganzer Abschnitt fehlt, ist hier nicht erkennbar.",
        "Geprüft wird nur, was der Datenstrom enthält. Über einen gefilterten Ausschnitt lässt sich "
        + "keine Aussage über die Kette treffen — deshalb nimmt der Prüfer keine Inhaltsfilter an.",
    ];

    public static async Task<AuditChainReport> VerifyAsync(
        IAsyncEnumerable<AuditExportEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var segments = new List<AuditChainSegment>();
        AuditChainBreak? firstBreak = null;
        long verified = 0;
        long unchained = 0;
        var isFirstOfStream = true;

        Guid? currentChain = null;
        long expectedIndex = 0;
        string? runningHash = null;
        long segmentStart = 0;
        long segmentCount = 0;
        long segmentLast = 0;

        await foreach (var entry in entries.WithCancellation(ct).ConfigureAwait(false))
        {
            if (!entry.IsChained)
            {
                unchained++;

                // Ein Eintrag ohne Kettenglied NACH dem Beginn der Kette ist kein Altbestand,
                // sondern eine Zeile, die am Schreiber vorbeigekommen ist.
                if (currentChain is not null)
                {
                    firstBreak ??= new AuditChainBreak(entry.Sequence, null,
                        "Eintrag ohne Kettenglied, obwohl die Kette an dieser Stelle bereits läuft.");
                }

                isFirstOfStream = false;
                continue;
            }

            if (entry.ChainId != currentChain)
            {
                if (currentChain is { } previousChain)
                {
                    segments.Add(new AuditChainSegment(previousChain, segmentStart, segmentLast, segmentCount));
                }

                // Ein neuer Abschnitt beginnt bei 0 — es sei denn, der Datenstrom selbst beginnt
                // mitten drin, weil jemand ab einer Zeilennummer exportiert hat.
                if (!isFirstOfStream && (entry.ChainIndex != 0 || entry.PreviousHash != AuditChain.Genesis))
                {
                    firstBreak ??= new AuditChainBreak(entry.Sequence, entry.ChainId,
                        $"Der Abschnitt {entry.ChainId} beginnt bei Index {entry.ChainIndex} statt bei 0 — "
                        + "die Einträge davor fehlen.");
                }

                currentChain = entry.ChainId;
                expectedIndex = entry.ChainIndex!.Value;
                segmentStart = entry.Sequence;
                segmentCount = 0;
                runningHash = null;
            }

            if (entry.ChainIndex != expectedIndex)
            {
                firstBreak ??= new AuditChainBreak(entry.Sequence, entry.ChainId,
                    $"Erwartet war Position {expectedIndex} im Abschnitt, gefunden {entry.ChainIndex} — "
                    + "dazwischen fehlt mindestens ein Eintrag.");
            }

            // 1. Rechnet der Eintrag gegen seinen eigenen Vorgängerhash auf? → findet Änderungen.
            if (AuditChain.Link(entry.PreviousHash ?? AuditChain.Genesis, entry.Event) != entry.EntryHash)
            {
                firstBreak ??= new AuditChainBreak(entry.Sequence, entry.ChainId,
                    "Der Inhalt passt nicht zu seinem Kettenglied — dieser Eintrag wurde nach dem "
                    + "Schreiben geändert.");
            }

            // 2. Ist sein Vorgängerhash der des tatsächlichen Vorgängers? → findet Löschungen.
            if (runningHash is { } expectedPrevious && entry.PreviousHash != expectedPrevious)
            {
                firstBreak ??= new AuditChainBreak(entry.Sequence, entry.ChainId,
                    "Der Vorgängerhash zeigt nicht auf die vorige Zeile — dazwischen wurde etwas "
                    + "entfernt oder die Reihenfolge geändert.");
            }

            runningHash = entry.EntryHash;
            expectedIndex = entry.ChainIndex!.Value + 1;
            segmentLast = entry.Sequence;
            segmentCount++;
            verified++;
            isFirstOfStream = false;
        }

        if (currentChain is { } last)
        {
            segments.Add(new AuditChainSegment(last, segmentStart, segmentLast, segmentCount));
        }

        return new AuditChainReport(verified, unchained, segments, firstBreak, ClaimLimits);
    }
}
