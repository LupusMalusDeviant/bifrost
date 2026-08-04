using System.Globalization;
using System.Text;

namespace Bifrost.Governance.Tests;

/// <summary>
/// Erzeugt den lesbaren Bericht aus <c>traceability.json</c> — generiert, nie von Hand gepflegt.
/// </summary>
internal static class TraceabilityReport
{
    public static string Path => System.IO.Path.Combine(RepoTree.Root, "docs", "acceptance", "traceability.md");

    public static string Render(TraceabilityDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = new StringBuilder();
        text.AppendLine("# Rückverfolgung — Anforderung, Paket, Test, Artefakt");
        text.AppendLine();
        text.AppendLine("> **Generiert aus `traceability.json`.** Änderungen gehören in die Quelle, nicht hierher —");
        text.AppendLine("> `TraceabilityTests` färbt den Lauf rot, sobald beides auseinanderläuft.");
        text.AppendLine();
        text.AppendLine("Eine Zeile zählt nur, wenn der genannte Test wirklich existiert; ein Test in dieser Suite");
        text.AppendLine("prüft genau das. **`offen` ist ein zulässiger Status — eine weggelassene Anforderung nicht.**");
        text.AppendLine();

        Summary(text, document);

        foreach (var level in (string[])["must", "should", "could"])
        {
            var rows = document.Requirements.Where(r => r.Level == level).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            text.Append("## ").AppendLine(level switch
            {
                "must" => "Must",
                "should" => "Should",
                _ => "Could",
            });
            text.AppendLine();
            text.AppendLine("| Anforderung | Paket | Status | Test | Artefakt |");
            text.AppendLine("|---|---|---|---|---|");

            foreach (var row in rows)
            {
                text.Append("| **").Append(row.Id).Append("** ").Append(Shorten(row.Title))
                    .Append(" | ").Append(row.WorkPackage)
                    .Append(" | ").Append(Mark(row.Status))
                    .Append(" | ").Append(Code(row.Test))
                    .Append(" | ").Append(Code(row.Artifact))
                    .AppendLine(" |");
            }

            text.AppendLine();
        }

        var withNotes = document.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.Note)).ToList();
        if (withNotes.Count > 0)
        {
            text.AppendLine("## Wo es nicht rund ist — und warum");
            text.AppendLine();
            foreach (var row in withNotes)
            {
                text.Append("- **").Append(row.Id).Append("** (").Append(row.Status).Append("): ")
                    .AppendLine(row.Note);
            }

            text.AppendLine();
        }

        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void Summary(StringBuilder text, TraceabilityDocument document)
    {
        var must = document.Requirements.Where(r => r.Level == "must").ToList();
        var openMust = must.Where(r => r.Status != "erfuellt").ToList();

        text.Append("**Anforderungen:** ").Append(Count(document.Requirements.Count))
            .Append(" · **Must:** ").Append(Count(must.Count))
            .Append(" · **Must ohne \"erfuellt\":** ").AppendLine(Count(openMust.Count));
        text.AppendLine();

        text.AppendLine(openMust.Count == 0
            ? "**Alle Must-Anforderungen sind erfüllt.**"
            : $"**{openMust.Count} Must-Anforderungen sind noch nicht erfüllt.** Solange das so steht, ist der "
                + "Release Candidate keiner — die Zeilen stehen unten mit Grund.");
        text.AppendLine();

        foreach (var status in (string[])["erfuellt", "teilweise", "offen", "entfaellt"])
        {
            var count = document.Requirements.Count(r => r.Status == status);
            if (count > 0)
            {
                text.Append("- ").Append(Mark(status)).Append(' ').Append(Count(count)).AppendLine();
            }
        }

        text.AppendLine();
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Mark(string status) => status switch
    {
        "erfuellt" => "erfüllt",
        "teilweise" => "teilweise",
        "offen" => "**offen**",
        _ => "entfällt",
    };

    private static string Code(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : "`" + value + "`";

    private static string Shorten(string title)
    {
        var text = title.Replace("|", "\\|", StringComparison.Ordinal);
        return text.Length <= 90 ? text : text[..87] + "…";
    }
}
