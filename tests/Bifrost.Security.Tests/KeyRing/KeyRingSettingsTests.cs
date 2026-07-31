using AwesomeAssertions;

using Bifrost.Server.KeyRing;

using Xunit;

namespace Bifrost.Security.Tests.KeyRing;

/// <summary>
/// Die Betriebsarten und der <c>_FILE</c>-Zusatz (FR-P048).
/// <para>
/// Der rote Faden: <b>Widersprüchliche Angaben werden nicht aufgelöst, sondern gemeldet.</b> Jede
/// stillschweigend gewählte Rangfolge ist eine Regel, die man nachlesen muss — und wer sie falsch
/// erinnert, betreibt danach eine Instanz mit dem falschen Geheimnis.
/// </para>
/// </summary>
public class KeyRingSettingsTests
{
    private static Func<string, string?> Config(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void Nothing_declared_is_not_a_mode_but_the_absence_of_one()
    {
        var settings = KeyRingSettings.Resolve(Config());

        settings.Mode.Should().Be(KeyRingProtectionMode.Undeclared);
        settings.IsProtected.Should().BeFalse();
    }

    [Fact]
    public void Unprotected_operation_is_an_explicit_choice()
    {
        var settings = KeyRingSettings.Resolve(
            Config((KeyRingSwitch.Protection, KeyRingSwitch.NoneValue)));

        settings.Mode.Should().Be(KeyRingProtectionMode.None);
        settings.Declared.Should().Be(KeyRingProtectionMode.None);
    }

    [Fact]
    public void An_unknown_declared_mode_is_an_error_and_not_a_shrug()
    {
        // Ein Tippfehler ('zertifikat') darf nicht in den ungeschützten Betrieb führen, den der
        // Betreiber gerade abwählen wollte.
        var resolve = () => KeyRingSettings.Resolve(
            Config((KeyRingSwitch.Protection, "zertifikat")));

        resolve.Should().Throw<KeyRingConfigurationException>()
            .WithMessage("*kein bekannter Betriebsmodus*");
    }

    [Fact]
    public void A_password_from_a_file_makes_the_mode_file_secret()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "streng-geheim-4711\n");

            var settings = KeyRingSettings.Resolve(Config(
                (KeyRingSwitch.CertificatePath, "/secrets/keyring.pfx"),
                (KeyRingSwitch.CertificatePassword + FileSecret.Suffix, file)));

            settings.Mode.Should().Be(KeyRingProtectionMode.FileSecret);
            settings.PasswordSource.Should().Be(SecretSource.File);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Exactly_one_trailing_newline_is_stripped_from_a_secret_file()
    {
        // 'echo geheim > secret.txt' schreibt einen — und ein Passwort mit angehängtem '\n' öffnet
        // kein PFX. Weiter wird nicht getrimmt.
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, " geheim \r\n");

            var value = FileSecret.Read(
                Config((KeyRingSwitch.CertificatePassword + FileSecret.Suffix, file)),
                KeyRingSwitch.CertificatePassword);

            value.Value.Should().Be(" geheim ");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Both_forms_of_the_same_secret_are_a_conflict()
    {
        var resolve = () => FileSecret.Read(
            Config(
                (KeyRingSwitch.CertificatePassword, "aus-der-umgebung"),
                (KeyRingSwitch.CertificatePassword + FileSecret.Suffix, "/run/secrets/pw")),
            KeyRingSwitch.CertificatePassword);

        resolve.Should().Throw<KeyRingConfigurationException>()
            .WithMessage("*keine Rangfolge*");
    }

    [Fact]
    public void A_missing_secret_file_is_an_error_and_not_an_empty_password()
    {
        var resolve = () => FileSecret.Read(
            Config((KeyRingSwitch.CertificatePassword + FileSecret.Suffix, "/gibt/es/nicht")),
            KeyRingSwitch.CertificatePassword);

        resolve.Should().Throw<KeyRingConfigurationException>();
    }

    [Fact]
    public void Declaring_file_secret_without_supplying_one_is_refused()
    {
        var resolve = () => KeyRingSettings.Resolve(Config(
            (KeyRingSwitch.Protection, KeyRingSwitch.FileSecretValue),
            (KeyRingSwitch.CertificatePath, "/secrets/keyring.pfx"),
            (KeyRingSwitch.CertificatePassword, "in-der-umgebung")));

        resolve.Should().Throw<KeyRingConfigurationException>()
            .WithMessage("*verlangt ein Zertifikat*");
    }

    [Fact]
    public void Declaring_none_while_configuring_a_certificate_is_a_contradiction()
    {
        var resolve = () => KeyRingSettings.Resolve(Config(
            (KeyRingSwitch.Protection, KeyRingSwitch.NoneValue),
            (KeyRingSwitch.CertificatePath, "/secrets/keyring.pfx")));

        resolve.Should().Throw<KeyRingConfigurationException>()
            .WithMessage("*widersprechen sich*");
    }

    [Fact]
    public void A_previous_certificate_without_a_current_one_is_refused()
    {
        var resolve = () => KeyRingSettings.Resolve(
            Config((KeyRingSwitch.PreviousCertificatePath, "/secrets/alt.pfx")));

        resolve.Should().Throw<KeyRingConfigurationException>();
    }

    [Fact]
    public void The_settings_never_print_the_password()
    {
        var settings = KeyRingSettings.Resolve(Config(
            (KeyRingSwitch.CertificatePath, "/secrets/keyring.pfx"),
            (KeyRingSwitch.CertificatePassword, "streng-geheim-4711")));

        // Ein 'record' hätte hier ein ToString() erzeugt, das jede Eigenschaft ausgibt — inklusive
        // des Passworts. Ein einziges LogDebug("{Settings}") hätte gereicht.
        settings.ToString().Should().NotContain("streng-geheim-4711");
        settings.ToString().Should().NotContain("/secrets/keyring.pfx");
    }
}
