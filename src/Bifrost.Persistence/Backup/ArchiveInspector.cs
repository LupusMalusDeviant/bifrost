using System.IO.Compression;
using System.Security.Cryptography;

using Bifrost.Abstractions.Operations;

namespace Bifrost.Persistence.Backup;

/// <summary>Was die Prüfung eines Archivs ergeben hat, ohne dass etwas ausgepackt wurde.</summary>
internal sealed class InspectedArchive
{
    public BackupManifestDocument? Manifest { get; set; }

    public List<string> Problems { get; } = [];

    /// <summary>Nutzlasteinträge (ohne Manifest und Prüfsummenliste), in Archivreihenfolge.</summary>
    public List<InspectedEntry> Entries { get; } = [];

    public long TotalUncompressedBytes { get; set; }

    public bool Valid => Problems.Count == 0 && Manifest is not null;
}

internal sealed record InspectedEntry(string Name, long Length);

/// <summary>
/// Liest ein Archiv <b>lesend</b> und beurteilt es (ADR-0024 E1/E4): Manifest zuerst, dann Pfade,
/// dann Prüfsummen, dann Vollständigkeit. Nichts davon schreibt etwas.
/// </summary>
internal static class ArchiveInspector
{
    private const long MaxManifestBytes = 1024 * 1024;

    public static async Task<InspectedArchive> InspectAsync(
        string archivePath, string? passphrase, CancellationToken ct)
    {
        var result = new InspectedArchive();
        if (!File.Exists(archivePath))
        {
            result.Problems.Add($"Die Archivdatei '{archivePath}' existiert nicht.");
            return result;
        }

        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            await InspectOpenArchiveAsync(archive, passphrase, result, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            result.Problems.Add($"Die Datei ist kein lesbares ZIP-Archiv: {ex.Message}");
        }
        catch (IOException ex)
        {
            result.Problems.Add($"Die Archivdatei lässt sich nicht lesen: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Problems.Add($"Die Archivdatei lässt sich nicht lesen: {ex.Message}");
        }

        return result;
    }

    private static async Task InspectOpenArchiveAsync(
        ZipArchive archive, string? passphrase, InspectedArchive result, CancellationToken ct)
    {
        if (archive.Entries.Count == 0)
        {
            result.Problems.Add("Das Archiv ist leer.");
            return;
        }

        if (archive.Entries.Count > BackupLayout.MaxEntryCount)
        {
            result.Problems.Add(
                $"Das Archiv enthält {archive.Entries.Count} Einträge und überschreitet die zulässige Anzahl.");
            return;
        }

        // ADR-0024 E1: Das Manifest steht an erster Stelle. Ein Archiv, das erst hinten sagt, was es
        // ist, zwingt den Leser, vorher etwas anzufassen.
        var first = archive.Entries[0];
        if (!string.Equals(first.FullName, BackupLayout.ManifestEntry, StringComparison.Ordinal))
        {
            result.Problems.Add(
                $"Der erste Eintrag ist '{first.FullName}' statt '{BackupLayout.ManifestEntry}' — " +
                "das Archiv folgt nicht dem Backupformat v1.");
            return;
        }

        if (first.Length > MaxManifestBytes)
        {
            result.Problems.Add("manifest.json ist unplausibel groß.");
            return;
        }

        var manifestBytes = await ReadAllAsync(first, ct).ConfigureAwait(false);
        var manifestHash = ChecksumFile.Hash(manifestBytes);
        if (!BackupManifestDocument.TryParse(manifestBytes, out var manifest, out var manifestProblem)
            || manifest is null)
        {
            result.Problems.Add(manifestProblem ?? "manifest.json ist ungültig.");
            return;
        }

        result.Manifest = manifest;

        if (manifest.FormatVersion > BackupLayout.FormatVersion)
        {
            result.Problems.Add(
                $"Das Archiv hat Formatversion {manifest.FormatVersion}; diese Installation kennt " +
                $"höchstens {BackupLayout.FormatVersion}.");
            return;
        }

        if (!ValidateEntryShape(archive, result, ct))
        {
            return;
        }

        var checksums = await ReadChecksumsAsync(archive, result, ct).ConfigureAwait(false);
        if (checksums is null)
        {
            return;
        }

        if (!ChecksumFile.Matches(
                checksums.Entries.GetValueOrDefault(BackupLayout.ManifestEntry), manifestHash))
        {
            result.Problems.Add("Die Prüfsumme von manifest.json stimmt nicht.");
        }

        await VerifyChecksumsAsync(archive, checksums, result, ct).ConfigureAwait(false);
        VerifyCompleteness(archive, manifest, result);

        if (manifest.IsEncrypted && !string.IsNullOrEmpty(passphrase))
        {
            VerifyDecryptable(archive, manifest, passphrase, result, ct);
        }
        else if (!manifest.IsEncrypted && !string.IsNullOrEmpty(passphrase))
        {
            result.Problems.Add("Es wurde eine Passphrase angegeben, das Archiv ist aber unverschlüsselt.");
        }
    }

    /// <summary>
    /// Pfade, Symlinks und Größen — alles, was gegen das Archiv als Fremdeingabe schützt, bevor
    /// irgendein Inhalt gelesen wird. Als Wurzel dient ein rein gedachtes Verzeichnis: Hier wird
    /// nichts geschrieben, es geht allein darum, ob die Namen überhaupt verankerbar sind.
    /// </summary>
    private static bool ValidateEntryShape(ZipArchive archive, InspectedArchive result, CancellationToken ct)
    {
        var virtualRoot = Path.Combine(Path.GetTempPath(), "bifrost-restore-anchor");
        long total = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (ArchiveEntryGuard.IsSymbolicLink(entry))
            {
                result.Problems.Add(
                    $"Eintrag '{entry.FullName}' ist ein symbolischer Verweis und wird abgelehnt.");
                continue;
            }

            if (!ArchiveEntryGuard.TryResolve(entry.FullName, virtualRoot, out _, out var pathProblem))
            {
                result.Problems.Add(pathProblem!);
                continue;
            }

            if (!ArchiveEntryGuard.IsWithinSizeLimits(
                    entry.FullName, entry.Length, entry.CompressedLength, out var sizeProblem))
            {
                result.Problems.Add(sizeProblem!);
                continue;
            }

            total += entry.Length;
            if (total > BackupLayout.MaxTotalUncompressedBytes)
            {
                result.Problems.Add("Das Archiv überschreitet entpackt die zulässige Gesamtgröße.");
                break;
            }
        }

        result.TotalUncompressedBytes = total;
        return result.Problems.Count == 0;
    }

    private static async Task<ChecksumFile?> ReadChecksumsAsync(
        ZipArchive archive, InspectedArchive result, CancellationToken ct)
    {
        var checksumEntry = archive.GetEntry(BackupLayout.ChecksumEntry);
        if (checksumEntry is null)
        {
            result.Problems.Add("Dem Archiv fehlt checksums.json — es ist unvollständig.");
            return null;
        }

        var checksumBytes = await ReadAllAsync(checksumEntry, ct).ConfigureAwait(false);
        if (!ChecksumFile.TryParse(checksumBytes, out var checksums, out var problem))
        {
            result.Problems.Add(problem ?? "checksums.json ist ungültig.");
            return null;
        }

        return checksums;
    }

    private static async Task VerifyChecksumsAsync(
        ZipArchive archive, ChecksumFile checksums, InspectedArchive result, CancellationToken ct)
    {
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName is BackupLayout.ChecksumEntry or BackupLayout.ManifestEntry
                || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var expected = checksums.Entries.GetValueOrDefault(entry.FullName);
            if (expected is null)
            {
                result.Problems.Add($"Eintrag '{entry.FullName}' ist nicht in checksums.json aufgeführt.");
                continue;
            }

            var actual = await HashEntryAsync(entry, ct).ConfigureAwait(false);
            if (!ChecksumFile.Matches(expected, actual))
            {
                result.Problems.Add($"Die Prüfsumme von '{entry.FullName}' stimmt nicht.");
                continue;
            }

            result.Entries.Add(new InspectedEntry(entry.FullName, entry.Length));
        }

        foreach (var listed in checksums.Entries.Keys)
        {
            if (listed != BackupLayout.ManifestEntry && archive.GetEntry(listed) is null)
            {
                result.Problems.Add($"checksums.json führt '{listed}' auf, das Archiv enthält es nicht.");
            }
        }
    }

    private static void VerifyCompleteness(
        ZipArchive archive, BackupManifestDocument manifest, InspectedArchive result)
    {
        foreach (var name in manifest.Sections)
        {
            if (!BackupManifestDocument.TryParseSection(name, out var section))
            {
                continue;
            }

            var zone = section switch
            {
                BackupSections.Database => BackupLayout.DatabaseZone,
                BackupSections.KeyRing => BackupLayout.KeyRingZone,
                BackupSections.Packages => BackupLayout.PackagesZone,
                _ => BackupLayout.ConfigZone,
            };

            if (!result.Entries.Any(e => e.Name.StartsWith(zone, StringComparison.Ordinal)))
            {
                result.Problems.Add(
                    $"Das Manifest nennt den Bereich '{name}', im Archiv steht dazu nichts — " +
                    "das Archiv ist unvollständig.");
            }
        }

        if (!manifest.Sections.Any(s => string.Equals(s, "database", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Der Name der Nutzlast hängt am Anbieter: SQLite legt eine Datenbankdatei ab, PostgreSQL
        // einen pg_dump. Ein unbekannter Anbieter im Manifest ist an anderer Stelle bereits ein
        // Blocker; hier wird dann der SQLite-Name geprüft, was den Befund nicht verfälscht.
        var expected = BackupManifestDocument.TryParseProvider(manifest.Database.Provider, out var provider)
            ? BackupLayout.DatabaseEntryFor(provider)
            : BackupLayout.DatabaseEntry;

        if (archive.GetEntry(expected) is null)
        {
            result.Problems.Add($"Dem Archiv fehlt '{expected}'.");
        }
    }

    private static void VerifyDecryptable(
        ZipArchive archive,
        BackupManifestDocument manifest,
        string passphrase,
        InspectedArchive result,
        CancellationToken ct)
    {
        if (!TryCreateCipher(manifest, passphrase, out var cipher, out var cipherProblem))
        {
            result.Problems.Add(cipherProblem!);
            return;
        }

        foreach (var entry in result.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var zipEntry = archive.GetEntry(entry.Name)!;
            if (zipEntry.Length > ArchivePayloadCipher.MaxEncryptedEntryBytes + 64)
            {
                result.Problems.Add($"Eintrag '{entry.Name}' ist zu groß für einen verschlüsselten Eintrag.");
                return;
            }

            using var content = zipEntry.Open();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            if (!cipher!.TryDecrypt(entry.Name, buffer.ToArray(), out _, out var problem))
            {
                // Beim ersten Fehlschlag aufhören: Eine falsche Passphrase scheitert an jedem
                // Eintrag, und eine Liste identischer Meldungen hilft niemandem.
                result.Problems.Add(problem!);
                return;
            }
        }
    }

    internal static bool TryCreateCipher(
        BackupManifestDocument manifest, string passphrase, out ArchivePayloadCipher? cipher, out string? problem)
    {
        cipher = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(manifest.Encryption.Salt))
        {
            problem = "Das Manifest nennt kein Salz — das verschlüsselte Archiv ist unbrauchbar.";
            return false;
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(manifest.Encryption.Salt);
        }
        catch (FormatException)
        {
            problem = "Das Salz im Manifest ist kein gültiges Base64.";
            return false;
        }

        try
        {
            cipher = ArchivePayloadCipher.FromSalt(passphrase, salt, manifest.Encryption.Iterations);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            problem = $"Die Verschlüsselungsangaben im Manifest sind unbrauchbar: {ex.Message}";
            return false;
        }

        return true;
    }

    internal static async Task<byte[]> ReadAllAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        using var content = entry.Open();
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        using var content = entry.Open();
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(content, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
