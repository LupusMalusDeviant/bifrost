using System.Globalization;

namespace Bifrost.Soak.Tests.Harness;

internal enum SoakMode
{
    /// <summary>Kurzlauf für den PR — Minuten, nicht Stunden.</summary>
    Smoke,

    /// <summary>Nachtlauf über Stunden.</summary>
    Long,
}

/// <summary>
/// Die Stellschrauben eines Soak-Laufs.
/// <para>
/// <b>Der 72-Stunden-Lauf steht hier nicht als Vorgabe</b>, sondern entsteht durch
/// <c>BIFROST_SOAK_DURATION_SECONDS</c>. Er ist ein Release-Candidate-Ereignis (M5-Vertrag §10) und
/// gilt bis dahin als ausstehend — eine Vorgabe, die niemand fährt, wäre eine Zusicherung, die
/// niemand einlöst.
/// </para>
/// </summary>
internal sealed record SoakSettings(
    SoakMode Mode,
    int DurationSeconds,
    int WarmupSeconds,
    int SampleIntervalSeconds,
    int ChaosIntervalSeconds,
    int Concurrency,
    int ThinkTimeMs,
    int Seed)
{
    public const string RunVariable = "BIFROST_RUN_SOAK";

    /// <summary>
    /// Der Lauf ist ein Nachweis, wenn er mindestens eine Stunde durchgehalten hat. Alles darunter
    /// ist ein Funktionsnachweis des Harness, kein Betriebsnachweis — und schreibt sich selbst so
    /// ins Ergebnis.
    /// </summary>
    public const int EvidenceDurationSeconds = 3600;

    public static SoakMode? ModeFromEnvironment() =>
        Environment.GetEnvironmentVariable(RunVariable)?.ToLowerInvariant() switch
        {
            "smoke" => SoakMode.Smoke,
            "long" => SoakMode.Long,
            _ => null,
        };

    /// <summary>
    /// <b>Warum ein Soak-Lauf Wartezeit braucht.</b> Ohne sie rufen die Arbeiter so schnell auf, wie
    /// der Gateway antwortet — beim ersten Lauf waren das ~3500 Aufrufe je Sekunde, mehr als jede
    /// realistische Installation je sieht. Ein Soak soll Dauerbetrieb nachstellen, nicht die
    /// Sättigungsgrenze; für die Grenze gibt es den Lastharness aus WP5.3. Ohne die Trennung misst
    /// man Verhalten unter Überlast und nennt es Dauerbetrieb.
    /// </summary>
    public const int DefaultThinkTimeMs = 25;

    public static SoakSettings For(SoakMode mode) => mode switch
    {
        SoakMode.Smoke => new SoakSettings(
            mode,
            DurationSeconds: Read("BIFROST_SOAK_DURATION_SECONDS", 180),
            WarmupSeconds: Read("BIFROST_SOAK_WARMUP_SECONDS", 20),
            SampleIntervalSeconds: Read("BIFROST_SOAK_SAMPLE_SECONDS", 5),
            ChaosIntervalSeconds: Read("BIFROST_SOAK_CHAOS_SECONDS", 25),
            Concurrency: Read("BIFROST_SOAK_CONCURRENCY", 8),
            ThinkTimeMs: Read("BIFROST_SOAK_THINK_MS", DefaultThinkTimeMs),
            Seed: Read("BIFROST_SOAK_SEED", 20260802)),
        SoakMode.Long => new SoakSettings(
            mode,
            DurationSeconds: Read("BIFROST_SOAK_DURATION_SECONDS", 4 * 3600),
            WarmupSeconds: Read("BIFROST_SOAK_WARMUP_SECONDS", 120),
            SampleIntervalSeconds: Read("BIFROST_SOAK_SAMPLE_SECONDS", 15),
            ChaosIntervalSeconds: Read("BIFROST_SOAK_CHAOS_SECONDS", 120),
            Concurrency: Read("BIFROST_SOAK_CONCURRENCY", 8),
            ThinkTimeMs: Read("BIFROST_SOAK_THINK_MS", DefaultThinkTimeMs),
            Seed: Read("BIFROST_SOAK_SEED", 20260802)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public bool IsEvidence => DurationSeconds >= EvidenceDurationSeconds;

    private static int Read(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : fallback;
}
