using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SlowTools>();

await builder.Build().RunAsync();

[McpServerToolType]
internal sealed class SlowTools
{
    [McpServerTool(Name = "sleep")]
    [Description("Waits the given number of milliseconds, then returns.")]
    public static async Task<string> Sleep(
        [Description("Delay in milliseconds.")] int milliseconds,
        CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken);
        return $"Slept {milliseconds} ms";
    }

    /// <summary>
    /// Wie <see cref="Sleep"/>, meldet aber die <b>tatsächlich</b> verbrauchte Zeit als Zahl zurück
    /// (Millisekunden, invariant formatiert).
    /// <para>
    /// <b>Warum es das braucht (WP5.3).</b> Der Lastharness zieht den Fremdanteil vom Messwert ab,
    /// um den Gateway-Overhead zu beziffern. Zieht er die <em>angeforderte</em> Zeit ab, misst er
    /// die Auflösung des Systemtimers mit: Ein <c>Task.Delay(20)</c> dauert unter Windows regelmäßig
    /// gut 31 ms. Der erste Lauf dieses Harness hat deshalb rund 11 ms „Gateway-Overhead" gemeldet,
    /// die keiner waren. Was hier zurückkommt, ist der gemessene Upstream-Span.
    /// </para>
    /// </summary>
    [McpServerTool(Name = "sleep_measured")]
    [Description("Waits the given number of milliseconds and returns the elapsed time in milliseconds.")]
    public static async Task<string> SleepMeasured(
        [Description("Delay in milliseconds.")] int milliseconds,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(milliseconds, cancellationToken);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds.ToString(
            "0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
