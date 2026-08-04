using System.Globalization;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Usage;

namespace Bifrost.Core.Usage;

/// <summary>
/// Grenzen, die <b>vor</b> der Rechnung gezogen werden. Eine Statistik, die je Werkzeug und
/// Identität wächst, ist auf einem großen Katalog selbst das Problem (M6-Vertrag §5).
/// </summary>
public sealed record UsageCockpitOptions
{
    /// <summary>Wie viele Auditseiten höchstens gelesen werden.</summary>
    public int MaxAuditPages { get; init; } = 200;

    public int AuditPageSize { get; init; } = 500;

    /// <summary>Wie viele Werkzeuge der Bericht nennt — nach Kosten sortiert.</summary>
    public int MaxToolsListed { get; init; } = 500;

    /// <summary>Wie weit zurück ein Fenster reichen darf.</summary>
    public TimeSpan MaxWindow { get; init; } = TimeSpan.FromDays(90);
}

/// <summary>
/// Das Token-Cockpit (M6/WP6.4): Es stellt zusammen, was ein Profil einem Agenten an Schema zumutet
/// (<em>Exposure</em>) und was davon im Zeitfenster wirklich benutzt wurde (<em>Invocation</em>).
/// <para>
/// <b>Es ändert nichts.</b> Kein Pfad hier schreibt an einem Profil, einer Rolle oder einem Grant.
/// Das ist keine Zurückhaltung, sondern die Bauform: Eine Statistik, die Rechte anpassen darf,
/// wäre eine Rechteverwaltung mit Heuristik.
/// </para>
/// </summary>
public sealed class UsageCockpit : IUsageCockpit
{
    private readonly IToolCatalog _catalog;
    private readonly IAuditQuery _audit;
    private readonly TimeProvider _time;
    private readonly UsageCockpitOptions _options;

    public UsageCockpit(
        IToolCatalog catalog, IAuditQuery audit, TimeProvider time, UsageCockpitOptions? options = null)
    {
        _catalog = catalog;
        _audit = audit;
        _time = time;
        _options = options ?? new UsageCockpitOptions();
    }

    public async Task<UsageReport> AnalyseAsync(IdentityId identity, UsageWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);

        var notes = new List<string>();
        var effective = Clamp(window, notes);
        var view = _catalog.GetViewFor(identity);
        var invocations = await CountAsync(identity, effective, notes, ct);

        var exposed = view.PinnedTools
            .Select(entry => new ToolUsage(
                entry.Name.Value,
                entry.EstimatedSchemaTokens,
                invocations.GetValueOrDefault(entry.Name.Value).Count,
                invocations.GetValueOrDefault(entry.Name.Value).Last))
            .OrderByDescending(t => t.EstimatedSchemaTokens)
            .ThenBy(t => t.Tool, StringComparer.Ordinal)
            .ToList();

        // Aufgerufen, aber nicht im Profil: der Lazy-Pfad ueber die Meta-Tools. Und Werkzeuge, die
        // es nicht mehr gibt — die kosten heute nichts und gehoeren trotzdem in den Bericht.
        var exposedNames = view.PinnedTools.Select(e => e.Name.Value).ToHashSet(StringComparer.Ordinal);
        var extra = invocations
            .Where(pair => !exposedNames.Contains(pair.Key))
            .Select(pair => new ToolUsage(
                pair.Key,
                _catalog.Find(new NamespacedToolName(pair.Key))?.EstimatedSchemaTokens ?? 0,
                pair.Value.Count,
                pair.Value.Last,
                StillInCatalog: _catalog.Find(new NamespacedToolName(pair.Key)) is not null))
            .OrderByDescending(t => t.Invocations)
            .ThenBy(t => t.Tool, StringComparer.Ordinal)
            .ToList();

        if (extra.Any(t => !t.StillInCatalog))
        {
            notes.Add(
                "Einige aufgerufene Werkzeuge stehen nicht mehr im Katalog — sie kosten nichts mehr, "
                + "der Aufruf hat aber stattgefunden.");
        }

        return new UsageReport(
            identity,
            effective,
            view.PinnedTools.Count,
            view.EstimatedContextTokens,
            invocations.Values.Sum(v => v.Count),
            Trim(exposed, notes, "Exponierte Werkzeuge"),
            Trim(extra, notes, "Aufgerufene Werkzeuge außerhalb des Profils"),
            notes);
    }

    public async Task<ProfileSimulation> SimulateAsync(
        IdentityId identity, UsageWindow window, IReadOnlyList<string> proposedTools, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(proposedTools);

        var notes = new List<string>();
        var effective = Clamp(window, notes);
        var view = _catalog.GetViewFor(identity);
        var invocations = await CountAsync(identity, effective, notes, ct);

        var proposed = proposedTools.ToHashSet(StringComparer.Ordinal);
        var unknown = proposed.Where(name => _catalog.Find(new NamespacedToolName(name)) is null).ToList();
        if (unknown.Count > 0)
        {
            // Ein Vorschlag, der Werkzeuge nennt, die es nicht gibt, ist kein Vorschlag — er waere
            // nach der Uebernahme ein Profil mit toten Eintraegen.
            notes.Add($"Nicht im Katalog und deshalb ohne Wirkung: {string.Join(", ", unknown.Order(StringComparer.Ordinal))}");
        }

        var removed = view.PinnedTools
            .Where(entry => !proposed.Contains(entry.Name.Value))
            .ToList();

        var tokensAfter = view.PinnedTools
            .Where(entry => proposed.Contains(entry.Name.Value))
            .Sum(entry => entry.EstimatedSchemaTokens);

        // Die Konsequenz zuerst zusammenstellen: Was faellt weg, das benutzt wurde?
        var removedButUsed = removed
            .Select(entry => new ToolUsage(
                entry.Name.Value,
                entry.EstimatedSchemaTokens,
                invocations.GetValueOrDefault(entry.Name.Value).Count,
                invocations.GetValueOrDefault(entry.Name.Value).Last))
            .Where(usage => usage.Invocations > 0)
            .OrderByDescending(usage => usage.Invocations)
            .ToList();

        if (removedButUsed.Count > 0)
        {
            notes.Add(
                $"{removedButUsed.Count} entfernte Werkzeug(e) wurden im Fenster benutzt. Nach der "
                + "Übernahme erreicht der Agent sie nur noch über den Lazy-Pfad (search_tools).");
        }

        return new ProfileSimulation(
            identity,
            effective,
            view.EstimatedContextTokens,
            tokensAfter,
            [.. removed.Select(r => r.Name.Value).Order(StringComparer.Ordinal)],
            removedButUsed,
            notes);
    }

    /// <summary>
    /// Zählt Aufrufe im Fenster. Bewusst seitenweise mit harter Obergrenze: Ein Cockpit, das eine
    /// unbegrenzte Auditmenge in den Arbeitsspeicher zieht, ist auf einer belasteten Instanz das
    /// nächste Problem — und ausgerechnet dort will man es aufrufen.
    /// </summary>
    private async Task<Dictionary<string, (long Count, DateTimeOffset? Last)>> CountAsync(
        IdentityId identity, UsageWindow window, List<string> notes, CancellationToken ct)
    {
        var counts = new Dictionary<string, (long Count, DateTimeOffset? Last)>(StringComparer.Ordinal);

        for (var page = 1; page <= _options.MaxAuditPages; page++)
        {
            var result = await _audit.QueryAsync(
                new AuditFilter(
                    From: window.From,
                    To: window.To,
                    Caller: identity,
                    Kind: AuditEventKind.ToolCall,
                    Page: page,
                    PageSize: _options.AuditPageSize),
                ct);

            foreach (var entry in result.Items)
            {
                if (entry.Tool is not { Length: > 0 } tool)
                {
                    continue;
                }

                var current = counts.GetValueOrDefault(tool);
                counts[tool] = (
                    current.Count + 1,
                    current.Last is { } last && last > entry.Timestamp ? last : entry.Timestamp);
            }

            if (result.Items.Count < _options.AuditPageSize)
            {
                return counts;
            }

            if (page == _options.MaxAuditPages)
            {
                notes.Add(
                    $"Abgeschnitten: Es wurden {(_options.MaxAuditPages * _options.AuditPageSize)
                        .ToString("N0", CultureInfo.InvariantCulture)} Auditeinträge gelesen, es gibt mehr. "
                    + "Die Zahlen sind Untergrenzen, keine Summen.");
            }
        }

        return counts;
    }

    private UsageWindow Clamp(UsageWindow window, List<string> notes)
    {
        var now = _time.GetUtcNow();
        var to = window.To > now ? now : window.To;
        var from = window.From;

        if (to - from > _options.MaxWindow)
        {
            from = to - _options.MaxWindow;
            notes.Add(
                $"Fenster auf {_options.MaxWindow.TotalDays:0} Tage gekürzt — länger zurück reicht "
                + "die Aufbewahrung des Audits nicht verlässlich.");
        }

        return new UsageWindow(from, to);
    }

    private List<ToolUsage> Trim(List<ToolUsage> tools, List<string> notes, string what)
    {
        if (tools.Count <= _options.MaxToolsListed)
        {
            return tools;
        }

        notes.Add(
            $"{what}: {tools.Count} gefunden, {_options.MaxToolsListed} genannt (nach Kosten sortiert).");
        return tools.Take(_options.MaxToolsListed).ToList();
    }
}
