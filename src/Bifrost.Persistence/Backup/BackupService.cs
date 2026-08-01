using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using Bifrost.Abstractions.Operations;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Erzeugt und prüft Sicherungsarchive (WP2.1, ADR-0024).
/// <para>
/// Die Reihenfolge ist der eigentliche Inhalt dieser Klasse: Nutzlast einsammeln, Manifest schreiben,
/// Nutzlast schreiben und dabei die Prüfsummen bilden, Prüfsummen ablegen, Datei durchschreiben,
/// <b>dann erst</b> umbenennen. Bis zum letzten Schritt existiert kein Pfad, unter dem jemand ein
/// halbes Archiv für ein ganzes halten könnte.
/// </para>
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly BackupOptions _options;

    public BackupService(BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetPath);

        // ADR-0024 E2: Fehlt das Werkzeug, endet der Aufruf HIER — bevor eine temporäre Datei
        // entstanden ist. Ein "kein pg_dump" nach dem halben Archiv wäre dieselbe Absage mit
        // Aufräumarbeit.
        if (_options.Provider == DatabaseProvider.Postgres
            && request.Sections.HasFlag(BackupSections.Database))
        {
            PostgresTools.Require(_options.PostgresToolDirectory);
        }

        var targetPath = Path.GetFullPath(request.TargetPath);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException(
                "Das Ziel muss eine Datei in einem Verzeichnis sein.", nameof(request));

        if (File.Exists(targetPath))
        {
            throw new IOException(
                $"'{targetPath}' existiert bereits. Ein Backup überschreibt kein vorhandenes Archiv.");
        }

        Directory.CreateDirectory(targetDirectory);

        // ADR-0024 E4: Die temporäre Datei liegt im ZIELVERZEICHNIS. Ein Move über
        // Dateisystemgrenzen ist kein Umbenennen, sondern Kopieren und Löschen — und genau dazwischen
        // läge dann ein Archiv, das vollständig aussieht.
        var tempArchive = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        var work = Directory.CreateTempSubdirectory("bifrost-backup-");
        try
        {
            var (entries, migrationId) = await CollectEntriesAsync(request.Sections, work.FullName, ct)
                .ConfigureAwait(false);
            var manifest = BuildManifest(entries, migrationId, request.Passphrase, out var cipher);

            await WriteArchiveAsync(tempArchive, manifest, entries, cipher, ct).ConfigureAwait(false);

            File.Move(tempArchive, targetPath);
            var size = new FileInfo(targetPath).Length;
            return new BackupResult(targetPath, size, manifest.ToContract());
        }
        catch
        {
            // Ein Abbruch — Ausnahme wie Abbruchsignal — darf nichts hinterlassen, das nach einem
            // Archiv aussieht.
            TryDelete(tempArchive);
            throw;
        }
        finally
        {
            TryDeleteDirectory(work.FullName);
        }
    }

    public async Task<BackupInspection> InspectAsync(
        string archivePath, string? passphrase, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var inspected = await ArchiveInspector.InspectAsync(archivePath, passphrase, ct).ConfigureAwait(false);
        return new BackupInspection(
            inspected.Valid,
            inspected.Manifest?.ToContract(),
            inspected.Problems.AsReadOnly());
    }

    // ── Einsammeln ──────────────────────────────────────────────────────────────────────────────

    private sealed record PlannedEntry(string Name, string SourceFile);

    private async Task<(List<PlannedEntry> Entries, string? MigrationId)> CollectEntriesAsync(
        BackupSections sections, string workDirectory, CancellationToken ct)
    {
        var entries = new List<PlannedEntry>();
        string? migrationId = null;

        if (sections.HasFlag(BackupSections.Database))
        {
            if (_options.Provider is DatabaseProvider.Postgres)
            {
                // ADR-0024 E2: pg_dump im custom-Format. Der Migrationsstand wird VOR dem Dump
                // gelesen — danach ist die Verbindung nicht mehr nötig, und ein Manifest ohne Stand
                // ließe das Rückwärts-Tor aus E6 ins Leere laufen.
                var connectionString = _options.RequiredPostgresConnectionString;
                migrationId = await PostgresBackup
                    .ReadLatestMigrationAsync(connectionString, ct)
                    .ConfigureAwait(false);

                var dump = Path.Combine(workDirectory, "database.dump");
                await PostgresBackup
                    .CreateAsync(connectionString, dump, _options.PostgresToolDirectory, ct)
                    .ConfigureAwait(false);
                entries.Add(new PlannedEntry(BackupLayout.DatabaseDumpEntry, dump));
            }
            else
            {
                var snapshot = Path.Combine(workDirectory, "database.db");
                SqliteSnapshot.Create(_options.ResolvedSqliteFile, snapshot);
                migrationId = SqliteSnapshot.ReadLatestMigration(snapshot);
                entries.Add(new PlannedEntry(BackupLayout.DatabaseEntry, snapshot));
            }
        }

        if (sections.HasFlag(BackupSections.KeyRing))
        {
            entries.AddRange(FromDirectory(_options.ResolvedKeyRingDirectory, BackupLayout.KeyRingZone));
        }

        if (sections.HasFlag(BackupSections.Packages))
        {
            entries.AddRange(FromDirectory(_options.ResolvedPackagesDirectory, BackupLayout.PackagesZone));
        }

        if (sections.HasFlag(BackupSections.Config) && File.Exists(_options.ResolvedInstanceConfigPath))
        {
            entries.Add(new PlannedEntry(
                BackupLayout.InstanceConfigEntry, _options.ResolvedInstanceConfigPath));
        }

        return (entries, migrationId);
    }

    private static IEnumerable<PlannedEntry> FromDirectory(string directory, string zone)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            yield return new PlannedEntry(zone + relative, file);
        }
    }

    private BackupManifestDocument BuildManifest(
        List<PlannedEntry> entries,
        string? migrationId,
        string? passphrase,
        out ArchivePayloadCipher? cipher)
    {
        cipher = string.IsNullOrEmpty(passphrase) ? null : ArchivePayloadCipher.CreateNew(passphrase);

        // Ein Bereich steht nur dann im Manifest, wenn tatsächlich etwas darin liegt: Der Restore
        // stellt genau die enthaltenen Bereiche her, und ein leer versprochener Bereich wäre beim
        // Prüfen ein Unvollständigkeitsfehler.
        var sections = new List<string>();
        foreach (var kind in (BackupSectionKind[])
                 [BackupSectionKind.Database, BackupSectionKind.KeyRing,
                  BackupSectionKind.Packages, BackupSectionKind.Config])
        {
            var zone = BackupLayout.ZoneOf(kind);
            if (entries.Any(e => e.Name.StartsWith(zone, StringComparison.Ordinal)))
            {
                sections.Add(BackupManifestDocument.SectionName(kind));
            }
        }

        return new BackupManifestDocument
        {
            FormatVersion = BackupLayout.FormatVersion,
            ProductVersion = _options.ProductVersion,
            MinimumRestoreVersion = _options.MinimumRestoreVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            InstanceId = ReadInstanceId(_options.ResolvedInstanceConfigPath),
            Database = new BackupManifestDocument.DatabaseBlock
            {
                Provider = BackupManifestDocument.ProviderName(_options.Provider),
                Migration = migrationId,
            },
            Sections = sections,
            Encryption = cipher is null
                ? new BackupManifestDocument.EncryptionBlock { Algorithm = BackupLayout.EncryptionNone }
                : new BackupManifestDocument.EncryptionBlock
                {
                    Algorithm = BackupLayout.EncryptionAesGcm,
                    Kdf = BackupLayout.Kdf,
                    Iterations = BackupLayout.KdfIterations,
                    Salt = Convert.ToBase64String(cipher.Salt),
                },
            ChecksumAlgorithm = BackupLayout.ChecksumAlgorithm,
        };
    }

    /// <summary>
    /// Die Instanz-Id aus <c>config/instance.json</c>. Fehlt die Datei, bleibt das Feld leer — ein
    /// Backup legt sie <b>nicht</b> an: Eine Sicherung verändert die Instanz nicht, die sie sichert.
    /// </summary>
    private static string ReadInstanceId(string instanceConfigPath)
    {
        if (!File.Exists(instanceConfigPath))
        {
            return "";
        }

        try
        {
            // Über den Text und nicht über die Bytes: Eine mit BOM geschriebene instance.json ist im
            // Feld üblich, und ein Backup soll daran nicht die Instanz-Id verlieren.
            using var document = JsonDocument.Parse(File.ReadAllText(instanceConfigPath));
            return document.RootElement.TryGetProperty("instanceId", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // ── Schreiben ───────────────────────────────────────────────────────────────────────────────

    private static async Task WriteArchiveAsync(
        string tempArchive,
        BackupManifestDocument manifest,
        List<PlannedEntry> entries,
        ArchivePayloadCipher? cipher,
        CancellationToken ct)
    {
        var checksums = new ChecksumFile();
        var stream = new FileStream(
            tempArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Erster Eintrag: das Manifest (ADR-0024 E1).
                var manifestBytes = manifest.ToUtf8Json();
                await WriteBytesAsync(zip, BackupLayout.ManifestEntry, manifestBytes, CompressionLevel.Optimal, ct)
                    .ConfigureAwait(false);
                checksums.Entries[BackupLayout.ManifestEntry] = ChecksumFile.Hash(manifestBytes);

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    checksums.Entries[entry.Name] = cipher is null
                        ? await WriteFileAsync(zip, entry.Name, entry.SourceFile, ct).ConfigureAwait(false)
                        : await WriteEncryptedFileAsync(zip, entry.Name, entry.SourceFile, cipher, ct)
                            .ConfigureAwait(false);
                }

                // ADR-0024 E4: Die Prüfsummen stehen im Archiv, BEVOR es geschlossen wird.
                await WriteBytesAsync(
                        zip, BackupLayout.ChecksumEntry, checksums.ToUtf8Json(), CompressionLevel.Optimal, ct)
                    .ConfigureAwait(false);
            }

            // Erst auf die Platte, dann umbenennen. Ohne dieses Flush hinge die Atomarität an der
            // Laune des Schreibcaches.
            stream.Flush(flushToDisk: true);
        }
    }

    private static async Task WriteBytesAsync(
        ZipArchive zip, string name, byte[] content, CompressionLevel level, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, level);
        var target = entry.Open();
        await using (target.ConfigureAwait(false))
        {
            await target.WriteAsync(content, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> WriteFileAsync(
        ZipArchive zip, string name, string sourceFile, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var source = new FileStream(
            sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using (source.ConfigureAwait(false))
        {
            var target = entry.Open();
            await using (target.ConfigureAwait(false))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> WriteEncryptedFileAsync(
        ZipArchive zip, string name, string sourceFile, ArchivePayloadCipher cipher, CancellationToken ct)
    {
        var info = new FileInfo(sourceFile);
        if (info.Length > ArchivePayloadCipher.MaxEncryptedEntryBytes)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' ist mit {1} Bytes zu groß für einen verschlüsselten Archiveintrag.",
                name,
                info.Length));
        }

        var plaintext = await File.ReadAllBytesAsync(sourceFile, ct).ConfigureAwait(false);
        var sealedBytes = cipher.Encrypt(name, plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        // Geheimtext komprimiert nicht — der Versuch kostet Zeit und spart nichts.
        await WriteBytesAsync(zip, name, sealedBytes, CompressionLevel.NoCompression, ct).ConfigureAwait(false);
        return ChecksumFile.Hash(sealedBytes);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Aufräumen ist Höflichkeit, nicht Vertrag. Der eigentliche Fehler ist bereits unterwegs.
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
