using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Persistence;
using Bifrost.Persistence.Backup;
using Bifrost.Persistence.Startup;
using Bifrost.Upgrade.Tests.Harness;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Testcontainers.PostgreSql;

using Xunit;

namespace Bifrost.Upgrade.Tests;

/// <summary>
/// Voraussetzungen fuer die PostgreSQL-Backupfelder: ein Server <b>und</b> die Werkzeuge
/// <c>pg_dump</c>/<c>pg_restore</c> auf diesem Rechner (ADR-0024 E2).
///
/// <para>
/// <b>Warum die Serverversion aus dem Client abgeleitet wird und nicht fest bei 17 steht:</b>
/// <c>pg_dump</c> weigert sich, einen <i>neueren</i> Server zu sichern — mit "aborting because of
/// server version mismatch". Ein fest verdrahtetes <c>postgres:17-alpine</c> waere auf jedem
/// Rechner mit aelterem Client rot, und zwar aus einem Grund, der mit dem Pruefling nichts zu tun
/// hat. Geprueft werden soll, ob <i>unser</i> Code die Werkzeuge richtig bedient; also bekommt der
/// Server die Hauptversion des vorhandenen Clients.
/// </para>
///
/// <para>
/// Fehlt Docker oder fehlen die Werkzeuge, werden die Felder <b>uebersprungen und als uebersprungen
/// gemeldet</b> — nicht als bestanden. Mit <c>BIFROST_REQUIRE_POSTGRES=1</c> ist beides ein
/// Fehlschlag; genau so laeuft die CI.
/// </para>
/// </summary>
public sealed class PostgresBackupFixture : IAsyncLifetime
{
    private static bool PostgresRequired =>
        Environment.GetEnvironmentVariable("BIFROST_REQUIRE_POSTGRES") is "1" or "true";

    public PostgreSqlContainer? Container { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!PostgresTools.TryLocate(out var tools) || tools is null)
        {
            var reason =
                "pg_dump/pg_restore sind auf diesem Rechner nicht erreichbar. Ohne sie ist das "
                + "PostgreSQL-Backup nicht pruefbar — und wird deshalb uebersprungen statt gruen "
                + "gemeldet.\n" + PostgresTools.MissingMessage;

            if (PostgresRequired)
            {
                throw new InvalidOperationException(reason);
            }

            UnavailableReason = reason;
            return;
        }

        try
        {
            Container = new PostgreSqlBuilder(await ServerImageAsync(tools.DumpPath)).Build();
            await Container.StartAsync();
        }
        catch (Exception ex) when (!PostgresRequired)
        {
            UnavailableReason = $"PostgreSQL-Testcontainer nicht startbar: {ex.Message}";
        }
    }

    /// <summary>Das Serverabbild zur Hauptversion des vorhandenen <c>pg_dump</c>.</summary>
    private static async Task<string> ServerImageAsync(string dumpPath)
    {
        var major = await ReadMajorVersionAsync(dumpPath);

        // Ausserhalb der Spanne, fuer die es offizielle Abbilder gibt: lieber ein bekannter Stand
        // als ein Abbild, das es nicht gibt — der Container scheitert dann mit "manifest unknown"
        // und die Meldung sagt nichts ueber den Pruefling.
        return major is >= 13 and <= 18
            ? $"postgres:{major.ToString(CultureInfo.InvariantCulture)}-alpine"
            : "postgres:17-alpine";
    }

    private static async Task<int> ReadMajorVersionAsync(string dumpPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(dumpPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        // "pg_dump (PostgreSQL) 17.2 (Debian 17.2-1)" — gesucht ist die erste Zahl nach der Klammer.
        var digits = new string([.. text.SkipWhile(c => c != ')').Skip(1).SkipWhile(c => !char.IsDigit(c))
            .TakeWhile(char.IsDigit)]);
        return int.TryParse(digits, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }
    }
}

/// <summary>
/// Matrixfeld 9: <b>Backup und Restore auf PostgreSQL</b> (ADR-0024 E2/E3/E4/E5/E6), gegen einen
/// echten Server und mit den echten Werkzeugen.
///
/// <para>
/// Der Bestand wird durch die <b>echten</b> Stores geschrieben, wie in der uebrigen Matrix. Das ist
/// hier der springende Punkt: Ein Backup, das Zeilen zurueckbringt und ihren Geheimtext unlesbar
/// macht, sieht in der Tabelle unverdaechtig aus. Erst der Schluesselring im Archiv (E3) und eine
/// Nachpruefung, die wirklich entschluesselt, decken das auf.
/// </para>
/// </summary>
public sealed class PostgresBackupRestoreTests : IClassFixture<PostgresBackupFixture>, IAsyncLifetime
{
    private const string Passphrase = "eine-passphrase-fuer-ein-postgres-vollbackup";

    private readonly PostgresBackupFixture _fixture;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"bifrost-pg-backup-{Guid.NewGuid():N}");

    public PostgresBackupRestoreTests(PostgresBackupFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Aufraeumen ist kein Testergebnis.
        }

        return ValueTask.CompletedTask;
    }

    // ── Sichern und wiederherstellen, mit lesbarem Geheimtext ──────────────────────────────────

    [Fact]
    public async Task Backup_and_restore_keep_the_stored_ciphertext_readable()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("quelle", ct);
        var archive = Path.Combine(_root, "voll.zip");

        var created = await new BackupService(source.Options)
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        created.Manifest.Provider.Should().Be(DatabaseProvider.Postgres);
        created.Manifest.MigrationId.Should().Be(
            UpgradeHarness.PublishedMigrations(BifrostDbOptions.Postgres)[^1],
            "das Manifest sagt, auf welchem Schemastand das Archiv steht — daran haengt das "
            + "Rueckwaerts-Tor aus E6");
        created.Manifest.Encrypted.Should().BeTrue();
        created.Manifest.Sections.Should().Be(BackupSections.All);

        // ADR-0024 E2: Die Nutzlast ist ein pg_dump, keine Zeilenliste.
        EntryNames(archive).Should().Contain(
            "database/bifrost.dump", "die PostgreSQL-Nutzlast liegt unter ihrem eigenen Namen");
        EntryNames(archive).Should().NotContain("database/bifrost.db");

        // Ein leeres Ziel: eigene Datenbank, eigenes Verzeichnis (ADR-0024 E5, Regelfall).
        var target = await NewEmptyTargetAsync("ziel");

        var restore = new RestoreService(target.Options, new BackupService(target.Options));
        var plan = await restore.PlanAsync(new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);

        plan.CanApply.Should().BeTrue("Blocker: {0}", string.Join(" | ", plan.Blockers));
        plan.TargetIsEmpty.Should().BeTrue("eine frisch angelegte Datenbank hat keine eigenen Tabellen");

        var result = await restore.ApplyAsync(plan, ct);
        result.Applied.Should().BeTrue();
        result.RestoredSections.Should().HaveFlag(BackupSections.Database);
        result.RestoredSections.Should().HaveFlag(BackupSections.KeyRing);
        result.RestoredSections.Should().HaveFlag(BackupSections.Config);
        result.PreBackupPath.Should().BeNull("auf ein leeres Ziel wird nichts ueberschrieben");

        // Der Kern: vollstaendig UND lesbar. Der Schluesselring ist mit dem Archiv gereist.
        var restoredFactory = FactoryFor(target.ConnectionString);
        await using (var db = await restoredFactory.CreateDbContextAsync(ct))
        {
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty(
                "das Archiv stand auf dem heutigen Stand");
        }

        await UpgradePayloadWriter.VerifyAsync(
            restoredFactory, UpgradeHarness.KeyRing(Path.Combine(target.Directory, "keys")), source.Payload, ct);
    }

    /// <summary>
    /// Die Gegenprobe zur Lesbarkeitszusage: <b>ohne</b> Schluesselring im Archiv kommen die Zeilen
    /// zurueck und ihr Inhalt nicht. Ohne diese Probe koennte die Zusage oben auch dann gruen sein,
    /// wenn sie gar nichts entschluesselt (ADR-0024 E3).
    /// </summary>
    [Fact]
    public async Task Without_the_key_ring_the_restored_rows_are_unreadable()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("ohne-ring", ct);
        var archive = Path.Combine(_root, "ohne-ring.zip");

        await new BackupService(source.Options).CreateAsync(
            new BackupRequest(archive, BackupSections.Database | BackupSections.Config, Passphrase), ct);

        var target = await NewEmptyTargetAsync("ohne-ring-ziel");
        var restore = new RestoreService(target.Options, new BackupService(target.Options));
        var plan = await restore.PlanAsync(new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        plan.CanApply.Should().BeTrue("Blocker: {0}", string.Join(" | ", plan.Blockers));

        (await restore.ApplyAsync(plan, ct)).RestoredSections.Should().NotHaveFlag(BackupSections.KeyRing);

        var restoredFactory = FactoryFor(target.ConnectionString);
        await using (var db = await restoredFactory.CreateDbContextAsync(ct))
        {
            (await db.ConfigVersions.CountAsync(r => r.ServerId == source.Payload.Server.Value, ct))
                .Should().Be(2, "die Zeilen kommen sehr wohl durch");
        }

        var read = async () => await new EfUpstreamConfigStore(
                restoredFactory, UpgradeHarness.KeyRing(Path.Combine(target.Directory, "keys")))
            .GetVersionAsync(source.Payload.Server, new ConfigVersionId(2), ct);

        await read.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>(
            "ohne den Schluesselring ist der Geheimtext unbrauchbar — und genau das muss auffallen");
    }

    /// <summary>
    /// Das Format ist eine Entscheidung (ADR-0024 E2) und deshalb pruefbar: Ein <c>pg_dump</c> im
    /// custom-Format beginnt mit der Kennung <c>PGDMP</c>. Ein Klartext-Skript oder eine
    /// selbstgebaute Zeilenliste taete das nicht — und genau das soll hier nie entstehen.
    /// </summary>
    [Fact]
    public async Task The_payload_is_a_pg_dump_in_the_documented_custom_format()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("format", ct);
        var archive = Path.Combine(_root, "format.zip");

        // Unverschluesselt, damit die Kennung im Archiv wirklich sichtbar ist.
        await new BackupService(source.Options)
            .CreateAsync(new BackupRequest(archive, BackupSections.Database), ct);

        using var zip = ZipFile.OpenRead(archive);
        using var entry = zip.GetEntry("database/bifrost.dump")!.Open();
        var magic = new byte[5];
        await entry.ReadExactlyAsync(magic, ct);

        Encoding.ASCII.GetString(magic).Should().Be(
            "PGDMP", "das custom-Format ist die dokumentierte Entscheidung aus ADR-0024 E2");
    }

    // ── Abbruch mitten im Schreiben ────────────────────────────────────────────────────────────

    /// <summary>
    /// ADR-0024 E4: Ein abgebrochenes Backup hinterlaesst nichts, was jemand fuer ein Archiv halten
    /// koennte — weder unter dem Zielnamen noch als temporaere Datei.
    /// <para>
    /// Der Abbruchzeitpunkt wird <b>gemessen und nicht geraten</b>: Ein erster Lauf sagt, wie lange
    /// eine vollstaendige Sicherung dauert; danach wird nach einem Bruchteil davon abgebrochen. Ein
    /// fester Wert waere je nach Rechner entweder „vor dem ersten Byte" oder „nach dem letzten".
    /// </para>
    /// <para>
    /// <b>Und wenn der Abbruch trotzdem zu spaet kam</b>, wird der Bruchteil halbiert und erneut
    /// versucht. Ein Test, der bei einem schnellen Lauf einfach durchfaellt, waere ein Flackern —
    /// und ein Flackern in einem Sicherheitsnetz wird irgendwann abgeschaltet statt repariert.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cancelled_backup_leaves_no_archive_behind()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("storno", ct);

        // Genug Nutzlast, damit pg_dump messbar arbeitet.
        await SeedBulkAsync(source.ConnectionString, megabytes: 24, ct);

        var reference = Path.Combine(_root, "referenz.zip");
        var clock = Stopwatch.StartNew();
        await new BackupService(source.Options)
            .CreateAsync(new BackupRequest(reference, BackupSections.Database), ct);
        clock.Stop();
        File.Delete(reference);

        var cancelled = false;
        var delay = clock.Elapsed / 2;

        for (var attempt = 0; attempt < 5 && !cancelled; attempt++, delay /= 2)
        {
            var target = Path.Combine(_root, $"storniert-{attempt}.zip");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(delay);

            try
            {
                await new BackupService(source.Options)
                    .CreateAsync(new BackupRequest(target, BackupSections.Database), cts.Token);

                // Zu spaet abgebrochen: Das Archiv ist fertig geworden. Kein Befund, nur ein zu
                // grosszuegig gewaehlter Zeitpunkt — weg damit und naeher heran.
                File.Delete(target);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                File.Exists(target).Should().BeFalse(
                    "ein Teilarchiv darf nie unter dem Zielnamen liegen");
            }
        }

        cancelled.Should().BeTrue(
            "der Abbruch muss mindestens einmal mitten hineingefallen sein, sonst prueft dieser Test "
            + "nichts");
        Directory.EnumerateFiles(_root, "*.tmp").Should().BeEmpty("die temporaere Datei wird aufgeraeumt");
    }

    // ── Verschluesselung: falsche Passphrase ───────────────────────────────────────────────────

    /// <summary>
    /// ADR-0024 E5: Eine falsche Passphrase faellt in der <b>Vorpruefung</b> auf, nicht beim
    /// Schreiben. Die Zielinstanz bleibt unveraendert — inklusive der Zeilen, die dort schon standen,
    /// und ohne eine Vorsicherung, die es gar nicht braucht.
    /// </summary>
    [Fact]
    public async Task A_wrong_passphrase_leaves_the_target_untouched()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("krypto-quelle", ct);
        var archive = Path.Combine(_root, "krypto.zip");
        await new BackupService(source.Options)
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        // Ein Ziel mit eigenem Bestand: Nur so laesst sich „unveraendert" ueberhaupt zeigen.
        var target = await NewInstanceAsync("krypto-ziel", ct);
        var restore = new RestoreService(target.Options, new BackupService(target.Options));

        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.Replace, "die-falsche-passphrase"), ct);

        plan.CanApply.Should().BeFalse("eine falsche Passphrase ist ein Blocker, kein Versuch");
        plan.Blockers.Should().NotBeEmpty();

        var apply = async () => await restore.ApplyAsync(plan, ct);
        await apply.Should().ThrowAsync<InvalidOperationException>();

        // Der eigene Bestand des Ziels ist unangetastet: dieselben Zeilen, derselbe Schluesselring.
        await UpgradePayloadWriter.VerifyAsync(
            FactoryFor(target.ConnectionString), target.Protection, target.Payload, ct);

        Directory.Exists(Path.Combine(target.Directory, "backups")).Should().BeFalse(
            "ohne Anwendung entsteht auch keine Vorsicherung");
    }

    // ── Replace samt Vorsicherung ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ADR-0024 E5: Ohne Ausweg kein Ueberschreiben. Geprueft wird beides — dass die Vorsicherung
    /// <b>entsteht</b> und dass sie ein <b>gueltiges Archiv</b> ist. Eine Datei, die nur so heisst,
    /// waere kein Ausweg.
    /// </summary>
    [Fact]
    public async Task Replace_overwrites_an_existing_instance_and_keeps_a_valid_pre_backup()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("ersetzen-quelle", ct);
        var archive = Path.Combine(_root, "ersetzen.zip");
        await new BackupService(source.Options)
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        var target = await NewInstanceAsync("ersetzen-ziel", ct);
        var targetBackup = new BackupService(target.Options);
        var restore = new RestoreService(target.Options, targetBackup);

        var refusal = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        refusal.CanApply.Should().BeFalse("ohne --replace laeuft ein Restore nur auf ein leeres Ziel");
        refusal.TargetIsEmpty.Should().BeFalse();

        var plan = await restore.PlanAsync(new RestoreRequest(archive, RestoreMode.Replace, Passphrase), ct);
        plan.CanApply.Should().BeTrue("Blocker: {0}", string.Join(" | ", plan.Blockers));
        plan.PreBackupPath.Should().NotBeNull("vor einem Replace entsteht eine Sicherung");

        var result = await restore.ApplyAsync(plan, ct);
        result.Applied.Should().BeTrue();
        result.PreBackupPath.Should().NotBeNull();
        File.Exists(result.PreBackupPath!).Should().BeTrue();

        var inspection = await targetBackup.InspectAsync(result.PreBackupPath!, Passphrase, ct);
        inspection.Valid.Should().BeTrue(
            "eine Vorsicherung, die kein gueltiges Archiv ist, waere kein Ausweg. Befunde: {0}",
            string.Join(" | ", inspection.Problems));
        inspection.Manifest!.Provider.Should().Be(DatabaseProvider.Postgres);

        // Im Ziel steht jetzt der Bestand der QUELLE — mit dem Schluesselring der Quelle, der
        // mitgereist ist.
        await UpgradePayloadWriter.VerifyAsync(
            FactoryFor(target.ConnectionString),
            UpgradeHarness.KeyRing(Path.Combine(target.Directory, "keys")),
            source.Payload,
            ct);
    }

    // ── Rueckwaerts-Tor ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ADR-0024 E6: Ein Archiv, das eine hoehere Mindestversion verlangt, wird <b>abgelehnt statt
    /// versucht</b> — und die Zielinstanz bleibt so, wie sie war.
    /// </summary>
    [Fact]
    public async Task An_archive_from_a_newer_version_is_refused_instead_of_attempted()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("zukunft-quelle", ct);
        var futureOptions = new BackupOptions
        {
            DataDirectory = source.Directory,
            Provider = DatabaseProvider.Postgres,
            PostgresConnectionString = source.ConnectionString,
            ProductVersion = "99.0.0",
            MinimumRestoreVersion = "99.0.0",
        };

        var archive = Path.Combine(_root, "zukunft.zip");
        await new BackupService(futureOptions)
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        var target = await NewEmptyTargetAsync("zukunft-ziel");
        var restore = new RestoreService(target.Options, new BackupService(target.Options));

        var plan = await restore.PlanAsync(new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);

        plan.CanApply.Should().BeFalse("rueckwaerts wird abgelehnt, nicht versucht (ADR-0024 E6)");
        plan.Blockers.Should().Contain(b => b.Contains("99.0.0", StringComparison.Ordinal));
        plan.Manifest.Should().NotBeNull("das Manifest wird gelesen — nur eben nicht angewendet");

        var apply = async () => await restore.ApplyAsync(plan, ct);
        await apply.Should().ThrowAsync<InvalidOperationException>();

        // „Abgelehnt statt versucht" heisst hier: In der Zieldatenbank steht danach nichts.
        (await CountUserTablesAsync(target.ConnectionString, ct)).Should().Be(
            0, "eine Absage, die trotzdem Tabellen anlegt, ist keine Absage");
        Directory.EnumerateFileSystemEntries(target.Directory).Should().BeEmpty();
    }

    /// <summary>
    /// Die zweite Haelfte des Tores: ein Migrationsstand, den dieser Build nicht kennt. Er ist eine
    /// Tatsache und keine Selbstauskunft des Archivs — deshalb haelt er auch dann, wenn die
    /// Mindestversion erreichbar aussieht.
    /// </summary>
    [Fact]
    public async Task An_archive_with_an_unknown_migration_is_refused()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var source = await NewInstanceAsync("fremdstand-quelle", ct);

        await using (var connection = new NpgsqlConnection(source.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") "
                + "VALUES ('99991231235959_ZukunftsSchema', '99.0.0')";
            await command.ExecuteNonQueryAsync(ct);
        }

        var archive = Path.Combine(_root, "fremdstand.zip");
        var created = await new BackupService(source.Options)
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);
        created.Manifest.MigrationId.Should().Be("99991231235959_ZukunftsSchema");

        var target = await NewEmptyTargetAsync("fremdstand-ziel");
        var restore = new RestoreService(target.Options, new BackupService(target.Options));
        var plan = await restore.PlanAsync(new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);

        plan.CanApply.Should().BeFalse(
            "ein Stand, den dieser Build nicht kennt, stammt aus einer neueren Instanz");
        plan.Blockers.Should().Contain(b => b.Contains("ZukunftsSchema", StringComparison.Ordinal));
        (await CountUserTablesAsync(target.ConnectionString, ct)).Should().Be(0);
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record Instance(
        string Directory,
        string ConnectionString,
        BackupOptions Options,
        UpgradePayload Payload,
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider Protection);

    private sealed record EmptyTarget(string Directory, string ConnectionString, BackupOptions Options);

    /// <summary>Eine vollstaendige Instanz: migrierte Datenbank, Schluesselring, Bestand, Konfiguration.</summary>
    private async Task<Instance> NewInstanceAsync(string name, CancellationToken ct)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);

        var connectionString = await NewDatabaseAsync(name);
        var factory = FactoryFor(connectionString);
        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.CreatedFromMigrations);

        var protection = UpgradeHarness.KeyRing(Path.Combine(directory, "keys"));
        var payload = await UpgradePayloadWriter.WriteAsync(
            factory, protection, UpgradeHarness.PublishedMigrations(BifrostDbOptions.Postgres), ct);

        var configDirectory = Path.Combine(directory, "config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "instance.json"),
            $$"""{"instanceId":"{{Guid.NewGuid()}}"}""",
            ct);

        return new Instance(directory, connectionString, OptionsFor(directory, connectionString), payload, protection);
    }

    private async Task<EmptyTarget> NewEmptyTargetAsync(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var connectionString = await NewDatabaseAsync(name);
        return new EmptyTarget(directory, connectionString, OptionsFor(directory, connectionString));
    }

    private static BackupOptions OptionsFor(string directory, string connectionString) => new()
    {
        DataDirectory = directory,
        Provider = DatabaseProvider.Postgres,
        PostgresConnectionString = connectionString,
        ProductVersion = BifrostProductInfo.Version,
        MinimumRestoreVersion = BackupLayout.DefaultMinimumRestoreVersion,

        // ADR-0024 E6: Ohne diese Menge kann das Rueckwaerts-Tor nicht pruefen und sagt das auch.
        KnownMigrationIds = KnownMigrations.For(DatabaseProvider.Postgres),
    };

    private async Task<string> NewDatabaseAsync(string name)
    {
        var sanitised = new string([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var dbName = $"bifrost_bk_{sanitised}_{Guid.NewGuid():N}";
        dbName = dbName[..Math.Min(dbName.Length, 60)];

        await using (var admin = new NpgsqlConnection(_fixture.Container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_fixture.Container.GetConnectionString())
        {
            Database = dbName,
        }.ConnectionString;
    }

    private static UpgradeDbFactory FactoryFor(string connectionString)
        => new(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Postgres, connectionString)
            .Options);

    /// <summary>Nutzlast, damit <c>pg_dump</c> messbar arbeitet — ohne sie waere jeder Abbruch zu spaet.</summary>
    private static async Task SeedBulkAsync(string connectionString, int megabytes, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ballast (id serial PRIMARY KEY, nutzlast bytea NOT NULL)";
            await create.ExecuteNonQueryAsync(ct);
        }

        var block = new byte[1024 * 1024];
        Random.Shared.NextBytes(block);

        for (var i = 0; i < megabytes; i++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ballast (nutzlast) VALUES (@p)";
            insert.Parameters.AddWithValue("p", block);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<long> CountUserTablesAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            """;
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static IReadOnlyList<string> EntryNames(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    private void MarkSkippedIfUnavailable()
        => Assert.SkipWhen(_fixture.UnavailableReason is not null, _fixture.UnavailableReason ?? string.Empty);
}
