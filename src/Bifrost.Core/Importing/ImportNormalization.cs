using System.Text.RegularExpressions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>
/// Bringt einen von einem Providerparser gelieferten Server in die Form, die dieses Gateway kennt —
/// Namen, Transporte, Argumente, Umgebung, Header, URLs.
/// <para>
/// <b>Warum das zentral steht und nicht in jedem Parser:</b> Ein Parser je Quellformat ist richtig
/// (WP4.2), aber die Zielform ist für alle dieselbe. Läge die Normalisierung in den Parsern, gäbe es
/// vier Meinungen darüber, was ein zulässiger Slug ist — und die vierte wäre die falsche. Der Parser
/// beantwortet „was stand da?", diese Klasse beantwortet „wie heißt das hier?".
/// </para>
/// <para>
/// <b>Die Abbildung ist idempotent.</b> Ein bereits normalisierter Server kommt unverändert und ohne
/// neue Befunde zurück. Ohne diese Zusage wäre ein zweiter Blick auf denselben Plan ein anderer Plan.
/// </para>
/// <para>
/// <b>Was hier nicht passiert:</b> Es wird nichts aufgelöst und nichts geraten. Ein
/// <c>${HOME}/bin/server</c> bleibt stehen, eine maskierte Kopfzeile bleibt maskiert. Auflösen hieße,
/// die Umgebung dieser Instanz für die der Quellmaschine zu halten.
/// </para>
/// </summary>
public static partial class ImportNormalization
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentName();

    /// <summary>
    /// Normalisiert einen Server und hängt die dabei entstandenen Befunde an die des Parsers an.
    /// </summary>
    /// <param name="path">Der Ort im Quelldokument, etwa <c>mcpServers/GitHub Issues</c>.</param>
    [NoHostExecution(
        "Formt Namen, Umgebung, Header und Transportangaben um und liefert eine Kopie. Startet "
        + "nichts, persistiert nichts und beurteilt nichts an der Ausfuehrungsart — dafuer ist "
        + "ImportRiskScanner zustaendig, und der fragt die Policy.")]
    public static ImportCandidate Normalize(ImportCandidate candidate, string path)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var findings = new List<ImportFinding>(candidate.Findings);
        var config = candidate.Config;

        config = NormalizeIdentity(candidate.SourceName, config, path, findings);
        config = NormalizeStdio(config, path, findings);
        config = NormalizeCli(config, path, findings);
        config = NormalizeHttp(config, path, findings);

        // Der Kern des Meilensteins, und er steht mit Absicht am Ende: Was hier herauskommt, ist ein
        // Vorschlag. Ein Plan, dessen Server bereits eingeschaltet wären, hätte den Unterschied
        // zwischen „analysiert" und „angelegt" nur noch im Namen.
        config = config with { Enabled = false };

        return candidate with { Config = config, Findings = findings };
    }

    /// <summary>Name und Slug — die Namespacing-Basis der Werkzeugnamen (FR-03).</summary>
    private static UpstreamServerConfig NormalizeIdentity(
        string sourceName, UpstreamServerConfig config, string path, List<ImportFinding> findings)
    {
        var result = ImportSlug.Normalize(sourceName);

        if (result.Changed)
        {
            findings.Add(new ImportFinding(
                ImportReason.Lossy,
                ImportSeverity.Warning,
                $"Der Servername '{sourceName}' ist als Slug nicht zulaessig und heisst hier "
                + $"'{result.Slug}'. Der Slug ist die Namespacing-Basis der Werkzeugnamen: Fuer den "
                + "Agenten heissen die Werkzeuge dieses Servers ab jetzt anders.",
                path,
                "Den Namen vor dem Anlegen bestaetigen oder aendern — danach ist eine Umbenennung "
                + "eine Aenderung an den Werkzeugnamen."));
        }

        var displayName = string.IsNullOrWhiteSpace(sourceName) ? result.Slug : sourceName.Trim();
        return config with { Slug = result.Slug, DisplayName = displayName };
    }

    private static UpstreamServerConfig NormalizeStdio(
        UpstreamServerConfig config, string path, List<ImportFinding> findings)
    {
        if (config.Stdio is not { } stdio)
        {
            return config;
        }

        var command = ImportPathShape.Unquote(stdio.Command);
        if (!string.Equals(command, stdio.Command, StringComparison.Ordinal))
        {
            findings.Add(new ImportFinding(
                ImportReason.Lossy,
                ImportSeverity.Info,
                "Das Kommando stand in Anfuehrungszeichen; die gehoeren zur Schreibweise der "
                + "Quelldatei und nicht zum Pfad. Der Aufruf laeuft hier ohne Shell — ein "
                + "Anfuehrungszeichen waere Teil des Dateinamens.",
                $"{path}/command"));
        }

        return config with
        {
            Stdio = stdio with
            {
                Command = command,
                Arguments = [.. stdio.Arguments],
                EnvironmentVariables = NormalizeEnvironment(stdio.EnvironmentVariables, path, findings),
                WorkingDirectory = Blank(stdio.WorkingDirectory),
            },
        };
    }

    private static UpstreamServerConfig NormalizeCli(
        UpstreamServerConfig config, string path, List<ImportFinding> findings)
    {
        if (config.Cli is not { } cli)
        {
            return config;
        }

        return config with
        {
            Cli = cli with
            {
                Executable = ImportPathShape.Unquote(cli.Executable),
                EnvironmentVariables = NormalizeEnvironment(cli.EnvironmentVariables, path, findings),
                WorkingDirectory = Blank(cli.WorkingDirectory),
            },
        };
    }

    /// <summary>
    /// Umgebungsvariablen. Namen, die nicht auf jeder Plattform zulässig sind, werden
    /// <b>entfernt und benannt</b> — sie würden den Validator später zum Abbruch bringen, und dann
    /// stünde der Fehler an einer Stelle, an der niemand mehr weiß, aus welcher Zeile er kam.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? NormalizeEnvironment(
        IReadOnlyDictionary<string, string>? environment, string path, List<ImportFinding> findings)
    {
        if (environment is null || environment.Count == 0)
        {
            return environment;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in environment)
        {
            var name = entry.Key.Trim();
            if (!EnvironmentName().IsMatch(name))
            {
                findings.Add(new ImportFinding(
                    ImportReason.Lossy,
                    ImportSeverity.Warning,
                    $"Die Umgebungsvariable '{entry.Key}' hat einen plattformuebergreifend "
                    + "unzulaessigen Namen und wird nicht uebernommen. Erlaubt sind Buchstaben, "
                    + "Ziffern und '_', beginnend mit einem Buchstaben oder '_'.",
                    $"{path}/env/{entry.Key}",
                    "Die Variable auf der Zielinstanz unter einem zulaessigen Namen nachtragen."));
                continue;
            }

            if (!result.TryAdd(name, entry.Value))
            {
                findings.Add(new ImportFinding(
                    ImportReason.Lossy,
                    ImportSeverity.Warning,
                    $"Die Umgebungsvariable '{name}' kommt mehrfach vor; welcher Wert gilt, sagt die "
                    + "Quelle nicht. Uebernommen wird der erste.",
                    $"{path}/env/{name}"));
            }
        }

        return result;
    }

    /// <summary>
    /// HTTP: Headernamen und Ziel. Werte bleiben unangetastet — ein getrimmtes Token ist ein
    /// anderes Token, und ein Header mit führendem Leerzeichen ist ein Hinweis, kein Schmutz.
    /// </summary>
    private static UpstreamServerConfig NormalizeHttp(
        UpstreamServerConfig config, string path, List<ImportFinding> findings)
    {
        if (config.Http is not { } http)
        {
            return config;
        }

        if (http.Headers is not { Count: > 0 } headers)
        {
            return config;
        }

        // Headernamen sind laut RFC 9110 ohne Rücksicht auf Groß- und Kleinschreibung gleich.
        // 'authorization' und 'Authorization' zweimal zu uebernehmen hiesse, sich auf die
        // Reihenfolge einer fremden Datei zu verlassen.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in headers)
        {
            var name = entry.Key.Trim();
            if (name.Length == 0)
            {
                findings.Add(new ImportFinding(
                    ImportReason.Lossy,
                    ImportSeverity.Warning,
                    "Ein Header ohne Namen wird nicht uebernommen.",
                    $"{path}/headers"));
                continue;
            }

            if (!result.TryAdd(name, entry.Value))
            {
                var kept = result.Keys.First(existing =>
                    string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

                findings.Add(new ImportFinding(
                    ImportReason.Lossy,
                    ImportSeverity.Warning,
                    $"Der Header '{name}' kollidiert mit '{kept}' — Headernamen unterscheiden nicht "
                    + $"zwischen Gross- und Kleinschreibung. Uebernommen wird '{kept}', der zuerst "
                    + "genannte.",
                    $"{path}/headers/{name}"));
            }
        }

        return config with { Http = http with { Headers = result } };
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
