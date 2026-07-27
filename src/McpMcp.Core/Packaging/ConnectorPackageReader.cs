using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpMcp.Abstractions;

namespace McpMcp.Core.Packaging;

/// <summary>Ein geprüftes Paket: Manifest, Herausgeber und die verifizierten Dateien im Archiv.</summary>
public sealed record VerifiedConnectorPackage(
    ConnectorManifest Manifest,
    string ManifestSha256,
    PublisherKey Publisher,
    ConnectorTrustLevel TrustLevel);

/// <summary>
/// Liest und <b>verifiziert</b> ein Connector-Paket (<c>.mcpkg</c>, ADR-0016), bevor irgendetwas
/// davon die Platte berührt.
/// <para>
/// Ein Paket ist ein ZIP mit <c>manifest.json</c>, der zugehörigen Ed25519-Signatur
/// <c>manifest.sig</c> und den Nutzdateien. Signiert wird das Manifest, und das Manifest nennt den
/// SHA-256 <em>jeder</em> Nutzdatei. Eine Datei im Archiv, die im Manifest nicht vorkommt, ist
/// deshalb kein harmloser Beifang, sondern unsigniert — und wird abgewiesen.
/// </para>
/// <para>
/// Die Reihenfolge der Prüfungen ist Absicht: erst Archivgrenzen, dann Signatur, dann Manifest,
/// dann Hashes. Wer das Manifest vor der Signatur auswertet, trifft Entscheidungen auf Daten, die
/// noch niemand bestätigt hat.
/// </para>
/// </summary>
public static class ConnectorPackageReader
{
    /// <summary>Größtes zulässiges Archiv.</summary>
    public const long MaxArchiveBytes = 64 * 1024 * 1024;

    /// <summary>Größte zulässige Summe aller <em>entpackten</em> Dateien — Zip-Bomben-Grenze.</summary>
    public const long MaxUnpackedBytes = 256 * 1024 * 1024;

    /// <summary>Höchstzahl Einträge. Viele winzige Einträge sind auch ein Angriff.</summary>
    public const int MaxEntries = 256;

    /// <summary>Das Manifest ist Metadaten, kein Nutzinhalt.</summary>
    public const long MaxManifestBytes = 1024 * 1024;

    public const string ManifestEntry = "manifest.json";
    public const string SignatureEntry = "manifest.sig";

    private const int Ed25519SignatureBytes = 64;

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>
    /// Prüft das Paket vollständig gegen die aktiven Herausgeber. Wirft bei jedem Zweifel; der
    /// Rückgabewert bedeutet: Signatur gültig, Manifest schlüssig, alle Hashes stimmen.
    /// </summary>
    public static VerifiedConnectorPackage Verify(
        Stream package, IReadOnlyList<PublisherKey> publishers)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(publishers);

        using var archive = OpenArchive(package);
        var manifestBytes = ReadEntry(archive, ManifestEntry, MaxManifestBytes);
        var signature = ReadEntry(archive, SignatureEntry, Ed25519SignatureBytes);
        if (signature.Length != Ed25519SignatureBytes)
        {
            throw new ConnectorPackageException(
                $"'{SignatureEntry}' ist {signature.Length} Byte lang; eine Ed25519-Signatur hat "
                + $"{Ed25519SignatureBytes}.");
        }

        // Signatur ZUERST. Alles danach wertet Daten aus, hinter denen ein Herausgeber steht.
        var publisher = FindSigner(manifestBytes, signature, publishers);
        var manifest = ParseManifest(manifestBytes);

        // Das Manifest nennt seinen Herausgeber selbst — stimmt das nicht mit dem überein, der
        // wirklich signiert hat, ist eine der beiden Angaben falsch, und raten hilft hier nicht.
        if (!string.Equals(manifest.PublisherKeyId, publisher.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorPackageException(
                $"Das Manifest nennt den Herausgeber '{manifest.PublisherKeyId}', signiert hat aber "
                + $"'{publisher.KeyId}'.");
        }

        VerifyPayloads(archive, manifest);
        return new VerifiedConnectorPackage(
            manifest,
            Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            publisher,
            publisher.TrustLevel);
    }

    /// <summary>
    /// Packt die im Manifest deklarierten Dateien nach <paramref name="targetDirectory"/> aus.
    /// Erst nach <see cref="Verify"/> aufrufen — die Methode prüft nicht erneut, sie schreibt.
    /// </summary>
    public static void Extract(Stream package, ConnectorManifest manifest, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var root = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(root);
        using var archive = OpenArchive(package);

        // Auch das Manifest kommt mit auf die Platte: Ohne es ließe sich später nicht mehr
        // nachvollziehen, wogegen die Dateien einmal geprüft wurden.
        WriteFile(root, ManifestEntry, ReadEntry(archive, ManifestEntry, MaxManifestBytes));
        WriteFile(root, SignatureEntry, ReadEntry(archive, SignatureEntry, Ed25519SignatureBytes));
        foreach (var payload in manifest.Payloads)
        {
            WriteFile(root, payload.Path, ReadEntry(archive, payload.Path, MaxUnpackedBytes));
        }
    }

    private static ZipArchive OpenArchive(Stream package)
    {
        if (package.CanSeek)
        {
            if (package.Length > MaxArchiveBytes)
            {
                throw new ConnectorPackageException(
                    $"Paket überschreitet {MaxArchiveBytes / (1024 * 1024)} MB.");
            }

            package.Position = 0;
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new ConnectorPackageException($"Paket ist kein lesbares Archiv: {exception.Message}");
        }

        try
        {
            if (archive.Entries.Count > MaxEntries)
            {
                throw new ConnectorPackageException(
                    $"Paket hat {archive.Entries.Count} Einträge; erlaubt sind {MaxEntries}.");
            }

            long unpacked = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                EnsureSafeEntryName(entry.FullName);

                // Doppelte Namen: Welcher Eintrag gilt, entscheidet sonst die Zip-Implementierung.
                // Ein Angreifer könnte einen gehashten und einen ausgepackten Eintrag unterschieden
                // wissen wollen — genau das ist der Trick, und er endet hier.
                if (!seen.Add(entry.FullName))
                {
                    throw new ConnectorPackageException(
                        $"Eintrag '{entry.FullName}' kommt mehrfach vor — die Zuordnung wäre nicht eindeutig.");
                }

                unpacked += entry.Length;
                if (unpacked > MaxUnpackedBytes)
                {
                    throw new ConnectorPackageException(
                        $"Entpackt überschreitet das Paket {MaxUnpackedBytes / (1024 * 1024)} MB.");
                }
            }
        }
        catch
        {
            archive.Dispose();
            throw;
        }

        return archive;
    }

    /// <summary>
    /// Weist Einträge ab, die beim Auspacken aus dem Zielverzeichnis ausbrechen würden (Zip-Slip)
    /// oder auf Windows anders gelesen werden als auf Linux.
    /// </summary>
    private static void EnsureSafeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ConnectorPackageException("Eintrag ohne Namen.");
        }

        if (name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ConnectorPackageException(
                $"Eintrag '{name}' enthält einen Backslash — im Archiv ist '/' der Trenner.");
        }

        if (Path.IsPathRooted(name) || name.StartsWith('/'))
        {
            throw new ConnectorPackageException($"Eintrag '{name}' ist ein absoluter Pfad.");
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ConnectorPackageException(
                $"Eintrag '{name}' enthält einen Doppelpunkt — auf Windows wäre das ein Laufwerk "
                + "oder ein alternativer Datenstrom.");
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment is ".." or ".")
            {
                throw new ConnectorPackageException(
                    $"Eintrag '{name}' zeigt aus dem Paket heraus.");
            }
        }
    }

    private static byte[] ReadEntry(ZipArchive archive, string name, long maxBytes)
    {
        var entry = archive.GetEntry(name)
            ?? throw new ConnectorPackageException($"Paket enthält '{name}' nicht.");
        if (entry.Length > maxBytes)
        {
            throw new ConnectorPackageException($"'{name}' ist größer als erlaubt ({maxBytes} Byte).");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        // Gegen die Längenangabe im Zip-Header lesen: Sie ist eine Behauptung des Archivs, kein
        // Versprechen. Mehr als angekündigt fließt hier nicht durch.
        var copied = CopyLimited(stream, buffer, maxBytes);
        return copied ? buffer.ToArray()
            : throw new ConnectorPackageException($"'{name}' liefert mehr Daten als angekündigt.");
    }

    private static bool CopyLimited(Stream source, Stream target, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return false;
            }

            target.Write(buffer, 0, read);
        }

        return true;
    }

    private static PublisherKey FindSigner(
        byte[] manifestBytes, byte[] signature, IReadOnlyList<PublisherKey> publishers)
    {
        var active = publishers.Where(p => p.IsActive).ToList();
        if (active.Count == 0)
        {
            throw new ConnectorPackageException(
                "Es ist kein Herausgeber gepinnt — ohne vertrauenswürdigen Schlüssel wird nichts "
                + "installiert (fail-closed).");
        }

        foreach (var publisher in active)
        {
            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(publisher.PublicKeyBase64);
            }
            catch (FormatException)
            {
                continue;
            }

            if (keyBytes.Length != 32)
            {
                continue;
            }

            if (Ed25519Verifier.Verify(keyBytes, manifestBytes, signature))
            {
                return publisher;
            }
        }

        throw new ConnectorPackageException(
            "Die Signatur des Manifests passt zu keinem gepinnten Herausgeber.");
    }

    private static ConnectorManifest ParseManifest(byte[] manifestBytes)
    {
        ConnectorManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ConnectorManifest>(manifestBytes, ManifestJson);
        }
        catch (JsonException exception)
        {
            throw new ConnectorPackageException($"Manifest ist kein gültiges JSON: {exception.Message}");
        }

        if (manifest is null)
        {
            throw new ConnectorPackageException("Manifest ist leer.");
        }

        if (!string.Equals(manifest.Schema, ConnectorManifest.SchemaV1, StringComparison.Ordinal))
        {
            throw new ConnectorPackageException(
                $"Unbekanntes Manifest-Schema '{manifest.Schema}' — erwartet '{ConnectorManifest.SchemaV1}'.");
        }

        if (!string.Equals(
            manifest.ContractVersion, ConnectorManifest.SupportedContractVersion, StringComparison.Ordinal))
        {
            throw new ConnectorPackageException(
                $"Das Paket verlangt Vertragsversion '{manifest.ContractVersion}'; dieses Gateway "
                + $"spricht '{ConnectorManifest.SupportedContractVersion}'.");
        }

        EnsureIdentifier(manifest.Id, "Paket-Id");
        EnsureVersion(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            throw new ConnectorPackageException("Manifest ohne Anzeigenamen.");
        }

        if (manifest.Payloads.Count == 0)
        {
            throw new ConnectorPackageException("Manifest deklariert keine Dateien.");
        }

        return manifest;
    }

    private static void VerifyPayloads(ZipArchive archive, ConnectorManifest manifest)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in manifest.Payloads)
        {
            EnsureSafeEntryName(payload.Path);
            if (!declared.Add(payload.Path))
            {
                throw new ConnectorPackageException(
                    $"Datei '{payload.Path}' ist im Manifest mehrfach deklariert.");
            }

            var content = ReadEntry(archive, payload.Path, MaxUnpackedBytes);
            var actual = Convert.ToHexStringLower(SHA256.HashData(content));
            if (!string.Equals(actual, payload.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConnectorPackageException(
                    $"Datei '{payload.Path}' hat den Hash {actual}, das Manifest nennt {payload.Sha256}.");
            }
        }

        // Nicht deklarierte Einträge sind nicht signiert. Sie stillschweigend zu ignorieren hieße,
        // sie beim Auspacken zu übergehen — und beim nächsten, der es anders macht, liegen sie da.
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName is ManifestEntry or SignatureEntry
                || entry.FullName.EndsWith('/')
                || declared.Contains(entry.FullName))
            {
                continue;
            }

            throw new ConnectorPackageException(
                $"Eintrag '{entry.FullName}' steht nicht im Manifest und ist damit nicht signiert.");
        }

        if (!declared.Contains(manifest.EntryPoint))
        {
            throw new ConnectorPackageException(
                $"Der Entry Point '{manifest.EntryPoint}' ist keine deklarierte Datei.");
        }

        if (!declared.Contains(manifest.SignaturePath))
        {
            throw new ConnectorPackageException(
                $"Die Signatur '{manifest.SignaturePath}' zum Entry Point ist keine deklarierte Datei.");
        }
    }

    private static void WriteFile(string root, string relative, byte[] content)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        // Gürtel und Hosenträger: Die Namensprüfung oben sollte das schon ausschließen, aber ein
        // Schreibvorgang außerhalb des Ziels ist der Fehler, den man nicht einmal machen darf.
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ConnectorPackageException($"Eintrag '{relative}' zeigt aus dem Zielverzeichnis heraus.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
    }

    private static void EnsureIdentifier(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ConnectorPackageException($"{what} fehlt oder ist zu lang.");
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                throw new ConnectorPackageException(
                    $"{what} '{value}' enthält '{c}'. Erlaubt sind Buchstaben, Ziffern, '.', '-' und '_' "
                    + "— der Wert wird zu einem Verzeichnisnamen.");
            }
        }
    }

    private static void EnsureVersion(string value)
    {
        EnsureIdentifier(value, "Version");
        if (!System.Version.TryParse(value.Split('-', 2)[0], out _))
        {
            throw new ConnectorPackageException(
                $"Version '{value}' ist nicht als Zahlenfolge lesbar (erwartet etwa '1.2.0' oder '1.2.0-rc1').");
        }
    }
}

/// <summary>
/// Ed25519-Verifikation (RFC 8032) über BouncyCastle.
/// <para>
/// .NET 10 bringt Ed25519 nicht öffentlich mit. Statt eigener Kurvenarithmetik an einer
/// Vertrauensgrenze steht hier geprüfter Bibliothekscode — dieselbe Prüfung, die der Rust-Host mit
/// <c>ed25519-dalek</c> für die Component-Bytes macht, nur für das Manifest.
/// </para>
/// </summary>
internal static class Ed25519Verifier
{
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.PublicKeySize
            || signature.Length != Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.SignatureSize)
        {
            return false;
        }

        try
        {
            var parameters = new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(publicKey, 0);
            var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            verifier.Init(forSigning: false, parameters);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException)
        {
            // Ein Public Key, der kein gültiger Kurvenpunkt ist, ist kein Sonderfall, sondern
            // schlicht kein passender Schlüssel.
            return false;
        }
    }
}
