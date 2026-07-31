using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Persistence.Backup;

using Microsoft.Data.Sqlite;

using Xunit;

namespace Bifrost.Core.Tests.Backup;

/// <summary>
/// WP2.1: Ein Archiv entsteht konsistent — oder es entsteht nicht. Die Tests hier prüfen genau die
/// zwei Zusagen aus ADR-0024, die man einer Sicherung nicht ansieht: dass sie den Stand der
/// Datenbank trifft (E2) und dass ein Abbruch nichts hinterlässt, das man für ein Archiv halten
/// könnte (E4).
/// </summary>
public sealed class BackupCreationTests
{
    [Fact]
    public async Task Backup_reads_a_consistent_database_while_others_are_reading()
    {
        using var instance = new InstanceDirectory("wal");
        using var archives = new ArchiveDirectory();
        using var writer = instance.CreateDatabaseWithOpenWal(rows: 500);
        instance.WriteKeyRing();
        instance.WritePackage();
        instance.WriteInstanceConfig("instanz-1");

        // Nebenläufige Leser: Die Online-Backup-API muss neben ihnen laufen können.
        using var readers = new CancellationTokenSource();
        var reading = Task.Run(() =>
        {
            var seen = 0;
            while (!readers.IsCancellationRequested)
            {
                _ = InstanceDirectory.CountRows(instance.DatabaseFile);
                seen++;
            }

            return seen;
        });

        var service = new BackupService(instance.Options());
        var result = await service.CreateAsync(
            new BackupRequest(archives.File("voll.zip")), TestContext.Current.CancellationToken);

        await readers.CancelAsync();
        (await reading).Should().BeGreaterThan(0, "während der Sicherung wurde tatsächlich gelesen");

        result.Manifest.Sections.Should().Be(BackupSections.All);
        result.Manifest.MigrationId.Should().Be("20260731000000_Initial");
        result.Manifest.InstanceId.Should().Be("instanz-1");

        var snapshot = Path.Combine(archives.Root, "aus-archiv.db");
        File.WriteAllBytes(snapshot, ArchiveSurgery.ReadEntryBytes(result.ArchivePath, BackupLayout.DatabaseEntry));
        InstanceDirectory.CountRows(snapshot).Should().Be(500, "die Sicherung trifft den committeten Stand");

        // Die Gegenprobe, die ADR-0024 E2 begründet: Dieselbe Datei bloß zu kopieren ergibt bei
        // offenem WAL eine Sicherung, der man ihr Alter nicht ansieht.
        var naiveCopy = Path.Combine(archives.Root, "naive.db");
        using (var source = new FileStream(
                   instance.DatabaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var target = File.Create(naiveCopy))
        {
            source.CopyTo(target);
        }

        long? naiveRows = null;
        try
        {
            naiveRows = InstanceDirectory.CountRows(naiveCopy);
        }
        catch (SqliteException)
        {
            // Noch schlimmer als "zu wenige Zeilen": In der Hauptdatei steht die Tabelle noch gar nicht.
        }

        naiveRows.Should().NotBe(500, "die reine Dateikopie enthält den WAL-Inhalt nicht");
    }

    [Fact]
    public async Task A_failure_while_writing_leaves_no_archive_behind()
    {
        using var instance = new InstanceDirectory("abbruch");
        using var archives = new ArchiveDirectory();
        using var writer = instance.CreateDatabaseWithOpenWal(rows: 20);
        instance.WriteKeyRing();
        instance.WritePackage();

        // Eine Paketdatei ist gesperrt. Datenbank und Key-Ring stehen zu diesem Zeitpunkt bereits im
        // temporären Archiv — der Fehler trifft also mitten ins Schreiben.
        var locked = Path.Combine(instance.PackagesDirectory, "demo", "payload.bin");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var target = archives.File("halb.zip");
        var service = new BackupService(instance.Options());

        var act = async () => await service.CreateAsync(
            new BackupRequest(target), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<IOException>();
        File.Exists(target).Should().BeFalse("ein Teilarchiv darf nie unter dem Zielnamen liegen");
        archives.TempLeftovers().Should().BeEmpty("die temporäre Datei wird aufgeräumt");
    }

    [Fact]
    public async Task A_cancelled_backup_leaves_no_archive_behind()
    {
        using var instance = new InstanceDirectory("storno");
        using var archives = new ArchiveDirectory();
        using var writer = instance.CreateDatabaseWithOpenWal(rows: 20);
        instance.WriteKeyRing();

        // Groß und zufällig: Der Inhalt komprimiert nicht, das Schreiben dauert lange genug, um den
        // Abbruch wirklich mitten hinein fallen zu lassen.
        Directory.CreateDirectory(Path.Combine(instance.PackagesDirectory, "gross"));
        File.WriteAllBytes(
            Path.Combine(instance.PackagesDirectory, "gross", "daten.bin"),
            InstanceDirectory.RandomBytes(64 * 1024 * 1024));

        var target = archives.File("storniert.zip");
        var service = new BackupService(instance.Options());
        using var cts = new CancellationTokenSource();

        var backup = service.CreateAsync(new BackupRequest(target), cts.Token);
        var sawPartialArchive = false;
        var watcher = Task.Run(async () =>
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!backup.IsCompleted)
            {
                var temp = Directory.EnumerateFiles(archives.Root, "*.tmp").FirstOrDefault();
                if (temp is not null)
                {
                    sawPartialArchive = true;

                    // Erst abbrechen, wenn schon Nutzlast im temporären Archiv steht — sonst prüfte
                    // der Test bloß einen Abbruch vor dem ersten Byte.
                    if (new FileInfo(temp).Length > 512 * 1024 || waited.Elapsed > TimeSpan.FromSeconds(3))
                    {
                        await cts.CancelAsync();
                        return;
                    }
                }

                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        });

        var act = async () => await backup;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await watcher;

        sawPartialArchive.Should().BeTrue("der Abbruch fiel in ein bereits begonnenes Archiv");
        File.Exists(target).Should().BeFalse();
        archives.TempLeftovers().Should().BeEmpty();
    }

    [Fact]
    public async Task An_existing_target_is_never_overwritten()
    {
        using var instance = new InstanceDirectory("bestand");
        using var archives = new ArchiveDirectory();
        using var writer = instance.CreateDatabaseWithOpenWal(rows: 5);

        var target = archives.File("da.zip");
        await File.WriteAllTextAsync(target, "kein archiv", TestContext.Current.CancellationToken);

        var service = new BackupService(instance.Options());
        var act = async () => await service.CreateAsync(
            new BackupRequest(target), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<IOException>();
        (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken)).Should().Be("kein archiv");
    }

    [Fact]
    public async Task A_postgres_instance_refuses_loudly_instead_of_exporting_rows()
    {
        using var instance = new InstanceDirectory("postgres");
        using var archives = new ArchiveDirectory();

        var options = new BackupOptions
        {
            DataDirectory = instance.Root,
            Provider = DatabaseProvider.Postgres,
        };

        var act = async () => await new BackupService(options).CreateAsync(
            new BackupRequest(archives.File("pg.zip")), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*pg_dump*");
        File.Exists(archives.File("pg.zip")).Should().BeFalse();
    }
}
