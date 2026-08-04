using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Performance.Tests.Harness;

internal sealed record PerfRunResult(
    int SchemaVersion,
    string RunId,
    DateTimeOffset StartedAt,
    PerfEnvironment Environment,
    PerfSettings Settings,
    bool Complete,
    IReadOnlyList<PerfScenarioResult> Scenarios,
    IReadOnlyList<string> Notes);

internal sealed record PerfEnvironment(
    string Os, int ProcessorCount, string Runtime, string Host);

internal sealed record PerfScenarioResult(
    string Name,
    string Mode,
    string Transport,
    int UpstreamLatencyMs,
    IReadOnlyList<LoadMeasurement> Runs,
    LoadMeasurement Median,
    PerfProcessMetrics Process);

internal sealed record PerfProcessMetrics(
    double CpuSeconds,
    double CpuPercentOfOneCore,
    double PeakWorkingSetMb,
    double EndWorkingSetMb,
    int Threads,
    int Handles);

/// <summary>
/// Die Stellschrauben eines Laufs — und zugleich das, was am Ergebnis mitgeschrieben wird.
/// <para>
/// <b>Warum die Werte im Artefakt stehen:</b> Ein Lauf über fünf Sekunden und einer über sechzig
/// sehen im JSON sonst gleich aus. <see cref="IsComplete"/> unterscheidet beides, und nur ein
/// vollständiger Lauf gehört in ein Abnahmedokument (M5-Vertrag §1.3).
/// </para>
/// </summary>
internal sealed record PerfSettings(
    int DurationSeconds,
    int WarmupSeconds,
    int Runs,
    int Concurrency,
    int UpstreamLatencyMs)
{
    public const int MinimumDurationSeconds = 30;
    public const int MinimumRuns = 3;

    public static PerfSettings FromEnvironment() => new(
        DurationSeconds: Read("BIFROST_PERF_DURATION_SECONDS", MinimumDurationSeconds),
        WarmupSeconds: Read("BIFROST_PERF_WARMUP_SECONDS", 5),
        Runs: Read("BIFROST_PERF_RUNS", MinimumRuns),
        Concurrency: Read("BIFROST_PERF_CONCURRENCY", 32),
        UpstreamLatencyMs: Read("BIFROST_PERF_UPSTREAM_LATENCY_MS", 20));

    [JsonIgnore]
    public bool IsComplete => DurationSeconds >= MinimumDurationSeconds && Runs >= MinimumRuns;

    public LoadOptions ToLoadOptions(string toolName) => new(
        toolName, UpstreamLatencyMs, Concurrency, WarmupSeconds, DurationSeconds);

    private static int Read(string name, int fallback) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : fallback;
}

internal static class PerfArtifacts
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Median je Kennzahl über alle Läufe — nicht der beste und nicht der letzte.</summary>
    public static LoadMeasurement Median(IReadOnlyList<LoadMeasurement> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("Ohne Lauf gibt es keinen Median.", nameof(runs));
        }

        static double Med(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
        }

        return new LoadMeasurement(
            Calls: (int)Med(runs.Select(r => (double)r.Calls)),
            Errors: runs.Sum(r => r.Errors),
            P50: Med(runs.Select(r => r.P50)),
            P95: Med(runs.Select(r => r.P95)),
            P99: Med(runs.Select(r => r.P99)),
            Max: runs.Max(r => r.Max),
            ThroughputPerSecond: Med(runs.Select(r => r.ThroughputPerSecond)),
            WallSeconds: Med(runs.Select(r => r.WallSeconds)),
            UpstreamP50: Med(runs.Select(r => r.UpstreamP50)),
            UpstreamP95: Med(runs.Select(r => r.UpstreamP95)),
            GatewayOverheadP50: Med(runs.Select(r => r.GatewayOverheadP50)),
            GatewayOverheadP95: Med(runs.Select(r => r.GatewayOverheadP95)));
    }

    public static PerfProcessMetrics Metrics(ProcessSnapshot before, ProcessSnapshot after, double wallSeconds)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var cpu = (after.Cpu - before.Cpu).TotalSeconds;
        return new PerfProcessMetrics(
            CpuSeconds: Math.Round(cpu, 2),
            CpuPercentOfOneCore: wallSeconds > 0 ? Math.Round(cpu / wallSeconds * 100, 1) : 0,
            PeakWorkingSetMb: Math.Round(after.PeakWorkingSetBytes / 1024d / 1024d, 1),
            EndWorkingSetMb: Math.Round(after.WorkingSetBytes / 1024d / 1024d, 1),
            Threads: after.Threads,
            Handles: after.Handles);
    }

    public static PerfEnvironment Environment() => new(
        Os: System.Environment.OSVersion.VersionString,
        ProcessorCount: System.Environment.ProcessorCount,
        Runtime: System.Environment.Version.ToString(),
        Host: System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "ci" : "local");

    /// <summary>Schreibt das Ergebnis nach <c>artifacts/perf/&lt;run-id&gt;.json</c> (M5-Vertrag §4).</summary>
    public static string Write(PerfRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var directory = Path.Combine(TestServerPaths.RepoRoot, "artifacts", "perf");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{result.RunId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, Options));
        return path;
    }

    public static string NewRunId(DateTimeOffset startedAt) =>
        startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N")[..6];
}
