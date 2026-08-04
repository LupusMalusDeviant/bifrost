using System.Diagnostics;
using AwesomeAssertions;
using Bifrost.TestServers.Common;
using Xunit;

namespace Bifrost.Integration.Tests;

/// <summary>
/// FR-P056: Was ein hängender Lauf hinterlässt — <b>Sequenz, Dump und aktive Kindprozessliste</b>.
/// <para>
/// <b>Geprüft wird ohne echten Hänger.</b> Einen zu erzwingen hieße, zehn Minuten zu warten und die
/// Suite mutwillig blockieren; geprüft wird stattdessen das, was dann geschrieben würde — mit einem
/// echten Kindprozess, der wirklich läuft. Der Wächter, der den Bericht auslöst, ist eine Frist um
/// diesen Aufruf herum und nichts weiter.
/// </para>
/// </summary>
public sealed class HangReportTests
{
    private readonly ITestOutputHelper _output;

    public HangReportTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>Der Kern.</b> Ein Testlauf hängt in diesem Projekt selten an sich selbst, sondern an einer
    /// Gegenstelle, die nicht antwortet — der Bericht muss sie beim Namen nennen, mit Id und
    /// Startzeit. „Ein Kindprozess läuft noch" beantwortet die Frage nicht, die dann zählt: welcher.
    /// </summary>
    [Fact]
    public async Task The_report_names_a_child_process_that_is_really_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"bifrost-hang-{Guid.NewGuid():N}");

        using var child = Process.Start(new ProcessStartInfo(TestPaths.EchoServerExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        try
        {
            // Der Prozess braucht einen Moment, bis ihn die Prozessliste des Systems fuehrt.
            await Task.Delay(500, ct);

            var path = HangReport.Write(root, "Testfall, kein echter Haenger", ["12:00:00 erster Test", "12:00:05 zweiter Test"]);
            var report = await File.ReadAllTextAsync(path, ct);
            _output.WriteLine(report);

            report.Should().Contain("EchoServer", "der Bericht nennt die laufende Gegenstelle");
            report.Should().Contain(child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "ohne die Id ist die Zeile nicht nachverfolgbar");
            report.Should().Contain("zweiter Test", "die Sequenz endet mit dem, was beim Schreiben lief");
            report.Should().Contain("erster Test", "und traegt die Vorgaenger, weil die den Haenger meist erklaeren");
            report.Should().Contain("blame-hang", "der Bericht sagt, wo der Speicherabzug liegt");
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(5000);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Die Gegenprobe: <b>Kein Kindprozess ist auch eine Aussage.</b> Ein Bericht, der dann schweigt,
    /// ließe offen, ob niemand lief oder ob niemand nachgesehen hat — und der Unterschied entscheidet,
    /// wo man als Nächstes sucht.
    /// </summary>
    [Fact]
    public async Task Without_children_the_report_says_so_instead_of_staying_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"bifrost-hang-{Guid.NewGuid():N}");

        try
        {
            var report = await File.ReadAllTextAsync(
                HangReport.Write(root, "Testfall ohne Kinder", []), ct);

            report.Should().Contain("Aktive Kindprozesse");
            report.Should().Contain("keine — der Haenger liegt dann im Testhost selbst");
            report.Should().Contain("der Lauf hing, bevor ein Test begann");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Die Frist muss über dem längsten legitimen Test liegen — sonst schreibt der Wächter Berichte
    /// über den Chaoslauf, und niemand liest sie mehr.
    /// </summary>
    [Fact]
    public void The_watchdog_waits_longer_than_the_longest_legitimate_test()
    {
        HangWatchdog.Timeout.Should().BeGreaterThan(TimeSpan.FromMinutes(8),
            "der Chaoslauf braucht rund sieben Minuten am Stueck");
    }
}
