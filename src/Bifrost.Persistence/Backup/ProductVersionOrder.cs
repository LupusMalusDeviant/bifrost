namespace Bifrost.Persistence.Backup;

/// <summary>
/// Versionsvergleich für die Restore-Vorprüfung (ADR-0024 E6). Verglichen wird der numerische Kern
/// <c>Major.Minor.Patch</c>; ein Vorabkennzeichen (<c>0.12.0-rc1</c>) wird abgeschnitten, weil ein
/// Release-Kandidat dieselbe Schemafähigkeit hat wie sein Release.
/// </summary>
internal static class ProductVersionOrder
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value.Split('-', 2)[0].Split('+', 2)[0];
        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>Ist <paramref name="current"/> mindestens <paramref name="minimum"/>?</summary>
    public static bool IsAtLeast(string? current, string? minimum, out string? problem)
    {
        problem = null;
        if (!TryParse(current, out var currentVersion))
        {
            problem = $"Die eigene Produktversion '{current}' ist nicht lesbar.";
            return false;
        }

        if (!TryParse(minimum, out var minimumVersion))
        {
            problem = $"Die Mindestversion '{minimum}' im Manifest ist nicht lesbar.";
            return false;
        }

        return currentVersion >= minimumVersion;
    }
}
