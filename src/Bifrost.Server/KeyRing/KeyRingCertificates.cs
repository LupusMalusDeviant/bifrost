using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Bifrost.Core.Diagnostics.Checks;

namespace Bifrost.Server.KeyRing;

/// <summary>Das Ergebnis von <see cref="KeyRingCertificates.Create"/>.</summary>
/// <param name="CertificatePath">Wohin das PFX geschrieben wurde.</param>
/// <param name="PasswordPath">
/// Wohin das Passwort geschrieben wurde — als Datei, damit es über
/// <c>BIFROST_KEYRING_CERT_PASSWORD_FILE</c> zugeführt werden kann und nicht in <c>.env</c> landet.
/// </param>
/// <param name="Thumbprint">Der Fingerabdruck, an dem sich ein Zertifikat wiedererkennen lässt.</param>
/// <param name="NotAfter">Ab wann es abgelaufen ist.</param>
public sealed record KeyRingCertificateCreation(
    string CertificatePath, string PasswordPath, string Thumbprint, DateTimeOffset NotAfter);

/// <summary>Was eine Prüfung über ein vorhandenes Zertifikat sagen kann.</summary>
/// <param name="Path">Der geprüfte Pfad.</param>
/// <param name="Loadable">Ließ sich das PFX mit dem angegebenen Passwort öffnen?</param>
/// <param name="HasPrivateKey">
/// Trägt es den privaten Schlüssel? Ein PFX ohne ihn kann verschlüsseln, aber <b>nicht
/// entschlüsseln</b> — der Ring wäre nach dem nächsten Neustart unlesbar, und zwar erst dann.
/// </param>
/// <param name="Thumbprint">Fingerabdruck, wenn ladbar.</param>
/// <param name="NotAfter">Ablauf, wenn ladbar.</param>
/// <param name="Permissions">Wie streng die Datei abgeschirmt ist.</param>
/// <param name="Problem">Der Grund, wenn etwas nicht stimmt.</param>
public sealed record KeyRingCertificateInspection(
    string Path,
    bool Loadable,
    bool HasPrivateKey,
    string? Thumbprint,
    DateTimeOffset? NotAfter,
    SecretFilePermissionState Permissions,
    string? Problem);

/// <summary>
/// Erzeugen und Prüfen des Zertifikats, mit dem der Key-Ring verschlüsselt wird (WP3.3, Auftrag 3).
/// <para>
/// <b>Warum das ins Produkt gehört und nicht in die Dokumentation:</b> Der bisherige Weg war eine
/// <c>openssl</c>-Zeile in <c>docs/operations.md</c>. Sie erzeugt ein brauchbares Zertifikat — und
/// eine Datei mit den Standardrechten des Systems, in einem Verzeichnis, das oft genug das
/// Datenverzeichnis ist. Beides zusammen macht den Schutz zunichte, ohne dass irgendetwas davon
/// sichtbar wäre. Ein Setup-Weg, der die Rechte selbst setzt und das Ergebnis anschließend nachliest,
/// ist der Unterschied zwischen einer Anleitung und einer Zusage.
/// </para>
/// </summary>
public static class KeyRingCertificates
{
    /// <summary>Vorgabe-Laufzeit. Lang, weil ein Ablauf hier den Ring unlesbar macht, nicht nur eine Verbindung.</summary>
    public static TimeSpan DefaultLifetime { get; } = TimeSpan.FromDays(3650);

    /// <summary>Der Vorgabe-Antragstellername.</summary>
    public const string DefaultSubject = "CN=bifrost-keyring";

    /// <summary>
    /// Erzeugt ein selbstsigniertes PFX <b>und</b> die zugehörige Passwortdatei, beide mit
    /// restriktiven Rechten.
    /// </summary>
    /// <exception cref="IOException">Wenn eine der beiden Dateien schon existiert.</exception>
    public static KeyRingCertificateCreation Create(
        string certificatePath,
        string passwordPath,
        TimeProvider time,
        string subject = DefaultSubject,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordPath);
        ArgumentNullException.ThrowIfNull(time);

        var directory = Path.GetDirectoryName(Path.GetFullPath(certificatePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var now = time.GetUtcNow();
        var notAfter = now + (lifetime ?? DefaultLifetime);

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            new X500DistinguishedName(subject), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        // Der Ring wird mit dem öffentlichen Schlüssel verschlüsselt und mit dem privaten wieder
        // geöffnet — Key/DataEncipherment ist das, was DataProtection hier tatsächlich tut.
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // Eine Stunde Vorlauf: Zwischen zwei Rechnern mit leicht auseinanderlaufenden Uhren wäre ein
        // frisch erzeugtes Zertifikat sonst „noch nicht gültig".
        using var certificate = request.CreateSelfSigned(now.AddHours(-1), notAfter);

        var password = GeneratePassword();
        var pfx = certificate.Export(X509ContentType.Pfx, password);

        SecretFilePermissions.WriteRestricted(certificatePath, pfx);
        try
        {
            SecretFilePermissions.WriteRestricted(
                passwordPath, System.Text.Encoding.UTF8.GetBytes(password));
        }
        catch
        {
            // Ein PFX ohne sein Passwort ist eine Datei, die niemand mehr benutzen kann. Entweder
            // beides oder nichts.
            TryDelete(certificatePath);
            throw;
        }

        return new KeyRingCertificateCreation(
            Path.GetFullPath(certificatePath),
            Path.GetFullPath(passwordPath),
            certificate.Thumbprint,
            notAfter);
    }

    /// <summary>
    /// Prüft ein vorhandenes PFX, ohne etwas zu verändern. Wirft nicht — jeder Fehlschlag ist ein
    /// Befund und wird als solcher zurückgegeben.
    /// </summary>
    public static KeyRingCertificateInspection Inspect(string certificatePath, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);

        var permissions = SecretFilePermissions.Describe(certificatePath);
        if (!File.Exists(certificatePath))
        {
            return new KeyRingCertificateInspection(
                certificatePath, false, false, null, null, permissions,
                $"Am Pfad '{KeyRingLayout.ShortPath(certificatePath)}' liegt keine Datei.");
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password);
            return new KeyRingCertificateInspection(
                certificatePath,
                true,
                certificate.HasPrivateKey,
                certificate.Thumbprint,
                certificate.NotAfter,
                permissions,
                certificate.HasPrivateKey
                    ? null
                    : "Das PFX enthält keinen privaten Schlüssel. Damit lässt sich der Key-Ring "
                        + "verschlüsseln, aber nie wieder öffnen.");
        }
        catch (CryptographicException)
        {
            // Die Ausnahme selbst nennt gern den Pfad; der Text hier tut es nur gekürzt.
            return new KeyRingCertificateInspection(
                certificatePath, false, false, null, null, permissions,
                $"Das PFX '{KeyRingLayout.ShortPath(certificatePath)}' ließ sich nicht öffnen — "
                + "falsches oder fehlendes Passwort, oder die Datei ist kein PKCS#12.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new KeyRingCertificateInspection(
                certificatePath, false, false, null, null, permissions,
                $"Das PFX '{KeyRingLayout.ShortPath(certificatePath)}' ist nicht lesbar "
                + $"({exception.GetType().Name}).");
        }
    }

    /// <summary>
    /// Lädt die Zertifikate dieser Konfiguration — aktuelles zuerst, danach das vorherige.
    /// </summary>
    /// <exception cref="KeyRingConfigurationException">
    /// Wenn eines fehlt, sich nicht öffnen lässt oder keinen privaten Schlüssel trägt. Der Start
    /// bricht dann ab, und das ist richtig so: Ein Gateway, der mit einem unbrauchbaren Zertifikat
    /// hochkommt, legt beim ersten Zugriff einen neuen Schlüssel an.
    /// </exception>
    public static IReadOnlyList<X509Certificate2> Load(KeyRingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var loaded = new List<X509Certificate2>();
        try
        {
            foreach (var path in settings.DecryptionCertificatePaths)
            {
                var inspection = Inspect(path, settings.PasswordFor(path));
                if (!inspection.Loadable || !inspection.HasPrivateKey)
                {
                    throw new KeyRingConfigurationException(
                        inspection.Problem ?? $"'{KeyRingLayout.ShortPath(path)}' ist unbrauchbar.");
                }

                loaded.Add(X509CertificateLoader.LoadPkcs12FromFile(path, settings.PasswordFor(path)));
            }
        }
        catch
        {
            foreach (var certificate in loaded)
            {
                certificate.Dispose();
            }

            throw;
        }

        return loaded;
    }

    /// <summary>Ein Passwort, das niemand tippen muss — es lebt in einer Datei mit 0600.</summary>
    private static string GeneratePassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(33))
            .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
