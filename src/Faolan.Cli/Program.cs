using System.Net.Http.Headers;
using System.Text.Json;

using Faolan.Abstractions;
using Faolan.Cli;

// Alt benannte Variablen (MCPMCP_*, BIFROST_*) übernehmen, bevor irgendetwas sie liest —
// dieselbe Kette wie im Server (Vertrag umbenennung-faolan §2.2).
// Der Hinweis geht nach stderr, damit --json-Ausgaben auf stdout unverändert bleiben.
var adoptedLegacyVariables = EnvironmentNameChain.Adopt();
if (adoptedLegacyVariables.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"Hinweis: alt benannte Umgebungsvariablen übernommen ({string.Join(", ", adoptedLegacyVariables)}); "
        + $"auf {EnvironmentNameChain.CurrentPrefix} umstellen.");
}

var arguments = args.ToList();
var jsonOutput = false;
var tokenFromStdin = false;
string? configPath = null;
while (arguments.Count > 0)
{
    if (arguments[0] == "--json")
    {
        jsonOutput = true;
        arguments.RemoveAt(0);
        continue;
    }

    if (arguments[0] == "--token-stdin")
    {
        tokenFromStdin = true;
        arguments.RemoveAt(0);
        continue;
    }

    if (arguments[0] == "--config")
    {
        if (arguments.Count < 2)
        {
            await Console.Error.WriteLineAsync("--config verlangt einen Wert.");
            return GatewayCli.UsageError;
        }

        configPath = arguments[1];
        arguments.RemoveRange(0, 2);
        continue;
    }

    break;
}

// Vor dem Laden der Konfiguration: '--version' und '--help' duerfen weder eine Konfigurationsdatei
// noch eine gueltige Endpunkt-Einstellung voraussetzen.
if (GatewayCli.TryRunInfoCommand(arguments, jsonOutput, Console.Out) is { } infoExitCode)
{
    return infoExitCode;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var (configuration, token) = await CliConfiguration.LoadAsync(
        configPath,
        tokenFromStdin,
        Console.In,
        cancellation.Token);
    using var client = new HttpClient
    {
        BaseAddress = configuration.Endpoint,
        Timeout = TimeSpan.FromSeconds(100),
    };
    if (token is not null)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    return await new GatewayCli(client, Console.In, Console.Out, Console.Error, jsonOutput)
        .RunAsync(arguments, cancellation.Token);
}
catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return GatewayCli.UsageError;
}
