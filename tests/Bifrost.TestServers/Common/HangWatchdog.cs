using System.Globalization;
using System.Runtime.CompilerServices;
using Xunit;

namespace Bifrost.TestServers.Common;

/// <summary>
/// Beobachtet den Testlauf und schreibt bei Stillstand einen <see cref="HangReport"/> (FR-P056).
/// <para>
/// <b>Abtastend statt ereignisgesteuert.</b> Der Wächter fragt jede Sekunde, welcher Test gerade
/// läuft. Das braucht keinen Haken in der Testplattform, überlebt jede Version davon und kostet
/// nichts — und es liefert nebenbei die Sequenz, weil er sich merkt, was er gesehen hat.
/// </para>
/// <para>
/// <b>Die Frist ist bewusst großzügig.</b> Sie muss über dem längsten <em>legitimen</em> Test
/// liegen; in diesem Bestand ist das der Chaoslauf mit rund sieben Minuten. Zehn Minuten ohne
/// Fortschritt sind danach kein langsamer Test mehr, sondern ein Hänger — und sie liegen unter der
/// Frist, mit der die CI den Abzug zieht, damit die Kindprozesse noch leben, wenn sie gezählt
/// werden.
/// </para>
/// </summary>
public static class HangWatchdog
{
    /// <summary>Frist ohne Testwechsel. Über <c>BIFROST_HANG_TIMEOUT_SECONDS</c> anpassbar.</summary>
    public static TimeSpan Timeout { get; } =
        int.TryParse(
            Environment.GetEnvironmentVariable("BIFROST_HANG_TIMEOUT_SECONDS"),
            CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(10);

    private static readonly Lock Gate = new();
    private static readonly List<string> Sequence = [];
    private static Timer? _timer;
    private static string? _current;
    private static DateTimeOffset _since = DateTimeOffset.UtcNow;
    private static bool _reported;

    /// <summary>
    /// Startet sich selbst, sobald die Testassembly geladen ist.
    /// <para>
    /// <b>Warum ein Modulinitialisierer und keine Zeile je Projekt:</b> Vier Projekte starten echte
    /// Prozesse. Vier Stellen, an denen der Wächter angemeldet wird, sind vier Stellen, an denen er
    /// beim fünften Projekt vergessen wird — und ein Wächter, der nur manchmal läuft, ist keiner.
    /// Wer die Datei einbindet, hat ihn.
    /// </para>
    /// </summary>
    [ModuleInitializer]
    public static void Arm() => Start(RepositoryRoot());

    /// <summary>
    /// Die Wurzel wird gesucht, nicht abgeleitet: Der Testlauf startet in einem
    /// <c>bin</c>-Verzeichnis, dessen Tiefe je Projekt anders ist.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bifrost.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        // Ohne Wurzel schreibt der Bericht neben die Testassembly. Ein Waechter, der am fehlenden
        // Pfad scheitert, faellt genau dann aus, wenn er gebraucht wird.
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Startet den Wächter. Mehrfaches Aufrufen ist gutartig — die Testprojekte binden diese Datei
    /// jeweils selbst ein, und keines weiß von den anderen.
    /// </summary>
    public static void Start(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        lock (Gate)
        {
            if (_timer is not null)
            {
                return;
            }

            _timer = new Timer(_ => Sample(repositoryRoot), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Die zuletzt gesehenen Tests, ältester zuerst — die Sequenz des Berichts.</summary>
    public static IReadOnlyList<string> Observed()
    {
        lock (Gate)
        {
            return [.. Sequence];
        }
    }

    private static void Sample(string repositoryRoot)
    {
        string? running;
        try
        {
            running = TestContext.Current.Test?.TestDisplayName;
        }
        catch (InvalidOperationException)
        {
            // Ausserhalb eines Testkontexts — der Lauf beginnt oder endet gerade.
            return;
        }

        lock (Gate)
        {
            if (running != _current)
            {
                _current = running;
                _since = DateTimeOffset.UtcNow;
                _reported = false;

                if (running is { Length: > 0 })
                {
                    Sequence.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {running}");

                    // Nur die letzten Einträge: Bei zweitausend Tests ist die vollstaendige Liste
                    // kein Bericht mehr, sondern ein zweites Protokoll.
                    if (Sequence.Count > 25)
                    {
                        Sequence.RemoveAt(0);
                    }
                }

                return;
            }

            if (_reported || running is not { Length: > 0 } || DateTimeOffset.UtcNow - _since < Timeout)
            {
                return;
            }

            // Genau einmal je haengendem Test: Ein Bericht je Sekunde waere Rauschen, und der
            // zweite saehe genauso aus wie der erste.
            _reported = true;
        }

        HangReport.Write(
            repositoryRoot,
            $"Kein Testwechsel seit {Timeout.TotalMinutes:0} Minuten — laufend: {running}",
            Observed());
    }
}
