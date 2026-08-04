using Bifrost.ConnectorKit;

// Das Kit läuft ohne Gateway: Bauen und Signieren passieren auf dem Rechner des Entwicklers,
// und ein privater Schlüssel hat auf keiner Netzstrecke etwas verloren.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var cli = new ConnectorKitCli(Console.Out, Console.Error);
return await cli.RunAsync(args, cancellation.Token);
