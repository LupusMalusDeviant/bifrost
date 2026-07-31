namespace Bifrost.Persistence.Backup;

/// <summary>
/// Die festen Namen und Grenzen des Archivformats v1 (ADR-0024 E1, M2-Vertrag §2).
/// <para>
/// Alles hier ist Formatvertrag: Wer einen dieser Werte ändert, ändert das Archivformat und braucht
/// eine neue <see cref="FormatVersion"/>. Die Grenzen (<see cref="MaxEntryCount"/> und Freunde) sind
/// dagegen Schutzwerte des Lesers — ein älteres Archiv bleibt gültig, wenn sie steigen.
/// </para>
/// </summary>
public static class BackupLayout
{
    /// <summary>Format des Archivs, nicht des Produkts.</summary>
    public const int FormatVersion = 1;

    public const string ManifestEntry = "manifest.json";
    public const string ChecksumEntry = "checksums.json";

    public const string DatabaseZone = "database/";
    public const string KeyRingZone = "keyring/";
    public const string PackagesZone = "packages/";
    public const string ConfigZone = "config/";

    /// <summary>Der Datenbankschnappschuss liegt immer unter demselben Namen — unabhängig davon,
    /// wie die Quelldatei auf der Platte heißt (v1.0 hieß sie <c>mcpmcp.db</c>).</summary>
    public const string DatabaseEntry = DatabaseZone + "bifrost.db";

    public const string InstanceConfigEntry = ConfigZone + "instance.json";

    public const string ChecksumAlgorithm = "sha-256";

    public const string EncryptionNone = "none";
    public const string EncryptionAesGcm = "aes-256-gcm";
    public const string Kdf = "pbkdf2-sha256";

    /// <summary>OWASP-Empfehlung für PBKDF2-SHA256; identisch zu <see cref="Pbkdf2Hasher"/>.</summary>
    public const int KdfIterations = 600_000;

    /// <summary>
    /// Unterhalb dieser Produktversion verweigert der Restore (ADR-0024 E6). 0.11.0 ist die erste
    /// Version mit migrationsverwaltetem Schema — ältere Instanzen haben keine Migrationshistorie,
    /// in die sich ein Archiv einfügen ließe.
    /// </summary>
    public const string DefaultMinimumRestoreVersion = "0.11.0";

    // ── Schutzgrenzen gegen Dekompressionsbomben (ADR-0024 E5) ──────────────────────────────────
    public const int MaxEntryCount = 20_000;

    /// <summary>Obergrenze der entpackten Gesamtgröße. 8 GiB ist großzügig für eine Gateway-Instanz
    /// und immer noch weit unter dem, was eine Bombe anrichten will.</summary>
    public const long MaxTotalUncompressedBytes = 8L * 1024 * 1024 * 1024;

    public const long MaxEntryUncompressedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Verhältnis entpackt:gepackt, ab dem ein Eintrag als Bombe gilt. Erst ab
    /// <see cref="RatioCheckThresholdBytes"/> geprüft, weil kleine Einträge naturgemäß gut komprimieren.</summary>
    public const long MaxCompressionRatio = 500;

    public const long RatioCheckThresholdBytes = 1024 * 1024;

    /// <summary>Die einzigen Präfixe, unter denen ein Eintrag liegen darf.</summary>
    public static readonly string[] AllowedZones =
        [DatabaseZone, KeyRingZone, PackagesZone, ConfigZone];

    public static string ZoneOf(BackupSectionKind kind) => kind switch
    {
        BackupSectionKind.Database => DatabaseZone,
        BackupSectionKind.KeyRing => KeyRingZone,
        BackupSectionKind.Packages => PackagesZone,
        BackupSectionKind.Config => ConfigZone,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>Ein Bereich als Einzelwert — <c>BackupSections</c> ist ein Flag-Enum und taugt nicht als Schlüssel.</summary>
public enum BackupSectionKind
{
    Database,
    KeyRing,
    Packages,
    Config,
}
