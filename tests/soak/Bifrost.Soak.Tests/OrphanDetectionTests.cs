using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Bifrost.Performance.Tests.Harness;
using Bifrost.Soak.Tests.Harness;
using Xunit;

namespace Bifrost.Soak.Tests;

/// <summary>
/// Die Gegenprobe für die stärkste Zusicherung des Soak-Laufs: „keine verwaisten Prozesse".
/// <para>
/// <b>Befund aus dem Audit:</b> Der Waisen-Detektor hat in jedem bisherigen Lauf eine leere Liste
/// gemeldet — und das sah aus wie ein Nachweis. Ein Detektor, der nie etwas findet, ist von einem
/// kaputten aber nicht zu unterscheiden. Hier bekommt er einen echten Nachzügler vorgesetzt.
/// </para>
/// </summary>
public sealed class OrphanDetectionTests
{
    [Fact]
    public async Task A_process_that_outlives_the_run_is_reported()
    {
        var metrics = Path.Combine(Path.GetTempPath(), $"soak-orphan-{Guid.NewGuid():N}.jsonl");
        using var self = Process.GetCurrentProcess();

        // Die Grundlinie entsteht beim Bau des Trackers — alles, was DANACH auftaucht und den Lauf
        // ueberlebt, ist ein Nachzuegler.
        await using var tracker = new ResourceTracker(
            self, metrics, TimeSpan.FromMinutes(5), () => (0, 0));

        tracker.OrphansAfterShutdown().Should().BeEmpty("vor dem Start gibt es nichts zu finden");

        var nachzuegler = Process.Start(new ProcessStartInfo(TestServerPaths.Executable("EchoServer"))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        try
        {
            // Der Prozess muss dem Betriebssystem bekannt sein, bevor die Suche ihn sehen kann.
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            // Bewusst "enthaelt" und nicht "genau einer": Im Lauf ueber die ganze Solution starten
            // andere Suiten gleichzeitig eigene Testserver. Genau daran ist dieser Test beim
            // CI-Nachbau umgefallen — und ein Test, der nur allein gruen ist, prueft die Ordnung
            // des Laufplans mit, nicht das Produkt.
            tracker.OrphansAfterShutdown().Should().Contain(
                beschreibung => beschreibung.Contains(nachzuegler.Id.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal),
                "der Nachzuegler dieses Tests muss benannt werden");
        }
        finally
        {
            nachzuegler.Kill(entireProcessTree: true);
            await nachzuegler.WaitForExitAsync(CancellationToken.None);
            nachzuegler.Dispose();

            // Erst den Mitschrieb schliessen, dann die Datei loeschen — sonst haelt der Tracker sie
            // noch offen, und der Test faellt an seinem eigenen Aufraeumen um.
            await tracker.DisposeAsync();
            File.Delete(metrics);
        }
    }
}
