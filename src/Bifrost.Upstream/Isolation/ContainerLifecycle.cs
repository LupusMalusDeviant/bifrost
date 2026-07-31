using System.Diagnostics;
using System.Globalization;

namespace Bifrost.Upstream.Isolation;

/// <summary>
/// Start, Gesundheitsprüfung, Drain, Stop, Kill und Aufräumen laufender Container (ADR-0018,
/// WP3.2 Punkt 4).
/// <para>
/// <b>Warum es diese Klasse überhaupt gibt:</b> Im Host-Modus ist das gestartete Programm ein Kind
/// des Gateways; es zu beenden heißt, den Prozessbaum zu töten, und das Betriebssystem räumt den
/// Rest ab (siehe <c>ProcessHygiene</c>). Ein <c>docker run</c> ist kein Elternprozess, sondern ein
/// Client zum Daemon. Stirbt der Client, läuft der Container weiter. Der Prozessbaum-Kill hat hier
/// kein Gegenstück — deshalb wird über den <see cref="ContainerIdentity.Name"/> abgeräumt.
/// </para>
/// <para>
/// <b>Aufräumen wirft nicht und ist nicht abbrechbar.</b> Jede Aufräummethode läuft auf einem Pfad,
/// auf dem schon etwas schiefgegangen ist (Timeout, Abbruch, Dispose) — dort ist der übergebene
/// Token in aller Regel bereits abgebrochen. Nähme sie einen entgegen, fiele das Aufräumen genau
/// dann aus, wenn es gebraucht wird; und eine Ausnahme daraus würde die eigentliche Ursache
/// verdecken. Deshalb steht in diesen Signaturen kein <see cref="CancellationToken"/>.
/// </para>
/// </summary>
internal static class ContainerLifecycle
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Die Runtimes, mit denen dieser Prozess je einen Container gestartet hat. Der Aufräumlauf
    /// beim Herunterfahren fragt <b>nur</b> diese — eine Installation ohne Container soll beim
    /// Beenden nicht auf einen Docker-Aufruf warten, den sie nie gebraucht hat.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UsedRuntimes
        = new(StringComparer.Ordinal);

    /// <summary>Meldet, dass mit dieser Runtime ein Container gestartet wurde.</summary>
    public static void NoteLaunch(string runtime) => UsedRuntimes.TryAdd(runtime, 0);

    /// <summary>
    /// Räumt beim Herunterfahren alles ab, was diese Instanz gestartet hat — über jede Runtime, die
    /// tatsächlich benutzt wurde.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SweepAllRuntimesAsync(string instanceId)
    {
        var removed = new List<string>();
        foreach (var runtime in UsedRuntimes.Keys)
        {
            removed.AddRange(await SweepInstanceAsync(runtime, instanceId).ConfigureAwait(false));
        }

        return removed;
    }

    /// <summary>
    /// Läuft dieser Container noch? Die Gesundheitsprüfung einer stehenden Sitzung: Ein Upstream,
    /// dessen Container weg ist, ist tot — auch wenn die Rohre noch offen aussehen.
    /// </summary>
    public static async Task<bool> IsRunningAsync(string runtime, string name, CancellationToken ct)
    {
        var (exitCode, output) = await RunAsync(
            runtime, ["inspect", "--format", "{{.State.Running}}", name], ct).ConfigureAwait(false);

        return exitCode == 0 && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ordentliches Beenden: erst <c>stop</c> mit Gnadenfrist (das Programm bekommt SIGTERM und
    /// darf aufräumen), danach <c>rm --force</c> als Nachlauf. Das zweite ist kein zweiter Versuch
    /// des ersten, sondern der Fall „<c>--rm</c> hat nicht gegriffen".
    /// </summary>
    public static async Task StopAsync(string runtime, string name, int stopTimeoutSeconds)
    {
        var grace = Math.Max(0, stopTimeoutSeconds).ToString(CultureInfo.InvariantCulture);
        await RunAsync(runtime, ["stop", "--time", grace, name], CancellationToken.None)
            .ConfigureAwait(false);
        await RemoveAsync(runtime, name).ConfigureAwait(false);
    }

    /// <summary>
    /// Hartes Beenden ohne Gnadenfrist — für den Zeitüberschreitungsfall. Nur den Client zu töten
    /// genügt dort ausdrücklich nicht: Das Kommando liefe im Container weiter, und die Zeitgrenze
    /// wäre eine Zusage an den Aufrufer, die niemand einhält.
    /// </summary>
    public static async Task KillAsync(string runtime, string name)
    {
        await RunAsync(runtime, ["kill", name], CancellationToken.None).ConfigureAwait(false);
        await RemoveAsync(runtime, name).ConfigureAwait(false);
    }

    private static Task<(int ExitCode, string Output)> RemoveAsync(string runtime, string name)
        => RunAsync(runtime, ["rm", "--force", "--volumes", name], CancellationToken.None);

    /// <summary>
    /// Räumt alles ab, was <b>diese</b> Gateway-Instanz gestartet hat. Bewusst nach dem
    /// Instanz-Etikett und nicht nach dem Besitz-Etikett: Zwei Gateways am selben Daemon sind ein
    /// realer Betriebsfall, und ein Aufräumlauf, der fremde Container abräumt, ist gefährlicher als
    /// der Zustand, den er beheben soll.
    /// </summary>
    /// <returns>Die Kennungen der abgeräumten Container — für Protokoll und Test.</returns>
    public static async Task<IReadOnlyList<string>> SweepInstanceAsync(
        string runtime, string instanceId)
    {
        var (exitCode, output) = await RunAsync(
            runtime,
            ["ps", "--all", "--quiet", "--filter", ContainerIdentity.InstanceFilter(instanceId)],
            CancellationToken.None).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return [];
        }

        var ids = output
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await RunAsync(runtime, ["rm", "--force", "--volumes", .. ids], CancellationToken.None)
            .ConfigureAwait(false);
        return ids;
    }

    /// <summary>
    /// Ruft die Runtime auf und liefert Exitcode und stdout. Schluckt jeden Fehler: Eine nicht
    /// erreichbare Runtime beim Aufräumen ist kein Grund, den Aufrufer scheitern zu lassen — der
    /// Container ist dann ohnehin nicht mehr unsere Angelegenheit.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(
        string runtime, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = runtime,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or OperationCanceledException or IOException)
        {
            return (-1, string.Empty);
        }
    }
}
