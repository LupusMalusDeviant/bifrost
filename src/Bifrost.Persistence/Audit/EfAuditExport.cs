using System.Runtime.CompilerServices;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence.Audit;

/// <summary>
/// Die Leseseite des Auditexports (M6/WP6.2, FR-P072).
/// <para>
/// <b>Keyset statt Seitenzahl.</b> <c>Skip(n).Take(m)</c> setzt voraus, dass sich die Menge
/// zwischen zwei Seiten nicht ändert. Beim Audit ändert sie sich immer — nebenher wird geschrieben.
/// Hier läuft der Cursor deshalb über die fortlaufende Zeilennummer: Jede Runde holt, was
/// <em>größer</em> als die letzte gesehene ist. Eine Zeile kann damit weder ausgelassen noch zweimal
/// geliefert werden, egal wie viel nebenher dazukommt.
/// </para>
/// <para>
/// <b>Warum in Blöcken und nicht mit einem offenen Leser über alles:</b> Ein Leser, der über eine
/// Million Zeilen offen bleibt, hält auf SQLite eine Lesetransaktion — und die steht dem
/// Audit-Schreiber im Weg, der nach ADR-0007 nie warten soll. Der Speicherbedarf bleibt so oder so
/// beschränkt (ein Block), aber die Sperre wird zwischen den Blöcken freigegeben.
/// </para>
/// </summary>
public sealed class EfAuditExport : IAuditExport
{
    /// <summary>
    /// Zeilen je Runde. Groß genug, dass die Rundenkosten nicht auffallen, klein genug, dass der
    /// Speicherbedarf eine Konstante bleibt und nicht mit dem Log wächst.
    /// </summary>
    public const int BatchSize = 1000;

    private readonly IDbContextFactory<BifrostDbContext> _factory;

    public EfAuditExport(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async IAsyncEnumerable<AuditExportEntry> StreamAsync(
        AuditExportRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var after = request.After ?? 0;
        var remaining = request.Limit ?? long.MaxValue;

        while (remaining > 0)
        {
            var take = (int)Math.Min(BatchSize, remaining);

            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await Filtered(db, request.Filter)
                .Where(r => r.Id > after)

                // Aufsteigend, nicht „neueste zuerst" wie in der Ansicht: Ein Export ist eine
                // Fortschreibung, keine Übersicht — und die Kette liest sich nur vorwärts.
                .OrderBy(r => r.Id)
                .Take(take)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (rows.Count == 0)
            {
                yield break;
            }

            foreach (var row in rows)
            {
                yield return new AuditExportEntry(
                    row.Id,
                    AuditBatchWriter.ToEvent(row),
                    row.ChainId,
                    row.ChainIndex,
                    row.PreviousHash,
                    row.EntryHash);
            }

            after = rows[^1].Id;
            remaining -= rows.Count;

            if (rows.Count < take)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Dieselben Filter wie die Ansicht (FR-23). Bewusst dieselbe Auslegung, damit ein Export nicht
    /// etwas anderes zeigt als der Bildschirm, aus dem er angestoßen wurde.
    /// </summary>
    private static IQueryable<AuditEventRow> Filtered(BifrostDbContext db, AuditFilter filter)
    {
        var query = db.AuditEvents.AsNoTracking().AsQueryable();

        if (filter.From is { } from)
        {
            query = query.Where(r => r.Timestamp >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.Timestamp <= to);
        }

        if (filter.Caller is { } caller)
        {
            query = query.Where(r => r.CallerId == caller.Value);
        }

        if (filter.Server is { } server)
        {
            query = query.Where(r => r.ServerId == server.Value);
        }

        if (filter.ToolPrefix is { } tool)
        {
            query = query.Where(r => r.Tool != null && r.Tool.StartsWith(tool));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(r => r.Status == (int)status);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(r => r.Kind == (int)kind);
        }

        if (filter.Origin is { } origin)
        {
            query = query.Where(r => r.Origin == (int)origin);
        }

        return query;
    }
}
