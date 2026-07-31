using Bifrost.Abstractions;
using Bifrost.Upstream;

// Wirt fuer den Nachweis aus WP0.4.
//
// Warum ein eigenes Programm und kein Test: Der Nachweis lautet "ein HART abgebrochener Wirt
// hinterlaesst keinen Kindprozess". Ein Test kann seinen eigenen Testhost nicht abschiessen — also
// braucht es einen zweiten Prozess, den der Test toeten darf.
//
// Dieser Wirt geht bewusst ueber den PRODUKTPFAD (StdioUpstreamConnector), nicht ueber ein eigenes
// Process.Start: Geprueft werden soll die Hygiene, die das Produkt herstellt, nicht eine im Test
// nachgebaute.
//
// Aufruf:  Bifrost.TestServers.OrphanHost <pfad-zum-echo-server>
// Ausgabe: eine Zeile "READY <pid-des-kindes>", danach laeuft der Prozess bis er getoetet wird.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Bifrost.TestServers.OrphanHost <pfad-zum-upstream-executable>");
    return 2;
}

var connector = new StdioUpstreamConnector();
var config = new UpstreamServerConfig(
    "orphan-probe",
    "Wirt fuer den Waisen-Nachweis",
    UpstreamTransportKind.Stdio,
    Enabled: true,
    Stdio: new StdioTransportOptions(args[0], []));

// Vor dem Start merken, welche Prozesse dieses Namens es schon gibt — die Zeile unten soll den
// NEUEN nennen und nicht irgendeinen.
var name = Path.GetFileNameWithoutExtension(args[0]);
var before = System.Diagnostics.Process.GetProcessesByName(name).Select(p => p.Id).ToHashSet();

await using var connection = await connector.ConnectAsync(new ServerId(Guid.NewGuid()), config, CancellationToken.None);

// Discovery erzwingt einen echten Roundtrip: Danach steht fest, dass das Kind wirklich laeuft und
// antwortet — ein gestarteter Prozess allein waere ein schwaecherer Nachweis.
await connection.DiscoverAsync(CancellationToken.None);

var child = System.Diagnostics.Process.GetProcessesByName(name)
    .FirstOrDefault(p => !before.Contains(p.Id));

Console.WriteLine($"READY {child?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}");
Console.Out.Flush();

// Warten, bis jemand diesen Prozess beendet. Kein Timeout: Der Test raeumt auf, und ein Wirt, der
// sich selbst beendet, wuerde den Nachweis wertlos machen.
await Task.Delay(Timeout.Infinite);
return 0;
