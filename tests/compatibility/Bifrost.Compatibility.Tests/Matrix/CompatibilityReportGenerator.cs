using System.Globalization;
using System.Text;

namespace Bifrost.Compatibility.Tests.Matrix;

/// <summary>
/// Erzeugt <c>docs/acceptance/compatibility.md</c> aus der Matrix.
/// <para>
/// <b>Warum generiert und nicht gepflegt:</b> Eine zweite, handgeschriebene Fassung derselben
/// Aussagen driftet — und zwar immer in dieselbe Richtung, weil ein <c>pass</c> gern stehen bleibt,
/// wenn der Lauf es nicht mehr hergibt. Der Bericht hat deshalb keine eigene Wahrheit.
/// </para>
/// </summary>
public static class CompatibilityReportGenerator
{
    public static string Generate(CompatibilityMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var sb = new StringBuilder();
        sb.AppendLine("# Kompatibilitätsmatrix");
        sb.AppendLine();
        sb.AppendLine("<!-- GENERIERT aus tests/compatibility/matrix.json — nicht von Hand ändern.");
        sb.AppendLine("     Neu erzeugen: BIFROST_UPDATE_COMPAT_REPORT=1 dotnet test tests/compatibility/Bifrost.Compatibility.Tests -->");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Stand der Matrix:** {matrix.GeneratedAt:yyyy-MM-dd} · **Schema:** v{matrix.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine("Jede Zeile verweist auf einen Lauf oder ein handgeführtes Protokoll. `untested` heißt");
        sb.AppendLine("ungeprüft — nicht „geht nicht\" und nicht „geht\".");
        sb.AppendLine();

        AppendLegend(sb);
        AppendTable(sb, matrix);
        AppendEvidence(sb, matrix);
        AppendLimitations(sb, matrix);
        AppendGaps(sb, matrix);

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static void AppendLegend(StringBuilder sb)
    {
        sb.AppendLine("## Werte");
        sb.AppendLine();
        sb.AppendLine("| Wert | Bedeutung |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| `pass` | in einem Lauf oder Protokoll durchgehend funktioniert |");
        sb.AppendLine("| `partial` | funktioniert mit benannter Einschränkung (Spalte unten) |");
        sb.AppendLine("| `fail` | geprüft und fehlgeschlagen |");
        sb.AppendLine("| `unsupported` | der Client oder Betriebsmodus kennt die Fähigkeit nicht |");
        sb.AppendLine("| `untested` | **nicht geprüft** — offene Lücke, keine Aussage |");
        sb.AppendLine();
    }

    private static void AppendTable(StringBuilder sb, CompatibilityMatrix matrix)
    {
        sb.AppendLine("## Matrix");
        sb.AppendLine();
        sb.Append("| Client | Version | OS | Transport | Modus | Protokoll |");
        foreach (var feature in CompatibilityFeatures.All)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {feature} |");
        }

        sb.AppendLine();
        sb.Append("|---|---|---|---|---|---|");
        foreach (var _ in CompatibilityFeatures.All)
        {
            sb.Append("---|");
        }

        sb.AppendLine();

        foreach (var entry in Ordered(matrix))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| `{entry.Client}` | {entry.ClientVersion} | {entry.Os} | {entry.Transport} | {entry.Mode} | {entry.ProtocolVersion} |");
            foreach (var feature in CompatibilityFeatures.All)
            {
                var value = entry.Features.TryGetValue(feature, out var v) ? v : "—";
                sb.Append(CultureInfo.InvariantCulture, $" {value} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine();
    }

    private static void AppendEvidence(StringBuilder sb, CompatibilityMatrix matrix)
    {
        sb.AppendLine("## Belege");
        sb.AppendLine();
        sb.AppendLine("| Zeile | Art | Beleg | Datum | Prüfer |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var entry in Ordered(matrix))
        {
            if (entry.Evidence is null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{entry.Key}` | — | *nichts geprüft* | — | — |");
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| `{entry.Key}` | {entry.Evidence.Kind} | `{entry.Evidence.Ref}` | {entry.Evidence.Date} | {entry.Evidence.Operator ?? "—"} |");
        }

        sb.AppendLine();
    }

    private static void AppendLimitations(StringBuilder sb, CompatibilityMatrix matrix)
    {
        var withLimits = Ordered(matrix).Where(e => e.Limitations.Count > 0).ToList();
        if (withLimits.Count == 0)
        {
            return;
        }

        sb.AppendLine("## Bekannte Einschränkungen (FR-P065)");
        sb.AppendLine();
        foreach (var entry in withLimits)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"**`{entry.Key}`**");
            sb.AppendLine();
            foreach (var limitation in entry.Limitations)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {limitation}");
            }

            sb.AppendLine();
        }
    }

    /// <summary>
    /// Die Liste der offenen Punkte steht **im** Bericht, nicht daneben. Ein Bericht, der nur zeigt,
    /// was geprüft ist, liest sich vollständig — und ist es nie.
    /// </summary>
    private static void AppendGaps(StringBuilder sb, CompatibilityMatrix matrix)
    {
        var untested = Ordered(matrix)
            .SelectMany(e => CompatibilityFeatures.All
                .Where(f => !e.Features.TryGetValue(f, out var v) || v == CompatibilityValues.Untested)
                .Select(f => (Entry: e, Feature: f)))
            .ToList();

        sb.AppendLine("## Offene Lücken");
        sb.AppendLine();

        if (untested.Count == 0)
        {
            sb.AppendLine("Keine. Jede Zelle ist geprüft.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{untested.Count} von {matrix.Entries.Count * CompatibilityFeatures.All.Count} Zellen sind ungeprüft:");
        sb.AppendLine();

        foreach (var group in untested.GroupBy(x => x.Entry.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- `{group.Key}`: {string.Join(", ", group.Select(x => x.Feature))}");
        }

        sb.AppendLine();
    }

    private static IEnumerable<CompatibilityEntry> Ordered(CompatibilityMatrix matrix) =>
        matrix.Entries.OrderBy(e => e.Client, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal);
}
