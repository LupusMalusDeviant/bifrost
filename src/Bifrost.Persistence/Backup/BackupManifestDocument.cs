using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Bifrost.Abstractions.Operations;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Die serialisierte Fassung des Manifests (M2-Vertrag §2). Sie liegt <b>unverschlüsselt</b> als
/// erster Eintrag im Archiv: Ein Werkzeug muss beurteilen können, was es vor sich hat, bevor es
/// irgendetwas auspackt (ADR-0024 E1).
/// <para>
/// <b>Abweichung vom Vertragsbeispiel, bewusst und gemeldet:</b> Der Verschlüsselungsblock trägt
/// zusätzlich <c>salt</c>. Das Salz ist kein Geheimnis, muss aber beim Restore verfügbar sein,
/// bevor die Nutzlast angefasst wird — und ein Salz pro Eintrag hieße, die 600.000 PBKDF2-Runden
/// pro Datei zu bezahlen.
/// </para>
/// </summary>
internal sealed record BackupManifestDocument
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; init; } = "";

    [JsonPropertyName("minimumRestoreVersion")]
    public string MinimumRestoreVersion { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("instanceId")]
    public string InstanceId { get; init; } = "";

    [JsonPropertyName("database")]
    public DatabaseBlock Database { get; init; } = new();

    [JsonPropertyName("sections")]
    public IReadOnlyList<string> Sections { get; init; } = [];

    [JsonPropertyName("encryption")]
    public EncryptionBlock Encryption { get; init; } = new();

    [JsonPropertyName("checksumAlgorithm")]
    public string ChecksumAlgorithm { get; init; } = BackupLayout.ChecksumAlgorithm;

    internal sealed record DatabaseBlock
    {
        [JsonPropertyName("provider")]
        public string Provider { get; init; } = "sqlite";

        [JsonPropertyName("migration")]
        public string? Migration { get; init; }
    }

    internal sealed record EncryptionBlock
    {
        [JsonPropertyName("algorithm")]
        public string Algorithm { get; init; } = BackupLayout.EncryptionNone;

        [JsonPropertyName("kdf")]
        public string? Kdf { get; init; }

        [JsonPropertyName("iterations")]
        public int Iterations { get; init; }

        /// <summary>Base64. Kein Geheimnis — siehe Klassenkommentar.</summary>
        [JsonPropertyName("salt")]
        public string? Salt { get; init; }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    /// <summary>
    /// Liest das Manifest fail-closed: Was nicht eindeutig verstanden wird, gilt als ungültig — ein
    /// Archiv, dessen Kopf man nur ungefähr versteht, darf nicht angefasst werden.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out BackupManifestDocument? document, out string? problem)
    {
        document = null;
        problem = null;
        try
        {
            document = JsonSerializer.Deserialize<BackupManifestDocument>(utf8, SerializerOptions);
        }
        catch (JsonException ex)
        {
            problem = $"manifest.json ist kein gültiges JSON: {ex.Message}";
            return false;
        }

        if (document is null)
        {
            problem = "manifest.json ist leer.";
            return false;
        }

        if (document.FormatVersion <= 0)
        {
            problem = "manifest.json nennt keine Formatversion.";
            document = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(document.ProductVersion)
            || string.IsNullOrWhiteSpace(document.MinimumRestoreVersion))
        {
            problem = "manifest.json nennt keine Produkt- oder Mindestversion.";
            document = null;
            return false;
        }

        if (!TryParseProvider(document.Database.Provider, out _))
        {
            problem = $"manifest.json nennt einen unbekannten Datenbankanbieter: '{document.Database.Provider}'.";
            document = null;
            return false;
        }

        foreach (var section in document.Sections)
        {
            if (!TryParseSection(section, out _))
            {
                problem = $"manifest.json nennt einen unbekannten Bereich: '{section}'.";
                document = null;
                return false;
            }
        }

        if (!IsKnownAlgorithm(document.Encryption.Algorithm))
        {
            problem = $"manifest.json nennt ein unbekanntes Verschlüsselungsverfahren: '{document.Encryption.Algorithm}'.";
            document = null;
            return false;
        }

        return true;
    }

    private static bool IsKnownAlgorithm(string algorithm)
        => algorithm is BackupLayout.EncryptionNone or BackupLayout.EncryptionAesGcm;

    public bool IsEncrypted => Encryption.Algorithm == BackupLayout.EncryptionAesGcm;

    public BackupManifest ToContract()
    {
        // Ein unbekannter Anbieter ist beim Parsen bereits gescheitert; hier bleibt der Vorgabewert.
        _ = TryParseProvider(Database.Provider, out var provider);
        var sections = BackupSections.None;
        foreach (var name in Sections)
        {
            if (TryParseSection(name, out var section))
            {
                sections |= section;
            }
        }

        return new BackupManifest(
            FormatVersion,
            ProductVersion,
            MinimumRestoreVersion,
            CreatedAt,
            InstanceId,
            provider,
            Database.Migration,
            sections,
            IsEncrypted,
            ChecksumAlgorithm);
    }

    public static string SectionName(BackupSectionKind kind) => kind switch
    {
        BackupSectionKind.Database => "database",
        BackupSectionKind.KeyRing => "keyring",
        BackupSectionKind.Packages => "packages",
        BackupSectionKind.Config => "config",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string ProviderName(DatabaseProvider provider)
        => provider == DatabaseProvider.Postgres ? "postgres" : "sqlite";

    public static bool TryParseProvider(string? value, out DatabaseProvider provider)
    {
        switch (value?.ToUpperInvariant())
        {
            case "SQLITE":
                provider = DatabaseProvider.Sqlite;
                return true;
            case "POSTGRES":
                provider = DatabaseProvider.Postgres;
                return true;
            default:
                provider = DatabaseProvider.Sqlite;
                return false;
        }
    }

    public static bool TryParseSection(string? value, out BackupSections section)
    {
        switch (value?.ToUpperInvariant())
        {
            case "DATABASE":
                section = BackupSections.Database;
                return true;
            case "KEYRING":
                section = BackupSections.KeyRing;
                return true;
            case "PACKAGES":
                section = BackupSections.Packages;
                return true;
            case "CONFIG":
                section = BackupSections.Config;
                return true;
            default:
                section = BackupSections.None;
                return false;
        }
    }

    public string DescribeCreatedAt()
        => CreatedAt.ToString("u", CultureInfo.InvariantCulture);
}
