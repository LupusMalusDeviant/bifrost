using AwesomeAssertions;

using Bifrost.Persistence;
using Bifrost.Server.KeyRing;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Bifrost.Security.Tests.KeyRing;

/// <summary>
/// Der Lebenslauf eines Key-Rings: anlegen, wiederfinden, verlieren, zurückholen (WP3.3).
/// <para>
/// Jeder dieser Tests prüft am Ende dieselbe Frage: <b>Ist ein neuer Ring entstanden, wo keiner
/// hätte entstehen dürfen?</b> Das ist der Ausfall, um den es geht — nicht der Startfehler, sondern
/// der fehlerfreie Start auf leeren Schlüsseln.
/// </para>
/// </summary>
public class KeyRingLifecycleTests
{
    [Fact]
    public async Task A_fresh_instance_creates_a_ring_and_records_a_witness()
    {
        using var world = new KeyRingWorld();

        var verdict = await world.StartAsync();

        verdict.Kind.Should().Be(KeyRingVerdictKind.FreshInstance);
        verdict.Blocks.Should().BeFalse();
        world.KeyFileCount.Should().Be(1, "der erste Schlüssel entsteht beim ersten Start");
        new KeyRingWitnessFile(world.DataDirectory).Read()
            .Should().NotBeNull("ohne Zeugeneintrag sähe der nächste Start wieder wie eine "
                + "Neuinstallation aus");
    }

    [Fact]
    public async Task A_restart_finds_the_same_ring_and_adds_nothing()
    {
        using var world = new KeyRingWorld();
        await world.StartAsync();
        var first = world.KeyIds;

        var verdict = await world.StartAsync();

        verdict.Kind.Should().Be(KeyRingVerdictKind.Established);
        world.KeyIds.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task A_restart_under_certificate_protection_finds_the_same_ring()
    {
        using var world = new KeyRingWorld();
        world.UseFileSecret();
        await world.StartAsync();
        var first = world.KeyIds;
        Bifrost.Server.KeyRing.KeyRingDirectory.Read(world.KeyRingDirectory)
            .Should().OnlyContain(key => key.Encrypted, "mit Zertifikat liegt kein Klartext mehr da");

        var verdict = await world.StartAsync();

        verdict.Kind.Should().Be(KeyRingVerdictKind.Established);
        world.KeyIds.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task A_lost_key_ring_is_named_and_no_new_one_is_created()
    {
        // Der v0.11.0-Fall: Die Instanz lief, dann war das Volume ein anderes.
        using var world = new KeyRingWorld();
        await world.StartAsync();
        world.LoseKeyRing();

        var verdict = await world.StartAsync();

        verdict.Kind.Should().Be(KeyRingVerdictKind.Lost);
        verdict.Blocks.Should().BeTrue();
        verdict.Summary.Should().Contain("zuletzt");
        verdict.Remediation.Should().Contain("KEIN neuer Ring");
        world.KeyFileCount.Should().Be(0, "genau das ist der Punkt: es entsteht kein Ersatzring");
    }

    [Fact]
    public async Task Ciphertext_in_the_database_betrays_a_lost_ring_even_without_a_witness()
    {
        // Das Datenverzeichnis ist komplett neu — also auch ohne Zeugeneintrag. Die Datenbank liegt
        // woanders (PostgreSQL) und hat den Geheimtext noch.
        using var world = new KeyRingWorld();

        var verdict = await world.StartAsync(ciphertextRows: 7);

        verdict.Kind.Should().Be(KeyRingVerdictKind.Lost);
        verdict.Summary.Should().Contain("7");
        world.KeyFileCount.Should().Be(0);
    }

    [Fact]
    public async Task An_unanswerable_database_is_not_read_as_an_empty_one()
    {
        // 'null' heisst „nicht beantwortbar". Eine frische SQLite-Datei hat die Tabellen noch nicht;
        // daraus darf kein Verlustverdacht werden — sonst käme keine Neuinstallation je hoch.
        using var world = new KeyRingWorld();

        var verdict = await world.StartAsync(ciphertextRows: null);

        verdict.Kind.Should().Be(KeyRingVerdictKind.FreshInstance);
        world.KeyFileCount.Should().Be(1);
    }

    [Fact]
    public async Task A_completely_exchanged_ring_is_reported_but_does_not_block()
    {
        using var world = new KeyRingWorld();
        await world.StartAsync();

        // Ring weg, aber ein anderer steht da — so sieht eine Wiederherstellung aus, und so sieht
        // ein vertauschtes Volume aus. Der Start läuft weiter, aber nicht stillschweigend.
        world.LoseKeyRing();
        using var foreignWorld = new KeyRingWorld();
        await foreignWorld.StartAsync();
        world.RestoreKeyRing(foreignWorld.KeyRingDirectory);

        var verdict = await world.StartAsync();

        verdict.Kind.Should().Be(KeyRingVerdictKind.Replaced);
        verdict.Blocks.Should().BeFalse();
        verdict.Summary.Should().Contain("ausgetauscht");
    }

    [Fact]
    public async Task A_restored_ring_decrypts_the_restored_database_content()
    {
        // ADR-0024 E3: Datenbank und Key-Ring gehören in dieselbe Sicherung. Hier ist der Beweis,
        // dass ein zurückgespielter Ring den zurückgespielten Geheimtext auch wirklich öffnet.
        using var world = new KeyRingWorld();
        world.UseFileSecret();
        await world.StartAsync();

        byte[] ciphertext;
        using (var services = world.BuildServices())
        {
            ciphertext = services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(CryptographicNames.UpstreamConfigPurpose)
                .Protect("upstream-token-4711"u8.ToArray());
        }

        var backup = world.BackupKeyRing();
        world.LoseKeyRing();
        world.RestoreKeyRing(backup);

        var verdict = await world.StartAsync();
        verdict.Blocks.Should().BeFalse();

        using var restored = world.BuildServices();
        var plaintext = restored.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(CryptographicNames.UpstreamConfigPurpose)
            .Unprotect(ciphertext);

        System.Text.Encoding.UTF8.GetString(plaintext).Should().Be("upstream-token-4711");
    }

    [Fact]
    public async Task A_witness_that_cannot_be_read_is_not_treated_as_a_fresh_instance()
    {
        using var world = new KeyRingWorld();
        await world.StartAsync();
        world.LoseKeyRing();

        var witnessPath = Bifrost.Core.Diagnostics.Checks.KeyRingLayout.WitnessPathFor(world.DataDirectory);
        await File.WriteAllTextAsync(
            witnessPath, "{ kaputt", TestContext.Current.CancellationToken);

        var verdict = await world.StartAsync();

        verdict.Kind.Should().Be(KeyRingVerdictKind.Lost);
        verdict.Summary.Should().Contain("unlesbar");
        world.KeyFileCount.Should().Be(0);
    }
}
