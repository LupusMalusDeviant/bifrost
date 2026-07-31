using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Persistence.Backup;

using Xunit;

namespace Bifrost.Core.Tests.Backup;

/// <summary>
/// WP2.1: <c>InspectAsync</c> beurteilt ein Archiv, <b>ohne</b> es zurückzuspielen (ADR-0024 E4).
/// Ein Teilarchiv, ein verändertes Archiv und ein Archiv mit falscher Passphrase müssen sich hier
/// zeigen — sonst zeigt sich das erst im Ernstfall.
/// </summary>
public sealed class BackupInspectionTests
{
    private static async Task<(string Archive, BackupService Service, InstanceDirectory Instance, ArchiveDirectory Archives)>
        MakeBackupAsync(string label, string? passphrase = null)
    {
        var instance = new InstanceDirectory(label);
        var archives = new ArchiveDirectory();
        using (instance.CreateDatabaseWithOpenWal(rows: 50))
        {
            instance.WriteKeyRing();
            instance.WritePackage();
            instance.WriteInstanceConfig($"instanz-{label}");
            var service = new BackupService(instance.Options());
            var result = await service.CreateAsync(
                new BackupRequest(archives.File("voll.zip"), BackupSections.All, passphrase),
                TestContext.Current.CancellationToken);
            return (result.ArchivePath, service, instance, archives);
        }
    }

    [Fact]
    public async Task A_freshly_written_archive_is_valid()
    {
        var (archive, service, instance, archives) = await MakeBackupAsync("gut");
        using var _ = instance;
        using var __ = archives;

        var inspection = await service.InspectAsync(archive, null, TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeTrue(string.Join(" | ", inspection.Problems));
        inspection.Manifest!.FormatVersion.Should().Be(BackupLayout.FormatVersion);
        inspection.Manifest.Encrypted.Should().BeFalse();
        inspection.Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tampered_checksum_makes_the_archive_invalid()
    {
        var (archive, service, instance, archives) = await MakeBackupAsync("checksumme");
        using var _ = instance;
        using var __ = archives;

        var checksums = JsonNode.Parse(ArchiveSurgery.ReadEntryText(archive, BackupLayout.ChecksumEntry))!;
        checksums["entries"]!.AsObject()[BackupLayout.DatabaseEntry] = new string('A', 64);
        ArchiveSurgery.ReplaceEntry(
            archive, BackupLayout.ChecksumEntry, Encoding.UTF8.GetBytes(checksums.ToJsonString()));

        var inspection = await service.InspectAsync(archive, null, TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeFalse();
        inspection.Problems.Should().Contain(p => p.Contains(BackupLayout.DatabaseEntry, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tampered_payload_makes_the_archive_invalid()
    {
        var (archive, service, instance, archives) = await MakeBackupAsync("nutzlast");
        using var _ = instance;
        using var __ = archives;

        ArchiveSurgery.ReplaceEntry(archive, BackupLayout.DatabaseEntry, Encoding.UTF8.GetBytes("keine datenbank"));

        var inspection = await service.InspectAsync(archive, null, TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeFalse();
        inspection.Problems.Should().Contain(p => p.Contains("Prüfsumme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_checksum_list_makes_the_archive_invalid()
    {
        var (archive, service, instance, archives) = await MakeBackupAsync("unvollstaendig");
        using var _ = instance;
        using var __ = archives;

        using (var zip = System.IO.Compression.ZipFile.Open(
                   archive, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry(BackupLayout.ChecksumEntry)!.Delete();
        }

        var inspection = await service.InspectAsync(archive, null, TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeFalse();
        inspection.Problems.Should().Contain(p => p.Contains("checksums.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_encrypted_archive_keeps_its_manifest_readable_and_its_payload_secret()
    {
        var (archive, service, instance, archives) = await MakeBackupAsync("krypto", "richtige-passphrase");
        using var _ = instance;
        using var __ = archives;

        // Das Manifest liegt unverschlüsselt im Archiv — sonst könnte ein Werkzeug nicht prüfen,
        // was es vor sich hat (ADR-0024 E3).
        var manifestText = ArchiveSurgery.ReadEntryText(archive, BackupLayout.ManifestEntry);
        manifestText.Should().Contain(BackupLayout.EncryptionAesGcm);
        manifestText.Should().Contain(BackupLayout.Kdf);

        // Die Nutzlast dagegen darf nirgends im Klartext auftauchen.
        var raw = await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(raw).Should().NotContain("geheim-0");

        var inspection = await service.InspectAsync(
            archive, "richtige-passphrase", TestContext.Current.CancellationToken);
        inspection.Valid.Should().BeTrue(string.Join(" | ", inspection.Problems));
        inspection.Manifest!.Encrypted.Should().BeTrue();
    }

    [Fact]
    public async Task An_encrypted_archive_with_a_wrong_passphrase_says_so_clearly()
    {
        var (archive, service, instance, archives) = await MakeBackupAsync("falsch", "richtige-passphrase");
        using var _ = instance;
        using var __ = archives;

        var inspection = await service.InspectAsync(
            archive, "falsche-passphrase", TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeFalse();
        inspection.Problems.Should().ContainSingle(
            "eine falsche Passphrase scheitert an jedem Eintrag — eine Meldung genügt");
        inspection.Problems[0].Should().Contain("falsche Passphrase");
    }

    [Fact]
    public async Task A_passphrase_for_an_unencrypted_archive_is_reported_instead_of_ignored()
    {
        var (archive, service, instance, archives) = await MakeBackupAsync("ohne");
        using var _ = instance;
        using var __ = archives;

        var inspection = await service.InspectAsync(archive, "wozu", TestContext.Current.CancellationToken);

        inspection.Valid.Should().BeFalse();
        inspection.Problems.Should().Contain(p => p.Contains("unverschlüsselt", StringComparison.Ordinal));
    }
}
