using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics.Checks;

/// <summary>
/// BFR-KEY-0001 — der Key-Ring ist da und nicht leer.
/// <para>
/// Er entschlüsselt die at-rest verschlüsselten Upstream-Zugangsdaten, die OAuth-Token und die
/// Webhook-Secrets. Fehlt er auf einer bestehenden Installation, ist die Datenbank zwar lesbar,
/// aber jedes darin gespeicherte Geheimnis unbrauchbar — und der Gateway startet trotzdem.
/// </para>
/// </summary>
public sealed class KeyRingPresenceCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.KeyRingPresent;

    public DiagnosticScope Scope => DiagnosticScope.KeyRing;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var directory = context.KeyRingDirectory;
        var details = CheckOutcome.Details(("verzeichnis", directory));

        if (!context.Files.DirectoryExists(directory))
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                $"Das Key-Ring-Verzeichnis '{directory}' existiert nicht.",
                "Bei einer Neuinstallation legt der Start es an. Bei einer bestehenden sind die "
                + "gespeicherten Upstream-Zugangsdaten damit unbrauchbar — Volume prüfen und den "
                + "Key-Ring aus der Sicherung zurückholen, bevor der Gateway startet.",
                details));
        }

        // Der DataProtection-Key-Ring legt je Schlüssel eine 'key-<guid>.xml' ab.
        var keys = context.Files.ListFiles(directory, "key-*.xml");
        if (keys.Count == 0)
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                $"Das Key-Ring-Verzeichnis '{directory}' enthält keinen Schlüssel.",
                "Beim ersten Start entsteht einer. Auf einer bestehenden Installation heisst ein "
                + "leeres Verzeichnis: Der Key-Ring ist weg und die gespeicherten Zugangsdaten sind "
                + "nicht mehr entschlüsselbar.",
                details));
        }

        return Task.FromResult(CheckOutcome.Pass(
            Code,
            $"Der Key-Ring enthält {keys.Count} Schlüsseldatei(en).",
            CheckOutcome.Details(
                ("verzeichnis", directory),
                ("schluesseldateien", DetailFormat.Count(keys.Count)))));
    }
}

/// <summary>
/// BFR-KEY-0002 — der Key-Ring liegt ungeschützt.
/// <para>
/// Ohne Zertifikat steht das Schlüsselmaterial im Klartext neben der Datenbank. Wer einen
/// Volume-Abzug oder ein Backup in die Hand bekommt, hat damit auch die Upstream-Zugangsdaten.
/// </para>
/// <para>
/// Bewusst eine <b>Warnung</b> und kein Fehler: Der Gateway läuft so, und für eine Einzelinstanz
/// mit restriktiven Verzeichnisrechten ist es eine vertretbare Entscheidung. Sie soll nur
/// getroffen und nicht vorgefunden werden.
/// </para>
/// </summary>
public sealed class KeyRingProtectionCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.KeyRingUnprotected;

    public DiagnosticScope Scope => DiagnosticScope.KeyRing;

    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.KeyRingCertificatePath is not null)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code,
                "Der Key-Ring wird mit einem Zertifikat verschlüsselt (BIFROST_KEYRING_CERT_PATH ist gesetzt).",
                CheckOutcome.Details(("geschuetzt", "ja"))));
        }

        return Task.FromResult(CheckOutcome.Warning(
            Code,
            $"Der Key-Ring unter '{context.KeyRingDirectory}' liegt ungeschützt. Er entschlüsselt "
            + "die gespeicherten Upstream-Zugangsdaten.",
            "Entweder das Verzeichnis restriktiv halten (nur der Gateway-Benutzer) oder den Key-Ring "
            + "mit einem PFX verschlüsseln: BIFROST_KEYRING_CERT_PATH und "
            + "BIFROST_KEYRING_CERT_PASSWORD (docs/operations.md, Abschnitt 'Key-Ring schützen'). "
            + "Das Zertifikat getrennt vom Datenverzeichnis sichern — ohne es sind die Zugangsdaten "
            + "unbrauchbar.",
            CheckOutcome.Details(
                ("verzeichnis", context.KeyRingDirectory),
                ("geschuetzt", "nein"))));
    }
}

/// <summary>
/// BFR-KEY-0003 — das konfigurierte Zertifikat liegt auch dort, wo es stehen soll.
/// <para>
/// Fehlt die Datei, bricht der Start ab — der Zertifikatsladevorgang passiert vor dem ersten
/// Request. Das ist ein Fehler und keine Warnung.
/// </para>
/// </summary>
public sealed class KeyRingCertificateCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.KeyRingCertificate;

    public DiagnosticScope Scope => DiagnosticScope.KeyRing;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.KeyRingCertificatePath;
        if (path is null)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code, "Kein Key-Ring-Zertifikat konfiguriert (siehe " + DiagnosticCodes.KeyRingUnprotected + ")."));
        }

        // Nur der Pfad, nie das Passwort: BIFROST_KEYRING_CERT_PASSWORD wird hier nicht angefasst.
        var details = CheckOutcome.Details(("pfad", path));
        return Task.FromResult(context.Files.FileExists(path)
            ? CheckOutcome.Pass(Code, $"Das Key-Ring-Zertifikat '{path}' ist vorhanden.", details)
            : CheckOutcome.Fail(
                Code,
                $"BIFROST_KEYRING_CERT_PATH zeigt auf '{path}', dort liegt keine Datei.",
                "Der Start bricht damit ab. Pfad prüfen; unter Compose muss das Secret deklariert "
                + "UND die Datei vorhanden sein, sonst scheitert schon 'up'.",
                details));
    }
}
