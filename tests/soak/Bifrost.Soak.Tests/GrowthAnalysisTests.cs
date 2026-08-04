using AwesomeAssertions;
using Bifrost.Soak.Tests.Harness;
using Xunit;

namespace Bifrost.Soak.Tests;

/// <summary>
/// Die Gegenprobe des Urteils (M5-Vertrag §1.1): Der Detektor bekommt Messreihen mit bekanntem
/// Verhalten vorgelegt — ein eingebautes Leck muss auffallen, ein rauschender, aber flacher Verlauf
/// nicht.
/// <para>
/// Ohne diese Fälle wäre „72 Stunden ohne ungebremstes Wachstum" die Aussage eines Prüfers, den
/// niemand geprüft hat. Und die Richtung des Irrtums wäre die unangenehme: Ein Detektor, der nie
/// anschlägt, meldet jeden Lauf als sauber.
/// </para>
/// </summary>
public sealed class GrowthAnalysisTests
{
    [Fact]
    public void A_steadily_leaking_series_is_flagged()
    {
        // 60 Minuten, 5 MB je Minute — das klassische Bild eines Lecks.
        var leaking = Series(minutes: 60, start: 200, perMinute: 5, noise: 0);

        var verdict = GrowthAnalysis.Analyse(leaking);

        verdict.IsUnbounded.Should().BeTrue(verdict.Reason);
        verdict.SlopePerMinute.Should().BeApproximately(5, 0.1);
    }

    [Fact]
    public void A_flat_but_noisy_series_is_not_flagged()
    {
        var noisy = Series(minutes: 60, start: 300, perMinute: 0, noise: 25);

        GrowthAnalysis.Analyse(noisy).IsUnbounded.Should().BeFalse();
    }

    /// <summary>
    /// Der Fall, der eine reine Steigungsschwelle blamiert: Ein Prozess, der sich von 400 auf
    /// 430 MB einpendelt, steigt messbar — aber nicht ungebremst.
    /// </summary>
    [Fact]
    public void A_process_that_settles_at_a_higher_level_is_not_a_leak()
    {
        var settling = Enumerable.Range(0, 60)
            .Select(i => ((double)i, 400 + (30 * (1 - Math.Exp(-i / 5.0)))))
            .ToList();

        GrowthAnalysis.Analyse(settling).IsUnbounded.Should().BeFalse();
    }

    /// <summary>
    /// Eine sichtbare Steigung ohne nennenswertes Verhältnis ist ebenfalls kein Leck — sonst würde
    /// jeder Prozess mit großem Grundverbrauch dauernd Alarm auslösen.
    /// </summary>
    [Fact]
    public void Growth_needs_both_slope_and_ratio()
    {
        var slowlyGrowing = Series(minutes: 30, start: 2000, perMinute: 3, noise: 0);

        var verdict = GrowthAnalysis.Analyse(slowlyGrowing);

        verdict.SlopePerMinute.Should().BeGreaterThan(GrowthAnalysis.DefaultSlopeThresholdMbPerMinute);
        verdict.IsUnbounded.Should().BeFalse("2000 → 2090 MB ist ein Zehntel mehr, kein Weglaufen");
    }

    [Fact]
    public void Too_few_points_produce_no_verdict()
    {
        var verdict = GrowthAnalysis.Analyse([(0, 100), (1, 900)]);

        verdict.IsUnbounded.Should().BeFalse();
        verdict.HasVerdict.Should().BeFalse();
        verdict.Reason.Should().Contain("Zu wenige Messpunkte");
    }

    /// <summary>
    /// Der Fall, den der erste Lauf dieses Harness produziert hat: ein Fenster von Sekunden, aus dem
    /// eine Steigung „pro Minute" hochgerechnet wird. Aus so einem Lauf darf weder „Leck" noch
    /// „sauber" folgen — er sagt nichts.
    /// </summary>
    [Fact]
    public void A_window_of_seconds_yields_no_statement_in_either_direction()
    {
        // Eine halbe Minute, steil steigend — genau das Bild einer Aufwärmphase.
        var tooShort = Enumerable.Range(0, 6).Select(i => (i * 0.1, 200 + (i * 30d))).ToList();

        var verdict = GrowthAnalysis.Analyse(tooShort);

        verdict.HasVerdict.Should().BeFalse();
        verdict.IsUnbounded.Should().BeFalse("keine Aussage ist kein Freispruch, aber auch kein Befund");
        verdict.Reason.Should().Contain("keine");
    }

    private static List<(double Minutes, double Value)> Series(
        int minutes, double start, double perMinute, double noise)
    {
        // Fester Zufall: Ein Test, der mal rot und mal grün ist, prüft nichts.
        var random = new Random(4711);
        return [.. Enumerable.Range(0, minutes).Select(i =>
            ((double)i, start + (perMinute * i) + (noise * (random.NextDouble() - 0.5))))];
    }
}
