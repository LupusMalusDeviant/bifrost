using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Bifrost.Abstractions;
using ModelContextProtocol.Client;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// WP8.3: formale NFR-01/02-Messung (20 Sessions, 100 In-Flight-Calls, 100-Tool-Katalog).
/// Läuft **nicht** in der normalen Suite — CI-Runner sind keine Referenz-Hardware und würden nur
/// Rauschen messen. Bewusst per Umgebungsvariable scharfgeschaltet:
/// <c>$env:BIFROST_RUN_BENCHMARK=1; dotnet test --filter FullyQualifiedName~PerformanceBenchmark</c>
/// </summary>
public sealed class PerformanceBenchmarkTests : IClassFixture<GatewayFixture>
{
    private const int Sessions = 20;
    private const int CallsPerSession = 50;
    private const int MaxInFlight = 100;

    private readonly GatewayFixture _gw;
    private readonly ITestOutputHelper _output;

    public PerformanceBenchmarkTests(GatewayFixture gw, ITestOutputHelper output)
    {
        _gw = gw;
        _output = output;
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("BIFROST_RUN_BENCHMARK") == "1";

    [Fact]
    public async Task Nfr01_gateway_overhead_and_tools_list_under_load()
    {
        Assert.SkipUnless(Enabled, "Benchmark nur mit BIFROST_RUN_BENCHMARK=1 (Referenz-Hardware).");

        var serverId = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                "bench", "Benchmark-Upstream (100 Tools)", UpstreamTransportKind.Stdio, Enabled: true,
                Stdio: new StdioTransportOptions(TestPaths.Executable("BulkServer"), [])),
            TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(serverId)?.State == UpstreamState.Healthy, timeoutMs: 30000);

        var toolCount = _gw.Supervisor.GetInventory(serverId)!.Tools.Count;
        toolCount.Should().Be(100, "der Referenz-Katalog verlangt 100 Tools");

        // Alle 100 Tools pinnen → tools/list liefert den vollen Katalog (NFR-01, 2. Schranke).
        var pinned = _gw.Supervisor.GetInventory(serverId)!.Tools
            .Select(t => NamespacedToolName.Create("bench", t.Name)).ToList();
        var profile = new ToolProfile(ProfileId.New(), "bench-full", pinned, LazyToolsEnabled: false);
        var (_, apiKey) = await _gw.SeedAdminAsync("bench-agent", profile);

        var clients = new List<McpClient>();
        for (var i = 0; i < Sessions; i++)
        {
            clients.Add(await _gw.ConnectClientAsync(apiKey));
        }

        try
        {
            // ── Warmup (JIT, Verbindungen, Katalog-Cache) ───────────────────────
            foreach (var client in clients)
            {
                await client.CallToolAsync("bench__tool_000",
                    new Dictionary<string, object?> { ["target"] = "warmup", ["dryRun"] = true });
            }

            // ── tools/list über den 100-Tool-Katalog ────────────────────────────
            var listDurations = new ConcurrentBag<double>();
            await Task.WhenAll(clients.Select(client => Task.Run(async () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var tools = await client.ListToolsAsync();
                    sw.Stop();
                    tools.Count.Should().Be(100);
                    listDurations.Add(sw.Elapsed.TotalMilliseconds);
                }
            })));

            // ── tools/call unter Last: 20 Sessions × 50 Calls, ≤100 gleichzeitig ─
            var callDurations = new ConcurrentBag<double>();
            var errors = 0;
            using var gate = new SemaphoreSlim(MaxInFlight);
            var wall = Stopwatch.StartNew();

            await Task.WhenAll(clients.Select(client => Task.Run(async () =>
            {
                for (var i = 0; i < CallsPerSession; i++)
                {
                    await gate.WaitAsync();
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var result = await client.CallToolAsync($"bench__tool_{i % 100:D3}",
                            new Dictionary<string, object?> { ["target"] = "bench", ["dryRun"] = true });
                        sw.Stop();
                        if (result.IsError == true)
                        {
                            Interlocked.Increment(ref errors);
                        }

                        callDurations.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            })));

            wall.Stop();

            var calls = Percentiles(callDurations);
            var lists = Percentiles(listDurations);
            var totalCalls = callDurations.Count;
            var throughput = totalCalls / wall.Elapsed.TotalSeconds;

            _output.WriteLine("=== NFR-01/02 Referenzmessung ===");
            _output.WriteLine($"Maschine:     {Environment.ProcessorCount} logische Kerne, {Environment.OSVersion}, .NET {Environment.Version}");
            _output.WriteLine($"Aufbau:       {Sessions} Sessions, {CallsPerSession} Calls/Session, max {MaxInFlight} gleichzeitig, {toolCount} Tools");
            _output.WriteLine($"tools/call:   n={totalCalls}  p50={calls.P50:0.0} ms  p95={calls.P95:0.0} ms  p99={calls.P99:0.0} ms  max={calls.Max:0.0} ms");
            _output.WriteLine($"tools/list:   n={listDurations.Count}  p50={lists.P50:0.0} ms  p95={lists.P95:0.0} ms  p99={lists.P99:0.0} ms");
            _output.WriteLine($"Durchsatz:    {throughput:0} Calls/s über {wall.Elapsed.TotalSeconds:0.0} s");
            _output.WriteLine($"Fehler:       {errors}");

            errors.Should().Be(0, "unter Last darf kein Call scheitern (NFR-02)");
            calls.P95.Should().BeLessThan(50, "NFR-01: Gateway-Overhead p95 ≤ 50 ms");
            lists.P95.Should().BeLessThan(200, "NFR-01: tools/list mit 100 Tools p95 ≤ 200 ms");
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }

            await _gw.Supervisor.RemoveAsync(serverId, DrainPolicy.Immediate, TestContext.Current.CancellationToken);
        }
    }

    private static (double P50, double P95, double P99, double Max) Percentiles(ConcurrentBag<double> values)
    {
        var sorted = values.Order().ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * q))];
        return (At(0.50), At(0.95), At(0.99), sorted[^1]);
    }

    private static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
