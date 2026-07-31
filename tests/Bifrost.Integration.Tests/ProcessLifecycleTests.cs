using System.Diagnostics;
using System.Globalization;

using AwesomeAssertions;
using Bifrost.TestServers.Common;
using Xunit;

namespace Bifrost.Integration.Tests;

/// <summary>
/// WP0.4 — der Nachweis, nicht die Behauptung: Ein <b>hart abgebrochener</b> Wirt hinterlässt keinen
/// laufenden Upstream-Prozess.
/// <para>
/// Der normale Weg ist längst geprüft (<c>SupervisorIntegrationTests.Dispose_leaves_no_zombie_processes</c>):
/// Wer ordentlich <c>DisposeAsync</c> aufruft, räumt auf. Der interessante Fall ist der andere — ein
/// Prozess, der gar nicht mehr zum Aufräumen kommt: abgewürgter Testlauf, Timeout des CI-Runners,
/// getöteter Container. Dafür trägt nicht unser Code die Verantwortung, sondern das Betriebssystem:
/// unter Windows ein Job-Objekt mit <c>KILL_ON_JOB_CLOSE</c> (siehe <c>ProcessHygiene</c>), unter
/// Linux die stdio-EOF-Semantik — ein MCP-Server beendet sich, wenn sein stdin schließt.
/// </para>
/// <para>
/// <b>Warum ein zweiter Prozess nötig ist:</b> Ein Test kann seinen eigenen Testhost nicht
/// abschießen. Deshalb startet dieser Test den Wirt <c>OrphanHost</c>, der über den
/// <em>Produktpfad</em> einen echten stdio-Upstream hochfährt, und tötet dann diesen Wirt.
/// </para>
/// <para>
/// <b>Bewusst ohne <c>entireProcessTree</c>:</b> Würde der Test den ganzen Baum töten, prüfte er
/// seine eigene Aufräumarbeit statt der Hygiene, die das Produkt herstellt. Genau die soll hier
/// belegt werden.
/// </para>
/// </summary>
public sealed class ProcessLifecycleTests
{
    private const string UpstreamProcessName = "Bifrost.TestServers.EchoServer";

    [Fact]
    public async Task A_hard_killed_host_leaves_no_upstream_process()
    {
        var ct = TestContext.Current.CancellationToken;
        var echo = TestPaths.EchoServerExecutable;

        var host = StartHost(echo);
        try
        {
            var childId = await ReadChildProcessIdAsync(host, ct);
            childId.Should().NotBeNull("der Wirt meldet die Prozess-Id seines Upstreams");

            var child = Process.GetProcessById(childId!.Value);
            child.HasExited.Should().BeFalse("vor dem Abschuss muss der Upstream laufen");

            // Hart, ohne Baum: nur der Wirt.
            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync(ct);

            await WaitUntilAsync(
                () => HasExited(childId.Value),
                timeoutMs: 20000,
                because: "der Upstream-Prozess muss ohne Zutun des Gateways sterben, wenn sein Wirt "
                    + "hart abgebrochen wird (WP0.4-DoD, ADR-0005)");
        }
        finally
        {
            // Der Test darf nichts hinterlassen — auch nicht, wenn er scheitert.
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }

            host.Dispose();
        }
    }

    /// <summary>
    /// Die Gegenprobe zur Baseline: Nach einem vollständigen Testlauf dieser Klasse darf kein
    /// zusätzlicher Upstream-Prozess übrig sein.
    /// <para>
    /// <b>Warum „läuft" an der Prozess-Id hängt und nicht an einer Namenszählung:</b> Der Wirt
    /// meldet die Id des Kindes, das er gerade gestartet hat — das ist eine Tatsache. Eine Zählung
    /// nach Namen ist eine Vermutung darüber, wie das Betriebssystem den Prozess benennt, und diese
    /// Vermutung ist im ersten Releaselauf zweimal falsch gewesen: erst wegen der 15-Zeichen-Grenze
    /// von <c>/proc/&lt;pid&gt;/comm</c>, dann noch einmal, obwohl der Wirt sein Kind im selben
    /// Moment gefunden hatte.
    /// </para>
    /// <para>
    /// Die Namenszählung bleibt — aber nur dort, wo sie hingehört: als <b>Aufräumprobe</b> am Ende.
    /// Findet sie nichts, schlägt sie nicht fehl; sie kann nur zu viel finden, und genau das soll
    /// sie melden. Bewusst gegen den Prozessnamen und nicht gegen ein Muster wie „alles mit bifrost
    /// im Namen": Ein Test, der fremde Prozesse abräumt, ist gefährlicher als der Zustand, den er
    /// beheben soll.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_suite_returns_to_its_process_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = UpstreamProcessLookup.FindByExecutableName(UpstreamProcessName).Count;

        var host = StartHost(TestPaths.EchoServerExecutable);
        int childId;
        try
        {
            var reported = await ReadChildProcessIdAsync(host, ct);
            reported.Should().NotBeNull("der Wirt meldet die Prozess-Id seines Upstreams");
            childId = reported!.Value;

            HasExited(childId).Should().BeFalse(
                "waehrend der Wirt laeuft, laeuft auch sein Upstream");

            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync(ct);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }

            host.Dispose();
        }

        await WaitUntilAsync(
            () => HasExited(childId),
            timeoutMs: 20000,
            because: "nach dem Lauf darf der Upstream-Prozess des Wirts nicht mehr laufen");

        UpstreamProcessLookup.FindByExecutableName(UpstreamProcessName).Count
            .Should().BeLessThanOrEqualTo(
                baseline,
                "diese Klasse darf keinen zusaetzlichen Upstream-Prozess hinterlassen");
    }

    private static Process StartHost(string upstreamExecutable)
    {
        var start = new ProcessStartInfo(TestPaths.Executable("OrphanHost"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(upstreamExecutable);

        return Process.Start(start)
            ?? throw new InvalidOperationException("OrphanHost konnte nicht gestartet werden.");
    }

    /// <summary>Liest die vom Wirt gemeldete Kind-Prozess-Id ("READY &lt;pid&gt;").</summary>
    private static async Task<int?> ReadChildProcessIdAsync(Process host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        while (!timeout.IsCancellationRequested)
        {
            var line = await host.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null)
            {
                var error = await host.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException($"OrphanHost endete ohne READY. stderr: {error}");
            }

            if (line.StartsWith("READY ", StringComparison.Ordinal)
                && int.TryParse(line[6..], CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            // Kein Prozess mit dieser Id mehr — genau das ist der Erfolgsfall.
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string because)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Bedingung nicht erreicht: {because}.");
            }

            await Task.Delay(100);
        }
    }
}
