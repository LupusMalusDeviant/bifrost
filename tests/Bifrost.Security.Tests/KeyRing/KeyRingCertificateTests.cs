using AwesomeAssertions;

using Bifrost.Persistence;
using Bifrost.Server.KeyRing;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Bifrost.Security.Tests.KeyRing;

/// <summary>
/// Zertifikat erzeugen, prüfen, wechseln — und was passiert, wenn das falsche dasteht (WP3.3,
/// Aufträge 3 und 4).
/// </summary>
public class KeyRingCertificateTests
{
    [Fact]
    public void Setup_creates_a_usable_certificate_with_restrictive_permissions()
    {
        using var world = new KeyRingWorld();

        var created = world.CreateCertificate();

        File.Exists(created.CertificatePath).Should().BeTrue();
        File.Exists(created.PasswordPath).Should().BeTrue();
        SecretFilePermissions.Describe(created.CertificatePath).Restricted
            .Should().BeTrue("ein PFX mit privatem Schlüssel, das jeder lesen darf, ist kein Schutz");
        SecretFilePermissions.Describe(created.PasswordPath).Restricted.Should().BeTrue();

        var password = File.ReadAllText(created.PasswordPath);
        var inspection = KeyRingCertificates.Inspect(created.CertificatePath, password);
        inspection.Loadable.Should().BeTrue();
        inspection.HasPrivateKey.Should().BeTrue(
            "ohne privaten Schlüssel liesse sich der Ring verschlüsseln, aber nie wieder öffnen");
    }

    [Fact]
    public void Setup_never_overwrites_an_existing_certificate()
    {
        using var world = new KeyRingWorld();
        world.CreateCertificate();

        var again = () => world.CreateCertificate();

        // Ein zweiter Setup-Lauf, der die Datei ersetzt, hätte den Ring der Instanz entwertet —
        // bevor irgendjemand gefragt wurde.
        again.Should().Throw<IOException>();
    }

    [Fact]
    public void A_password_supplied_as_a_file_secret_opens_the_certificate()
    {
        // FR-P048: Das PFX geht als Compose-Secret, und sein Passwort jetzt auch.
        using var world = new KeyRingWorld();
        var created = world.UseFileSecret();

        var settings = KeyRingSettings.Resolve(world.Value);

        settings.Mode.Should().Be(KeyRingProtectionMode.FileSecret);
        settings.PasswordSource.Should().Be(SecretSource.File);
        var certificates = KeyRingCertificates.Load(settings);
        try
        {
            certificates.Should().ContainSingle()
                .Which.HasPrivateKey.Should().BeTrue();
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }

        created.PasswordPath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void A_missing_certificate_file_stops_the_composition_with_a_reason()
    {
        using var world = new KeyRingWorld();
        world.Configuration[KeyRingSwitch.CertificatePath] =
            Path.Combine(world.SecretsDirectory, "gibt-es-nicht.pfx");

        var compose = () => world.BuildServices();

        compose.Should().Throw<KeyRingConfigurationException>()
            .WithMessage("*gibt-es-nicht.pfx*");
        world.KeyFileCount.Should().Be(0, "ein abgebrochener Start legt keinen Ring an");
    }

    [Fact]
    public async Task The_wrong_certificate_is_named_and_leaves_the_ring_untouched()
    {
        using var world = new KeyRingWorld();
        world.UseFileSecret("erst");
        await world.StartAsync();
        var before = world.KeyIds;

        // Jemand tauscht das Zertifikat aus, ohne das alte weiter anzugeben — der klassische
        // Rotationsfehler.
        var andere = world.CreateCertificate("andere");
        world.Configuration[KeyRingSwitch.CertificatePath] = andere.CertificatePath;
        world.Configuration[KeyRingSwitch.CertificatePassword + FileSecret.Suffix] = andere.PasswordPath;

        var verdict = await world.StartAsync();

        verdict.Kind.Should().Be(KeyRingVerdictKind.Unreadable);
        verdict.Blocks.Should().BeTrue();
        verdict.Remediation.Should().Contain(KeyRingSwitch.PreviousCertificatePath);
        world.KeyIds.Should().BeEquivalentTo(before, "es entsteht KEIN stiller neuer Ring");
    }

    [Fact]
    public async Task Rotation_keeps_the_ring_readable_when_the_previous_certificate_stays()
    {
        using var world = new KeyRingWorld();
        var alt = world.UseFileSecret("alt");
        await world.StartAsync();

        byte[] ciphertext;
        using (var services = world.BuildServices())
        {
            ciphertext = services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(CryptographicNames.WebhookSecretPurpose)
                .Protect("webhook-secret"u8.ToArray());
        }

        var neu = world.CreateCertificate("neu");
        world.Configuration[KeyRingSwitch.CertificatePath] = neu.CertificatePath;
        world.Configuration[KeyRingSwitch.CertificatePassword + FileSecret.Suffix] = neu.PasswordPath;
        world.Configuration[KeyRingSwitch.PreviousCertificatePath] = alt.CertificatePath;
        world.Configuration[KeyRingSwitch.PreviousCertificatePassword + FileSecret.Suffix] = alt.PasswordPath;

        var verdict = await world.StartAsync();

        verdict.Blocks.Should().BeFalse();
        using var rotated = world.BuildServices();
        var plaintext = rotated.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(CryptographicNames.WebhookSecretPurpose)
            .Unprotect(ciphertext);
        System.Text.Encoding.UTF8.GetString(plaintext).Should().Be("webhook-secret");
    }

    [Fact]
    public async Task The_rotation_probe_says_no_before_the_switch_instead_of_after()
    {
        // Auftrag 4: Testentschlüsselung VOR dem Umschalten. Ein Wechsel, der erst im Betrieb
        // auffällt, hat die Instanz bereits unlesbar gemacht.
        using var world = new KeyRingWorld();
        var alt = world.UseFileSecret("alt");
        await world.StartAsync();

        var neu = world.CreateCertificate("neu");
        var neuesZertifikat = KeyRingCertificates.Load(Settings(world, neu));
        var beide = KeyRingCertificates.Load(Settings(world, neu, alt));
        try
        {
            var nurNeu = KeyRingProbe.Read(world.KeyRingDirectory, neuesZertifikat);
            var mitAltem = KeyRingProbe.Read(world.KeyRingDirectory, beide);

            nurNeu.AllReadable.Should().BeFalse("ohne das alte Zertifikat bleibt das Altmaterial zu");
            nurNeu.Describe().Should().Contain("nicht lesbar");
            mitAltem.AllReadable.Should().BeTrue();
        }
        finally
        {
            foreach (var certificate in neuesZertifikat.Concat(beide))
            {
                certificate.Dispose();
            }
        }
    }

    [Fact]
    public async Task The_probe_never_writes_into_the_real_key_ring()
    {
        // Die Probe läuft auf einer Kopie — sonst könnte sie genau den Schaden anrichten, vor dem
        // sie warnen soll: Ein Ring ohne passendes Zertifikat verleitet DataProtection dazu, daneben
        // einen frischen Schlüssel anzulegen.
        using var world = new KeyRingWorld();
        world.UseFileSecret();
        await world.StartAsync();
        var before = world.KeyIds;

        var report = KeyRingProbe.Read(world.KeyRingDirectory, []);

        report.AllReadable.Should().BeFalse("ohne Zertifikat ist ein verschlüsselter Ring nicht zu öffnen");
        world.KeyIds.Should().BeEquivalentTo(before, "die Probe legt im echten Verzeichnis nichts an");
    }

    private static KeyRingSettings Settings(
        KeyRingWorld world, KeyRingCertificateCreation current, KeyRingCertificateCreation? previous = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [KeyRingSwitch.CertificatePath] = current.CertificatePath,
            [KeyRingSwitch.CertificatePassword + FileSecret.Suffix] = current.PasswordPath,
        };
        if (previous is not null)
        {
            values[KeyRingSwitch.PreviousCertificatePath] = previous.CertificatePath;
            values[KeyRingSwitch.PreviousCertificatePassword + FileSecret.Suffix] = previous.PasswordPath;
        }

        _ = world;
        return KeyRingSettings.Resolve(name => values.GetValueOrDefault(name));
    }
}
