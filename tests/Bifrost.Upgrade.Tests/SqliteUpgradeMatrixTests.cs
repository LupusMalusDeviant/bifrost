using AwesomeAssertions;

using Bifrost.Persistence;
using Bifrost.Persistence.Startup;
using Bifrost.Upgrade.Tests.Harness;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Bifrost.Upgrade.Tests;

/// <summary>
/// Die Upgrade-Matrix auf SQLite — dem Zero-Setup-Default und damit dem Provider, unter dem die
/// meisten Instanzen tatsaechlich hochgezogen werden.
///
/// <para>
/// Geprueft wird je Fixturestand: Das Schema kommt hinterher, der Bestand ist vollstaendig, und der
/// Geheimtext ist weiterhin lesbar. Was <b>nicht</b> geprueft wird, steht in
/// <c>docs/upgrade-matrix.md</c> — nicht hier als stiller weisser Fleck.
/// </para>
/// </summary>
public sealed class SqliteUpgradeMatrixTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"bifrost-upgrade-sqlite-{Guid.NewGuid():N}");

    /// <summary>
    /// Jeder veroeffentlichte Migrationsstand ist ein Matrixfeld. Die Liste kommt aus der
    /// Migrations-Assembly, nicht aus einer gepflegten Konstante: Eine neue Migration erweitert die
    /// Matrix damit von selbst, statt sie stillschweigend unvollstaendig zu lassen.
    /// </summary>
    public static TheoryData<string> PublishedMigrations()
    {
        var data = new TheoryData<string>();
        foreach (var migration in UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite))
        {
            data.Add(migration);
        }

        return data;
    }

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Aufraeumen ist kein Testergebnis.
        }

        return ValueTask.CompletedTask;
    }

    // ── Matrixfeld 1: leere Datenbank -> aktueller Stand ────────────────────────────────────────

    [Fact]
    public async Task Empty_database_reaches_the_current_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("leer");

        var outcome = await new DatabaseInitializer(factory).InitializeAsync(ct);

        outcome.Should().Be(DatabaseInitOutcome.CreatedFromMigrations);

        await using var db = await factory.CreateDbContextAsync(ct);
        (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
        (await db.Database.GetAppliedMigrationsAsync(ct))
            .Should().Equal(UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite));
    }

    // ── Matrixfeld 2: jeder veroeffentlichte Stand -> aktueller Stand, mit Bestand ──────────────

    [Theory]
    [MemberData(nameof(PublishedMigrations))]
    public async Task Published_migration_state_upgrades_without_losing_data(string fixtureMigration)
    {
        var ct = TestContext.Current.CancellationToken;
        var published = UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite);
        var (factory, _) = NewDatabase(Shorten(fixtureMigration));
        var protection = UpgradeHarness.KeyRing(Path.Combine(_directory, Shorten(fixtureMigration) + "-keys"));

        // 1. Fixture: gezielt bis zu dieser Migration hochziehen.
        await UpgradeHarness.CreateFixtureAsync(factory, fixtureMigration, ct);
        var applied = UpgradeHarness.Through(published, fixtureMigration);

        // 2. Bestand auf dem ALTEN Stand schreiben — durch die echten Stores.
        var payload = await UpgradePayloadWriter.WriteAsync(factory, protection, applied, ct);

        var pendingBefore = new List<string>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            pendingBefore.AddRange(await db.Database.GetPendingMigrationsAsync(ct));
            pendingBefore.Should().Equal(
                published.Skip(applied.Count),
                "das Fixture steht wirklich auf dem alten Stand — sonst prueft der Test nichts");
        }

        // 3. Das Upgrade: derselbe Startpfad, den auch der Dienst nimmt.
        var outcome = await new DatabaseInitializer(factory).InitializeAsync(ct);
        outcome.Should().Be(DatabaseInitOutcome.Migrated,
            "eine migrationsverwaltete Datenbank wird migriert, nicht neu angelegt oder gestempelt");

        // 4. Danach: Schema aktuell, Bestand vollstaendig, Geheimtext lesbar.
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
            (await db.Database.GetAppliedMigrationsAsync(ct)).Should().Equal(published);

            var journal = await MigrationJournal.ReadAllAsync(db, ct);
            if (pendingBefore.Count > 0)
            {
                journal.Should().ContainSingle("genau ein Migrationslauf hat stattgefunden");
                journal[0].State.Should().Be(MigrationRunState.Completed);
                journal[0].FromMigration.Should().Be(fixtureMigration);
                journal[0].ToMigration.Should().Be(published[^1]);
            }
            else
            {
                journal.Should().BeEmpty("ohne ausstehende Migration wird nichts vermerkt");
            }
        }

        await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);

        // 5. Ein zweiter Start ist folgenlos — ein Upgrade darf nicht bei jedem Start erneut laufen.
        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.Migrated);
        await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);
    }

    // ── Matrixfeld 2b: v1.0-Schema ohne Migrationshistorie -> aktueller Stand ──────────────────

    /// <summary>
    /// v1.0-Datenbanken entstanden per <c>EnsureCreated</c> und haben deshalb <b>keine</b>
    /// <c>__EFMigrationsHistory</c>. Der Startpfad stempelt eine Baseline, statt CREATE TABLE auf
    /// bestehende Tabellen zu fahren.
    /// <para>
    /// Dass die Baseline laeuft, haelt bereits WP3 fest. Neu ist hier der <b>verschluesselte</b>
    /// Bestand: Das Stempeln fasst Daten nicht an, und genau das muss auch fuer Geheimtext gelten.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Legacy_schema_without_history_is_baselined_and_keeps_its_ciphertext()
    {
        var ct = TestContext.Current.CancellationToken;
        var published = UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite);
        var (factory, _) = NewDatabase("v10-ohne-historie");
        var protection = UpgradeHarness.KeyRing(Path.Combine(_directory, "v10-keys"));

        await UpgradeHarness.CreateFixtureAsync(factory, published[0], ct);
        var payload = await UpgradePayloadWriter.WriteAsync(
            factory, protection, UpgradeHarness.Through(published, published[0]), ct);

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"__EFMigrationsHistory\"", ct);
            (await db.Database.GetAppliedMigrationsAsync(ct))
                .Should().BeEmpty("v1.0 kannte keine Migrationen");
        }

        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.BaselinedLegacySchema);

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
            (await db.Database.GetAppliedMigrationsAsync(ct)).Should().Equal(published);
        }

        await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);
    }

    /// <summary>
    /// Die Gegenprobe zu Matrixfeld 2: <b>ohne</b> das Upgrade bleibt der Stand alt. Ohne diese
    /// Zeile koennte die Behauptung „nach dem Upgrade steht nichts mehr aus" auch dann gruen sein,
    /// wenn der Initializer gar nichts tut.
    /// </summary>
    [Fact]
    public async Task Without_the_upgrade_the_fixture_still_reports_pending_migrations()
    {
        var ct = TestContext.Current.CancellationToken;
        var published = UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite);
        var (factory, _) = NewDatabase("gegenprobe-ausstehend");

        await UpgradeHarness.CreateFixtureAsync(factory, published[0], ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        (await db.Database.GetPendingMigrationsAsync(ct))
            .Should().HaveCount(published.Count - 1, "der Fixturestand ist wirklich aelter");
    }

    /// <summary>
    /// Die Gegenprobe zur Lesbarkeitspruefung: Wird der Geheimtext beschaedigt, schlaegt genau die
    /// Nachpruefung fehl, die im Matrixtest gruen ist. Damit steht fest, dass diese Zusage traegt
    /// und nicht nebenbei mitlaeuft.
    /// </summary>
    [Fact]
    public async Task Damaged_ciphertext_makes_the_very_same_check_fail()
    {
        var ct = TestContext.Current.CancellationToken;
        var published = UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite);
        var (factory, _) = NewDatabase("gegenprobe-geheimtext");
        var protection = UpgradeHarness.KeyRing(Path.Combine(_directory, "gegenprobe-keys"));

        await UpgradeHarness.CreateFixtureAsync(factory, published[0], ct);
        var payload = await UpgradePayloadWriter.WriteAsync(
            factory, protection, UpgradeHarness.Through(published, published[0]), ct);
        await new DatabaseInitializer(factory).InitializeAsync(ct);

        // Vorbedingung: So, wie es ist, ist es lesbar.
        await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);

        // Ein einziges Bit — mehr braucht ein Upgrade nicht kaputtzumachen.
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var row = await db.ConfigVersions.SingleAsync(
                r => r.ServerId == payload.Server.Value && r.Version == 2, ct);
            var damaged = row.Payload.ToArray();
            damaged[^1] ^= 0xFF;
            row.Payload = damaged;
            await db.SaveChangesAsync(ct);
        }

        var verify = async () => await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);
        await verify.Should().ThrowAsync<Exception>(
            "die Nachpruefung muss bei unlesbarem Geheimtext rot werden — sonst prueft sie nichts");
    }

    /// <summary>
    /// Die Gegenprobe zur Vollstaendigkeitspruefung: Verschwindet eine Zeile, wird dieselbe
    /// Nachpruefung rot. „Vollstaendig" und „lesbar" sind zwei Zusagen, und beide brauchen einen
    /// Beleg, dass sie ueberhaupt greifen.
    /// </summary>
    [Fact]
    public async Task A_lost_row_makes_the_very_same_check_fail()
    {
        var ct = TestContext.Current.CancellationToken;
        var published = UpgradeHarness.PublishedMigrations(BifrostDbOptions.Sqlite);
        var (factory, _) = NewDatabase("gegenprobe-zeilenverlust");
        var protection = UpgradeHarness.KeyRing(Path.Combine(_directory, "gegenprobe-zeilen-keys"));

        await UpgradeHarness.CreateFixtureAsync(factory, published[0], ct);
        var payload = await UpgradePayloadWriter.WriteAsync(
            factory, protection, UpgradeHarness.Through(published, published[0]), ct);
        await new DatabaseInitializer(factory).InitializeAsync(ct);
        await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.ConfigVersions
                .Where(r => r.ServerId == payload.Server.Value && r.Version == 1)
                .ExecuteDeleteAsync(ct);
        }

        var verify = async () => await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);
        await verify.Should().ThrowAsync<Exception>(
            "eine verlorene Version des Verlaufs muss auffallen, nicht durchrutschen");
    }

    private (IDbContextFactory<BifrostDbContext> Factory, string Path) NewDatabase(string name)
    {
        var path = Path.Combine(_directory, $"{name}.db");
        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}")
            .Options;
        return (new UpgradeDbFactory(options), path);
    }

    /// <summary>Der Migrationsname ohne Zeitstempel — als Dateiname lesbarer.</summary>
    private static string Shorten(string migrationId)
    {
        var underscore = migrationId.IndexOf('_', StringComparison.Ordinal);
        return underscore < 0 ? migrationId : migrationId[(underscore + 1)..];
    }
}
