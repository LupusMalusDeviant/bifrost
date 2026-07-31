using Bifrost.Core.Diagnostics.Checks;

namespace Bifrost.Server.KeyRing;

/// <summary>
/// Die Betriebsarten des Key-Ring-Schutzes (WP3.3).
/// <para>
/// Der Key-Ring entschlüsselt <b>sämtliche</b> Upstream-Zugangsdaten, OAuth-Token und
/// Webhook-Secrets dieser Instanz. Wie er geschützt wird, war bisher ein einzelner Pfad in einer
/// Umgebungsvariablen — gesetzt oder nicht. Das reicht nicht: „nicht gesetzt" kann heißen
/// <i>„bewusst ohne Zertifikat, das Verzeichnis ist restriktiv"</i> oder <i>„daran hat nie jemand
/// gedacht"</i>, und diese beiden Lagen dürfen im Bericht nicht gleich aussehen.
/// </para>
/// </summary>
public enum KeyRingProtectionMode
{
    /// <summary>
    /// Es wurde nichts erklärt. Der Ring liegt im Klartext neben der Datenbank — aber niemand hat
    /// das entschieden. <b>Kein gültiger Betriebsmodus</b>, sondern die Abwesenheit einer Wahl.
    /// </summary>
    Undeclared = 0,

    /// <summary>PFX-Zertifikat; das Passwort kommt aus der Konfiguration (Umgebungsvariable).</summary>
    Certificate = 1,

    /// <summary>
    /// PFX-Zertifikat; das Passwort kommt aus einer <b>Datei</b> (Container-/Compose-Secret).
    /// Das ist FR-P048: Das Passwort steht dann weder in <c>.env</c> noch in <c>docker inspect</c>.
    /// </summary>
    FileSecret = 2,

    /// <summary>
    /// Ausdrücklich ungeschützt. Eine legitime Wahl für eine Einzelinstanz mit restriktiven
    /// Verzeichnisrechten — aber eine <b>Wahl</b>, die getroffen und nicht vorgefunden wird.
    /// </summary>
    None = 3,
}

/// <summary>Die Namen der Einstellungen. Stabil — Runbooks und Compose-Dateien zeigen darauf.</summary>
public static class KeyRingSwitch
{
    // Die Namen selbst stehen in Bifrost.Core (KeyRingLayout) — die Diagnose braucht sie und kann
    // keine Serverreferenz halten. Hier stehen sie als Weiterleitung, damit ein umbenannter Schalter
    // nicht an einer der beiden Stellen zurueckbleibt.

    /// <summary>Die ausdrückliche Erklärung des Betriebsmodus.</summary>
    public const string Protection = KeyRingLayout.ProtectionSetting;

    /// <summary>Pfad des PFX, mit dem <b>neue</b> Schlüssel verschlüsselt werden.</summary>
    public const string CertificatePath = KeyRingLayout.CertificatePathSetting;

    /// <summary>Passwort des PFX. Mit <c>_FILE</c>-Suffix auch als Datei-Secret (FR-P048).</summary>
    public const string CertificatePassword = KeyRingLayout.CertificatePasswordSetting;

    /// <summary>
    /// Das <b>vorherige</b> Zertifikat. Es verschlüsselt nichts mehr, entschlüsselt aber weiterhin —
    /// ohne diese Angabe wäre jeder Zertifikatswechsel ein Totalverlust des Altmaterials.
    /// </summary>
    public const string PreviousCertificatePath = KeyRingLayout.PreviousCertificatePathSetting;

    /// <summary>Passwort des vorherigen PFX; ebenfalls mit <c>_FILE</c>-Suffix.</summary>
    public const string PreviousCertificatePassword = KeyRingLayout.PreviousCertificatePasswordSetting;

    /// <summary>Wert für <see cref="Protection"/>: Zertifikat, Passwort aus der Umgebung.</summary>
    public const string CertificateValue = KeyRingLayout.CertificateMode;

    /// <summary>Wert für <see cref="Protection"/>: Zertifikat, Passwort aus einer Datei.</summary>
    public const string FileSecretValue = KeyRingLayout.FileSecretMode;

    /// <summary>Wert für <see cref="Protection"/>: ausdrücklich ungeschützt.</summary>
    public const string NoneValue = KeyRingLayout.NoneMode;

    /// <summary>Der geschriebene Name eines Modus — für Meldungen, Diagnose und die Zeugendatei.</summary>
    public static string Format(KeyRingProtectionMode mode) => mode switch
    {
        KeyRingProtectionMode.Certificate => CertificateValue,
        KeyRingProtectionMode.FileSecret => FileSecretValue,
        KeyRingProtectionMode.None => NoneValue,
        _ => "undeclared",
    };

    /// <summary>
    /// Liest die Erklärung. <c>null</c> heißt „nichts erklärt"; ein <b>unbekannter</b> Wert ist ein
    /// Fehler und kein „dann eben nichts" — sonst führte ein Tippfehler (<c>zertifikat</c>) genau in
    /// den ungeschützten Betrieb, den der Betreiber gerade abwählen wollte.
    /// </summary>
    public static KeyRingProtectionMode? ParseDeclared(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            CertificateValue => KeyRingProtectionMode.Certificate,
            FileSecretValue => KeyRingProtectionMode.FileSecret,
            NoneValue or "unprotected" => KeyRingProtectionMode.None,
            _ => throw new KeyRingConfigurationException(
                $"{Protection}='{raw}' ist kein bekannter Betriebsmodus. Erlaubt sind "
                + $"'{CertificateValue}', '{FileSecretValue}' und '{NoneValue}'."),
        };
    }
}

/// <summary>
/// Die aufgelöste Key-Ring-Konfiguration dieser Instanz.
/// <para>
/// <b>Bewusst kein <c>record</c>:</b> Ein Record erzeugt ein <c>ToString()</c>, das jede Eigenschaft
/// ausgibt — und dieses Objekt trägt das PFX-Passwort. Ein einziges <c>LogDebug("{Settings}")</c>
/// hätte damit das Passwort im Protokoll stehen. Die Ausgabe unten nennt Modus und Herkunft, nie
/// einen Geheimwert.
/// </para>
/// </summary>
public sealed class KeyRingSettings
{
    private KeyRingSettings(
        KeyRingProtectionMode mode,
        KeyRingProtectionMode? declared,
        string? certificatePath,
        SecretValue password,
        string? previousCertificatePath,
        SecretValue previousPassword)
    {
        Mode = mode;
        Declared = declared;
        CertificatePath = certificatePath;
        PreviousCertificatePath = previousCertificatePath;
        PasswordSource = password.Source;
        PreviousPasswordSource = previousPassword.Source;
        Password = password.Value;
        PreviousPassword = previousPassword.Value;
    }

    /// <summary>Der aufgelöste Modus — das, was tatsächlich gilt.</summary>
    public KeyRingProtectionMode Mode { get; }

    /// <summary>Was der Betreiber erklärt hat, oder <c>null</c>.</summary>
    public KeyRingProtectionMode? Declared { get; }

    public string? CertificatePath { get; }

    public string? PreviousCertificatePath { get; }

    /// <summary>Woher das Passwort kam. Der <b>Wert</b> verlässt dieses Objekt nie Richtung Ausgabe.</summary>
    public SecretSource PasswordSource { get; }

    public SecretSource PreviousPasswordSource { get; }

    internal string? Password { get; }

    internal string? PreviousPassword { get; }

    /// <summary>Wird der Ring verschlüsselt abgelegt?</summary>
    public bool IsProtected
        => Mode is KeyRingProtectionMode.Certificate or KeyRingProtectionMode.FileSecret;

    /// <summary>Modus und Herkunft — ohne Pfad, ohne Passwort. Was ins Protokoll darf.</summary>
    public override string ToString()
        => $"Key-Ring-Schutz: {KeyRingSwitch.Format(Mode)}"
            + (IsProtected ? $", Passwortquelle {PasswordSource}" : string.Empty);

    /// <summary>
    /// Löst die Konfiguration auf. <paramref name="configuration"/> ist die Nachschlagefunktion —
    /// im Serverprozess <c>IConfiguration</c>, im Kommandozeilenweg die Prozessumgebung.
    /// </summary>
    /// <exception cref="KeyRingConfigurationException">
    /// Wenn die Angaben sich widersprechen. Ausdrücklich kein Raten: Bei zwei einander
    /// widersprechenden Aussagen ist jede stillschweigend gewählte Auflösung der Fehler.
    /// </exception>
    public static KeyRingSettings Resolve(Func<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var declared = KeyRingSwitch.ParseDeclared(configuration(KeyRingSwitch.Protection));
        var certificatePath = Clean(configuration(KeyRingSwitch.CertificatePath));
        var previousPath = Clean(configuration(KeyRingSwitch.PreviousCertificatePath));
        var password = FileSecret.Read(configuration, KeyRingSwitch.CertificatePassword);
        var previousPassword = FileSecret.Read(configuration, KeyRingSwitch.PreviousCertificatePassword);

        var mode = certificatePath is not null
            ? password.Source is SecretSource.File
                ? KeyRingProtectionMode.FileSecret
                : KeyRingProtectionMode.Certificate
            : declared is KeyRingProtectionMode.None
                ? KeyRingProtectionMode.None
                : KeyRingProtectionMode.Undeclared;

        // Die Erklärung ist eine Zusage und wird deshalb geprüft. Sie greift nur nach oben: Wer
        // 'certificate' erklärt und das Passwort per Datei zuführt, hat mehr getan als versprochen —
        // das ist kein Fehler. Umgekehrt schon.
        switch (declared)
        {
            case KeyRingProtectionMode.FileSecret when mode is not KeyRingProtectionMode.FileSecret:
                throw new KeyRingConfigurationException(
                    $"{KeyRingSwitch.Protection}={KeyRingSwitch.FileSecretValue} verlangt ein "
                    + $"Zertifikat ({KeyRingSwitch.CertificatePath}) UND ein Passwort aus einer Datei "
                    + $"({KeyRingSwitch.CertificatePassword}{FileSecret.Suffix}). "
                    + $"Vorgefunden: {Describe(certificatePath is not null, password.Source)}.");

            case KeyRingProtectionMode.Certificate when certificatePath is null:
                throw new KeyRingConfigurationException(
                    $"{KeyRingSwitch.Protection}={KeyRingSwitch.CertificateValue} verlangt "
                    + $"{KeyRingSwitch.CertificatePath}; es ist nicht gesetzt.");

            case KeyRingProtectionMode.None when certificatePath is not null:
                // Zwei Aussagen, die einander ausschließen. Welche gewinnt, darf nicht davon
                // abhängen, in welcher Reihenfolge dieser Code sie liest.
                throw new KeyRingConfigurationException(
                    $"{KeyRingSwitch.Protection}={KeyRingSwitch.NoneValue} und "
                    + $"{KeyRingSwitch.CertificatePath} widersprechen sich: Entweder der Ring ist "
                    + "ausdrücklich ungeschützt oder er wird mit einem Zertifikat verschlüsselt. "
                    + "Eine der beiden Angaben entfernen.");
        }

        if (previousPath is not null && certificatePath is null)
        {
            throw new KeyRingConfigurationException(
                $"{KeyRingSwitch.PreviousCertificatePath} ist gesetzt, "
                + $"{KeyRingSwitch.CertificatePath} aber nicht. Das vorherige Zertifikat "
                + "entschlüsselt nur — ohne ein aktuelles gäbe es nichts, womit neue Schlüssel "
                + "verschlüsselt würden.");
        }

        return new KeyRingSettings(mode, declared, certificatePath, password, previousPath, previousPassword);
    }

    /// <summary>Alle Zertifikatspfade, die <b>entschlüsseln</b> dürfen — aktuelles zuerst.</summary>
    public IReadOnlyList<string> DecryptionCertificatePaths
        => CertificatePath is null
            ? []
            : PreviousCertificatePath is null
                ? [CertificatePath]
                : [CertificatePath, PreviousCertificatePath];

    internal string? PasswordFor(string certificatePath)
        => string.Equals(certificatePath, PreviousCertificatePath, StringComparison.Ordinal)
            ? PreviousPassword
            : Password;

    private static string Describe(bool hasCertificate, SecretSource source)
        => (hasCertificate ? "Zertifikat gesetzt" : "kein Zertifikat")
            + ", Passwortquelle " + source switch
            {
                SecretSource.File => "Datei",
                SecretSource.Environment => "Konfiguration/Umgebung",
                _ => "keine",
            };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Die Key-Ring-Konfiguration ist widersprüchlich oder unvollständig.</summary>
public sealed class KeyRingConfigurationException : InvalidOperationException
{
    public KeyRingConfigurationException()
        : base("Die Key-Ring-Konfiguration ist nicht auflösbar.")
    {
    }

    public KeyRingConfigurationException(string message)
        : base(message)
    {
    }

    public KeyRingConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
