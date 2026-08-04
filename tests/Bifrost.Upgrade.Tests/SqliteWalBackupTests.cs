using System.Text;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Persistence;
using Bifrost.Persistence.Backup;
using Bifrost.Upgrade.Tests.Harness;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Bifrost.Upgrade.Tests;

/// <summary>
/// Sicherung und Rückspielung bei <b>aktivem</b> WAL (ADR-0024 E2).
///
/// <para>
/// <see cref="SqliteSnapshot"/> benutzt die Online-Backup-API genau für diesen Fall — nur trat er
/// bis v0.6 nie ein, weil WAL nie eingeschaltet war. Die vorhandenen Backup-Tests laufen seither
/// zwar unter WAL, prüfen den Fall aber weiterhin nicht: Sie rufen vor der Sicherung
/// <c>ClearAllPools()</c>, und die letzte geschlossene Verbindung räumt das <c>-wal</c> weg. Ein
/// leeres WAL beweist über die Sicherung eines gefüllten nichts.
/// </para>
/// <para>
/// Hier bleibt deshalb eine Verbindung offen. Der Test zeigt zuerst, dass die zuletzt geschriebenen
/// Zeilen wirklich <b>nur</b> im <c>-wal</c> stehen — und danach, dass die Sicherung sie trotzdem
/// mitnimmt.
/// </para>
/// </summary>
public sealed class SqliteWalBackupTests : IAsyncLifetime
{
    private const string Marker = "WAL-ZEUGE-6A3F";
    private const string Passphrase = "eine-passphrase-fuer-das-wal-archiv";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"bifrost-wal-backup-{Guid.NewGuid():N}");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen ist kein Testergebnis.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Backup_with_an_active_wal_carries_the_uncheckpointed_pages()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "quelle");
        Directory.CreateDirectory(source);
        var dbFile = Path.Combine(source, "bifrost.db");

        var factory = SqliteFactory(dbFile);
        await new DatabaseInitializer(factory).InitializeAsync(ct);

        // Eine Verbindung bleibt offen. Damit bleibt auch das WAL liegen — genau die Lage, in der
        // eine Dateikopie still älter wäre als die Datenbank, die man zu sichern glaubt.
        await using var keepAlive = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbFile, Pooling = false }.ConnectionString);
        await keepAlive.OpenAsync(ct);

        // Den selbsttätigen Checkpoint abschalten, sonst prüft dieser Test das Gegenteil dessen,
        // was er behauptet: SQLite schiebt das WAL ab etwa 1000 Seiten von allein in die
        // Hauptdatei, und dann steht der Zeuge dort, wo er laut Vorbedingung nicht sein soll.
        //
        // Isoliert lief der Test gruen, im Gesamtlauf rot — die Auslastung entscheidet, ob der
        // Checkpoint dazwischenkommt. Dieselbe Zeile steht aus demselben Grund seit WP2.1 in
        // BackupTestKit.CreateDatabaseWithOpenWal; sie fehlte hier.
        await using (var pragma = keepAlive.CreateCommand())
        {
            pragma.CommandText = "PRAGMA wal_autocheckpoint=0";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.AuditEvents.AddRange(Enumerable.Range(0, 120).Select(i => new AuditEventRow
            {
                Timestamp = DateTimeOffset.UtcNow,
                Origin = (int)CallOrigin.Mcp,
                Kind = (int)AuditEventKind.ToolCall,
                Tool = $"{Marker}__{i}",
                Status = (int)InvocationStatus.Success,
            }));
            await db.SaveChangesAsync(ct);
        }

        // Der Nachweis, dass die Lage wirklich hergestellt ist: Die Zeilen stehen im '-wal' und
        // nicht in der Hauptdatei. Ohne diesen Schritt prüfte der Test unter Umständen gar nichts.
        var wal = dbFile + "-wal";
        File.Exists(wal).Should().BeTrue("bei aktivem WAL entsteht die Begleitdatei");
        Contains(wal, Marker).Should().BeTrue("die zuletzt geschriebenen Seiten liegen im WAL");
        Contains(dbFile, Marker).Should().BeFalse(
            "sie sind noch nicht in die Hauptdatei zurückgeschrieben — eine blosse Dateikopie hätte "
            + "sie verloren");

        // Sicherung, während die Verbindung noch offen ist.
        var archive = Path.Combine(_root, "wal.zip");
        var created = await new BackupService(OptionsFor(source))
            .CreateAsync(new BackupRequest(archive, BackupSections.Database, Passphrase), ct);
        created.Manifest.MigrationId.Should().NotBeNullOrWhiteSpace(
            "der Schnappschuss ist eine lesbare Datenbank, kein halber Dateistand");

        // Rückspielung in eine leere Zielinstallation.
        var target = Path.Combine(_root, "ziel");
        Directory.CreateDirectory(target);
        var targetOptions = OptionsFor(target);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        plan.CanApply.Should().BeTrue("Blocker: {0}", string.Join(" | ", plan.Blockers));
        (await restore.ApplyAsync(plan, ct)).Applied.Should().BeTrue();

        var restored = SqliteFactory(Path.Combine(target, "bifrost.db"));
        await using (var db = await restored.CreateDbContextAsync(ct))
        {
            (await db.AuditEvents.CountAsync(ct)).Should().Be(
                120, "der zurückgespielte Stand ist vollständig, WAL-Inhalt eingeschlossen");
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Der Rückbau. Bricht das Umschalten ab, muss die vorhandene Datenbank <b>mit ihren
    /// Begleitern</b> zurückbleiben: Ein gelöschtes <c>-wal</c> nähme die zuletzt bestätigten
    /// Transaktionen mit, und zwar ausgerechnet dann, wenn ohnehin schon etwas schiefgegangen ist.
    /// <para>
    /// Das hält heute an der <i>Reihenfolge</i> in <c>RestoreDatabaseAsync</c> — erst einhängen,
    /// dann aufräumen. Der Test prüft die Zusage und nicht die Reihenfolge: Wer sie umdreht, weil
    /// „aufräumen gehört nach vorn", wird hier rot.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rollback_of_a_restore_brings_the_wal_companions_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "rb-quelle");
        Directory.CreateDirectory(source);
        await new DatabaseInitializer(SqliteFactory(Path.Combine(source, "bifrost.db")))
            .InitializeAsync(ct);
        SqliteConnection.ClearAllPools();

        var archive = Path.Combine(_root, "rb.zip");
        await new BackupService(OptionsFor(source))
            .CreateAsync(new BackupRequest(archive, BackupSections.Database, Passphrase), ct);

        // Das Ziel wird so gebaut, dass das Einhängen der Datenbank **scheitern muss**: An der
        // Stelle, an die die Datei gehört, liegt ein Verzeichnis. Ein Verschieben dorthin schlägt
        // auf jedem Betriebssystem fehl — anders als ein offener Dateihandle, der nur unter Windows
        // stört. Der Rückbaustapel läuft danach, und genau darum geht es hier.
        var target = Path.Combine(_root, "rb-ziel");
        Directory.CreateDirectory(target);
        var targetDb = Path.Combine(target, "bifrost.db");
        Directory.CreateDirectory(targetDb);

        // Daneben liegen die Begleiter einer früheren Datenbank — mit dem Zeugen darin.
        var wal = targetDb + "-wal";
        await File.WriteAllTextAsync(wal, Marker, ct);
        var shm = targetDb + "-shm";
        await File.WriteAllTextAsync(shm, Marker, ct);

        var targetOptions = OptionsFor(target);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        plan.CanApply.Should().BeTrue(
            "ohne Datenbankdatei gilt das Ziel als leer; Blocker: {0}", string.Join(" | ", plan.Blockers));

        var act = async () => await restore.ApplyAsync(plan, ct);
        await act.Should().ThrowAsync<IOException>("das Einhängen der Datenbank kann hier nicht gelingen");

        File.Exists(wal).Should().BeTrue(
            "der Rückbau holt die WAL-Begleiter zurück; würden sie gelöscht statt beiseitegelegt, "
            + "nähmen sie die zuletzt bestätigten Transaktionen mit — ausgerechnet dann, wenn "
            + "ohnehin schon etwas schiefgegangen ist");
        Contains(wal, Marker).Should().BeTrue("und zwar unverändert");
        File.Exists(shm).Should().BeTrue("das '-shm' gehört dazu");
    }

    /// <summary>
    /// Roher Byte-Scan: Steht die Zeichenkette in dieser Datei? Ausdrücklich mit
    /// <see cref="FileShare.ReadWrite"/> — SQLite hält das <c>-wal</c> offen, und
    /// <c>File.ReadAllBytes</c> käme unter Windows nicht daran vorbei.
    /// </summary>
    private static bool Contains(string file, string needle)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
    }

    private static BackupOptions OptionsFor(string directory) => new()
    {
        DataDirectory = directory,
        Provider = DatabaseProvider.Sqlite,
        ProductVersion = BifrostProductInfo.Version,
        MinimumRestoreVersion = BackupLayout.DefaultMinimumRestoreVersion,
    };

    private static UpgradeDbFactory SqliteFactory(string path)
        => new(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}")
            .Options);
}
