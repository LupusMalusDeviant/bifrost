using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Npgsql;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Das Werkzeug fehlt — und das ist ein <b>Fehler mit Meldung</b>, kein stiller Rückfall
/// (ADR-0024 E2).
/// <para>
/// Ein eigener Ausnahmetyp: Der Aufrufer (Startkoordination, CLI, REST-Fassade) muss „das Werkzeug
/// ist nicht da" von „die Sicherung ist schiefgegangen" unterscheiden können. Die erste Lage behebt
/// ein Betreiber durch eine Installation, die zweite nicht.
/// </para>
/// <para>
/// Abgeleitet von <see cref="NotSupportedException"/>, weil genau das die Lage ist: Der Vorgang wird
/// von <em>dieser Installation</em> nicht unterstützt. Die REST-Fassade beantwortet ihn dadurch
/// weiterhin mit <c>501</c> und dem Text der Meldung — der Betreiber liest also am Aufrufpunkt, was
/// fehlt und wo er es herbekommt.
/// </para>
/// </summary>
public sealed class PostgresToolMissingException : NotSupportedException
{
    public PostgresToolMissingException(string message)
        : base(message)
    {
    }

    public PostgresToolMissingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public PostgresToolMissingException()
        : base(PostgresTools.MissingMessage)
    {
    }
}

/// <summary>Ein Aufruf von <c>pg_dump</c>/<c>pg_restore</c> ist fehlgeschlagen.</summary>
public sealed class PostgresToolFailedException : Exception
{
    public PostgresToolFailedException(string message)
        : base(message)
    {
    }

    public PostgresToolFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public PostgresToolFailedException()
        : base("Ein PostgreSQL-Werkzeug ist fehlgeschlagen.")
    {
    }
}

/// <summary>Die gefundenen Programme.</summary>
public sealed record PostgresToolset(string DumpPath, string RestorePath);

/// <summary>
/// Findet und startet <c>pg_dump</c> und <c>pg_restore</c> (ADR-0024 E2).
/// <para>
/// <b>Warum überhaupt fremde Programme:</b> ADR-0024 E2 verlangt sie ausdrücklich. Der
/// Alternativweg — ein selbstgeschriebener Zeilenexport — wäre eine zweite, schlechter geprüfte
/// Umsetzung derselben Aufgabe: Sie müsste Sequenzen, Fremdschlüsselreihenfolge, Typen,
/// Erweiterungen und Geheimtext in <c>bytea</c> selbst beherrschen. Fehlt das Werkzeug, ist das
/// deshalb ein Fehler mit Meldung.
/// </para>
/// <para>
/// <b>Wie das Passwort ankommt:</b> über eine <c>PGPASSFILE</c>, die für die Dauer des Aufrufs in
/// einem eigenen temporären Verzeichnis liegt und auf Unix mit 0600 angelegt wird. <b>Nicht</b> auf
/// der Kommandozeile: die steht in der Prozessliste jedes Benutzers auf dem Rechner. Auch nicht als
/// <c>PGPASSWORD</c>: die Umgebung eines Prozesses ist zwar besser geschützt als seine
/// Kommandozeile, aber die PostgreSQL-Dokumentation rät ausdrücklich davon ab, weil sie auf
/// manchen Systemen für andere Benutzer lesbar ist — und weil sie an Kindprozesse weitervererbt
/// wird. Die Datei wird nach dem Aufruf gelöscht, auch im Fehlerfall.
/// </para>
/// </summary>
public static class PostgresTools
{
    /// <summary>
    /// Verzeichnis, in dem die Werkzeuge liegen, falls sie nicht im <c>PATH</c> stehen. Gedacht für
    /// Installationen, die den Client neben einer anderen Serverversion halten.
    /// </summary>
    public const string BinDirectoryVariable = "BIFROST_POSTGRES_BIN";

    public const string DumpProgram = "pg_dump";
    public const string RestoreProgram = "pg_restore";

    public static string MissingMessage =>
        $"PostgreSQL-Sicherungen laufen laut ADR-0024 E2 über '{DumpProgram}' und '{RestoreProgram}'. "
        + "Keines der beiden Programme ist erreichbar (weder im PATH noch unter "
        + $"{BinDirectoryVariable}). Es gibt bewusst keinen Rückfall auf einen selbstgebauten "
        + "Zeilenexport — der wäre eine zweite, schlechter geprüfte Sicherung derselben Daten.\n"
        + "Abhilfe: das PostgreSQL-Clientpaket installieren "
        + "(Debian/Ubuntu: 'apt-get install postgresql-client'; Alpine: 'apk add postgresql17-client'; "
        + "RHEL/Fedora: 'dnf install postgresql'; macOS: 'brew install libpq'; "
        + "Windows: die 'Command Line Tools' des PostgreSQL-Installers) — oder "
        + $"{BinDirectoryVariable} auf das Verzeichnis setzen, in dem sie liegen. "
        + "Im Bifrost-Container ist das Clientpaket enthalten; fehlt es dort, ist ein fremdes Image "
        + "im Spiel.";

    /// <summary>
    /// Sucht beide Programme. Findet sie <b>zusammen</b> oder gar nicht: Ein <c>pg_dump</c> ohne
    /// <c>pg_restore</c> ergäbe Sicherungen, die niemand zurückspielen kann — die Lücke fiele erst
    /// im Ernstfall auf.
    /// </summary>
    /// <param name="binDirectory">
    /// Ein ausdrücklich genanntes Verzeichnis. Ist es gesetzt, wird <b>nur dort</b> gesucht — weder
    /// die Umgebungsvariable noch der <c>PATH</c> kommen dann zum Zug.
    /// <para>
    /// <b>Warum kein Weitersuchen:</b> Wer den Ort nennt, macht eine Aussage und keine Anregung. Ein
    /// Rückfall auf den <c>PATH</c> würde stillschweigend ein anderes, womöglich älteres
    /// <c>pg_dump</c> nehmen als das gemeinte — und der Unterschied fiele erst beim Zurückspielen
    /// auf, wenn es dafür zu spät ist.
    /// </para>
    /// </param>
    public static bool TryLocate(string? binDirectory, out PostgresToolset? toolset)
    {
        toolset = null;
        var directory = string.IsNullOrWhiteSpace(binDirectory)
            ? Environment.GetEnvironmentVariable(BinDirectoryVariable)
            : binDirectory;

        var dump = Find(DumpProgram, directory);
        var restore = Find(RestoreProgram, directory);
        if (dump is null || restore is null)
        {
            return false;
        }

        toolset = new PostgresToolset(dump, restore);
        return true;
    }

    /// <summary>Sucht in Umgebungsvariable und <c>PATH</c>.</summary>
    public static bool TryLocate(out PostgresToolset? toolset) => TryLocate(null, out toolset);

    /// <summary>Wie <see cref="TryLocate(string?, out PostgresToolset?)"/>, nur mit der Meldung statt eines <c>false</c>.</summary>
    public static PostgresToolset Require(string? binDirectory = null)
        => TryLocate(binDirectory, out var toolset) && toolset is not null
            ? toolset
            : throw new PostgresToolMissingException(
                string.IsNullOrWhiteSpace(binDirectory)
                    ? MissingMessage
                    : $"In '{binDirectory}' liegen weder '{DumpProgram}' noch '{RestoreProgram}'.\n"
                      + MissingMessage);

    private static string? Find(string program, string? binDirectory)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(binDirectory))
        {
            candidates.AddRange(WithExtensions(Path.Combine(binDirectory, program)));
        }
        else
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                // Ein unbrauchbarer PATH-Eintrag (etwa mit Anführungszeichen) darf die Suche nicht
                // beenden — er darf nur nicht gefunden werden.
                if (directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    continue;
                }

                candidates.AddRange(WithExtensions(Path.Combine(directory.Trim(), program)));
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Nicht lesbar heißt nicht gefunden.
            }
        }

        return null;
    }

    private static IEnumerable<string> WithExtensions(string basePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return basePath;
            yield break;
        }

        yield return basePath + ".exe";
        yield return basePath;
    }

    // ── Aufruf ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die Verbindungsangaben, zerlegt für die Kommandozeile. Das Passwort steht bewusst getrennt:
    /// Es ist der einzige Teil, der die Kommandozeile <b>nicht</b> sehen darf.
    /// </summary>
    public sealed record Target(string Host, int Port, string Database, string? User, string? Password, string? SslMode)
    {
        public static Target FromConnectionString(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Database))
            {
                throw new InvalidOperationException(
                    "Die PostgreSQL-Verbindungszeichenfolge nennt keine Datenbank. Ohne sie weiß "
                    + "pg_dump nicht, was es sichern soll.");
            }

            return new Target(
                string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host,
                builder.Port,
                builder.Database,
                string.IsNullOrWhiteSpace(builder.Username) ? null : builder.Username,
                string.IsNullOrEmpty(builder.Password) ? null : builder.Password,
                LibpqSslMode(builder.SslMode));
        }

        /// <summary>
        /// Npgsqls Aufzählung in die Schreibweise von libpq.
        /// <para>
        /// <b>Warum nicht <c>ToString().ToLower()</c>:</b> Das ergäbe für
        /// <c>VerifyFull</c> den Wert <c>verifyfull</c>, und libpq kennt nur <c>verify-full</c> — der
        /// Aufruf bräche mit „invalid sslmode value" ab, an einer Stelle, die mit der Sicherung nichts
        /// zu tun hat. Ein unbekannter Wert ergibt <c>null</c>: Dann bleibt <c>PGSSLMODE</c> ungesetzt
        /// und libpq nimmt seine eigene Vorgabe, statt an einer Übersetzung zu scheitern.
        /// </para>
        /// </summary>
        private static string? LibpqSslMode(Npgsql.SslMode mode) => mode switch
        {
            Npgsql.SslMode.Disable => "disable",
            Npgsql.SslMode.Allow => "allow",
            Npgsql.SslMode.Prefer => "prefer",
            Npgsql.SslMode.Require => "require",
            Npgsql.SslMode.VerifyCA => "verify-ca",
            Npgsql.SslMode.VerifyFull => "verify-full",
            _ => null,
        };

        /// <summary>Die Argumente, die jedes der beiden Programme gleich versteht.</summary>
        public IEnumerable<string> ConnectionArguments()
        {
            yield return "--host=" + Host;
            yield return "--port=" + Port.ToString(CultureInfo.InvariantCulture);
            if (User is not null)
            {
                yield return "--username=" + User;
            }

            yield return "--no-password";
        }
    }

    /// <summary>
    /// Startet ein Werkzeug, wartet darauf und liefert dessen Ausgabe. Ein Rückgabewert ungleich
    /// null wird zu <see cref="PostgresToolFailedException"/> — mit der <c>stderr</c>-Ausgabe im
    /// Text, weil genau dort steht, was PostgreSQL bemängelt hat.
    /// </summary>
    public static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Target target,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(target);

        var secrets = Directory.CreateTempSubdirectory("bifrost-pg-");
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // Das Passwort geht ausschließlich über die Datei (siehe Klassenkommentar).
            if (target.Password is not null)
            {
                startInfo.Environment["PGPASSFILE"] = WritePassFile(secrets.FullName, target.Password);
            }

            if (!string.IsNullOrWhiteSpace(target.SslMode))
            {
                startInfo.Environment["PGSSLMODE"] = target.SslMode;
            }

            // Meldungen auf Englisch anfordern: Die Absätze in unseren Fehlermeldungen zitieren
            // stderr, und eine je nach Serverlocale wechselnde Sprache macht sie unsuchbar.
            startInfo.Environment["LC_MESSAGES"] = "C";

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Zwischen Fund und Start kann das Programm verschwunden oder unausführbar sein.
                throw new PostgresToolMissingException(
                    $"'{executable}' ließ sich nicht starten: {ex.Message}\n{MissingMessage}", ex);
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ein Abbruch, der das Werkzeug weiterlaufen ließe, hinterließe eine halbe Datei —
                // und niemanden, der sie aufräumt.
                TryKill(process);

                // Die beiden Leseaufgaben laufen noch und würden sonst als unbeobachtete Ausnahme
                // enden. Sie werden hier abgeräumt, nicht ausgewertet: Ihr Inhalt interessiert
                // niemanden mehr, ihre Ausnahme aber taucht sonst irgendwann irgendwo auf.
                Observe(stdout);
                Observe(stderr);
                throw;
            }

            var errorText = (await stderr.ConfigureAwait(false)).Trim();
            var outputText = (await stdout.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0)
            {
                throw new PostgresToolFailedException(string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' ist mit Rückgabewert {1} fehlgeschlagen.\n{2}",
                    Path.GetFileName(executable),
                    process.ExitCode,
                    string.IsNullOrEmpty(errorText) ? outputText : errorText));
            }
        }
        finally
        {
            BackupService.TryDeleteDirectory(secrets.FullName);
        }
    }

    /// <summary>
    /// Schreibt die <c>PGPASSFILE</c>. Alle Felder außer dem Passwort stehen als <c>*</c>: Die Datei
    /// gilt für genau einen Aufruf, und Platzhalter ersparen das Maskieren von Wirtsnamen, die
    /// selbst einen Doppelpunkt enthalten können (IPv6).
    /// </summary>
    private static string WritePassFile(string directory, string password)
    {
        var file = Path.Combine(directory, "pgpass.conf");

        // Doppelpunkt und Rückwärtsschrägstrich trennen die Felder und müssen maskiert werden —
        // sonst wäre ein Passwort mit Doppelpunkt hinter dem ersten abgeschnitten.
        var escaped = password.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

        // Ohne abschließenden Zeilenumbruch überliest libpq die letzte Zeile auf manchen Ständen.
        File.WriteAllText(file, "*:*:*:*:" + escaped + "\n", new UTF8Encoding(false));

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // libpq verweigert eine Passwortdatei, die für Gruppe oder Welt lesbar ist — und zwar
            // still. Ohne diese Zeile liefe der Aufruf ins "no password supplied".
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return file;
    }

    /// <summary>Nimmt einer verwaisten Aufgabe ihre Ausnahme ab, ohne auf sie zu warten.</summary>
    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // Aufräumen ist Höflichkeit, nicht Vertrag.
        }
    }
}
