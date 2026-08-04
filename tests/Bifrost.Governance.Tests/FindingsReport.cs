using System.Globalization;
using System.Text;

namespace Bifrost.Governance.Tests;

/// <summary>
/// Erzeugt den lesbaren Bericht aus <c>findings.json</c>.
/// <para>
/// <b>Generiert, nie von Hand gepflegt</b> — dieselbe Regel wie beim Kompatibilitätsbericht
/// (M5-Vertrag §4) und aus demselben Grund: Zwei Fassungen derselben Liste driften auseinander, und
/// die falsche ist die, die jemand liest.
/// </para>
/// </summary>
internal static class FindingsReport
{
    public static string Path => System.IO.Path.Combine(RepoTree.Root, "docs", "acceptance", "findings.md");

    public static string Render(FindingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = new StringBuilder();
        text.AppendLine("# Befunde — Stand der Release-Candidate-Prüfung");
        text.AppendLine();
        text.AppendLine("> **Generiert aus `findings.json`.** Änderungen gehören in die Quelle, nicht hierher —");
        text.AppendLine("> `FindingsReportTests` färbt den Lauf rot, sobald beides auseinanderläuft.");
        text.AppendLine();

        var open = document.Findings.Where(f => f.Status == "offen").ToList();
        var fixedUp = document.Findings.Where(f => f.Status == "behoben").ToList();

        text.Append("**Offen:** ").Append(open.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" · **Behoben:** ").Append(fixedUp.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" · **Abdeckung ab:** ").AppendLine(document.Coverage.From);
        text.AppendLine();
        text.AppendLine(document.Coverage.Reason);
        text.AppendLine();

        var blocking = open.Count(f => f.Severity is "critical" or "high");
        text.AppendLine(blocking == 0
            ? "**Kein offener Critical- oder High-Befund.** Das ist die Bedingung aus WP7.1 — im "
                + "Rahmen der oben genannten Abdeckung."
            : $"**{blocking} offene(r) Releaseblocker.** Der Release Candidate ist keiner, solange das so steht.");
        text.AppendLine();

        Section(text, "Offen", open, withPlan: true);
        Section(text, "Behoben", fixedUp, withPlan: false);

        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void Section(StringBuilder text, string title, List<Finding> findings, bool withPlan)
    {
        text.Append("## ").AppendLine(title);
        text.AppendLine();

        if (findings.Count == 0)
        {
            text.AppendLine("_Keine._");
            text.AppendLine();
            return;
        }

        foreach (var finding in findings.OrderBy(f => Rank(f.Severity)).ThenBy(f => f.Id, StringComparer.Ordinal))
        {
            text.Append("### ").Append(finding.Id).Append(" · ").Append(finding.Title)
                .Append(" (").Append(finding.Severity).AppendLine(")");
            text.AppendLine();
            text.Append("_Quelle: ").Append(finding.Source).Append(" · Beleg: `")
                .Append(finding.Evidence).AppendLine("`_");
            text.AppendLine();
            text.AppendLine(finding.Detail);
            text.AppendLine();

            if (withPlan)
            {
                text.Append("**Owner:** ").Append(finding.Owner ?? "—")
                    .Append(" · **Termin:** ").AppendLine(finding.Due ?? "—");
                text.AppendLine();
                text.Append("**Mitigation:** ").AppendLine(finding.Mitigation ?? "—");
                text.AppendLine();
            }
        }
    }

    private static int Rank(string severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        _ => 3,
    };
}
