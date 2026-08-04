using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Bifrost.Performance.Tests.Harness;

/// <param name="Endpoint">
/// Wohin die Last geht. <c>null</c> heisst: direkt an den Gateway. Gesetzt wird das nur fuer den
/// Reverse-Proxy-Sprung — dort laeuft dieselbe Last ueber einen vorgeschalteten Proxy, damit der
/// Unterschied zwischen beiden Wegen der Anteil des Proxy ist und nicht eine andere Messung.
/// </param>
internal sealed record LoadOptions(
    string ToolName,
    int UpstreamLatencyMs,
    int Concurrency = 32,
    int WarmupSeconds = 5,
    int DurationSeconds = 30,
    Uri? Endpoint = null);

internal sealed record LoadMeasurement(
    int Calls,
    int Errors,
    double P50,
    double P95,
    double P99,
    double Max,
    double ThroughputPerSecond,
    double WallSeconds,
    double UpstreamP50,
    double UpstreamP95,
    double GatewayOverheadP50,
    double GatewayOverheadP95);

/// <summary>
/// Dauerlast statt Stoß.
/// <para>
/// Der bisherige Harness hat 1000 Aufrufe in ein Fenster von 0,1 Sekunden gedrückt und daraus einen
/// Durchsatz abgeleitet — eine Spitzenrate unter idealen Bedingungen, keine Kapazitätsaussage
/// (<c>docs/acceptance/performance.md</c>). Hier laufen feste virtuelle Clients über eine
/// vorgegebene Zeit; der Durchsatz ist damit das, was die Strecke <em>hält</em>, und nicht das, was
/// sie kurz schafft.
/// </para>
/// <para>
/// <b>Getrennte Spans:</b> Der Upstream schläft eine bekannte Zeit. Was darüber hinaus gemessen
/// wird, ist der Anteil des Gateways plus Netzstrecke — nur so lässt sich „Overhead" überhaupt
/// beziffern, statt Fremdlatenz mitzumessen.
/// </para>
/// </summary>
internal static class LoadDriver
{
    public static async Task<LoadMeasurement> RunAsync(
        GatewayProcess gateway, LoadOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);

        var clients = new List<(McpClient Client, HttpClient Http)>();
        for (var i = 0; i < options.Concurrency; i++)
        {
            var target = options.Endpoint ?? gateway.BaseAddress;
            var http = gateway.CreateHttpClient(target);
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = $"perf-{i}",
                    Endpoint = new Uri(target, "/mcp"),
                },
                http);
            clients.Add((await McpClient.CreateAsync(transport, cancellationToken: ct), http));
        }

        try
        {
            var arguments = new Dictionary<string, object?> { ["milliseconds"] = options.UpstreamLatencyMs };

            // Aufwärmen: JIT, Verbindungsaufbau, Upstream-Prozess, Kataloge. Der erste Lauf ist
            // sonst der Kaltstart — und genau so ist die 59-ms-Zahl vom 18.07. entstanden.
            await DriveAsync(clients, options.ToolName, arguments,
                TimeSpan.FromSeconds(options.WarmupSeconds), samples: null, ct);

            var samples = new ConcurrentBag<CallSample>();
            var wall = Stopwatch.StartNew();
            var errors = await DriveAsync(clients, options.ToolName, arguments,
                TimeSpan.FromSeconds(options.DurationSeconds), samples, ct);
            wall.Stop();

            if (samples.IsEmpty)
            {
                throw new InvalidOperationException("Der Lastlauf hat keinen einzigen Aufruf abgesetzt.");
            }

            var total = samples.Select(s => s.TotalMs).Order().ToArray();
            var upstream = samples.Select(s => s.UpstreamMs).Order().ToArray();
            // Der Overhead wird JE AUFRUF gebildet, nicht als Differenz zweier Perzentile: Zwei
            // Verteilungen voneinander abzuziehen ergibt keine dritte Verteilung.
            var overhead = samples.Select(s => Math.Max(0, s.TotalMs - s.UpstreamMs)).Order().ToArray();

            static double At(double[] sorted, double q) =>
                sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * q))];

            return new LoadMeasurement(
                Calls: total.Length,
                Errors: errors,
                P50: At(total, 0.50),
                P95: At(total, 0.95),
                P99: At(total, 0.99),
                Max: total[^1],
                ThroughputPerSecond: total.Length / wall.Elapsed.TotalSeconds,
                WallSeconds: wall.Elapsed.TotalSeconds,
                UpstreamP50: At(upstream, 0.50),
                UpstreamP95: At(upstream, 0.95),
                GatewayOverheadP50: At(overhead, 0.50),
                GatewayOverheadP95: At(overhead, 0.95));
        }
        finally
        {
            foreach (var (client, http) in clients)
            {
                await client.DisposeAsync();
                http.Dispose();
            }
        }
    }

    private static async Task<int> DriveAsync(
        IReadOnlyList<(McpClient Client, HttpClient Http)> clients,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan duration,
        ConcurrentBag<CallSample>? samples,
        CancellationToken ct)
    {
        var errors = 0;
        var deadline = Stopwatch.StartNew();

        await Task.WhenAll(clients.Select(entry => Task.Run(async () =>
        {
            while (deadline.Elapsed < duration && !ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                CallToolResult? result = null;
                try
                {
                    result = await entry.Client.CallToolAsync(toolName, arguments, cancellationToken: ct);
                    sw.Stop();
                    if (result.IsError == true)
                    {
                        Interlocked.Increment(ref errors);
                        continue;
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    sw.Stop();
                    Interlocked.Increment(ref errors);
                    continue;
                }

                // Erst nach dem Anhalten der Uhr auswerten — sonst steht die Auswertung im Messwert.
                samples?.Add(new CallSample(sw.Elapsed.TotalMilliseconds, UpstreamSpan(result)));
            }
        }, ct)));

        return errors;
    }

    /// <summary>
    /// Der vom Upstream selbst gemessene Span. <c>sleep_measured</c> gibt ihn als Zahl zurück —
    /// die angeforderte Zeit taugt nicht als Fremdanteil, weil <c>Task.Delay(20)</c> unter Windows
    /// regelmäßig gut 31 ms dauert. Genau dieser Irrtum hat im ersten Lauf dieses Harness rund
    /// 11 ms „Gateway-Overhead" erzeugt, die keiner waren.
    /// </summary>
    private static double UpstreamSpan(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            ? ms
            : throw new InvalidOperationException(
                $"Der Upstream hat keinen messbaren Span geliefert: '{text}'. "
                + "Der Lastlauf braucht 'sleep_measured', nicht 'sleep'.");
    }

    private readonly record struct CallSample(double TotalMs, double UpstreamMs);
}
