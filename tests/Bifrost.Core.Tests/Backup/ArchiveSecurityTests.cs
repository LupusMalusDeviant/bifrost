using System.Text;

using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Persistence.Backup;

using Xunit;

namespace Bifrost.Core.Tests.Backup;

/// <summary>
/// WP2.2: Das Archiv ist Fremdeingabe. Zip-Slip, symbolische Verweise und ein Archiv, das sich nicht
/// an sein eigenes Format hält, werden abgewehrt, <b>bevor</b> etwas entpackt wird (ADR-0024 E5).
/// </summary>
public sealed class ArchiveSecurityTests
{
    private static RestoreService RestoreInto(InstanceDirectory target)
    {
        var options = target.Options();
        return new RestoreService(options, new BackupService(options));
    }

    [Fact]
    public async Task An_entry_that_escapes_the_target_directory_is_rejected()
    {
        using var target = new InstanceDirectory("zipslip");
        using var archives = new ArchiveDirectory();
        var archive = archives.File("boese.zip");

        SyntheticArchive.Write(
            archive,
            [
                (BackupLayout.DatabaseEntry, Encoding.UTF8.GetBytes("datenbank"), 0),
                ("packages/../../../uebernommen.txt", Encoding.UTF8.GetBytes("hier war ich"), 0),
            ],
            ["database", "packages"]);

        var restore = RestoreInto(target);
        var plan = await restore.PlanAsync(new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("Zielverzeichnis heraus", StringComparison.Ordinal));

        var act = async () => await restore.ApplyAsync(plan, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Nichts geschrieben — weder im Ziel noch daneben.
        Directory.EnumerateFileSystemEntries(target.Root).Should().BeEmpty();
        Directory.EnumerateFiles(Path.GetTempPath(), "uebernommen.txt").Should().BeEmpty();
    }

    [Fact]
    public async Task An_absolute_entry_path_is_rejected()
    {
        using var target = new InstanceDirectory("absolut");
        using var archives = new ArchiveDirectory();
        var archive = archives.File("absolut.zip");

        SyntheticArchive.Write(
            archive,
            [
                (BackupLayout.DatabaseEntry, Encoding.UTF8.GetBytes("datenbank"), 0),
                ("/etc/passwort", Encoding.UTF8.GetBytes("nein"), 0),
            ],
            ["database"]);

        var plan = await RestoreInto(target).PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_symbolic_link_entry_is_rejected()
    {
        using var target = new InstanceDirectory("symlink");
        using var archives = new ArchiveDirectory();
        var archive = archives.File("symlink.zip");

        SyntheticArchive.Write(
            archive,
            [
                (BackupLayout.DatabaseEntry, Encoding.UTF8.GetBytes("datenbank"), 0),
                ("keyring/verweis", Encoding.UTF8.GetBytes("/etc/shadow"), SyntheticArchive.SymbolicLinkAttributes),
            ],
            ["database", "keyring"]);

        var plan = await RestoreInto(target).PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("symbolischer Verweis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_outside_the_known_zones_is_rejected()
    {
        using var target = new InstanceDirectory("fremdzone");
        using var archives = new ArchiveDirectory();
        var archive = archives.File("fremd.zip");

        SyntheticArchive.Write(
            archive,
            [
                (BackupLayout.DatabaseEntry, Encoding.UTF8.GetBytes("datenbank"), 0),
                ("skripte/start.sh", Encoding.UTF8.GetBytes("rm -rf /"), 0),
            ],
            ["database"]);

        var plan = await RestoreInto(target).PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("außerhalb der bekannten Bereiche", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_archive_whose_manifest_is_not_first_is_rejected()
    {
        using var target = new InstanceDirectory("reihenfolge");
        using var archives = new ArchiveDirectory();
        var archive = archives.File("reihenfolge.zip");

        SyntheticArchive.Write(
            archive,
            [(BackupLayout.DatabaseEntry, Encoding.UTF8.GetBytes("datenbank"), 0)],
            ["database"],
            manifestFirst: false);

        var plan = await RestoreInto(target).PlanAsync(
            new RestoreRequest(archive), TestContext.Current.CancellationToken);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("Backupformat v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_reported_instead_of_opened()
    {
        using var target = new InstanceDirectory("keinzip");
        using var archives = new ArchiveDirectory();
        var archive = archives.File("kein.zip");
        await File.WriteAllTextAsync(archive, "PK ist das nicht", TestContext.Current.CancellationToken);

        var inspection = await new BackupService(target.Options())
            .InspectAsync(archive, null, TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeFalse();
        inspection.Manifest.Should().BeNull();
        inspection.Problems.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_missing_archive_is_reported_instead_of_thrown()
    {
        using var target = new InstanceDirectory("fehlt");
        var inspection = await new BackupService(target.Options())
            .InspectAsync(Path.Combine(target.Root, "gibtsnicht.zip"), null, TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeFalse();
        inspection.Problems.Should().ContainSingle();
    }
}
