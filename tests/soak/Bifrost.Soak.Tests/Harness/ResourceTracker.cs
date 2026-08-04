using System.Diagnostics;
using System.Text.Json;
using Bifrost.TestServers.Common;

namespace Bifrost.Soak.Tests.Harness;

internal sealed record SoakSample(
    double ElapsedSeconds,
    DateTimeOffset At,
    double RssMb,
    double PrivateMb,
    int Threads,
    int Handles,
    int ChildProcesses,
    long CallsOk,
    long CallsFailed);

/// <summary>
/// Schreibt den Verlauf mit — Zeile für Zeile, sofort auf die Platte.
/// <para>
/// <b>Das ist die eigentliche Anforderung an einen Soak-Harness:</b> Ein Lauf über Stunden endet oft
/// nicht ordentlich. Wer die Messreihe erst am Ende schreibt, hat nach einem CI-Abbruch, einem
/// Timeout oder einem Absturz genau nichts — also ausgerechnet in den Fällen, die man untersuchen
/// wollte. Deshalb JSONL mit Flush je Zeile und nicht ein JSON-Dokument am Schluss.
/// </para>
/// </summary>
internal sealed class ResourceTracker : IAsyncDisposable
{
    private static readonly string[] TestServerExecutables =
    [
        "Bifrost.TestServers.EchoServer",
        "Bifrost.TestServers.SlowServer",
        "Bifrost.TestServers.CrashServer",
    ];

    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly List<SoakSample> _samples = [];
    private readonly Func<(long Ok, long Failed)> _counters;
    private readonly HashSet<int> _baselineChildren;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ResourceTracker(
        Process process, string metricsPath, TimeSpan interval, Func<(long Ok, long Failed)> counters)
    {
        _process = process;
        _counters = counters;
        _baselineChildren = [.. FindChildren()];
        Directory.CreateDirectory(Path.GetDirectoryName(metricsPath)!);
        _writer = new StreamWriter(metricsPath, append: false) { AutoFlush = true };
        _loop = Task.Run(() => SampleLoopAsync(interval, _stop.Token));
    }

    public IReadOnlyList<SoakSample> Samples
    {
        get
        {
            lock (_samples)
            {
                return [.. _samples];
            }
        }
    }

    /// <summary>Prozesse der Testserver, die es beim Start noch nicht gab.</summary>
    public IReadOnlyList<string> OrphansAfterShutdown() =>
        TestServerExecutables
            .SelectMany(name => UpstreamProcessLookup.FindByExecutableName(name)
                .Where(id => !_baselineChildren.Contains(id))
                .Select(id => $"{name} (PID {id})"))
            .ToList();

    private async Task SampleLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sample();
            }
            catch (InvalidOperationException)
            {
                // Der Gateway-Prozess ist weg — der Lauf endet, die bisherigen Zeilen bleiben.
                return;
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public SoakSample Sample()
    {
        _process.Refresh();
        var (ok, failed) = _counters();
        var sample = new SoakSample(
            ElapsedSeconds: Math.Round(_clock.Elapsed.TotalSeconds, 1),
            At: DateTimeOffset.UtcNow,
            RssMb: Math.Round(_process.WorkingSet64 / 1024d / 1024d, 1),
            PrivateMb: Math.Round(_process.PrivateMemorySize64 / 1024d / 1024d, 1),
            Threads: _process.Threads.Count,
            Handles: SafeHandles(_process),
            ChildProcesses: FindChildren().Count,
            CallsOk: ok,
            CallsFailed: failed);

        lock (_samples)
        {
            _samples.Add(sample);
        }

        lock (_writer)
        {
            _writer.WriteLine(JsonSerializer.Serialize(sample, SoakJson.Options));
        }

        return sample;
    }

    private static int SafeHandles(Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch (PlatformNotSupportedException)
        {
            return -1;
        }
    }

    private static List<int> FindChildren() =>
        [.. TestServerExecutables.SelectMany(UpstreamProcessLookup.FindByExecutableName)];

    private bool _disposed;

    /// <summary>
    /// Mehrfach aufrufbar. Der Lauf beendet den Mitschrieb ausdrücklich, bevor er auswertet — und
    /// danach räumt <c>await using</c> noch einmal auf.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        await _writer.DisposeAsync();
    }
}
