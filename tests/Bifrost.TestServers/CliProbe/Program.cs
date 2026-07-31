using System.Diagnostics;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    return 2;
}

switch (args[0])
{
    case "args":
        Console.Write(JsonSerializer.Serialize(args.Skip(1)));
        return 0;
    case "env":
        Console.Write(Environment.GetEnvironmentVariable(args[1]) ?? "<missing>");
        return 0;
    case "dual":
        Console.Out.Write(args[1]);
        Console.Error.Write(args[2]);
        return int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    case "output":
        await WriteBytesAsync(Console.OpenStandardOutput(), int.Parse(args[1]));
        await WriteBytesAsync(Console.OpenStandardError(), int.Parse(args[2]));
        return 0;
    case "unicode":
        Console.OutputEncoding = Encoding.UTF8;
        Console.Write("Grüße 🌍 — 日本語");
        return 0;
    case "sleep":
        await Task.Delay(TimeSpan.FromMilliseconds(int.Parse(args[1])));
        Console.Write("done");
        return 0;
    case "spawn-child":
        var child = StartChild(args[1]);
        Console.Write(child.Id);
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromMinutes(5));
        return 0;
    default:
        return 3;
}

static async Task WriteBytesAsync(Stream stream, int count)
{
    var block = Enumerable.Repeat((byte)'x', 8192).ToArray();
    while (count > 0)
    {
        var length = Math.Min(count, block.Length);
        await stream.WriteAsync(block.AsMemory(0, length));
        await stream.FlushAsync();
        count -= length;
    }
}

static Process StartChild(string milliseconds)
{
    var host = Environment.ProcessPath
        ?? throw new InvalidOperationException("Kein ProcessPath.");
    var start = new ProcessStartInfo(host)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(typeof(Bifrost.TestServers.CliProbeMarker).Assembly.Location);
    }
    start.ArgumentList.Add("sleep");
    start.ArgumentList.Add(milliseconds);
    return Process.Start(start)
        ?? throw new InvalidOperationException("Child konnte nicht gestartet werden.");
}
