using System.Diagnostics;
using Bifrost.Abstractions;
using Bifrost.Core.Execution;
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

    /// <summary>
    /// Ein Supervisor mit ausdruecklich erlaubter Host-Ausfuehrung (ADR-0025). Die Integrationstests
    /// starten echte stdio-Testserver, und stdio laeuft nativ — ohne die Erlaubnis kaeme keiner
    /// davon hoch. Sie steht hier sichtbar, statt dass der Kern sie stillschweigend annimmt.
    /// </summary>
    public static UpstreamSupervisor CreateSupervisor(SupervisorOptions? options = null)
        => new(
            [new StdioUpstreamConnector(), new StreamableHttpUpstreamConnector()],
            new InMemoryUpstreamConfigStore(),
            options ?? FastOptions,
            hostExecution: HostExecutionPolicy.AllowedByOperator());

    public static UpstreamServerConfig StdioServer(string slug, string serverFolder, TimeSpan? callTimeout = null)
        => new(
            slug,
            $"TestServer {serverFolder}",
            UpstreamTransportKind.Stdio,
            Enabled: true,
            Stdio: new StdioTransportOptions(TestPaths.Executable(serverFolder), []),
            CallTimeout: callTimeout);

    /// <summary>
    /// Wartet auf eine Bedingung, die ein echter Prozess herstellt.
    /// <para>
    /// <b>Warum 30 Sekunden und nicht 15:</b> Die Suite hat mit <c>Bifrost.Security.Tests</c> und
    /// <c>Bifrost.Upgrade.Tests</c> zwei Projekte bekommen, die selbst Prozesse und Container
    /// starten. Unter der Last des Gesamtlaufs sind zwei Tests hier hineingelaufen, die einzeln in
    /// zehn Sekunden durchlaufen — die Zeitschranke maß also die Auslastung der Maschine, nicht das
    /// Verhalten des Produkts.
    /// </para>
    /// <para>
    /// Ein Zeitlimit heraufzusetzen ist die schwächste aller Antworten, und sie ist hier trotzdem
    /// die richtige: Ein Test, der gelegentlich ohne Grund rot wird, kostet mehr als er sichert —
    /// man gewöhnt sich an rote Läufe. Die Schranke bleibt scharf genug, um einen Upstream zu
    /// fangen, der gar nicht hochkommt; genau dafür ist sie da.
    /// </para>
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 30000, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(
                    $"Bedingung nach {sw.Elapsed.TotalSeconds:0.#} s nicht erreicht"
                    + $"{(because is null ? string.Empty : $": {because}")}.");
            }

            await Task.Delay(25);
        }
    }
}
