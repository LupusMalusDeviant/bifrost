namespace Bifrost.Soak.Tests.Harness;

internal sealed record GrowthVerdict(
    double SlopePerMinute,
    double First,
    double Last,
    double Peak,
    bool IsUnbounded,
    /// <summary>
    /// Ob überhaupt geurteilt wurde. <c>false</c> heißt „keine Aussage", nicht „kein Wachstum" —
    /// der Unterschied entscheidet darüber, ob ein Lauf als Nachweis taugt.
    /// </summary>
    bool HasVerdict,
    string Reason);

/// <summary>
/// Beantwortet die einzige Frage, für die ein Soak-Lauf da ist: Wächst etwas, das nicht wachsen darf?
/// <para>
/// <b>Warum Steigung <em>und</em> Verhältnis:</b> Eine reine Schwelle („nicht mehr als 300 MB")
/// nimmt jeden Lauf ab, der klein genug anfängt, und lehnt jeden ab, der viel Cache hält. Eine
/// reine Steigung schlägt bei jedem Rauschen an. Ein Leck zeigt sich als beides zugleich —
/// dauerhafte Steigung <em>und</em> spürbares Verhältnis zum Startwert.
/// </para>
/// <para>
/// Der Wert wird an einer Reihe ohne Aufwärmphase gebildet. Die ersten Minuten eines .NET-Prozesses
/// wachsen immer: JIT, Verbindungspools, Caches. Wer sie mitrechnet, misst den Start und nennt ihn
/// Leck.
/// </para>
/// </summary>
internal static class GrowthAnalysis
{
    /// <summary>Ab dieser Steigung ist Wachstum nicht mehr Rauschen (MB je Minute).</summary>
    public const double DefaultSlopeThresholdMbPerMinute = 2.0;

    /// <summary>…und erst zusammen mit diesem Verhältnis Ende/Anfang gilt es als ungebremst.</summary>
    public const double DefaultRatioThreshold = 1.5;

    /// <summary>
    /// Kürzere Fenster ergeben <b>keine Aussage</b>.
    /// <para>
    /// Der erste Lauf dieses Harness hat aus 25 Sekunden eine Steigung von 285 MB <em>pro Minute</em>
    /// hochgerechnet — das ist keine Messung, das ist eine Division. Dass daraus trotzdem „kein
    /// Wachstum" wurde, lag allein an der zweiten Bedingung, und zwar knapp. Ein Urteil, das an
    /// solchen Zufällen hängt, ist keins.
    /// </para>
    /// </summary>
    public const double MinimumWindowMinutes = 5;

    public static GrowthVerdict Analyse(
        IReadOnlyList<(double Minutes, double Value)> series,
        double slopeThreshold = DefaultSlopeThresholdMbPerMinute,
        double ratioThreshold = DefaultRatioThreshold,
        double minimumWindowMinutes = MinimumWindowMinutes)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (series.Count < 3)
        {
            return new GrowthVerdict(0, 0, 0, 0, false, false,
                $"Zu wenige Messpunkte ({series.Count}) für eine Aussage — der Lauf war zu kurz.");
        }

        var slope = Slope(series);
        var first = series[0].Value;
        var last = series[^1].Value;
        var peak = series.Max(s => s.Value);
        var ratio = first > 0 ? last / first : 1;
        var window = series[^1].Minutes - series[0].Minutes;

        if (window < minimumWindowMinutes)
        {
            return new GrowthVerdict(slope, first, last, peak, false, false,
                $"Fenster {window:0.0} min unter {minimumWindowMinutes:0.0} min — keine "
                + $"Wachstumsaussage (beobachtet: {first:0} → {last:0}).");
        }

        var unbounded = slope > slopeThreshold && ratio > ratioThreshold;
        var reason = unbounded
            ? $"Steigung {slope:0.00}/min über der Schwelle {slopeThreshold:0.00} UND Ende/Anfang "
              + $"{ratio:0.00} über {ratioThreshold:0.00}."
            : $"Steigung {slope:0.00}/min, Ende/Anfang {ratio:0.00} — kein ungebremstes Wachstum.";

        return new GrowthVerdict(slope, first, last, peak, unbounded, true, reason);
    }

    /// <summary>Kleinste Quadrate. Ein Median über zwei Hälften wäre blind für den Verlauf dazwischen.</summary>
    private static double Slope(IReadOnlyList<(double Minutes, double Value)> series)
    {
        var meanX = series.Average(s => s.Minutes);
        var meanY = series.Average(s => s.Value);
        var numerator = series.Sum(s => (s.Minutes - meanX) * (s.Value - meanY));
        var denominator = series.Sum(s => (s.Minutes - meanX) * (s.Minutes - meanX));
        return denominator == 0 ? 0 : numerator / denominator;
    }
}
