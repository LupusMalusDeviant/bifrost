using System.Diagnostics;
using System.Reflection;
using Bifrost.Performance.Tests.Harness;

namespace Bifrost.Ui.E2E.Harness;

/// <summary>
/// Veröffentlicht den Server einmal je Lauf und liefert den Pfad der Executable.
/// <para>
/// <b>Warum nicht einfach <c>bin</c> wie in WP5.3 und WP5.4:</b> Weil die Oberfläche im
/// Entwicklungslayout nicht funktioniert — <c>_framework/blazor.web.js</c> kommt dort mit Status
/// 200 und <b>null Bytes</b> zurück, weil die Datei erst beim Veröffentlichen nach
/// <c>wwwroot</c> gelangt. Ohne sie startet der Blazor-Circuit nie: Die Seiten werden weiter
/// serverseitig gerendert und sehen benutzbar aus, aber kein Knopf tut etwas.
/// </para>
/// <para>
/// Genau dieser Ausfall ist im ersten echten Betrieb passiert (siehe
/// <c>BlazorScriptDeliveryTests</c>). Ein Browsertest gegen <c>bin</c> hätte ihn nicht gefunden —
/// er hätte ihn <em>gehabt</em>, und man hätte den Test für kaputt gehalten statt das Layout.
/// </para>
/// </summary>
internal static class PublishedGateway
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _executable;

    public static async Task<string> EnsureAsync(CancellationToken ct)
    {
        if (_executable is not null)
        {
            return _executable;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (_executable is not null)
            {
                return _executable;
            }

            // **Befund aus dem Audit.** Ohne '-c' veroeffentlicht 'dotnet publish' immer Debug —
            // auch dann, wenn die CI in Release baut und testet. Die Browserwege haetten damit ein
            // anderes Artefakt geprueft als das ausgelieferte, und zwar unbemerkt: Ein
            // Debug-Publish funktioniert ja. Das Verzeichnis traegt die Konfiguration jetzt im
            // Namen, sonst wuerde ein alter Stand wiederverwendet.
            var directory = Path.Combine(TestServerPaths.RepoRoot, "artifacts", "e2e-publish", Configuration);
            var executable = Path.Combine(
                directory, "Bifrost.Server" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

            // Wer die Veröffentlichung schon hat (CI-Schritt davor), veröffentlicht nicht noch
            // einmal — ABER nur, wenn sie auch zum Quellstand passt.
            //
            // **Befund aus WP6.2.** Ohne die Altersprüfung lief der neue Browserweg gegen die
            // Veröffentlichung des vorigen Pakets: Die Seite trug die neue Karte nicht, der Test
            // war rot, und die Ursache sah nach einem Fehler in der Oberfläche aus. Das ist die
            // schlimmste Sorte Fehlschlag — er zeigt auf die falsche Stelle. Schlimmer noch waere
            // der umgekehrte Fall: ein GRUENER Lauf gegen einen alten Stand.
            if (!File.Exists(executable)
                || !File.Exists(Path.Combine(directory, "wwwroot", "_framework", "blazor.web.js"))
                || NewestSourceChange() > File.GetLastWriteTimeUtc(executable))
            {
                await PublishAsync(directory, ct);
            }

            return _executable = executable;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Der jüngste Zeitpunkt, zu dem sich am Quellstand etwas geändert hat. Verglichen wird nur
    /// gegen <c>src/</c> — Testdateien landen nicht in der Veröffentlichung.
    /// </summary>
    private static DateTime NewestSourceChange()
    {
        var source = Path.Combine(TestServerPaths.RepoRoot, "src");
        if (!Directory.Exists(source))
        {
            // Kein Quellverzeichnis (etwa ein reiner Artefaktlauf in der CI): Dann ist die
            // vorhandene Veröffentlichung das Beste, was es gibt — und kein Grund, sie zu
            // verwerfen.
            return DateTime.MinValue;
        }

        var newest = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var stamp = File.GetLastWriteTimeUtc(file);
            if (stamp > newest)
            {
                newest = stamp;
            }
        }

        return newest;
    }

    /// <summary>Dieselbe Konfiguration, in der die Testassembly gebaut wurde.</summary>
    private static string Configuration => typeof(PublishedGateway).Assembly
        .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    private static async Task PublishAsync(string directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = TestServerPaths.RepoRoot,
        };
        start.ArgumentList.Add("publish");
        start.ArgumentList.Add(Path.Combine("src", "Bifrost.Server"));
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Configuration);
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("--nologo");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("dotnet publish ließ sich nicht starten.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish fehlgeschlagen ({process.ExitCode}):\n{stdout}\n{stderr}");
        }

        var script = Path.Combine(directory, "wwwroot", "_framework", "blazor.web.js");
        if (!File.Exists(script) || new FileInfo(script).Length == 0)
        {
            throw new InvalidOperationException(
                $"Die Veröffentlichung enthält keinen Blazor-Einstiegspunkt: {script}. "
                + "Ohne ihn ist die gesamte Oberfläche tot — siehe BlazorScriptDeliveryTests.");
        }
    }
}
