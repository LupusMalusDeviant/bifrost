using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bifrost.Abstractions;

namespace Bifrost.Core.Guardrails;

/// <summary>
/// Muster-basierte Secret-Erkennung (ADR-0011). Läuft bei jedem Call auf dem Hot Path, deshalb
/// in dieser Reihenfolge:
///
/// <list type="number">
/// <item><b>Multiscan</b> über alle Keywords (<see cref="SearchValues"/>, Ordinal) — kostet
/// Bruchteile einer Mikrosekunde und überspringt bei sauberer Nutzlast alles Weitere.</item>
/// <item><b>Keyword je Regel</b> (<c>IndexOf</c>, Ordinal) — halbiert die Kosten gegenüber
/// „alle Regex immer".</item>
/// <item><b>Regex</b> mit <c>NonBacktracking</c> + Timeout, nur für Regeln mit Keyword-Treffer.</item>
/// </list>
///
/// Zwei Fallstricke, die gemessen wurden und hier bewusst vermieden sind:
/// <c>OrdinalIgnoreCase</c> im Vorfilter kostet Faktor 10 (Groß-/Kleinschreibung gehört als
/// <c>(?i)</c> in den Regex), und die Regex-Konstruktion kostet ~100 ms für 50 Regeln — sie
/// gehört ausschließlich in den Reload, niemals in einen Request.
/// </summary>
public sealed class SecretGuard : IContentGuard
{
    private readonly GuardOptions _options;
    private volatile Compiled _compiled;

    public SecretGuard(IEnumerable<GuardRule> rules, GuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _options = options ?? new GuardOptions();
        _compiled = Build([.. rules], _options);
    }

    /// <summary>Baut den Regelsatz neu und tauscht ihn atomar aus (hot-swappable).</summary>
    public void Reload(IEnumerable<GuardRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _compiled = Build([.. rules], _options);
    }

    /// <summary>
    /// Prüft ein Muster so, wie es später ausgeführt wird — für die Validierung beim Speichern
    /// in der UI. Wirft mit sprechender Meldung, statt den Fehler in den Hot Path zu verschieben.
    /// </summary>
    public static void ValidatePattern(string pattern, TimeSpan matchTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (pattern.Length > MaxPatternLength)
        {
            throw new ArgumentException(
                $"Muster ist länger als {MaxPatternLength} Zeichen.", nameof(pattern));
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, matchTimeout);
        }
        catch (NotSupportedException ex)
        {
            // Die häufigste Ursache: Lookaround oder Rückwärtsreferenzen. Die kann die
            // NonBacktracking-Engine nicht — das muss der Editor sofort sagen.
            throw new ArgumentException(
                "Muster nutzt Konstrukte, die hier nicht unterstützt werden "
                + "(Lookahead/Lookbehind, Rückwärtsreferenzen, atomare Gruppen). "
                + $"Details: {ex.Message}", nameof(pattern), ex);
        }
    }

    public const int MaxPatternLength = 1000;

    public GuardVerdict Inspect(string rawJson, GuardDirection direction)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(rawJson))
        {
            return GuardVerdict.Clean;
        }

        var compiled = _compiled;
        var applicable = direction == GuardDirection.Outbound ? compiled.Outbound : compiled.Inbound;
        if (applicable.Length == 0)
        {
            return GuardVerdict.Clean;
        }

        // Multiscan: Findet keines der Keywords irgendetwas, kann keine Regel mit Vorfilter
        // greifen — dann bleiben nur die Regeln ohne Keyword zu prüfen.
        var anyKeyword = compiled.Keywords is { } keywords && rawJson.AsSpan().ContainsAny(keywords);

        List<GuardFinding>? findings = null;
        foreach (var entry in applicable)
        {
            if (entry.Keyword is { } keyword)
            {
                if (!anyKeyword || rawJson.IndexOf(keyword, StringComparison.Ordinal) < 0)
                {
                    continue;
                }
            }

            Match match;
            try
            {
                match = entry.Regex.Match(rawJson);
            }
            catch (RegexMatchTimeoutException)
            {
                // Zeitüberschreitung ist ein Befund, kein Durchwinken: Eine Nutzlast, die die
                // Prüfung sprengt, gilt als ungeprüft — und ungeprüft heißt blockiert (E4).
                (findings ??= []).Add(new GuardFinding(
                    entry.Rule.Id, $"{entry.Rule.Description} (Prüfung zeitüberschritten)",
                    direction, GuardMode.Block, Fingerprint: "timeout", Offset: 0, Length: 0));
                continue;
            }

            if (match.Success)
            {
                (findings ??= []).Add(new GuardFinding(
                    entry.Rule.Id,
                    entry.Rule.Description,
                    direction,
                    entry.Rule.Mode,
                    Fingerprint(match.Value),
                    match.Index,
                    match.Length));
            }
        }

        return findings is null ? GuardVerdict.Clean : new GuardVerdict(findings);
    }

    /// <summary>
    /// Kurzer Hash statt Klartext (ADR-0011): Gleiche Funde lassen sich zusammenführen, ohne
    /// dass die Secret-Erkennung Secrets in Logs und Traces kopiert.
    /// </summary>
    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static Compiled Build(ImmutableArray<GuardRule> rules, GuardOptions options)
    {
        var entries = new List<Entry>(rules.Length);
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            Regex regex;
            try
            {
                regex = new Regex(
                    rule.Pattern,
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    options.MatchTimeout);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Eine kaputte Regel darf den gesamten Regelsatz nicht mitreißen — sonst legt
                // ein Tippfehler in der UI die ganze Guardrail still.
                continue;
            }

            entries.Add(new Entry(rule, regex, rule.Keyword));
        }

        var keywords = entries.Select(e => e.Keyword).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

        return new Compiled(
            Outbound: [.. entries.Where(e => e.Rule.Direction is GuardDirection.Outbound or GuardDirection.Both)],
            Inbound: [.. entries.Where(e => e.Rule.Direction is GuardDirection.Inbound or GuardDirection.Both)],
            Keywords: keywords.Length > 0 ? SearchValues.Create(keywords, StringComparison.Ordinal) : null);
    }

    private sealed record Entry(GuardRule Rule, Regex Regex, string? Keyword);

    private sealed record Compiled(
        ImmutableArray<Entry> Outbound,
        ImmutableArray<Entry> Inbound,
        SearchValues<string>? Keywords);
}

/// <summary>Zusammenfassung eines Guard-Eingriffs für Fehlermeldung und Audit.</summary>
public static class GuardMessages
{
    /// <summary>Der Call wurde vor dem Upstream abgebrochen — kein Seiteneffekt eingetreten.</summary>
    public static string Outbound(IReadOnlyList<GuardFinding> findings)
        => $"Aufruf abgebrochen: Die Argumente enthalten mutmaßlich Zugangsdaten ({Describe(findings)}). "
            + "Der Upstream wurde nicht kontaktiert.";

    /// <summary>
    /// Der Call ist bereits gelaufen (ADR-0011, E1). Das muss der Text sagen, sonst wiederholt
    /// ein Agent den Aufruf und löst den Seiteneffekt ein zweites Mal aus.
    /// </summary>
    public static string Inbound(IReadOnlyList<GuardFinding> findings)
        => $"Der Aufruf wurde ausgeführt, aber sein Ergebnis wird zurückgehalten: Es enthält "
            + $"mutmaßlich Zugangsdaten ({Describe(findings)}). "
            + "NICHT wiederholen — eine Wiederholung führt die Aktion erneut aus. "
            + "Das Ergebnis ist über das Audit-Log eines Administrators einsehbar.";

    /// <summary>Nutzlast über der Prüfgrenze — ungeprüft heißt blockiert (ADR-0011, E4).</summary>
    public static string TooLarge(int chars, int limit)
        => $"Nutzlast ist mit {chars.ToString(CultureInfo.InvariantCulture)} Zeichen größer als die "
            + $"Prüfgrenze von {limit.ToString(CultureInfo.InvariantCulture)}; sie kann nicht auf "
            + "Zugangsdaten geprüft und wird deshalb nicht durchgelassen. "
            + "Abhilfe: Ergebnis-Kürzung aktivieren (BIFROST_MAX_RESULT_CHARS) oder die Prüfgrenze anheben.";

    private static string Describe(IReadOnlyList<GuardFinding> findings)
        => string.Join(", ", findings.Where(f => f.Mode == GuardMode.Block).Select(f => f.RuleDescription).Distinct());
}
