using System.Security.Cryptography;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Persistence;
using Bifrost.Persistence.Backup;
using Bifrost.Persistence.Startup;
using Bifrost.Upgrade.Tests.Harness;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Bifrost.Upgrade.Tests;

/// <summary>
/// Der Weg ueber das Archiv (ADR-0024 E6): Ein Backup einer <b>aelteren</b> Version darf in eine
/// neuere zurueckgespielt werden, die Migration laeuft danach. Der umgekehrte Weg wird
/// <b>abgelehnt</b>, nicht versucht.
///
/// <para>
/// Diese Suite ist der einzige Ort, an dem Datenbank <i>und</i> Schluesselring zusammen reisen —
/// und damit der einzige, an dem sich zeigen laesst, dass ein Restore nicht nur Zeilen, sondern
/// auch deren Lesbarkeit wiederherstellt (ADR-0024 E3).
/// </para>
/// </summary>
public sealed class BackupRestoreUpgradeTests : IAsyncLifetime
{
    private const string Passphrase = "eine-passphrase-fuer-ein-vollbackup";

    /// <summary>
    /// Eine bewusst alte Produktversion des Quellarchivs. 0.10.0 liegt unter der heutigen 0.11.0 —
    /// genau der Fall, den E6 erlauben muss.
    /// </summary>
    private const string OlderProductVersion = "0.10.0";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"bifrost-upgrade-archiv-{Guid.NewGuid():N}");

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
            // Aufraeumen ist kein Testergebnis.
        }

        return ValueTask.CompletedTask;
    }

    // ── Matrixfeld 3: Backup einer aelteren Version -> Restore -> Migration laeuft danach ───────

    [Fact]
    public async Task Backup_of_an_older_version_restores_and_migrates_afterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await NewSourceInstanceAsync("quelle", ct);

        var archive = Path.Combine(_root, "alt.zip");
        var created = await new BackupService(BackupOptionsFor(source.Directory, OlderProductVersion, OlderProductVersion))
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        created.Manifest.ProductVersion.Should().Be(OlderProductVersion);
        created.Manifest.MigrationId.Should().Be(
            source.FixtureMigration, "das Manifest sagt, auf welchem Schemastand das Archiv steht");
        created.Manifest.Encrypted.Should().BeTrue("ein Vollbackup traegt den Schluesselring");
        created.Manifest.Sections.Should().Be(BackupSections.All);

        // Die Zielinstallation ist die heutige — leer, unberuehrt.
        var target = Path.Combine(_root, "ziel");
        Directory.CreateDirectory(target);
        var targetOptions = BackupOptionsFor(target, BifrostProductInfo.Version, BackupLayout.DefaultMinimumRestoreVersion);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));

        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        plan.CanApply.Should().BeTrue(
            "vorwaerts ist erlaubt (ADR-0024 E6); Blocker: {0}", string.Join(" | ", plan.Blockers));
        plan.TargetIsEmpty.Should().BeTrue();

        var result = await restore.ApplyAsync(plan, ct);
        result.Applied.Should().BeTrue();
        result.RestoredSections.Should().HaveFlag(BackupSections.Database);
        result.RestoredSections.Should().HaveFlag(BackupSections.KeyRing);
        result.RestoredSections.Should().HaveFlag(BackupSections.Config);

        // Vor dem Start: Der wiederhergestellte Stand ist wirklich der alte.
        var restored = SqliteFactory(Path.Combine(target, "bifrost.db"));
        await using (var db = await restored.CreateDbContextAsync(ct))
        {
            (await db.Database.GetPendingMigrationsAsync(ct))
                .Should().NotBeEmpty("sonst waere das Archiv gar nicht aelter gewesen");
        }

        // Und jetzt der Punkt aus E6: die Migration laeuft DANACH.
        (await new DatabaseInitializer(restored).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.Migrated);

        await using (var db = await restored.CreateDbContextAsync(ct))
        {
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
        }

        // Der Bestand ist vollstaendig UND lesbar — der Schluesselring ist mitgereist.
        var restoredProtection = UpgradeHarness.KeyRing(Path.Combine(target, "keys"));
        await UpgradePayloadWriter.VerifyAsync(restored, restoredProtection, source.Payload, ct);
    }

    /// <summary>
    /// Die Gegenprobe zu E3, und zugleich der Nachweis, dass die Lesbarkeitspruefung des vorigen
    /// Tests traegt: Fehlt der Schluesselring im Archiv, kommen die Zeilen zurueck und ihr Inhalt
    /// nicht. Ein Restore, der das nicht bemerkt, gaebe eine Instanz zurueck, die erst beim ersten
    /// Upstream-Verbindungsaufbau auffliegt.
    /// </summary>
    [Fact]
    public async Task Without_the_key_ring_the_restored_rows_are_unreadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await NewSourceInstanceAsync("ohne-ring-quelle", ct);

        var archive = Path.Combine(_root, "ohne-ring.zip");
        await new BackupService(BackupOptionsFor(source.Directory, OlderProductVersion, OlderProductVersion))
            .CreateAsync(
                new BackupRequest(archive, BackupSections.Database | BackupSections.Config, Passphrase), ct);

        var target = Path.Combine(_root, "ohne-ring-ziel");
        Directory.CreateDirectory(target);
        var targetOptions = BackupOptionsFor(target, BifrostProductInfo.Version, BackupLayout.DefaultMinimumRestoreVersion);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));

        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        plan.CanApply.Should().BeTrue("Blocker: {0}", string.Join(" | ", plan.Blockers));
        (await restore.ApplyAsync(plan, ct)).RestoredSections
            .Should().NotHaveFlag(BackupSections.KeyRing);

        var restored = SqliteFactory(Path.Combine(target, "bifrost.db"));
        (await new DatabaseInitializer(restored).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.Migrated, "die Zeilen kommen sehr wohl durch");

        // Die Zeilen sind da …
        await using (var db = await restored.CreateDbContextAsync(ct))
        {
            (await db.ConfigVersions.CountAsync(r => r.ServerId == source.Payload.Server.Value, ct))
                .Should().Be(2);
        }

        // … und trotzdem ist der Bestand verloren.
        var freshRing = UpgradeHarness.KeyRing(Path.Combine(target, "keys"));
        var read = async () => await new EfUpstreamConfigStore(restored, freshRing)
            .GetVersionAsync(source.Payload.Server, new ConfigVersionId(2), ct);
        await read.Should().ThrowAsync<CryptographicException>(
            "ohne den Schluesselring ist der Geheimtext unbrauchbar — und genau das muss auffallen");
    }

    // ── Matrixfeld 4: Backup einer NEUEREN Version wird abgelehnt (E6 rueckwaerts) ──────────────

    [Fact]
    public async Task Backup_of_a_newer_version_is_refused_instead_of_attempted()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await NewSourceInstanceAsync("zukunft-quelle", ct);

        // Ein Archiv, das eine hoehere Mindestversion verlangt, als diese Installation hat.
        var archive = Path.Combine(_root, "zukunft.zip");
        await new BackupService(BackupOptionsFor(source.Directory, "99.0.0", "99.0.0"))
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        var target = Path.Combine(_root, "zukunft-ziel");
        Directory.CreateDirectory(target);
        var targetOptions = BackupOptionsFor(target, BifrostProductInfo.Version, BackupLayout.DefaultMinimumRestoreVersion);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));

        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);

        plan.CanApply.Should().BeFalse("rueckwaerts wird abgelehnt, nicht versucht (ADR-0024 E6)");
        plan.Blockers.Should().Contain(b => b.Contains("99.0.0", StringComparison.Ordinal));
        plan.Manifest.Should().NotBeNull("das Manifest wird gelesen — nur eben nicht angewendet");

        var apply = async () => await restore.ApplyAsync(plan, ct);
        await apply.Should().ThrowAsync<InvalidOperationException>();

        // „Abgelehnt statt versucht" heisst: Es steht nichts im Ziel. Nicht halb, nicht als Rest.
        File.Exists(Path.Combine(target, "bifrost.db")).Should().BeFalse();
        Directory.Exists(Path.Combine(target, "keys")).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(target).Should().BeEmpty(
            "eine Absage, die trotzdem Dateien hinterlaesst, ist keine Absage");
    }

    /// <summary>
    /// Die Gegenprobe zur Absage: <b>dasselbe</b> Archiv, nur mit einer Mindestversion, die diese
    /// Installation erfuellt — und es wird angewendet. Damit steht fest, dass die Ablehnung aus dem
    /// Versionsvergleich kam und nicht aus irgendetwas anderem am Archiv.
    /// <para>
    /// Nebenbefund, der in <c>docs/upgrade-matrix.md</c> steht: Das Tor ist
    /// <c>minimumRestoreVersion</c>, nicht <c>productVersion</c>. Ein Archiv aus 99.0.0, das eine
    /// niedrige Mindestversion behauptet, wird angenommen — mit Warnung, aber angenommen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_same_archive_with_a_reachable_minimum_version_is_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await NewSourceInstanceAsync("gegenprobe-quelle", ct);

        var archive = Path.Combine(_root, "gegenprobe.zip");
        await new BackupService(
                BackupOptionsFor(source.Directory, "99.0.0", BackupLayout.DefaultMinimumRestoreVersion))
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        var target = Path.Combine(_root, "gegenprobe-ziel");
        Directory.CreateDirectory(target);
        var targetOptions = BackupOptionsFor(target, BifrostProductInfo.Version, BackupLayout.DefaultMinimumRestoreVersion);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));

        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);

        plan.CanApply.Should().BeTrue(
            "nur die Mindestversion hat sich geaendert; Blocker: {0}", string.Join(" | ", plan.Blockers));
        plan.Warnings.Should().Contain(w => w.Contains("99.0.0", StringComparison.Ordinal),
            "eine neuere Herkunft bleibt eine Warnung — sie ist nur kein Riegel");
        (await restore.ApplyAsync(plan, ct)).Applied.Should().BeTrue();
    }

    /// <summary>
    /// <b>Der unangenehme Befund dieser Welle, als Test festgehalten.</b>
    ///
    /// <para>
    /// Das Tor aus E6 prueft die <i>selbst behauptete</i> <c>minimumRestoreVersion</c> des Archivs.
    /// <see cref="BackupOptions.MinimumRestoreVersion"/> ist heute eine Konstante (0.11.0), die keine
    /// Version anhebt. Ein Archiv aus einer spaeteren Version traegt darum <b>dieselbe</b>
    /// Mindestangabe — und laeuft an dem Tor vorbei, obwohl sein Schema neuer ist.
    /// </para>
    ///
    /// <para>
    /// Aufgehalten wird es erst eine Stufe spaeter: Der Start erkennt Migrationen, die er nicht
    /// kennt, und verweigert mit <c>BFR-DB-0102</c>. Der Schaden ist damit begrenzt, aber er ist
    /// nicht dort begrenzt, wo ADR-0024 E6 ihn begrenzt sehen wollte — das Archiv ist zu diesem
    /// Zeitpunkt bereits eingespielt. Siehe <c>docs/upgrade-matrix.md</c>, Abschnitt „Luecken".
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_archive_with_a_newer_schema_passes_the_version_gate_and_is_stopped_at_the_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await NewSourceInstanceAsync("zukunftsschema-quelle", ct);

        // Eine Migration eintragen, die dieser Stand nicht kennt — das Bild einer Datenbank, die von
        // einer neueren Version angefasst wurde (dieselbe Nachstellung wie in MigrationSafetyTests).
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(source.Directory, "bifrost.db")}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") "
                + "VALUES ('99991231235959_ZukunftsSchema', '99.0.0')";
            await command.ExecuteNonQueryAsync(ct);
        }

        SqliteConnection.ClearAllPools();

        // Das Archiv behauptet die heutige Mindestversion — genau das, was BackupOptions heute tut.
        var archive = Path.Combine(_root, "zukunftsschema.zip");
        var created = await new BackupService(
                BackupOptionsFor(source.Directory, BifrostProductInfo.Version, BackupLayout.DefaultMinimumRestoreVersion))
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);
        created.Manifest.MigrationId.Should().Be("99991231235959_ZukunftsSchema");

        var target = Path.Combine(_root, "zukunftsschema-ziel");
        Directory.CreateDirectory(target);
        var targetOptions = BackupOptionsFor(target, BifrostProductInfo.Version, BackupLayout.DefaultMinimumRestoreVersion);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));

        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        plan.CanApply.Should().BeTrue(
            "das Versionstor prueft nur die behauptete Mindestversion, nicht den Schemastand — "
            + "das ist der Befund, nicht der Wunsch");
        (await restore.ApplyAsync(plan, ct)).Applied.Should().BeTrue();

        // Erst hier faellt es auf — und dann wenigstens hart.
        var restored = SqliteFactory(Path.Combine(target, "bifrost.db"));
        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => new DatabaseInitializer(restored).InitializeAsync(ct));

        error.Code.Should().Be(MigrationDiagnosticCodes.UnknownNewerSchema);
        error.SafeDetails["unknownMigrations"].Should().Contain("ZukunftsSchema");
        error.Remediation.Should().Contain("Downgrade");
    }

    /// <summary>
    /// Ein verbrauchtes Handle laesst sich nicht erneut anwenden. Ohne diese Zusage koennte ein
    /// zweiter Lauf auf eine Instanz treffen, die der Plan nie geprueft hat — beim Upgrade genau der
    /// Fall, in dem die Zielinstanz zwischen den beiden Laeufen nicht mehr leer ist.
    /// </summary>
    [Fact]
    public async Task A_used_restore_handle_cannot_be_replayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await NewSourceInstanceAsync("handle-quelle", ct);

        var archive = Path.Combine(_root, "handle.zip");
        await new BackupService(BackupOptionsFor(source.Directory, OlderProductVersion, OlderProductVersion))
            .CreateAsync(new BackupRequest(archive, BackupSections.All, Passphrase), ct);

        var target = Path.Combine(_root, "handle-ziel");
        Directory.CreateDirectory(target);
        var targetOptions = BackupOptionsFor(target, BifrostProductInfo.Version, BackupLayout.DefaultMinimumRestoreVersion);
        var restore = new RestoreService(targetOptions, new BackupService(targetOptions));

        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, Passphrase), ct);
        (await restore.ApplyAsync(plan, ct)).Applied.Should().BeTrue();

        var again = async () => await restore.ApplyAsync(plan, ct);
        await again.Should().ThrowAsync<InvalidOperationException>("ein Handle ist einmalig");
    }

    /// <summary>
    /// Fuer PostgreSQL gibt es kein Archiv — und damit auch kein Matrixfeld „Backup einer aelteren
    /// Version". Dieser Test haelt die <b>Absage</b> fest, nicht eine Faehigkeit: Solange
    /// <c>pg_dump</c> nicht gebaut ist, muss der Aufruf scheitern statt still etwas Halbes zu
    /// erzeugen (ADR-0024 E2).
    /// </summary>
    [Fact]
    public async Task Postgres_backup_is_refused_rather_than_faked()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(_root, "pg-absage");
        Directory.CreateDirectory(directory);

        var options = new BackupOptions
        {
            DataDirectory = directory,
            Provider = DatabaseProvider.Postgres,
        };

        var act = async () => await new BackupService(options)
            .CreateAsync(new BackupRequest(Path.Combine(directory, "pg.zip")), ct);

        (await act.Should().ThrowAsync<NotSupportedException>()).Which
            .Message.Should().Contain("pg_dump");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record SourceInstance(
        string Directory,
        string FixtureMigration,
        UpgradePayload Payload,
        IDataProtectionProvider Protection);

    /// <summary>
    /// Eine vollstaendige Instanz auf einem <b>aelteren</b> Schemastand: Datenbank, Schluesselring,
    /// Instanzkonfiguration, ein Connector-Paket. Als Fixturestand dient die Migration vor der
    /// letzten — so ist garantiert etwas zu migrieren, und der Bestand deckt auch die Tabellen ab,
    /// die es erst seit der Mitte der Reihe gibt.
    /// </summary>
    private async Task<SourceInstance> NewSourceInstanceAsync(string name, CancellationToken ct)
    {
        var published = UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite);
        var fixtureMigration = published[^2];

        var directory = Path.Combine(_root, name);
        System.IO.Directory.CreateDirectory(directory);

        var factory = SqliteFactory(Path.Combine(directory, "bifrost.db"));
        await UpgradeHarness.CreateFixtureAsync(factory, fixtureMigration, ct);

        var protection = UpgradeHarness.KeyRing(Path.Combine(directory, "keys"));
        var payload = await UpgradePayloadWriter.WriteAsync(
            factory, protection, UpgradeHarness.Through(published, fixtureMigration), ct);

        var configDirectory = Path.Combine(directory, "config");
        System.IO.Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "instance.json"),
            $$"""{"instanceId":"{{Guid.NewGuid()}}"}""",
            ct);

        var packageDirectory = Path.Combine(directory, "packages", "com.example.paket");
        System.IO.Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "manifest.json"), "{}", ct);

        // Die Verbindung muss los sein, bevor die Online-Backup-API die Datei anfasst.
        SqliteConnection.ClearAllPools();
        return new SourceInstance(directory, fixtureMigration, payload, protection);
    }

    private static BackupOptions BackupOptionsFor(
        string directory, string productVersion, string minimumRestoreVersion) => new()
        {
            DataDirectory = directory,
            Provider = DatabaseProvider.Sqlite,
            ProductVersion = productVersion,
            MinimumRestoreVersion = minimumRestoreVersion,
        };

    private static UpgradeDbFactory SqliteFactory(string path)
        => new(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}")
            .Options);
}
