using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Abstractions;
using Bifrost.Performance.Tests.Harness;

namespace Bifrost.Soak.Tests.Harness;

internal static class SoakJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonSerializerOptions Indented { get; } = new(Options) { WriteIndented = true };
}

/// <summary>
/// <paramref name="WriteTool"/> ist der Hebel für die Gegenprobe: Zeigt er auf <c>crash__crash</c>,
/// zerlegt jeder Schreibaufruf seinen eigenen Upstream — ein bekannter, eingebauter Fehler, den der
/// Harness sehen muss (M5-Vertrag §1.1).
/// </summary>
internal sealed record SoakScenario(string Name, bool Chaos, string WriteTool = "echo__echo");

internal sealed record SoakSummary(
    int SchemaVersion,
    string RunId,
    string Scenario,
    string Mode,
    bool IsEvidence,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    SoakSettings Settings,
    long CallsOk,
    long CallsFailed,
    IReadOnlyDictionary<string, long> FailuresByReason,
    IReadOnlyList<ChaosEvent> ChaosEvents,
    GrowthVerdict RssGrowth,
    GrowthVerdict HandleGrowth,
    GrowthVerdict ThreadGrowth,
    int MaxChildProcesses,
    int ChildProcessesAtEnd,
    bool RecoveredAfterChaos,
    IReadOnlyList<string> OrphanProcesses,
    IReadOnlyList<string> Notes);

internal sealed record SoakOutcome(SoakSummary Summary, string Directory);

/// <summary>
/// Fährt einen Soak-Lauf: Dauerlast plus Störungen, mitgeschriebene Messreihe, am Ende ein Urteil.
/// <para>
/// <b>Was der Lauf beweisen soll</b>, ist nicht „es gab keine Fehler" — unter Chaos <em>muss</em> es
/// Fehler geben, sonst hat die Störung nicht gewirkt. Er beweist: kein ungebremstes Wachstum von
/// Speicher, Handles, Threads und Kindprozessen; und der Gateway bedient nach der letzten Störung
/// wieder.
/// </para>
/// </summary>
internal static class SoakRunner
{
    public static async Task<SoakOutcome> RunAsync(
        SoakSettings settings, SoakScenario scenario, Action<string> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(log);

        var startedAt = DateTimeOffset.UtcNow;
        var runId = startedAt.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + scenario.Name;
        var directory = Path.Combine(TestServerPaths.RepoRoot, "artifacts", "soak", runId);
        Directory.CreateDirectory(directory);

        long ok = 0, failed = 0;
        var failures = new Dictionary<string, long>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();

        await using var gateway = await GatewayProcess.StartAsync(new GatewayProcessOptions(), ct);
        await gateway.AddStdioUpstreamAsync("echo", "EchoServer", ct);
        var crashId = await gateway.AddStdioUpstreamAsync("crash", "CrashServer", ct);

        var assetName = $"soak-{Guid.NewGuid():N}";
        await CreateAssetAsync(gateway, assetName, ct);

        await using var tracker = new ResourceTracker(
            gateway.Process,
            Path.Combine(directory, "metrics.jsonl"),
            TimeSpan.FromSeconds(settings.SampleIntervalSeconds),
            () => (Interlocked.Read(ref ok), Interlocked.Read(ref failed)));

        using var workerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var chaos = new ChaosEngine(gateway, crashId, clock);
        var chaosTask = scenario.Chaos
            ? chaos.RunAsync(TimeSpan.FromSeconds(settings.ChaosIntervalSeconds), workerStop.Token)
            : Task.CompletedTask;

        var workers = Enumerable.Range(0, settings.Concurrency).Select(index => Task.Run(async () =>
        {
            var http = gateway.CreateHttpClient();
            var mix = new WorkloadMix(
                settings.Seed + index, AssetDelivery.PromptName(assetName), AssetDelivery.ResourceUri(assetName));
            var client = await WorkloadMix.ConnectAsync(gateway, http, index, workerStop.Token);

            try
            {
                while (!workerStop.IsCancellationRequested)
                {
                    var kind = mix.Next();
                    string? failure;
                    try
                    {
                        failure = await mix.ExecuteAsync(kind, client, http, scenario.WriteTool, workerStop.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failure = $"{kind}: {ex.GetType().Name}";
                    }

                    if (failure is null)
                    {
                        Interlocked.Increment(ref ok);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        lock (failures)
                        {
                            failures[failure] = failures.GetValueOrDefault(failure) + 1;
                        }
                    }

                    if (settings.ThinkTimeMs > 0)
                    {
                        await Task.Delay(settings.ThinkTimeMs, workerStop.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ende des Laufs.
            }
            finally
            {
                await client.DisposeAsync();
                http.Dispose();
            }
        }, workerStop.Token)).ToList();

        log($"Soak '{scenario.Name}' läuft: {settings.DurationSeconds} s, {settings.Concurrency} Clients, "
            + $"Chaos {(scenario.Chaos ? $"alle {settings.ChaosIntervalSeconds} s" : "aus")}.");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.DurationSeconds), ct);
        }
        finally
        {
            await workerStop.CancelAsync();
        }

        // Abbrechen heisst hier "fertig", nicht "kaputt": Beim Anhalten stehen Aufrufe und
        // Stoerungen mitten im Flug, und deren Abbruch darf den Lauf nicht als Fehlschlag
        // erscheinen lassen — sonst faellt ausgerechnet der geordnete Schluss um.
        await WhenAllIgnoringCancellation([.. workers, chaosTask]);
        tracker.Sample();

        // Erholung: Nach der letzten Störung muss der Gateway wieder bedienen. Ohne diese Probe
        // wäre ein Lauf auch dann grün, in dem am Ende gar nichts mehr ging — Fehler zählt er ja
        // nur, und unter Chaos sind Fehler erwartet.
        var recovered = await VerifyRecoveryAsync(gateway, ct);
        var samples = tracker.Samples;
        await tracker.DisposeAsync();
        await gateway.DisposeAsync();

        // Erst nach dem Herunterfahren: Vorher laufen die Upstreams berechtigterweise.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var orphans = tracker.OrphansAfterShutdown();

        var measured = samples.Where(s => s.ElapsedSeconds >= settings.WarmupSeconds).ToList();
        var summary = new SoakSummary(
            SchemaVersion: 1,
            RunId: runId,
            Scenario: scenario.Name,
            Mode: settings.Mode.ToString().ToLowerInvariant(),
            IsEvidence: settings.IsEvidence,
            StartedAt: startedAt,
            DurationSeconds: Math.Round(clock.Elapsed.TotalSeconds, 1),
            Settings: settings,
            CallsOk: Interlocked.Read(ref ok),
            CallsFailed: Interlocked.Read(ref failed),
            FailuresByReason: failures,
            ChaosEvents: chaos.Events,
            RssGrowth: Analyse(measured, s => s.RssMb),
            HandleGrowth: Analyse(measured, s => s.Handles),
            ThreadGrowth: Analyse(measured, s => s.Threads),
            MaxChildProcesses: samples.Count == 0 ? 0 : samples.Max(s => s.ChildProcesses),
            ChildProcessesAtEnd: samples.Count == 0 ? 0 : samples[^1].ChildProcesses,
            RecoveredAfterChaos: recovered,
            OrphanProcesses: orphans,
            Notes:
            [
                $"Messreihe: {samples.Count} Punkte, davon {measured.Count} nach der Aufwärmphase "
                + $"({settings.WarmupSeconds} s) — nur diese gehen in die Wachstumsaussage ein.",
                "Nicht enthalten: Freigabe-Last (Approval), PostgreSQL-Unterbrechung, knapper "
                + "Plattenplatz und Queuegrößen. Die ersten drei brauchen zusätzliche Umgebung, "
                + "Queuegrößen sind von außen nicht sichtbar.",
                settings.IsEvidence
                    ? "Dauer reicht für eine Betriebsaussage."
                    : $"NUR FUNKTIONSNACHWEIS: {settings.DurationSeconds} s. Ein Betriebsnachweis "
                      + $"beginnt bei {SoakSettings.EvidenceDurationSeconds} s; der 72-h-Lauf ist ein "
                      + "Release-Candidate-Ereignis.",
                Analyse(measured, s => s.RssMb).HasVerdict
                    ? "Das Messfenster reicht für eine Wachstumsaussage."
                    : "KEINE WACHSTUMSAUSSAGE: Das Messfenster ist zu kurz. Der Lauf belegt Ablauf, "
                      + "Erholung und Prozessaufräumen — nicht die Speicherkurve.",
            ]);

        File.WriteAllText(
            Path.Combine(directory, "summary.json"),
            JsonSerializer.Serialize(summary, SoakJson.Indented));

        return new SoakOutcome(summary, directory);
    }

    private static async Task WhenAllIgnoringCancellation(IReadOnlyList<Task> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static GrowthVerdict Analyse<T>(IReadOnlyList<SoakSample> samples, Func<SoakSample, T> selector)
        where T : struct, IConvertible =>
        GrowthAnalysis.Analyse(
            [.. samples.Select(s => (s.ElapsedSeconds / 60d, Convert.ToDouble(selector(s), System.Globalization.CultureInfo.InvariantCulture)))]);

    private static async Task<bool> VerifyRecoveryAsync(GatewayProcess gateway, CancellationToken ct)
    {
        try
        {
            await gateway.WaitUntilHealthyAsync("echo", ct);
            var http = gateway.CreateHttpClient();
            try
            {
                var client = await WorkloadMix.ConnectAsync(gateway, http, index: 999, ct);
                await using (client)
                {
                    for (var i = 0; i < 10; i++)
                    {
                        var result = await client.CallToolAsync(
                            "echo__echo", new Dictionary<string, object?> { ["message"] = "recovery" },
                            cancellationToken: ct);
                        if (result.IsError == true)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            finally
            {
                http.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task CreateAssetAsync(GatewayProcess gateway, string name, CancellationToken ct)
    {
        var response = await gateway.Client.PostAsJsonAsync(
            "api/v1/skills",
            new { name, description = "Soak-Fixture", content = "Inhalt fuer den Soak-Lauf." },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Asset '{name}' konnte nicht angelegt werden ({response.StatusCode}): "
                + await response.Content.ReadAsStringAsync(ct));
        }
    }
}
