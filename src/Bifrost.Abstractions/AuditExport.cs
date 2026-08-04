namespace Bifrost.Abstractions;

/// <summary>Die Ausgabeformate des Auditexports (M6/WP6.2, FR-P072).</summary>
public enum AuditExportFormat
{
    /// <summary>Ein JSON-Objekt je Zeile. Das Format für Weiterverarbeitung.</summary>
    Jsonl = 0,

    /// <summary>Kommagetrennt mit fester Spaltenfolge. Das Format für Tabellenkalkulationen.</summary>
    Csv = 1,
}

/// <summary>
/// Ein Eintrag des Exports: das Ereignis samt seiner <b>Position</b> und seiner Kettenglieder.
/// </summary>
/// <param name="Sequence">
/// Die fortlaufende Nummer der Zeile in der Ablage. Sie ist der Cursor — und der Grund, warum der
/// Export keine Seitenzahlen benutzt: Wer über Seiten blättert, verliert oder verdoppelt Einträge,
/// sobald nebenher geschrieben wird, und nebenher wird beim Audit immer geschrieben.
/// </param>
/// <param name="ChainId">
/// Der Kettenabschnitt, zu dem dieser Eintrag gehört, oder <c>null</c> für Einträge, die vor der
/// Kette entstanden sind. Siehe <see cref="AuditChainSegment"/> — die Kette ist bewusst je
/// Schreiberlauf geführt und nicht global.
/// </param>
/// <param name="ChainIndex">Die Position innerhalb des Abschnitts, bei 0 beginnend.</param>
/// <param name="PreviousHash">Der Hash des Vorgängers im Abschnitt.</param>
/// <param name="EntryHash">Der Hash dieses Eintrags.</param>
public sealed record AuditExportEntry(
    long Sequence,
    AuditEvent Event,
    Guid? ChainId = null,
    long? ChainIndex = null,
    string? PreviousHash = null,
    string? EntryHash = null)
{
    /// <summary>Trägt dieser Eintrag ein Kettenglied?</summary>
    public bool IsChained => ChainId is not null && EntryHash is not null;
}

/// <param name="After">
/// Exklusive Untergrenze: Es kommt nur, was <b>danach</b> geschrieben wurde. <c>null</c> beginnt am
/// Anfang. Ein Aufrufer, der einen Export fortsetzt, gibt hier die zuletzt gesehene
/// <see cref="AuditExportEntry.Sequence"/> an.
/// </param>
/// <param name="Limit">
/// Höchstzahl der Einträge, <c>null</c> für „alles". Bewusst kein Vorgabewert wie bei einer
/// Seitenabfrage: Ein Export, der still bei 100 Zeilen aufhört, sieht aus wie ein vollständiger.
/// </param>
public sealed record AuditExportRequest(
    AuditFilter Filter,
    long? After = null,
    int? Limit = null);

/// <summary>
/// Die Leseseite des Auditexports (FR-P072).
/// <para>
/// <b>Streaming ist Vertrag, nicht Umsetzungsdetail.</b> Ein Export, der erst eine Liste baut und
/// sie dann ausgibt, zieht bei einem Log von Millionen Zeilen den Arbeitsspeicher linear mit — und
/// fällt genau dort aus, wo ein Auditexport gebraucht wird: bei einem großen, alten Log.
/// </para>
/// </summary>
public interface IAuditExport
{
    IAsyncEnumerable<AuditExportEntry> StreamAsync(AuditExportRequest request, CancellationToken ct);
}

/// <summary>
/// Ein Abschnitt der Integritätskette — die Einträge <b>eines</b> Schreiberlaufs (FR-P073).
/// <para>
/// <b>Warum je Lauf und nicht eine einzige Kette:</b> Eine globale Kette setzt genau einen
/// Schreiber voraus. Laufen zwei Instanzen auf derselben Datenbank — oder wird eine Instanz neu
/// gestartet —, verschränken sich die Ketten, und der Prüfer meldete einen Bruch, wo keine
/// Manipulation war. Ein falscher Alarm über Manipulation ist schlimmer als keine Kette: Er
/// entwertet den echten Fund.
/// </para>
/// </summary>
public sealed record AuditChainSegment(Guid ChainId, long FirstSequence, long LastSequence, long Count);
