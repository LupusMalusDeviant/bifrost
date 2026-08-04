using System.Diagnostics;
using System.Net.Http.Json;
using Bifrost.Performance.Tests.Harness;
using Bifrost.TestServers.Common;
using ModelContextProtocol.Client;

namespace Bifrost.Soak.Tests.Harness;

internal sealed record ChaosEvent(double AtSeconds, string Action, string Detail);

/// <summary>
/// Die Störungen (FR-P052). Sie laufen <b>periodisch und in fester Reihenfolge</b>, nicht zufällig:
/// Ein Soak-Lauf soll wiederholbar sein, und „mal kam der Crash früher" macht zwei Läufe
/// unvergleichbar.
/// <para>
/// Was hier fehlt und im Ergebnis benannt wird: die Unterbrechung einer PostgreSQL-Verbindung und
/// der knappe Plattenplatz. Beides braucht Docker bzw. ein präpariertes Dateisystem
/// (M5-Vertrag §10).
/// </para>
/// </summary>
internal sealed class ChaosEngine(GatewayProcess gateway, Guid crashServerId, Stopwatch clock)
{
    private readonly List<ChaosEvent> _events = [];
    private int _round;

    public IReadOnlyList<ChaosEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await NextAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Record("chaos-failed", ex.Message);
            }
        }
    }

    private async Task NextAsync(CancellationToken ct)
    {
        switch (_round++ % 3)
        {
            case 0:
                KillUpstreamProcess();
                break;
            case 1:
                await CrashUpstreamFromInsideAsync(ct);
                break;
            default:
                await ReconnectAsync(ct);
                break;
        }
    }

    /// <summary>Der harte Fall: Der Upstream-Prozess verschwindet unter dem Gateway weg.</summary>
    private void KillUpstreamProcess()
    {
        var pids = UpstreamProcessLookup.FindByExecutableName("Bifrost.TestServers.EchoServer");
        if (pids.Count == 0)
        {
            Record("kill-upstream", "kein Prozess gefunden");
            return;
        }

        var pid = pids[0];
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            Record("kill-upstream", $"PID {pid} hart beendet");
        }
        catch (ArgumentException)
        {
            Record("kill-upstream", $"PID {pid} war schon weg");
        }
    }

    /// <summary>
    /// Der Upstream beendet sich <b>mitten im Aufruf</b> selbst — eine Antwort kommt nie. Das ist
    /// der Fall, den ein Kill von außen nicht erzeugt: Hier hängt eine offene Anfrage daran.
    /// </summary>
    private async Task CrashUpstreamFromInsideAsync(CancellationToken ct)
    {
        var http = gateway.CreateHttpClient();
        try
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Name = "chaos", Endpoint = new Uri(gateway.BaseAddress, "/mcp") },
                http);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            await client.CallToolAsync("crash__crash", new Dictionary<string, object?>(), cancellationToken: ct);
            Record("crash-upstream", "crash-Werkzeug hat geantwortet — unerwartet");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record("crash-upstream", $"Aufruf brach ab wie erwartet ({ex.GetType().Name})");
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>Abschalten und wieder anschalten — der geordnete Verbindungsabbruch.</summary>
    private async Task ReconnectAsync(CancellationToken ct)
    {
        await gateway.Client.PostAsJsonAsync(
            $"api/v1/servers/{crashServerId}/enabled", new { enabled = false }, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await gateway.Client.PostAsJsonAsync(
            $"api/v1/servers/{crashServerId}/enabled", new { enabled = true }, ct);
        Record("reconnect", $"Upstream {crashServerId} aus und wieder an");
    }

    private void Record(string action, string detail)
    {
        lock (_events)
        {
            _events.Add(new ChaosEvent(Math.Round(clock.Elapsed.TotalSeconds, 1), action, detail));
        }
    }
}
