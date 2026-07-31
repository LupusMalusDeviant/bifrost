using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Bifrost.Persistence.Backup;

using Microsoft.Data.Sqlite;

namespace Bifrost.Core.Tests.Backup;

/// <summary>
/// Wegwerf-Instanzverzeichnisse für die Backup- und Restore-Tests: eine echte SQLite-Datei, ein
/// Key-Ring, Paketdateien und <c>config/instance.json</c> — also genau das, was ADR-0024 E3 in ein
/// Vollbackup aufnimmt.
/// </summary>
internal sealed class InstanceDirectory : IDisposable
{
    public InstanceDirectory(string label)
    {
        Root = Path.Combine(Path.GetTempPath(), $"bifrost-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string DatabaseFile => Path.Combine(Root, "bifrost.db");

    public string KeyRingDirectory => Path.Combine(Root, "keys");

    public string PackagesDirectory => Path.Combine(Root, "packages");

    public string InstanceConfigFile => Path.Combine(Root, "config", "instance.json");

    public BackupOptions Options(
        string? productVersion = null,
        string? minimumRestoreVersion = null,
        IReadOnlySet<string>? knownMigrationIds = null)
        => new()
        {
            DataDirectory = Root,
            ProductVersion = productVersion ?? "0.11.0",
            MinimumRestoreVersion = minimumRestoreVersion ?? BackupLayout.DefaultMinimumRestoreVersion,

            // Vorgabe ist bewusst LEER: Die meisten Tests hier prüfen andere Dinge, und das
            // Rückwärts-Tor (ADR-0024 E6) meldet dann eine Warnung statt eines stillen Bestehens.
            KnownMigrationIds = knownMigrationIds
                ?? new HashSet<string>(StringComparer.Ordinal),
        };

    public static string ConnectionString(string file)
        => new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ConnectionString;

    /// <summary>
    /// Legt eine Datenbank im WAL-Modus an und schreibt Zeilen, <b>ohne</b> zu checkpointen. Genau
    /// diese Lage macht eine Dateikopie still älter als die Datenbank: Die Zeilen stehen in der
    /// <c>-wal</c>-Datei, nicht in der Hauptdatei.
    /// </summary>
    public SqliteConnection CreateDatabaseWithOpenWal(int rows)
    {
        var connection = new SqliteConnection(ConnectionString(DatabaseFile));
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "PRAGMA wal_autocheckpoint=0");
        Execute(connection, "CREATE TABLE notiz (id INTEGER PRIMARY KEY, text TEXT NOT NULL)");
        Execute(connection, """
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL PRIMARY KEY, "ProductVersion" TEXT NOT NULL)
            """);
        Execute(connection, """
            INSERT INTO "__EFMigrationsHistory" VALUES ('20260731000000_Initial', '10.0.0')
            """);

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO notiz (text) VALUES ($t)";
        var parameter = insert.CreateParameter();
        parameter.ParameterName = "$t";
        insert.Parameters.Add(parameter);
        for (var i = 0; i < rows; i++)
        {
            parameter.Value = $"zeile-{i}";
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return connection;
    }

    public void WriteKeyRing(int keys = 2)
    {
        Directory.CreateDirectory(KeyRingDirectory);
        for (var i = 0; i < keys; i++)
        {
            File.WriteAllText(
                Path.Combine(KeyRingDirectory, $"key-{i}.xml"),
                $"<key id=\"{i}\"><value>geheim-{i}</value></key>",
                Encoding.UTF8);
        }
    }

    public void WritePackage(string name = "demo", int sizeBytes = 1024)
    {
        var directory = Path.Combine(PackagesDirectory, name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), RandomBytes(sizeBytes));
        File.WriteAllText(Path.Combine(directory, "manifest.txt"), $"paket {name}");
    }

    public void WriteInstanceConfig(string instanceId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstanceConfigFile)!);
        File.WriteAllText(
            InstanceConfigFile,
            JsonSerializer.Serialize(new { instanceId }),
            Encoding.UTF8);
    }

    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    public static long CountRows(string databaseFile)
    {
        using var connection = new SqliteConnection(ConnectionString(databaseFile));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notiz";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ein hängendes Handle im Test soll den Testlauf nicht rot färben.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Ein Ablageort für Archive, getrennt vom Instanzverzeichnis.</summary>
internal sealed class ArchiveDirectory : IDisposable
{
    public ArchiveDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), $"bifrost-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public IEnumerable<string> TempLeftovers()
        => Directory.EnumerateFiles(Root, "*.tmp", SearchOption.TopDirectoryOnly);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Werkzeuge, um an einem fertigen Archiv gezielt zu manipulieren — das ist die einzige
/// Art, die Prüfungen ehrlich zu testen.</summary>
internal static class ArchiveSurgery
{
    public static string ReadEntryText(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var stream = archive.GetEntry(entryName)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static void ReplaceEntry(string archivePath, string entryName, byte[] content)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content);
    }

    public static byte[] ReadEntryBytes(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var stream = archive.GetEntry(entryName)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Baut ein Archiv aus rohen Einträgen — auch aus solchen, die kein ehrlicher Erzeuger
    /// schreiben würde.</summary>
    public static void Build(string archivePath, IEnumerable<(string Name, byte[] Content, int Attributes)> entries)
    {
        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content, attributes) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            if (attributes != 0)
            {
                entry.ExternalAttributes = attributes;
            }

            using var target = entry.Open();
            target.Write(content);
        }
    }
}

/// <summary>
/// Ein von Hand gebautes, formal stimmiges Archiv — die Grundlage für alle Angriffsproben. Nur wenn
/// Manifest und Prüfsummen korrekt sind, prüft man tatsächlich die Abwehr und nicht bloß einen
/// Formfehler.
/// </summary>
internal static class SyntheticArchive
{
    /// <summary>Externe ZIP-Attribute eines Unix-Symlinks (<c>0xA1FF</c> in den oberen 16 Bit).</summary>
    public const int SymbolicLinkAttributes = unchecked((int)0xA1FF0000);

    public static void Write(
        string archivePath,
        IEnumerable<(string Name, byte[] Content, int Attributes)> payload,
        IEnumerable<string> sections,
        string productVersion = "0.11.0",
        string minimumRestoreVersion = "0.11.0",
        bool manifestFirst = true)
    {
        var entries = payload.ToList();
        var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            productVersion,
            minimumRestoreVersion,
            createdAt = DateTimeOffset.UtcNow,
            instanceId = "synthetisch",
            database = new { provider = "sqlite", migration = (string?)null },
            sections = sections.ToArray(),
            encryption = new { algorithm = "none", kdf = (string?)null, iterations = 0, salt = (string?)null },
            checksumAlgorithm = "sha-256",
        }));

        var checksums = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifest)),
        };
        foreach (var (name, content, _) in entries)
        {
            checksums[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        }

        var checksumBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            algorithm = "sha-256",
            entries = checksums,
        }));

        var all = new List<(string Name, byte[] Content, int Attributes)>();
        if (manifestFirst)
        {
            all.Add(("manifest.json", manifest, 0));
        }

        all.AddRange(entries);
        all.Add(("checksums.json", checksumBytes, 0));
        if (!manifestFirst)
        {
            all.Add(("manifest.json", manifest, 0));
        }

        ArchiveSurgery.Build(archivePath, all);
    }
}
