using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Bifrost.TestServers.Common;

/// <summary>
/// Findet die laufenden Prozesse eines Testservers — plattformübergreifend.
/// <para>
/// <b>Warum es diese Klasse gibt:</b> <see cref="Process.GetProcessesByName(string)"/> liest unter
/// Linux <c>/proc/&lt;pid&gt;/comm</c>, und der Kernel kürzt diesen Namen auf <b>15 Zeichen</b>
/// (<c>TASK_COMM_LEN</c>). Ein Testserver heißt <c>Bifrost.TestServers.EchoServer</c> — 29 Zeichen.
/// Die Suche findet dort also nie etwas, und zwar ohne Fehlermeldung: Sie liefert eine leere Liste,
/// die aussieht wie „läuft nicht".
/// </para>
/// <para>
/// Aufgefallen ist das im <b>ersten Releaselauf überhaupt</b>. Der Nachweis aus WP0.4 galt als grün,
/// weil er nur unter Windows gelaufen war — seit seiner Entstehung war nichts gepusht worden. Ein
/// Test, der auf einer Plattform nichts prüft und trotzdem grün meldet, ist schlimmer als keiner:
/// Er behauptet eine Zusicherung, die dort nie galt.
/// </para>
/// </summary>
public static class UpstreamProcessLookup
{
    /// <summary>
    /// Beschreibt gefundene Prozesse so, dass ein Fehlschlag beantwortbar wird: Id, Startzeit und
    /// Programmpfad.
    /// <para>
    /// <b>Warum eine Zahl allein nicht reicht.</b> Die Aufräumprobe kann nur zu viel finden — und
    /// „gefunden: 1" beantwortet die einzige Frage nicht, die dann zählt: <em>wessen</em> Prozess.
    /// Die Startzeit trennt einen Nachzügler des eigenen Laufs von einem Fremden, der Pfad trennt
    /// die Baukonfiguration (ein zweiter Lauf derselben Suite auf demselben Rechner heißt genauso).
    /// Ohne diese drei Angaben bleibt für den nächsten Fehlschlag nur Raten übrig — und dieser Test
    /// hat schon zweimal falsche Antworten bekommen, weil geraten wurde.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> DescribeByExecutableName(string executableName)
    {
        var described = new List<string>();
        foreach (var id in FindByExecutableName(executableName))
        {
            described.Add(Describe(id));
        }

        return described;
    }

    private static string Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string started;
            try
            {
                started = process.StartTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is InvalidOperationException or SystemException)
            {
                started = "Start unbekannt";
            }

            string path;
            try
            {
                path = process.MainModule?.FileName ?? "Pfad unbekannt";
            }
            catch (Exception e) when (e is InvalidOperationException or SystemException)
            {
                // Ein fremder Prozess gibt sein Abbild nicht her. Genau das ist dann die Auskunft.
                path = "Pfad nicht lesbar (fremder Prozess?)";
            }

            return $"#{processId} seit {started}, {path}";
        }
        catch (ArgumentException)
        {
            // Zwischen Zählen und Beschreiben verschwunden — auch das gehört in die Meldung.
            return $"#{processId} (beim Nachsehen bereits beendet)";
        }
    }

    /// <summary>
    /// Alle laufenden Prozess-Ids zum angegebenen Programmnamen (ohne Endung).
    /// </summary>
    public static IReadOnlyList<int> FindByExecutableName(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Process.GetProcessesByName(executableName).Select(p => p.Id).ToArray();
        }

        // Unter Linux über die Kommandozeile statt über den gekürzten Namen. `/proc/<pid>/cmdline`
        // trägt die Argumente NUL-getrennt und ist nicht gekürzt.
        var found = new List<int>();
        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out var pid))
            {
                continue;
            }

            string cmdline;
            try
            {
                cmdline = File.ReadAllText(Path.Combine(entry, "cmdline"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Der Prozess ist zwischen Auflisten und Lesen verschwunden, oder er gehört
                // jemand anderem. Beides heißt: nicht unserer.
                continue;
            }

            if (cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Any(argument => Path.GetFileNameWithoutExtension(argument)
                    .Equals(executableName, StringComparison.Ordinal)))
            {
                found.Add(pid);
            }
        }

        return found;
    }
}
