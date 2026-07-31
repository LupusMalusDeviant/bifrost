using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bifrost.Cli;

/// <summary>
/// Version und Herkunft der laufenden CLI.
/// </summary>
/// <remarks>
/// Beides steht in einem einzigen Attribut: Das .NET-SDK erzeugt
/// <see cref="AssemblyInformationalVersionAttribute"/> im Format <c>SemVer+CommitSha</c>. Die
/// SemVer-Haelfte kommt aus <c>VersionPrefix</c> in <c>Directory.Build.props</c> (die einzige
/// Quelle der Version laut Distributionsvertrag), die Commit-Haelfte haengt das SDK-Ziel
/// <c>AddSourceRevisionToInformationalVersion</c> aus der SourceLink-Abfrage des Arbeitsbaums an.
/// Deshalb braucht die Versionsausgabe weder ein generiertes Codestueck noch ein zusaetzliches
/// Paket — sie liest nur, was der Build ohnehin schon hineingelegt hat.
/// <para>
/// Faellt die Commit-Ermittlung aus (Build ohne <c>.git</c>, etwa aus einem Quell-Tarball), steht
/// dort <see cref="UnknownCommit"/>. Das ist ein sichtbarer Mangel und keine stille Luege.
/// </para>
/// </remarks>
public static class CliVersion
{
    /// <summary>Platzhalter, wenn der Build keinen Commit hinterlegen konnte.</summary>
    public const string UnknownCommit = "unbekannt";

    private static readonly (string SemVer, string Commit) Parsed = Parse(
        typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Die SemVer-Version, zum Beispiel <c>0.11.0</c> oder <c>0.12.0-rc.1</c>.</summary>
    public static string SemVer => Parsed.SemVer;

    /// <summary>Der Commit-SHA des Builds oder <see cref="UnknownCommit"/>.</summary>
    public static string Commit => Parsed.Commit;

    /// <summary>Die Laufzeit, die diese Datei ausfuehrt, zum Beispiel <c>.NET 10.0.2</c>.</summary>
    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>Die Runtime-ID des Artefakts, zum Beispiel <c>win-x64</c>.</summary>
    public static string RuntimeIdentifier => RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// Die Ausgabe von <c>bifrost --version</c>. Ohne <paramref name="jsonOutput"/> drei Zeilen
    /// fuer Menschen, mit <paramref name="jsonOutput"/> ein einzeiliges Objekt fuer Skripte.
    /// </summary>
    public static string Describe(bool jsonOutput)
        => jsonOutput
            ? string.Format(
                CultureInfo.InvariantCulture,
                """{{"version":"{0}","commit":"{1}","runtime":"{2}","rid":"{3}"}}""",
                SemVer,
                Commit,
                Runtime,
                RuntimeIdentifier)
            : string.Format(
                CultureInfo.InvariantCulture,
                """
                bifrost {0}
                Commit:   {1}
                Laufzeit: {2}, {3}
                """,
                SemVer,
                Commit,
                Runtime,
                RuntimeIdentifier);

    private static (string SemVer, string Commit) Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return ("0.0.0", UnknownCommit);
        }

        // '+' trennt Build-Metadaten nach SemVer 2.0; alles danach ist der Commit. Steckte im
        // VersionPrefix schon ein '+', haengt das SDK den SHA mit '.' an — dann ist der Commit
        // das letzte Segment.
        var separator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (informationalVersion, UnknownCommit);
        }

        var semVer = informationalVersion[..separator];
        var metadata = informationalVersion[(separator + 1)..];
        var commit = metadata.Split('.')[^1];
        return (
            semVer.Length == 0 ? "0.0.0" : semVer,
            commit.Length == 0 ? UnknownCommit : commit);
    }
}
