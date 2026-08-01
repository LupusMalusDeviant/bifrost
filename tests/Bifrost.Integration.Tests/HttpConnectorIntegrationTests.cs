using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream;
using Xunit;

namespace Bifrost.Integration.Tests;

/// <summary>WP1.1: StreamableHttp-Konnektor gegen einen echten ASP.NET-Core-MCP-Server.</summary>
public class HttpConnectorIntegrationTests
{
    [Fact]
    public async Task Connector_discovers_and_calls_tool_over_streamable_http()
    {
        var port = GetFreePort();
        using var server = StartHttpServer(port);
        try
        {
            var connector = new StreamableHttpUpstreamConnector();
            var config = new UpstreamServerConfig(
                "http-echo",
                "HTTP-EchoServer",
                UpstreamTransportKind.StreamableHttp,
                Enabled: true,
                Http: new HttpTransportOptions(new Uri($"http://127.0.0.1:{port}")));

            var connection = await ConnectWithRetryAsync(connector, config);
            await using (connection)
            {
                var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);
                inventory.Tools.Should().ContainSingle(t => t.Name == "echo");

                var result = await connection.CallToolAsync(
                    "echo", JsonSerializer.SerializeToElement(new { message = "über HTTP" }), TestContext.Current.CancellationToken);
                result.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Echo: über HTTP");

                // Die ausgehandelte Fassung gegen eine ECHTE Gegenstelle. Ein Test mit erfundenem
                // Client bewiese nur, dass eine Eigenschaft durchgereicht wird; hier steht, dass
                // dabei auch etwas ankommt — und zwar eine Fassung, keine Familie.
                var protocol = connection.Protocol;
                protocol.Availability.Should().Be(UpstreamProtocolAvailability.Negotiated,
                    "gegen einen laufenden MCP-Server ist die Fassung ausgehandelt, nicht unbekannt");
                protocol.Version.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$",
                    "die Revisionen sind datumssortiert benannt");
                protocol.Capabilities.Should().Contain("tools",
                    "der Server hat gerade ein Werkzeug geliefert");
            }
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<IUpstreamConnection> ConnectWithRetryAsync(
        StreamableHttpUpstreamConnector connector, UpstreamServerConfig config)
    {
        var deadline = Stopwatch.StartNew();
        Exception? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                return await connector.ConnectAsync(ServerId.New(), config, TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(250);
            }
        }

        throw new TimeoutException($"HTTP-TestServer wurde nicht erreichbar: {last?.Message}", last);
    }

    private static Process StartHttpServer(int port)
    {
        // Der EINZIGE Startpfad eines Testservers, der nicht ueber den Produktweg laeuft: Alle
        // anderen gehen ueber StdioTransportOptions und damit ueber StdioUpstreamConnector, der die
        // Prozess-Hygiene selbst herstellt. Hier startet der Test direkt — und ohne diese Zeile
        // haengt das Kind an keinem Job-Objekt.
        //
        // Folge ohne sie: Ein hart abgebrochener Testlauf (Strg+C, Timeout des Runners, gekillter
        // Testhost) laesst diesen Prozess stehen. Er haelt dann seinen Port UND seine eigene
        // Programmdatei — der naechste Build scheitert mit "wird von einem anderen Prozess
        // verwendet", und die Ursache steht zwei Laeufe frueher. Genau dieses Bild gab es in dieser
        // Arbeitsumgebung mehrfach (WP0.4).
        ProcessHygiene.EnsureInitialized();

        var psi = new ProcessStartInfo
        {
            FileName = TestPaths.Executable("HttpServer"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add($"http://127.0.0.1:{port}");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("HTTP-TestServer-Prozess konnte nicht gestartet werden.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
