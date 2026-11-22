using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Faolan.Cli.Tests;

/// <summary>
/// Die Namenskette <c>MCPMCP_</c> → <c>BIFROST_</c> → <c>FAOLAN_</c> im <b>CLI-Prozess</b>
/// (Vertrag umbenennung-faolan §2.2, Nachweis aus §7).
/// <para>
/// Geprüft wird von außen, am gestarteten Programm: Die CLI hat keine Projektverweise und bindet
/// die Kette als Quelldatei ein. Ein Test gegen die Klasse allein bewiese nur, dass die Datei
/// stimmt — nicht, dass das Programm sie vor dem Lesen der Konfiguration anwendet.
/// </para>
/// </summary>
public sealed class CliLegacyEnvironmentTests
{
    [Theory]
    [InlineData("MCPMCP_")]
    [InlineData("BIFROST_")]
    public async Task Endpoint_and_token_under_an_old_prefix_reach_the_gateway(string prefix)
    {
        using var listener = Listener();
        var token = "cli-" + Guid.NewGuid().ToString("N");

        var run = RunCliAsync(new Dictionary<string, string>
        {
            [prefix + "ENDPOINT"] = Address(listener),
            [prefix + "TOKEN"] = token,
        });

        var request = await AcceptRequestAsync(listener);
        var (_, stderr) = await run;

        request.Should().Contain($"Authorization: Bearer {token}", "das Token kam unter dem alten Namen");
        stderr.Should().Contain(prefix + "ENDPOINT").And.Contain(prefix + "TOKEN")
            .And.Contain("FAOLAN_", "die Übernahme wird gemeldet, mit dem Namen, auf den umzustellen ist");
    }

    /// <summary>Der neueste gesetzte Name gewinnt — auch über die mittlere Generation.</summary>
    [Fact]
    public async Task The_current_name_wins_over_the_old_ones()
    {
        using var listener = Listener();
        using var decoy = Listener();
        var token = "cli-" + Guid.NewGuid().ToString("N");

        var run = RunCliAsync(new Dictionary<string, string>
        {
            ["MCPMCP_ENDPOINT"] = Address(decoy),
            ["BIFROST_ENDPOINT"] = Address(decoy),
            ["FAOLAN_ENDPOINT"] = Address(listener),
            ["FAOLAN_TOKEN"] = token,
        });

        var request = await AcceptRequestAsync(listener);
        await run;

        request.Should().Contain($"Authorization: Bearer {token}");
        decoy.Pending().Should().BeFalse("die alten Namen dürfen nicht gewinnen");
    }

    private static TcpListener Listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static string Address(TcpListener listener)
        => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

    /// <summary>Nimmt einen Request an, liest die Kopfzeilen und antwortet mit 503.</summary>
    private static async Task<string> AcceptRequestAsync(TcpListener listener)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = client.GetStream();
        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, timeout.Token);
        return head.ToString();
    }

    private static async Task<(int ExitCode, string Stderr)> RunCliAsync(IReadOnlyDictionary<string, string> environment)
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "faolan.dll");
        File.Exists(cli).Should().BeTrue($"die gebaute CLI liegt neben den Tests ({cli})");

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cli);
        // "servers list" stellt genau eine Anfrage; "status" stellt zwei (healthz, readyz).
        start.ArgumentList.Add("servers");
        start.ArgumentList.Add("list");

        // Die Umgebung des Testlaufs darf nicht mitspielen: Jede Generation wird geleert und nur
        // gesetzt, was der Fall vorgibt.
        foreach (var name in start.Environment.Keys.ToList())
        {
            if (name.StartsWith("MCPMCP_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("BIFROST_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("FAOLAN_", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(name);
            }
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        await stdout;
        return (process.ExitCode, await stderr);
    }
}
