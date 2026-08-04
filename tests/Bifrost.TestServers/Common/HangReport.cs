using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bifrost.TestServers.Common;

/// <summary>
/// Was ein hängender Testlauf hinterlässt (FR-P056): <b>Sequenz, Dump und aktive
/// Kindprozessliste</b>.
/// <para>
/// Die ersten beiden liefert die Testplattform selbst — <c>--blame-hang</c> schreibt eine
/// <c>Sequence.xml</c> und einen Speicherabzug. Was sie <b>nicht</b> liefert, ist die Liste der
/// Prozesse, die dieser Lauf gestartet hat: Gateway-Instanzen und Testserver, die als eigene
/// Programme laufen. Genau die sind bei einem Hänger die interessantesten — ein Testlauf hängt in
/// diesem Projekt selten an sich selbst, sondern an einer Gegenstelle, die nicht antwortet.
/// </para>
/// <para>
/// <b>Warum vor dem Abzug und nicht danach:</b> <c>--blame-hang</c> beendet den Testhost. Wer die
/// Kindprozesse erst danach sucht, findet entweder nichts mehr oder Waisen ohne Bezug. Deshalb
/// schreibt dieser Bericht früher — mit einer kürzeren Frist als der Abzug.
/// </para>
/// </summary>
public static class HangReport
{
    /// <summary>
    /// Die Programme, die dieser Testbestand als eigene Prozesse startet. Bewusst eine feste Liste
    /// und keine Suche über alle Prozesse: Ein Bericht, der fremde Prozesse des Rechners mitnimmt,
    /// ist bei einem Fehlschlag um 3 Uhr nachts das Gegenteil einer Hilfe.
    /// </summary>
    public static IReadOnlyList<string> KnownChildren { get; } =
    [
        "Bifrost.Server",
        "Bifrost.TestServers.EchoServer",
        "Bifrost.TestServers.SlowServer",
        "Bifrost.TestServers.BulkServer",
        "Bifrost.TestServers.CrashServer",
        "Bifrost.TestServers.HttpServer",
        "Bifrost.TestServers.OrphanHost",
        "Bifrost.TestServers.CliProbe",
        "bifrost-wasi-host",
    ];

    /// <summary>Wohin der Bericht geht — ein Verzeichnis, das die CI als Artefakt einsammelt.</summary>
    public static string DirectoryFor(string repositoryRoot)
        => Path.Combine(repositoryRoot, "artifacts", "hang");

    /// <summary>
    /// Schreibt den Bericht und gibt den Pfad zurück.
    /// </summary>
    /// <param name="repositoryRoot">Wurzel, unter der <c>artifacts/hang</c> entsteht.</param>
    /// <param name="reason">Warum der Bericht entsteht — die Frist, ein Abbruch, ein Signal.</param>
    /// <param name="sequence">
    /// Die zuletzt beobachteten Tests, ältester zuerst. Der <b>letzte</b> Eintrag ist der, der beim
    /// Schreiben lief; ohne die Vorgänger fehlt der Zusammenhang, der einen Hänger meist erklärt.
    /// </param>
    public static string Write(string repositoryRoot, string reason, IReadOnlyList<string> sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(sequence);

        var directory = DirectoryFor(repositoryRoot);
        Directory.CreateDirectory(directory);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"hang-{stamp}.txt");

        var text = new StringBuilder();
        text.Append("Haenger-Bericht  ").AppendLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        text.Append("Grund:           ").AppendLine(reason);

        using (var self = Process.GetCurrentProcess())
        {
            text.Append("Testhost:        PID ").Append(self.Id.ToString(CultureInfo.InvariantCulture))
                .Append(", seit ").AppendLine(self.StartTime.ToString("O", CultureInfo.InvariantCulture));
        }

        text.AppendLine();
        text.AppendLine("── Sequenz (aeltester zuerst, letzter lief beim Schreiben) ──");
        if (sequence.Count == 0)
        {
            text.AppendLine("  (keine — der Lauf hing, bevor ein Test begann)");
        }
        else
        {
            foreach (var entry in sequence)
            {
                text.Append("  ").AppendLine(entry);
            }
        }

        text.AppendLine();
        text.AppendLine("── Aktive Kindprozesse ──");
        var children = Children();
        if (children.Count == 0)
        {
            text.AppendLine("  (keine — der Haenger liegt dann im Testhost selbst, siehe Dump)");
        }
        else
        {
            foreach (var child in children)
            {
                text.Append("  ").AppendLine(child);
            }
        }

        text.AppendLine();
        text.AppendLine("── Dump ──");
        text.AppendLine("  Den Speicherabzug schreibt die Testplattform: --blame-hang-dump-type full.");
        text.AppendLine("  Er liegt neben den Testergebnissen, zusammen mit Sequence.xml.");

        File.WriteAllText(path, text.ToString());
        return path;
    }

    /// <summary>
    /// Die laufenden Prozesse aus <see cref="KnownChildren"/> — mit Id, Startzeit und Programmpfad.
    /// Eine Zahl allein beantwortet die Frage nicht, die bei einem Hänger zählt: <em>welcher</em>.
    /// </summary>
    public static IReadOnlyList<string> Children()
        => [.. KnownChildren.SelectMany(UpstreamProcessLookup.DescribeByExecutableName)];
}
