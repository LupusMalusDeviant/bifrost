using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics;

/// <summary>
/// Ein einzelner Befund. Klein gehalten, mit eigenem Code, eigenem Zeitlimit und ohne Seiteneffekt
/// auf den Zustand der Installation.
/// <para>
/// <b>Ein Check antwortet oder er wird abgeschnitten.</b> Er bekommt sein eigenes Zeitlimit, und
/// der Dienst wartet nicht darüber hinaus — auch dann nicht, wenn der Check den
/// <see cref="CancellationToken"/> ignoriert. Ein hängender Netzaufruf darf keinen Bericht kosten,
/// der zu neunzehn Zwanzigsteln fertig ist.
/// </para>
/// </summary>
public interface IDiagnosticCheck
{
    /// <summary>Der stabile Code aus <see cref="DiagnosticCodes"/>.</summary>
    string Code { get; }

    /// <summary>Bereich; entscheidet, ob der Check zu einem eingeschränkten Lauf gehört.</summary>
    DiagnosticScope Scope { get; }

    /// <summary>Eigenes Zeitlimit. Kurz für lokale Fragen, großzügiger für Netz und Prozesse.</summary>
    TimeSpan Timeout { get; }

    Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct);
}

/// <summary>Bausteine für Befunde, damit sie überall gleich aussehen.</summary>
public static class CheckOutcome
{
    public static DiagnosticCheck Pass(
        string code, string summary, IReadOnlyDictionary<string, string>? details = null)
        => new(code, CheckStatus.Pass, summary, null, details);

    public static DiagnosticCheck Warning(
        string code, string summary, string remediation, IReadOnlyDictionary<string, string>? details = null)
        => new(code, CheckStatus.Warning, summary, remediation, details);

    public static DiagnosticCheck Fail(
        string code, string summary, string remediation, IReadOnlyDictionary<string, string>? details = null)
        => new(code, CheckStatus.Fail, summary, remediation, details);

    /// <summary>
    /// Nicht anwendbar oder Voraussetzung fehlt. Der Grund ist Pflicht — „übersprungen" ohne
    /// Begründung ist genau das stille Bestehen, das der Vertrag ausschließt.
    /// </summary>
    public static DiagnosticCheck Skipped(
        string code, string reason, IReadOnlyDictionary<string, string>? details = null)
        => new(code, CheckStatus.Skipped, reason, null, details);

    /// <summary>Detailtabelle mit stabiler Reihenfolge.</summary>
    public static IReadOnlyDictionary<string, string> Details(params (string Key, string Value)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var details = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            details[key] = value;
        }

        return details;
    }
}
