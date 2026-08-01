using System.Text;
using System.Text.RegularExpressions;

namespace Bifrost.Core.Importing;

/// <summary>Das Ergebnis der Namensnormalisierung.</summary>
/// <param name="Slug">Der Name, wie er in <c>UpstreamServerConfig.Slug</c> stehen kann.</param>
/// <param name="Changed">
/// Ob dabei etwas verändert wurde. Der Slug ist die Namespacing-Basis der Werkzeugnamen (FR-03) —
/// ein still umbenannter Server heißt für den Agenten ab sofort anders, und niemand hat es gesehen.
/// </param>
public sealed record ImportSlugResult(string Slug, bool Changed);

/// <summary>
/// Übersetzt den Namen aus einer fremden Konfiguration in einen zulässigen Slug.
/// <para>
/// Fremde Clients erlauben als Schlüssel fast alles — Leerzeichen, Großbuchstaben, Punkte, Umlaute.
/// <c>UpstreamConfigValidator</c> erlaubt <c>a-z0-9_-</c>, höchstens 64 Zeichen, Beginn mit einer
/// Ziffer oder einem Kleinbuchstaben. Diese Klasse kennt <b>nur</b> diese eine Regel; sie prüft
/// nichts und wirft nichts. Ob der Slug am Ende zulässig ist, sagt weiterhin der Validator — zwei
/// Stellen mit derselben Regel wären zwei Wahrheiten, von denen eine veraltet.
/// </para>
/// <para>
/// <b>Kollisionen löst diese Klasse ausdrücklich nicht auf.</b> Zwei Namen können auf denselben
/// Slug fallen (<c>My Server</c> und <c>my-server</c>). Einen davon mit einer angehängten Ziffer zu
/// retten, wäre eine Entscheidung über fremde Werkzeugnamen; sie wird als Befund gemeldet.
/// </para>
/// </summary>
public static partial class ImportSlug
{
    /// <summary>Der Name, unter dem ein vollständig unbrauchbarer Quellname weiterlebt.</summary>
    public const string Fallback = "importierter-server";

    private const int MaxLength = 64;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex ValidSlug();

    /// <summary>Ist dieser Name bereits ein zulässiger Slug?</summary>
    public static bool IsValid(string name) => name is not null && ValidSlug().IsMatch(name);

    /// <summary>
    /// Normalisiert einen Quellnamen. Die Abbildung ist <b>idempotent</b>: Ein bereits zulässiger
    /// Slug kommt unverändert zurück, und ein zweiter Durchlauf ändert nichts mehr. Ohne diese
    /// Zusage wäre ein erneuter Import derselben Datei ein anderer Server.
    /// </summary>
    public static ImportSlugResult Normalize(string? sourceName)
    {
        var source = sourceName?.Trim() ?? string.Empty;
        if (IsValid(source))
        {
            return new ImportSlugResult(source, false);
        }

        var builder = new StringBuilder(source.Length);
        foreach (var character in source.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_')
            {
                builder.Append(character);
                continue;
            }

            // Alles andere wird zu einem Trennstrich — aber nie zwei hintereinander. Ein Name wie
            // "GitHub :: Issues" ergäbe sonst "github----issues", und das liest niemand mehr.
            if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var candidate = builder.ToString().TrimEnd('-');
        candidate = candidate.TrimStart('-', '_');

        // Der Validator verlangt einen Beginn mit a-z oder 0-9. Ein Name, von dem danach nichts mehr
        // übrig ist, bekommt einen sprechenden Ersatz statt eines erfundenen Kürzels.
        while (candidate.Length > 0 && !char.IsAsciiLetterLower(candidate[0]) && !char.IsAsciiDigit(candidate[0]))
        {
            candidate = candidate[1..];
        }

        if (candidate.Length == 0)
        {
            return new ImportSlugResult(Fallback, true);
        }

        if (candidate.Length > MaxLength)
        {
            candidate = candidate[..MaxLength].TrimEnd('-');
        }

        return new ImportSlugResult(candidate, !string.Equals(candidate, source, StringComparison.Ordinal));
    }
}
