using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics.Checks;

/// <summary>
/// Die Namen und Orte rund um den Key-Ring — <b>eine</b> Quelle für beide Seiten.
/// <para>
/// Sie stehen in <c>Bifrost.Core</c>, weil die Diagnose sie braucht und die Diagnose keine
/// Serverreferenz halten kann. Der Serverprozess leitet seine eigenen Konstanten hiervon ab
/// (<c>Bifrost.Server.KeyRing.KeyRingSwitch</c>), damit ein umbenannter Schalter nicht an einer der
/// beiden Stellen zurückbleibt — ein Bericht über eine Einstellung, die es nicht mehr gibt, ist
/// schlimmer als keiner.
/// </para>
/// </summary>
public static class KeyRingLayout
{
    /// <summary>Die ausdrückliche Erklärung des Betriebsmodus.</summary>
    public const string ProtectionSetting = "BIFROST_KEYRING_PROTECTION";

    public const string CertificatePathSetting = "BIFROST_KEYRING_CERT_PATH";

    public const string CertificatePasswordSetting = "BIFROST_KEYRING_CERT_PASSWORD";

    public const string PreviousCertificatePathSetting = "BIFROST_KEYRING_CERT_PATH_PREVIOUS";

    public const string PreviousCertificatePasswordSetting = "BIFROST_KEYRING_CERT_PASSWORD_PREVIOUS";

    /// <summary>Das Namenssuffix für Datei-/Container-Secrets (FR-P048).</summary>
    public const string FileSuffix = "_FILE";

    public const string CertificateMode = "certificate";

    public const string FileSecretMode = "file-secret";

    public const string NoneMode = "none";

    /// <summary>Das Namensmuster, unter dem DataProtection seine Schlüssel ablegt.</summary>
    public const string KeyFilePattern = "key-*.xml";

    /// <summary>Was diese Instanz zuletzt über ihren Key-Ring wusste.</summary>
    public const string WitnessFileName = "keyring.json";

    /// <summary>Der Zeugeneintrag liegt bei den übrigen Instanzangaben unter <c>config/</c>.</summary>
    public static string WitnessPathFor(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, "config", WitnessFileName);
    }

    /// <summary>
    /// Ein Pfad auf <b>Schlüsselmaterial</b>, gekürzt auf den Dateinamen.
    /// <para>
    /// Der Bericht nennt die Verzeichnisse <i>dieser Instanz</i> (Datenverzeichnis, Key-Ring) — die
    /// kennt ein Betreiber ohnehin, und ohne sie wäre die Diagnose nutzlos. Er nennt aber <b>nie</b>
    /// den vollen Ort von Schlüsselmaterial außerhalb davon: Wo ein PFX oder eine Passwortdatei
    /// liegt, ist die erste Angabe, die jemand braucht, der an den Key-Ring will. Der Dateiname
    /// genügt, um zu erkennen, welche Datei gemeint ist; der Weg dorthin steht in der Konfiguration
    /// und muss nicht auch noch im Bericht stehen.
    /// </para>
    /// </summary>
    public static string ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(nicht gesetzt)";
        }

        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? "…" : "…/" + name;
    }
}

/// <summary>
/// Die Key-Ring-Lage, wie ein Check sie aus der Umgebung ablesen kann — ohne Datenbank, ohne
/// Zertifikat zu öffnen und <b>ohne zu werfen</b>: Ein Diagnosebericht, der an einer
/// widersprüchlichen Konfiguration abstürzt, verschweigt genau den Befund, wegen dem er läuft.
/// </summary>
internal sealed record KeyRingView(
    string DataDirectory,
    string KeyRingDirectory,
    string? CertificatePath,
    string? PreviousCertificatePath,
    bool PasswordFromFile,
    string? PasswordFilePath,
    bool PasswordFromEnvironment,
    string? DeclaredMode)
{
    public bool HasCertificate => CertificatePath is not null;

    /// <summary>Der aufgelöste Modus, nach denselben Regeln wie im Serverprozess.</summary>
    public string ResolvedMode => HasCertificate
        ? PasswordFromFile ? KeyRingLayout.FileSecretMode : KeyRingLayout.CertificateMode
        : IsDeclaredNone ? KeyRingLayout.NoneMode : "undeclared";

    public bool IsDeclaredNone => string.Equals(
        DeclaredMode, KeyRingLayout.NoneMode, StringComparison.OrdinalIgnoreCase)
        || string.Equals(DeclaredMode, "unprotected", StringComparison.OrdinalIgnoreCase);

    public string WitnessPath => KeyRingLayout.WitnessPathFor(DataDirectory);

    public static KeyRingView From(DiagnosticContext context)
    {
        var passwordFile = context.Value(
            KeyRingLayout.CertificatePasswordSetting + KeyRingLayout.FileSuffix);

        return new KeyRingView(
            context.DataDirectory,
            context.KeyRingDirectory,
            context.Value(KeyRingLayout.CertificatePathSetting),
            context.Value(KeyRingLayout.PreviousCertificatePathSetting),
            passwordFile is not null,
            passwordFile,
            context.Value(KeyRingLayout.CertificatePasswordSetting) is not null,
            context.Value(KeyRingLayout.ProtectionSetting));
    }
}

/// <summary>
/// BFR-KEY-0001 — der Key-Ring ist da und nicht leer.
/// <para>
/// Er entschlüsselt die at-rest verschlüsselten Upstream-Zugangsdaten, die OAuth-Token und die
/// Webhook-Secrets. Fehlt er auf einer bestehenden Installation, ist die Datenbank zwar lesbar,
/// aber jedes darin gespeicherte Geheimnis unbrauchbar — und der Gateway startete früher trotzdem.
/// Seit WP3.3 tut er das nicht mehr; dieser Check ist der Blick von außen auf dieselbe Lage.
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
                + "Key-Ring aus der Sicherung zurückholen, bevor der Gateway startet. Ob dieser "
                + "Instanz Material fehlt, sagt " + DiagnosticCodes.KeyRingLoss + ".",
                details));
        }

        var keys = context.Files.ListFiles(directory, KeyRingLayout.KeyFilePattern);
        if (keys.Count == 0)
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                $"Das Key-Ring-Verzeichnis '{directory}' enthält keinen Schlüssel.",
                "Beim ersten Start entsteht einer. Auf einer bestehenden Installation heisst ein "
                + "leeres Verzeichnis: Der Key-Ring ist weg und die gespeicherten Zugangsdaten sind "
                + "nicht mehr entschlüsselbar — siehe " + DiagnosticCodes.KeyRingLoss + ".",
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
/// BFR-KEY-0002 — in welcher Betriebsart läuft der Schutz des Key-Rings?
/// <para>
/// Drei Betriebsarten sind eine Wahl: <c>certificate</c>, <c>file-secret</c> und ausdrücklich
/// <c>none</c>. Die vierte Lage — gar keine Erklärung — ist <b>keine</b> Betriebsart, sondern ihre
/// Abwesenheit, und nur sie ist eine Warnung. Ein ausdrücklich ungeschützter Ring besteht: Für eine
/// Einzelinstanz mit restriktiven Verzeichnisrechten ist das vertretbar, und eine Diagnose, die auf
/// einer korrekt eingerichteten Instanz nie grün wird, liest nach kurzer Zeit niemand mehr.
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

        var view = KeyRingView.From(context);
        var details = CheckOutcome.Details(
            ("modus", view.ResolvedMode),
            ("erklaert", view.DeclaredMode ?? "nein"),
            // Der Pfad NICHT: Wo das Zertifikat liegt, ist die erste Angabe, die jemand braucht, der
            // an den Key-Ring will. Dass eines wirkt, genügt hier.
            ("geschuetzt", DetailFormat.YesNo(view.HasCertificate)));

        if (view.HasCertificate)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code,
                view.PasswordFromFile
                    ? "Der Key-Ring wird mit einem Zertifikat verschlüsselt; dessen Passwort kommt "
                        + "aus einer Datei (Betriebsart 'file-secret')."
                    : "Der Key-Ring wird mit einem Zertifikat verschlüsselt (Betriebsart "
                        + "'certificate').",
                details));
        }

        if (view.IsDeclaredNone)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code,
                "Der Key-Ring liegt ungeschützt — ausdrücklich gewählt "
                + $"({KeyRingLayout.ProtectionSetting}={KeyRingLayout.NoneMode}). Die "
                + "Schlüsseldateien stehen im Klartext im Datenverzeichnis; jede Sicherung davon "
                + "enthält damit die Upstream-Zugangsdaten.",
                details));
        }

        return Task.FromResult(CheckOutcome.Warning(
            Code,
            $"Für den Key-Ring unter '{view.KeyRingDirectory}' wurde keine Betriebsart erklärt. Er "
            + "liegt im Klartext und entschlüsselt die gespeicherten Upstream-Zugangsdaten.",
            $"Eine der drei Betriebsarten wählen: {KeyRingLayout.CertificatePathSetting} setzen "
            + $"(Betriebsart '{KeyRingLayout.CertificateMode}'), zusätzlich das Passwort per "
            + $"{KeyRingLayout.CertificatePasswordSetting}{KeyRingLayout.FileSuffix} zuführen "
            + $"(Betriebsart '{KeyRingLayout.FileSecretMode}'), oder den ungeschützten Betrieb "
            + $"ausdrücklich wählen mit {KeyRingLayout.ProtectionSetting}={KeyRingLayout.NoneMode}. "
            + "Das Zertifikat getrennt vom Datenverzeichnis sichern — ohne es sind die Zugangsdaten "
            + "unbrauchbar (docs/operations.md, Abschnitt 'Key-Ring schützen').",
            details));
    }
}

/// <summary>
/// BFR-KEY-0003 — das konfigurierte Zertifikat liegt auch dort, wo es stehen soll.
/// <para>
/// Fehlt die Datei, bricht der Start ab — das Zertifikat wird geladen, bevor der erste Request
/// kommt. Das ist ein Fehler und keine Warnung.
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

        var view = KeyRingView.From(context);
        if (view.CertificatePath is null)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code, "Kein Key-Ring-Zertifikat konfiguriert (siehe " + DiagnosticCodes.KeyRingUnprotected + ")."));
        }

        // Nur der Dateiname, nie der Ort und nie das Passwort.
        var missing = new List<string>();
        foreach (var path in (string?[])[view.CertificatePath, view.PreviousCertificatePath])
        {
            if (path is not null && !context.Files.FileExists(path))
            {
                missing.Add(KeyRingLayout.ShortPath(path));
            }
        }

        var details = CheckOutcome.Details(
            ("zertifikat", KeyRingLayout.ShortPath(view.CertificatePath)),
            ("vorheriges", view.PreviousCertificatePath is null
                ? "keins"
                : KeyRingLayout.ShortPath(view.PreviousCertificatePath)));

        return Task.FromResult(missing.Count == 0
            ? CheckOutcome.Pass(
                Code,
                view.PreviousCertificatePath is null
                    ? $"Das Key-Ring-Zertifikat ({KeyRingLayout.ShortPath(view.CertificatePath)}) ist vorhanden."
                    : "Das Key-Ring-Zertifikat und das vorherige sind beide vorhanden — ein "
                        + "Zertifikatswechsel ist im Gange oder abgeschlossen.",
                details)
            : CheckOutcome.Fail(
                Code,
                $"Ein konfiguriertes Key-Ring-Zertifikat fehlt: {string.Join(", ", missing)}.",
                "Der Start bricht damit ab. Pfad prüfen; unter Compose muss das Secret deklariert "
                + "UND die Datei vorhanden sein, sonst scheitert schon 'up'.",
                details));
    }
}

/// <summary>
/// BFR-KEY-0004 — <b>fehlt Schlüsselmaterial, das vorhanden sein müsste?</b>
/// <para>
/// Der Zeugeneintrag <c>config/keyring.json</c> hält fest, dass diese Instanz schon einmal mit einem
/// Key-Ring gestartet ist. Liegt er vor und ist das Schlüsselverzeichnis trotzdem leer, ist der Ring
/// verloren — und mit ihm jedes gespeicherte Geheimnis. Genau dieser Ausfall hat beim
/// v0.11.0-Umstieg zugeschlagen: umbenanntes Volume, leere Ablage, Meldung „bereit".
/// </para>
/// <para>
/// Der Serverprozess bricht in dieser Lage den Start ab. Dieser Check ist der Weg, dasselbe von
/// außen zu sehen — <c>bifrost doctor</c> läuft auch dann, wenn der Gateway steht.
/// </para>
/// </summary>
public sealed class KeyRingLossCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.KeyRingLoss;

    public DiagnosticScope Scope => DiagnosticScope.KeyRing;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var view = KeyRingView.From(context);
        var witnessed = context.Files.FileExists(view.WitnessPath);
        var keys = context.Files.ListFiles(view.KeyRingDirectory, KeyRingLayout.KeyFilePattern);
        var details = CheckOutcome.Details(
            ("zeugeneintrag", DetailFormat.YesNo(witnessed)),
            ("schluesseldateien", DetailFormat.Count(keys.Count)));

        if (!witnessed)
        {
            return Task.FromResult(keys.Count > 0
                ? CheckOutcome.Pass(
                    Code,
                    "Es liegt Schlüsselmaterial vor. Der Zeugeneintrag fehlt noch — er entsteht beim "
                    + "nächsten Start dieser Instanz.",
                    details)
                : CheckOutcome.Skipped(
                    Code,
                    "Weder Schlüsselmaterial noch Zeugeneintrag. Aus dem Dateisystem allein ist "
                    + "das eine frische Installation; ob die Datenbank Geheimtext enthält, "
                    + "beantwortet erst der Start (der bricht dann ab)."));
        }

        if (keys.Count == 0)
        {
            return Task.FromResult(CheckOutcome.Fail(
                Code,
                "Diese Instanz hatte schon einmal einen Key-Ring, das Schlüsselverzeichnis ist aber "
                + "leer. Sämtliche gespeicherten Upstream-Zugangsdaten, OAuth-Token und "
                + "Webhook-Secrets sind damit nicht mehr entschlüsselbar.",
                "Der Start legt in dieser Lage KEINEN neuen Ring an, sondern bricht ab. Zuerst "
                + "prüfen, ob BIFROST_DATA_DIR auf das richtige Volume zeigt — ein umbenanntes "
                + "Volume sieht genau so aus. Andernfalls den Key-Ring aus der Sicherung "
                + "zurückspielen (ADR-0024: er liegt im Vollbackup unter 'keyring/').",
                details));
        }

        return Task.FromResult(CheckOutcome.Pass(
            Code,
            $"Schlüsselmaterial und Zeugeneintrag sind beide vorhanden ({keys.Count} Schlüssel).",
            details));
    }
}

/// <summary>
/// BFR-KEY-0005 — woher kommt das Passwort des Zertifikats? (FR-P048)
/// <para>
/// Ein Passwort in der Prozessumgebung steht in <c>.env</c>, in <c>docker inspect</c> und in
/// <c>/proc/&lt;pid&gt;/environ</c>. Zusammen mit einem PFX, das als Compose-Secret sauber eingehängt
/// ist, ergibt das einen Schutz, der genau an der schwächsten der beiden Angaben hängt.
/// </para>
/// </summary>
public sealed class KeyRingPasswordSourceCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.KeyRingPasswordSource;

    public DiagnosticScope Scope => DiagnosticScope.KeyRing;

    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var view = KeyRingView.From(context);
        if (!view.HasCertificate)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code, "Kein Key-Ring-Zertifikat konfiguriert — es gibt kein Passwort."));
        }

        var setting = KeyRingLayout.CertificatePasswordSetting;
        var fileSetting = setting + KeyRingLayout.FileSuffix;

        if (view.PasswordFromFile && view.PasswordFromEnvironment)
        {
            return Task.FromResult(CheckOutcome.Fail(
                Code,
                $"'{setting}' und '{fileSetting}' sind beide gesetzt.",
                "Es gibt bewusst keine Rangfolge zwischen ihnen — der Start bricht ab. Genau eine "
                + "der beiden Angaben entfernen.",
                CheckOutcome.Details(("quelle", "widersprüchlich"))));
        }

        if (view.PasswordFromFile)
        {
            var exists = view.PasswordFilePath is not null && context.Files.FileExists(view.PasswordFilePath);
            var details = CheckOutcome.Details(
                ("quelle", "datei"),
                ("datei", KeyRingLayout.ShortPath(view.PasswordFilePath)),
                ("vorhanden", DetailFormat.YesNo(exists)));

            return Task.FromResult(exists
                ? CheckOutcome.Pass(
                    Code,
                    $"Das Zertifikatspasswort kommt aus einer Datei ({fileSetting}) und steht damit "
                    + "weder in der Prozessumgebung noch in 'docker inspect'.",
                    details)
                : CheckOutcome.Fail(
                    Code,
                    $"'{fileSetting}' ist gesetzt, die Datei fehlt aber.",
                    "Der Start bricht ab. Unter Compose muss das Secret deklariert UND vorhanden "
                    + "sein.",
                    details));
        }

        if (view.PasswordFromEnvironment)
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                $"Das Zertifikatspasswort steht in der Konfiguration ({setting}) und damit im "
                + "Container in der Prozessumgebung.",
                $"Es ist für jeden lesbar, der 'docker inspect' ausführen darf — auch dann, wenn das "
                + $"PFX selbst sauber als Secret eingehängt ist. Stattdessen '{fileSetting}' auf ein "
                + "Datei-Secret zeigen lassen (docs/operations.md, 'Key-Ring schützen').",
                CheckOutcome.Details(("quelle", "umgebung"))));
        }

        return Task.FromResult(CheckOutcome.Pass(
            Code,
            "Für das Zertifikat ist kein Passwort konfiguriert; das PFX wird ohne geöffnet.",
            CheckOutcome.Details(("quelle", "keins"))));
    }
}
