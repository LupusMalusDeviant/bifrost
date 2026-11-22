namespace Faolan.Abstractions;

/// <summary>
/// Der Name der SQLite-Datenbank im Datenverzeichnis über die Umbenennungen hinweg
/// (Vertrag <c>docs/contracts/umbenennung-faolan.md</c> §2.2).
/// <para>
/// <b>Die vorhandene Datei gewinnt über den schöneren Namen.</b> Läge im Datenverzeichnis nur eine
/// <c>bifrost.db</c> oder <c>mcpmcp.db</c>, entstünde sonst daneben eine leere neue — ohne Server,
/// ohne Rollen, ohne Schlüssel, und ohne einen einzigen Fehler. Unter mehreren vorhandenen gewinnt
/// die jüngste Generation. Umbenannt wird nichts.
/// </para>
/// </summary>
public static class SqliteDatabaseFile
{
    /// <summary>Die Dateinamen vom gültigen zum ältesten.</summary>
    public static IReadOnlyList<string> Names { get; } = ["faolan.db", "bifrost.db", "mcpmcp.db"];

    /// <summary>Der Name für eine Neuinstallation.</summary>
    public static string CurrentName => Names[0];

    /// <summary>Die Datei, die eine Installation in <paramref name="dataDirectory"/> benutzt.</summary>
    public static string Resolve(string dataDirectory, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        exists ??= File.Exists;
        foreach (var name in Names)
        {
            var candidate = Path.Combine(dataDirectory, name);
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dataDirectory, CurrentName);
    }
}
