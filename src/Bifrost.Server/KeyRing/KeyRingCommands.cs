using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using Bifrost.Core.Diagnostics.Checks;

namespace Bifrost.Server.KeyRing;

/// <summary>
/// Die Kommandozeilenwege rund um den Key-Ring: einrichten, prüfen, einen Zertifikatswechsel
/// <b>vorher</b> durchspielen (WP3.3, Aufträge 3 und 4).
/// <para>
/// <b>Warum im Serverprozess und nicht in der CLI:</b> <c>bifrost</c> ist ein HTTP-Client — er
/// spricht mit einem <i>laufenden</i> Gateway. Genau der läuft hier aber nicht: Beim Einrichten gibt
/// es ihn noch nicht, und wenn der Key-Ring das Problem ist, kommt er nicht hoch. Außerdem entsteht
/// hier ein privater Schlüssel; ihn durch eine HTTP-Fassade zu schicken hieße, ihn durch jeden
/// Proxy und jeden Zwischenspeicher auf dem Weg zu tragen. Diese Wege laufen deshalb dort, wo die
/// Dateien liegen.
/// </para>
/// </summary>
public static class KeyRingCommands
{
    public const string Setup = "--keyring-setup";
    public const string Check = "--keyring-check";
    public const string Rotate = "--keyring-rotate";

    // Dieselbe Bedeutung wie bei 'bifrost doctor' (M2-Vertrag §4): 0 ok, 2 Bedienfehler,
    // 3 Warnung, 4 Befund. Ein Runbook muss nicht zwei Tabellen kennen.
    private const int Ok = 0;
    private const int Failed = 1;
    private const int Usage = 2;
    private const int Warning = 3;
    private const int Finding = 4;

    public const string UsageText =
        """
        Key-Ring (Exit-Codes: 0 ok · 1 Fehler · 2 Bedienfehler · 3 Warnung · 4 Befund):

          --keyring-setup [--cert <pfad>] [--password-file <pfad>] [--subject <CN=...>]
              Erzeugt ein selbstsigniertes PFX und die zugehoerige Passwortdatei, beide mit
              restriktiven Rechten (Unix 0600, Windows ACL ohne Vererbung). Ueberschreibt nie.

          --keyring-check
              Prueft die konfigurierte Lage: Betriebsart, Zertifikat, Rechte, Zeugeneintrag — und
              oeffnet den vorhandenen Ring probehalber auf einer KOPIE.

          --keyring-rotate --new-cert <pfad> [--new-password-file <pfad>]
              Spielt einen Zertifikatswechsel durch, BEVOR er stattfindet: Laesst sich der
              vorhandene Ring mit neuem UND altem Zertifikat noch oeffnen?

        Pfade und Rechte gelten auf dem Rechner, auf dem der Gateway laeuft.
        """;

    public static bool IsKeyRingCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(Setup) || args.Contains(Check) || args.Contains(Rotate);
    }

    /// <summary>Führt das erkannte Kommando aus und liefert den Rückgabewert des Prozesses.</summary>
    public static int Run(
        string[] args,
        Func<string, string?> configuration,
        string dataDirectory,
        TimeProvider time,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            if (args.Contains(Setup))
            {
                return RunSetup(args, dataDirectory, time, output);
            }

            return args.Contains(Rotate)
                ? RunRotate(args, configuration, dataDirectory, output, error)
                : RunCheck(configuration, dataDirectory, output);
        }
        catch (KeyRingConfigurationException exception)
        {
            error.WriteLine(exception.Message);
            return Finding;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(exception.Message);
            return Failed;
        }
    }

    // ── Einrichten ──────────────────────────────────────────────────────────────────────────────

    private static int RunSetup(
        string[] args, string dataDirectory, TimeProvider time, TextWriter output)
    {
        // Vorgabe: NEBEN das Datenverzeichnis, nicht hinein. Ein Zertifikat, das im gesicherten
        // Verzeichnis liegt, steckt in jedem Backup mit drin — und dann schuetzt es genau gegen
        // nichts mehr (ADR-0024 E3).
        var certificatePath = ArgumentAfter(args, "--cert")
            ?? Path.Combine(DefaultSecretsDirectory(dataDirectory), "keyring.pfx");
        var passwordPath = ArgumentAfter(args, "--password-file")
            ?? certificatePath + ".password";
        var subject = ArgumentAfter(args, "--subject") ?? KeyRingCertificates.DefaultSubject;

        var created = KeyRingCertificates.Create(certificatePath, passwordPath, time, subject);
        var certificatePermissions = SecretFilePermissions.Describe(created.CertificatePath);
        var passwordPermissions = SecretFilePermissions.Describe(created.PasswordPath);

        output.WriteLine($"Zertifikat erzeugt: {created.CertificatePath}");
        output.WriteLine($"  Fingerabdruck: {created.Thumbprint}");
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"  Gueltig bis:   {created.NotAfter:u}"));
        output.WriteLine($"  Rechte:        {certificatePermissions.Description}");
        output.WriteLine($"Passwortdatei:  {created.PasswordPath}");
        output.WriteLine($"  Rechte:        {passwordPermissions.Description}");
        output.WriteLine();
        output.WriteLine("Damit starten (Betriebsart 'file-secret' — das Passwort steht dann weder in");
        output.WriteLine(".env noch in 'docker inspect'):");
        output.WriteLine($"  {KeyRingSwitch.Protection}={KeyRingSwitch.FileSecretValue}");
        output.WriteLine($"  {KeyRingSwitch.CertificatePath}={created.CertificatePath}");
        output.WriteLine(
            $"  {KeyRingSwitch.CertificatePassword}{FileSecret.Suffix}={created.PasswordPath}");
        output.WriteLine();
        output.WriteLine(
            "Beide Dateien getrennt vom Datenverzeichnis sichern. Gehen sie verloren, ist der "
            + "Key-Ring nicht mehr zu oeffnen und saemtliche gespeicherten Zugangsdaten sind weg.");

        return certificatePermissions.Restricted && passwordPermissions.Restricted ? Ok : Warning;
    }

    // ── Prüfen ──────────────────────────────────────────────────────────────────────────────────

    private static int RunCheck(
        Func<string, string?> configuration, string dataDirectory, TextWriter output)
    {
        var settings = KeyRingSettings.Resolve(configuration);
        var keyRingDirectory = KeyRingDirectory.PathFor(dataDirectory);
        var keys = KeyRingDirectory.Read(keyRingDirectory);
        var witnessPath = KeyRingLayout.WitnessPathFor(dataDirectory);

        output.WriteLine($"Betriebsart:      {KeyRingSwitch.Format(settings.Mode)}"
            + (settings.Declared is null ? "  (nicht ausdruecklich erklaert)" : "  (erklaert)"));
        output.WriteLine($"Schluessel:       {KeyRingDirectory.Describe(keys)}");
        output.WriteLine($"Verzeichnis:      {keyRingDirectory}");
        output.WriteLine($"Zeugeneintrag:    {(File.Exists(witnessPath) ? "vorhanden" : "fehlt")}");

        var worst = Ok;
        if (settings.Mode is KeyRingProtectionMode.Undeclared)
        {
            output.WriteLine();
            output.WriteLine(
                $"WARNUNG: Es wurde keine Betriebsart erklaert. Der Ring liegt im Klartext. "
                + $"Entweder {KeyRingSwitch.CertificatePath} setzen oder den ungeschuetzten "
                + $"Betrieb ausdruecklich waehlen ({KeyRingSwitch.Protection}={KeyRingSwitch.NoneValue}).");
            worst = Warning;
        }

        if (settings.Mode is KeyRingProtectionMode.Certificate)
        {
            output.WriteLine();
            output.WriteLine(
                $"WARNUNG: Das Zertifikatspasswort steht in der Prozessumgebung. Es ist fuer jeden "
                + $"lesbar, der 'docker inspect' ausfuehren darf. Stattdessen "
                + $"{KeyRingSwitch.CertificatePassword}{FileSecret.Suffix} auf ein Datei-Secret "
                + $"zeigen lassen.");
            worst = Math.Max(worst, Warning);
        }

        var certificates = new List<X509Certificate2>();
        try
        {
            foreach (var path in settings.DecryptionCertificatePaths)
            {
                var inspection = KeyRingCertificates.Inspect(path, settings.PasswordFor(path));
                output.WriteLine();
                output.WriteLine($"Zertifikat {KeyRingLayout.ShortPath(path)}:");
                output.WriteLine($"  Ladbar:        {(inspection.Loadable ? "ja" : "nein")}");
                output.WriteLine($"  Privater Key:  {(inspection.HasPrivateKey ? "ja" : "nein")}");
                output.WriteLine($"  Fingerabdruck: {inspection.Thumbprint ?? "-"}");
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  Gueltig bis:   {inspection.NotAfter?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}"));
                output.WriteLine($"  Rechte:        {inspection.Permissions.Description}");
                if (inspection.Problem is not null)
                {
                    output.WriteLine($"  BEFUND:        {inspection.Problem}");
                    worst = Finding;
                }
                else if (!inspection.Permissions.Restricted)
                {
                    worst = Math.Max(worst, Warning);
                }

                if (inspection.Loadable)
                {
                    certificates.Add(X509CertificateLoader.LoadPkcs12FromFile(
                        path, settings.PasswordFor(path)));
                }
            }

            if (keys.Count > 0)
            {
                var report = KeyRingProbe.Read(keyRingDirectory, certificates);
                output.WriteLine();
                output.WriteLine($"Leseprobe:        {report.Describe()}");
                if (!report.AllReadable)
                {
                    worst = Finding;
                }
            }
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }

        return worst;
    }

    // ── Zertifikatswechsel durchspielen ─────────────────────────────────────────────────────────

    private static int RunRotate(
        string[] args,
        Func<string, string?> configuration,
        string dataDirectory,
        TextWriter output,
        TextWriter error)
    {
        var newPath = ArgumentAfter(args, "--new-cert");
        if (newPath is null)
        {
            error.WriteLine($"{Rotate} verlangt --new-cert <pfad>.");
            error.WriteLine(UsageText);
            return Usage;
        }

        var newPasswordFile = ArgumentAfter(args, "--new-password-file");
        var newPassword = newPasswordFile is null
            ? null
            : FileSecret.Read(
                name => name.EndsWith(FileSecret.Suffix, StringComparison.Ordinal) ? newPasswordFile : null,
                KeyRingSwitch.CertificatePassword).Value;

        var settings = KeyRingSettings.Resolve(configuration);
        var keyRingDirectory = KeyRingDirectory.PathFor(dataDirectory);

        var inspection = KeyRingCertificates.Inspect(newPath, newPassword);
        if (!inspection.Loadable || !inspection.HasPrivateKey)
        {
            error.WriteLine($"Das neue Zertifikat ist unbrauchbar: {inspection.Problem}");
            return Finding;
        }

        var certificates = new List<X509Certificate2>
        {
            X509CertificateLoader.LoadPkcs12FromFile(newPath, newPassword),
        };
        try
        {
            // Das ALTE bleibt in der Menge. Ohne es wäre das hier keine Probe, sondern die Frage,
            // ob der Ring schon kaputt ist — und die Antwort käme zu spät.
            certificates.AddRange(KeyRingCertificates.Load(settings));

            var report = KeyRingProbe.Read(keyRingDirectory, certificates);
            output.WriteLine($"Neues Zertifikat: {KeyRingLayout.ShortPath(newPath)}");
            output.WriteLine($"  Fingerabdruck:  {inspection.Thumbprint}");
            output.WriteLine($"  Rechte:         {inspection.Permissions.Description}");
            output.WriteLine($"Leseprobe:        {report.Describe()}");

            if (!report.AllReadable)
            {
                output.WriteLine();
                output.WriteLine(
                    "NICHT UMSTELLEN. Mit dieser Zertifikatslage waere der vorhandene Ring nicht "
                    + "vollstaendig zu oeffnen — der Wechsel wuerde die Instanz unlesbar machen.");
                return Finding;
            }

            output.WriteLine();
            output.WriteLine("Der Wechsel ist gefahrlos. Dafuer setzen:");
            output.WriteLine($"  {KeyRingSwitch.CertificatePath}={Path.GetFullPath(newPath)}");
            if (settings.CertificatePath is not null)
            {
                output.WriteLine(
                    $"  {KeyRingSwitch.PreviousCertificatePath}={Path.GetFullPath(settings.CertificatePath)}");
            }

            if (newPasswordFile is not null)
            {
                output.WriteLine(
                    $"  {KeyRingSwitch.CertificatePassword}{FileSecret.Suffix}={Path.GetFullPath(newPasswordFile)}");
            }

            output.WriteLine();
            output.WriteLine(
                "Das vorherige Zertifikat bleibt noetig, solange auch nur ein Schluessel damit "
                + "verschluesselt ist. DataProtection verschluesselt bestehende Schluessel nicht "
                + "nach — sie werden erst mit der Zeit durch neue abgeloest.");
            return Ok;
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    // ── Kleinkram ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Neben dem Datenverzeichnis, nicht darin — siehe Kommentar in <c>RunSetup</c>.</summary>
    private static string DefaultSecretsDirectory(string dataDirectory)
    {
        var full = Path.GetFullPath(dataDirectory);
        var parent = Path.GetDirectoryName(full);
        return parent is null ? Path.Combine(full, "secrets") : Path.Combine(parent, "secrets");
    }

    private static string? ArgumentAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length
            && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1]
                : null;
    }
}
