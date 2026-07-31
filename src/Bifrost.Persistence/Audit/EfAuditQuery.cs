using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence.Audit;

/// <summary>Abfrage-Seite des Audits (FR-23): Filter + Paging, neueste zuerst.</summary>
public sealed class EfAuditQuery : IAuditQuery
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;

    public EfAuditQuery(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<PagedResult<AuditEvent>> QueryAsync(AuditFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.PageSize, 1);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
            // Präfix statt Gleichheit: die UI sucht nach Server-Namespaces wie "github__",
            // ein exakter Vergleich hätte dort immer null Treffer geliefert.
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

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(r => r.Timestamp)
            .ThenByDescending(r => r.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<AuditEvent>(
            [.. rows.Select(AuditBatchWriter.ToEvent)], total, filter.Page, filter.PageSize);
    }
}
