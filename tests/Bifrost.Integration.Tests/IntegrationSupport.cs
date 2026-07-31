using System.Diagnostics;
using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;
using Bifrost.Upstream;

namespace Bifrost.Integration.Tests;

internal static class IntegrationSupport
{
    /// <summary>Schnelle Zyklen für Tests: 500ms-Ping, Restart nach 250ms.</summary>
    public static SupervisorOptions FastOptions { get; } = new()
    {
        HealthCheckInterval = TimeSpan.FromMilliseconds(500),
        HealthyResetWindow = TimeSpan.FromSeconds(30),
        DefaultCallTimeout = TimeSpan.FromSeconds(30),
        DefaultDrainGrace = TimeSpan.FromSeconds(2),
        DefaultRestartPolicy = new RestartPolicy(
            MaxRetries: 10,
            InitialBackoff: TimeSpan.FromMilliseconds(250),
            BackoffMultiplier: 1.5,
            MaxBackoff: TimeSpan.FromSeconds(2)),
    };

    public static UpstreamSupervisor CreateSupervisor(SupervisorOptions? options = null)
        => new(
            [new StdioUpstreamConnector(), new StreamableHttpUpstreamConnector()],
            new InMemoryUpstreamConfigStore(),
            options ?? FastOptions);

    public static UpstreamServerConfig StdioServer(string slug, string serverFolder, TimeSpan? callTimeout = null)
        => new(
            slug,
            $"TestServer {serverFolder}",
            UpstreamTransportKind.Stdio,
            Enabled: true,
            Stdio: new StdioTransportOptions(TestPaths.Executable(serverFolder), []),
            CallTimeout: callTimeout);

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Bedingung nicht erreicht{(because is null ? string.Empty : $": {because}")}.");
            }

            await Task.Delay(25);
        }
    }
}
