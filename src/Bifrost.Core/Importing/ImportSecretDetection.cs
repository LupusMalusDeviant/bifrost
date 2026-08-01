using System.Text.RegularExpressions;

namespace Bifrost.Core.Importing;

/// <summary>Wie ein einzelner Wert einer fremden Konfiguration eingeschätzt wird.</summary>
/// <param name="IsSecret">Ob diese Stelle als Zugangsdatum gemeldet wird.</param>
/// <param name="Looked">
/// Woran es erkannt wurde — <b>nie</b> der Wert und nie ein Teil davon. Für die Rückfrage an den
/// Betreiber: „Ich halte das für ein Zugangsdatum, weil …" ist beantwortbar.
/// </param>
/// <param name="ValuePresent">
/// Ob die Quelle einen brauchbaren Wert mitbringt. <c>false</c> heißt: Die Quelle war leer,
/// maskiert oder eine Verweisform.
/// </param>
/// <param name="Masked">
/// Ob der Wert erkennbar unkenntlich gemacht wurde. Ein maskierter Wert wird gemeldet und
/// <b>nicht</b> rekonstruiert — ein erratener Wert, der fast stimmt, ist schlimmer als ein
/// fehlender: Er läuft durch jede Prüfung und scheitert erst am fremden Dienst, mit einer Meldung,
/// die nach einem Netzproblem aussieht.
/// </param>
public sealed record ImportSecretVerdict(bool IsSecret, string Looked, bool ValuePresent, bool Masked)
{
    /// <summary>Der Befund „hier steht nichts, was nach einem Zugangsdatum aussieht".</summary>
    public static ImportSecretVerdict None { get; } = new(false, string.Empty, false, false);
}

/// <summary>
/// Die Heuristik, die Zugangsdaten in einer <b>fremden</b> Konfiguration markiert.
/// <para>
/// <b>Der Unterschied zu <c>ConfigurationSecretScrubber</c> ist Absicht und keine Doppelung.</b>
/// Der Scrubber arbeitet in die andere Richtung: Er <em>entfernt</em> Werte vor einem Export und
/// hält deshalb jede Umgebungsvariable pauschal für ein Geheimnis — fail-closed, weil der Preis
/// eines Irrtums dort ein Leck ist. Hier wird nichts entfernt und nichts geschrieben; hier wird
/// einem Menschen gezeigt, was er gleich anlegt. Dieselbe Pauschalregel würde eine Liste erzeugen,
/// in der <c>NODE_ENV=production</c> neben dem echten Token steht — und eine Liste, in der alles
/// wichtig ist, liest niemand zu Ende.
/// </para>
/// <para>
/// Was beide teilen, ist die harte Regel: <b>Der Wert verlässt diese Klasse nicht.</b> Weder als
/// Präfix noch als Länge, Hash oder Zeichenklasse. Ein maskiertes Zugangsdatum ist ein
/// Zugangsdatum mit weniger Zeichen.
/// </para>
/// </summary>
public static partial class ImportSecretDetection
{
    /// <summary>Header, die ihrer Natur nach Autorisierungsdaten tragen.</summary>
    private static readonly HashSet<string> AuthorizationHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "api-key",
        "apikey",
        "x-auth-token",
        "x-access-token",
        "x-session-token",
    };

    [GeneratedRegex(
        "(token|secret|passwo?rd|passwd|pwd|credential|apikey|api[_-]?key|access[_-]?key|private[_-]?key"
        + "|auth|bearer|session|cookie|signature|client[_-]?secret|salt)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();

    /// <summary>
    /// Formen, die ein Zugangsdatum eines bekannten Anbieters hat. Welche davon zutrifft, wird
    /// <b>nicht</b> berichtet: Der Anbietername wäre schon eine Aussage über den Wert.
    /// </summary>
    [GeneratedRegex(
        @"^(gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{16,}"
        + @"|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|glpat-[A-Za-z0-9_-]{16,}"
        + @"|ey[A-Za-z0-9_-]{8,}\.ey[A-Za-z0-9_-]{8,}\..*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownCredentialShape();

    /// <summary>Ein Autorisierungs-Header mit Schema und Wert (<c>Bearer …</c>, <c>Basic …</c>).</summary>
    [GeneratedRegex(@"^(Bearer|Basic|Token|ApiKey)\s+\S{8,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeWithValue();

    /// <summary>
    /// Ein langes, zeichenreiches Zufallswort ohne Pfad- oder Satzstruktur. Absichtlich eng
    /// gefasst: Ein absoluter Pfad und eine URL enthalten <c>/</c>, <c>\</c> oder <c>:</c> und
    /// fallen damit heraus.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9+_=-]{32,}$", RegexOptions.CultureInvariant)]
    private static partial Regex RandomLooking();

    /// <summary>Eine Verweisform statt eines Wertes: <c>${…}</c>, <c>$FOO</c>, <c>%FOO%</c>.</summary>
    [GeneratedRegex(@"^(\$\{[^}]*\}|\$[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceForm();

    /// <summary>Maskierungen, wie sie in geteilten Konfigurationen und Dokumentationen vorkommen.</summary>
    [GeneratedRegex(@"(\*{3,}|x{4,}|X{4,}|•{3,}|\.{3,}|…|<[^>]*>|\bREDACTED\b|\bREMOVED\b"
        + @"|\bPLACEHOLDER\b|\bCHANGE_?ME\b|\bTODO\b|\bYOUR[_ -]|\bDEIN[_ -]|\bBEISPIEL\b|\bEXAMPLE\b|\bHIER\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaskForm();

    /// <summary>
    /// Beurteilt einen HTTP-Header.
    /// </summary>
    public static ImportSecretVerdict InspectHeader(string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (AuthorizationHeaders.Contains(name.Trim()))
        {
            return Judge("Header traegt seiner Art nach Autorisierungsdaten", value);
        }

        if (SecretName().IsMatch(name))
        {
            return Judge("Headername nennt ein Zugangsdatum", value);
        }

        if (value is not null && SchemeWithValue().IsMatch(value.Trim()))
        {
            return Judge("Headerwert hat die Form 'Schema + Zugangsdatum'", value);
        }

        return InspectValueOnly(value);
    }

    /// <summary>
    /// Beurteilt eine Umgebungsvariable.
    /// </summary>
    public static ImportSecretVerdict InspectEnvironment(string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (SecretName().IsMatch(name))
        {
            return Judge("Name der Umgebungsvariablen nennt ein Zugangsdatum", value);
        }

        return InspectValueOnly(value);
    }

    /// <summary>
    /// Beurteilt einen Wert ohne Namensbezug — für Stellen, an denen der Name nichts verrät
    /// (Kommandoargumente, freie Felder).
    /// </summary>
    public static ImportSecretVerdict InspectValueOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ImportSecretVerdict.None;
        }

        var trimmed = value.Trim();

        if (KnownCredentialShape().IsMatch(trimmed))
        {
            return Judge("Wert hat die Form eines bekannten Zugangsdatums", value);
        }

        if (SchemeWithValue().IsMatch(trimmed))
        {
            return Judge("Wert hat die Form 'Schema + Zugangsdatum'", value);
        }

        if (RandomLooking().IsMatch(trimmed) && HasMixedCharacters(trimmed))
        {
            return Judge("Wert hat Laenge und Zeichenvielfalt eines Zufallsgeheimnisses", value);
        }

        return ImportSecretVerdict.None;
    }

    /// <summary>
    /// Ist dieser Wert erkennbar unkenntlich gemacht oder ein Verweis statt eines Wertes?
    /// <para>
    /// Öffentlich, weil dieselbe Frage auch außerhalb der Zugangsdaten auftaucht: Ein Kommando
    /// <c>${HOME}/bin/server</c> ist genauso wenig auflösbar wie ein maskiertes Token, und beides
    /// wird gemeldet statt geraten.
    /// </para>
    /// </summary>
    public static bool LooksMasked(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return ReferenceForm().IsMatch(trimmed) || MaskForm().IsMatch(trimmed);
    }

    private static ImportSecretVerdict Judge(string looked, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ImportSecretVerdict(true, looked + "; die Quelle traegt keinen Wert", false, false);
        }

        return LooksMasked(value)
            ? new ImportSecretVerdict(true, looked + "; der Wert ist maskiert oder eine Verweisform", false, true)
            : new ImportSecretVerdict(true, looked, true, false);
    }

    /// <summary>
    /// Ziffern <b>und</b> Buchstaben. Eine reine Buchstabenfolge dieser Länge ist eher ein Satz
    /// ohne Leerzeichen als ein Schlüssel, eine reine Ziffernfolge eher eine Kennnummer.
    /// </summary>
    private static bool HasMixedCharacters(string value)
        => value.Any(char.IsAsciiDigit) && value.Any(char.IsAsciiLetter);
}
