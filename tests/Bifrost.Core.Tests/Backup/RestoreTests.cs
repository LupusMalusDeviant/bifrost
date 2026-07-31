using System.Text.Json;

using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Persistence.Backup;

using Xunit;

namespace Bifrost.Core.Tests.Backup;

/// <summary>
/// WP2.2: Der Restore ist nach Vorgabe ein Vorgang auf einer leeren Instanz (ADR-0024 E5). Alles
/// andere verlangt eine ausdrückliche Bestätigung — und dann eine Sicherung des Altzustands, bevor
/// überhaupt etwas ersetzt wird.
/// </summary>
public sealed class RestoreTests
{
    private static async Task<string> BackupOfAsync(
        InstanceDirectory source,
        ArchiveDirectory archives,
        int rows,
        string? passphrase = null,
        string? minimumRestoreVersion = null)
    {
        using (source.CreateDatabaseWithOpenWal(rows))
        {
            source.WriteKeyRing();
            source.WritePackage();
            source.WriteInstanceConfig("quelle");
        }

        var service = new BackupService(source.Options(minimumRestoreVersion: minimumRestoreVersion));
        var result = await service.CreateAsync(
            new BackupRequest(archives.File("quelle.zip"), BackupSections.All, passphrase),
            TestContext.Current.CancellationToken);
        return result.ArchivePath;
    }

    private static RestoreService RestoreInto(InstanceDirectory target)
    {
        var options = target.Options();
        return new RestoreService(options, new BackupService(options));
    }

    [Fact]
    public async Task A_restore_into_an_empty_target_brings_everything_back()
    {
        using var source = new InstanceDirectory("quelle");
        using var target = new InstanceDirectory("ziel");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 120);

        var restore = RestoreInto(target);
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.TargetIsEmpty.Should().BeTrue();
        plan.Blockers.Should().BeEmpty();
        plan.CanApply.Should().BeTrue();
        plan.PreBackupPath.Should().BeNull("auf einem leeren Ziel gibt es nichts zu sichern");

        var result = await restore.ApplyAsync(plan, TestContext.Current.CancellationToken);

        result.Applied.Should().BeTrue();
        result.RestoredSections.Should().Be(BackupSections.All);
        InstanceDirectory.CountRows(target.DatabaseFile).Should().Be(120);
        Directory.EnumerateFiles(target.KeyRingDirectory).Should().HaveCount(2);
        File.Exists(Path.Combine(target.PackagesDirectory, "demo", "manifest.txt")).Should().BeTrue();
        (await File.ReadAllTextAsync(target.InstanceConfigFile, TestContext.Current.CancellationToken))
            .Should().Contain("quelle");
    }

    [Fact]
    public async Task A_populated_target_blocks_the_restore_unless_replace_was_chosen()
    {
        using var source = new InstanceDirectory("quelle2");
        using var target = new InstanceDirectory("belegt");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 30);

        using (target.CreateDatabaseWithOpenWal(rows: 7))
        {
            target.WriteKeyRing(keys: 1);
        }

        var restore = RestoreInto(target);
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.TargetIsEmpty.Should().BeFalse();
        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("nicht leer", StringComparison.Ordinal));

        // Und der Plan lässt sich auch nicht trotzdem anwenden.
        var act = async () => await restore.ApplyAsync(plan, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        InstanceDirectory.CountRows(target.DatabaseFile).Should().Be(7, "am Ziel wurde nichts angefasst");
    }

    [Fact]
    public async Task Replace_secures_the_previous_state_before_overwriting_it()
    {
        using var source = new InstanceDirectory("quelle3");
        using var target = new InstanceDirectory("ersetzen");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 200);

        using (target.CreateDatabaseWithOpenWal(rows: 9))
        {
            target.WriteKeyRing(keys: 1);
            target.WritePackage("alt");
        }

        var restore = RestoreInto(target);
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.Replace), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeTrue(string.Join(" | ", plan.Blockers));
        plan.TargetIsEmpty.Should().BeFalse();
        plan.PreBackupPath.Should().NotBeNull("ohne Ausweg kein Überschreiben");

        var result = await restore.ApplyAsync(plan, TestContext.Current.CancellationToken);

        result.Applied.Should().BeTrue();
        result.PreBackupPath.Should().NotBeNull();
        File.Exists(result.PreBackupPath!).Should().BeTrue();
        InstanceDirectory.CountRows(target.DatabaseFile).Should().Be(200);
        Directory.Exists(Path.Combine(target.PackagesDirectory, "alt")).Should().BeFalse("das Ziel wurde ersetzt");

        // Die Sicherung des Altzustands ist ein vollwertiges, prüfbares Archiv.
        var check = await new BackupService(target.Options())
            .InspectAsync(result.PreBackupPath!, null, TestContext.Current.CancellationToken);
        check.Valid.Should().BeTrue(string.Join(" | ", check.Problems));

        var previous = Path.Combine(archives.Root, "vorher.db");
        File.WriteAllBytes(
            previous, ArchiveSurgery.ReadEntryBytes(result.PreBackupPath!, BackupLayout.DatabaseEntry));
        InstanceDirectory.CountRows(previous).Should().Be(9, "der Altzustand ist erhalten geblieben");
    }

    [Fact]
    public async Task An_encrypted_archive_with_a_wrong_passphrase_restores_nothing()
    {
        using var source = new InstanceDirectory("quelle4");
        using var target = new InstanceDirectory("krypto-ziel");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 40, passphrase: "richtig");

        var restore = RestoreInto(target);
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, "falsch"),
            TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("falsche Passphrase", StringComparison.Ordinal));

        var act = async () => await restore.ApplyAsync(plan, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        File.Exists(target.DatabaseFile).Should().BeFalse("kein Teil-Restore");
        Directory.Exists(target.KeyRingDirectory).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(target.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task An_encrypted_archive_with_the_right_passphrase_restores_completely()
    {
        using var source = new InstanceDirectory("quelle5");
        using var target = new InstanceDirectory("krypto-ok");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 40, passphrase: "richtig");

        var restore = RestoreInto(target);
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive, RestoreMode.EmptyTargetOnly, "richtig"),
            TestContext.Current.CancellationToken);
        plan.CanApply.Should().BeTrue(string.Join(" | ", plan.Blockers));

        var result = await restore.ApplyAsync(plan, TestContext.Current.CancellationToken);

        result.Applied.Should().BeTrue();
        InstanceDirectory.CountRows(target.DatabaseFile).Should().Be(40);
        (await File.ReadAllTextAsync(
                Path.Combine(target.KeyRingDirectory, "key-0.xml"), TestContext.Current.CancellationToken))
            .Should().Contain("geheim-0");
    }

    [Fact]
    public async Task An_archive_that_demands_a_newer_product_version_is_refused()
    {
        using var source = new InstanceDirectory("quelle6");
        using var target = new InstanceDirectory("zu-alt");
        using var archives = new ArchiveDirectory();

        // Das Archiv verlangt 99.0.0 — diese Installation ist 0.11.0 (ADR-0024 E6: rückwärts nein).
        var archive = await BackupOfAsync(source, archives, rows: 5, minimumRestoreVersion: "99.0.0");

        var restore = RestoreInto(target);
        var plan = await restore.PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("99.0.0", StringComparison.Ordinal));
        Directory.EnumerateFileSystemEntries(target.Root).Should().BeEmpty("die Vorprüfung schreibt nichts");
    }

    [Fact]
    public async Task A_plan_from_another_service_instance_is_refused_instead_of_guessed()
    {
        using var source = new InstanceDirectory("quelle7");
        using var target = new InstanceDirectory("fremd");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 5);

        var plan = await RestoreInto(target).PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        var act = async () => await RestoreInto(target).ApplyAsync(plan, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Der Grund, warum der Plan ein Handle trägt. Über eine HTTP-Schnittstelle geht er als JSON
    /// hinaus und kommt als <em>neues Objekt</em> zurück — an der Objektidentität wiedererkannt
    /// wäre er dort niemals anwendbar, und ein Restore über die API grundsätzlich unmöglich.
    /// </summary>
    [Fact]
    public async Task A_plan_that_travelled_as_json_is_still_applicable()
    {
        using var source = new InstanceDirectory("quelle8");
        using var target = new InstanceDirectory("ziel8");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 7);

        var service = RestoreInto(target);
        var plan = await service.PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);
        plan.CanApply.Should().BeTrue();

        var travelled = JsonSerializer.Deserialize<RestorePlan>(JsonSerializer.Serialize(plan))!;
        travelled.Should().NotBeSameAs(plan);
        travelled.Token.Should().Be(plan.Token);

        var result = await service.ApplyAsync(travelled, TestContext.Current.CancellationToken);

        result.Applied.Should().BeTrue();
    }

    /// <summary>
    /// Das Rückwärts-Tor aus ADR-0024 E6, zweiter Anlauf. Der Versionsvergleich allein hielt nicht:
    /// Die Mindestversion im Manifest ist eine Angabe des Archivs über sich selbst und stand für
    /// jedes Archiv auf demselben Wert — ein Archiv aus einer neueren Instanz kam damit durch und
    /// wurde eingespielt. Geprüft wird jetzt der Migrationsstand, denn der ist eine Tatsache.
    /// </summary>
    [Fact]
    public async Task An_archive_from_a_newer_schema_is_refused_before_anything_is_written()
    {
        using var source = new InstanceDirectory("quelle10");
        using var target = new InstanceDirectory("ziel10");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 4);

        // Das Ziel kennt eine ANDERE Migration als die im Archiv — genau die Lage einer Instanz,
        // der man ein Archiv aus einer neueren Version unterschiebt.
        var options = target.Options(
            knownMigrationIds: new HashSet<string>(StringComparer.Ordinal) { "20250101000000_Aelter" });
        var plan = await new RestoreService(options, new BackupService(options))
            .PlanAsync(new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("20260731000000_Initial", StringComparison.Ordinal));
        Directory.EnumerateFileSystemEntries(target.Root).Should().BeEmpty(
            "abgelehnt heißt: nicht versucht — die Vorprüfung schreibt nichts");
    }

    /// <summary>
    /// Die Gegenprobe. Dasselbe Archiv, nur ist der Stand diesmal bekannt — damit ist belegt, dass
    /// die Ablehnung oben aus dem Migrationsvergleich kam und nicht aus irgendetwas anderem.
    /// </summary>
    [Fact]
    public async Task The_same_archive_is_applied_when_the_schema_is_known()
    {
        using var source = new InstanceDirectory("quelle11");
        using var target = new InstanceDirectory("ziel11");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 4);

        var options = target.Options(
            knownMigrationIds: new HashSet<string>(StringComparer.Ordinal) { "20260731000000_Initial" });
        var plan = await new RestoreService(options, new BackupService(options))
            .PlanAsync(new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeTrue(string.Join(" | ", plan.Blockers));
    }

    /// <summary>
    /// Ein Schutz, der still ausfällt, ist schlimmer als keiner: Wer die bekannten Migrationen nicht
    /// mitgibt, bekommt das gesagt, statt ein ungeprüftes Archiv als geprüft gemeldet zu bekommen.
    /// </summary>
    [Fact]
    public async Task Without_the_known_migrations_the_gate_says_so_instead_of_staying_silent()
    {
        using var source = new InstanceDirectory("quelle12");
        using var target = new InstanceDirectory("ziel12");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 4);

        var plan = await RestoreInto(target)
            .PlanAsync(new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.Warnings.Should().Contain(w => w.Contains("Migrationsstand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_is_applied_at_most_once()
    {
        using var source = new InstanceDirectory("quelle9");
        using var target = new InstanceDirectory("ziel9");
        using var archives = new ArchiveDirectory();
        var archive = await BackupOfAsync(source, archives, rows: 3);

        var service = RestoreInto(target);
        var plan = await service.PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);
        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        var again = async () => await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        await again.Should().ThrowAsync<InvalidOperationException>(
            "ein verbrauchtes Handle träfe beim zweiten Lauf eine Instanz, die der Plan nie geprüft hat");
    }
}
