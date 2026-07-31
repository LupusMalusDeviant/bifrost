using AwesomeAssertions;

using Bifrost.Server.KeyRing;

using Xunit;

namespace Bifrost.Security.Tests.KeyRing;

/// <summary>
/// Die Kommandozeilenwege: einrichten, prüfen, einen Zertifikatswechsel durchspielen.
/// <para>
/// Geprüft wird vor allem der <b>Exit-Code</b> — er ist das, worauf ein Skript oder ein
/// Bereitstellungsschritt reagiert. Eine Ausgabe, die „NICHT UMSTELLEN" sagt und mit 0 endet, wäre
/// eine Warnung, die keine Maschine sieht.
/// </para>
/// </summary>
public class KeyRingCommandTests
{
    private const int Ok = 0;
    private const int Usage = 2;
    private const int Warning = 3;
    private const int Finding = 4;

    [Fact]
    public void Setup_creates_certificate_and_password_file_and_prints_the_file_secret_variant()
    {
        using var world = new KeyRingWorld();
        var certificate = Path.Combine(world.SecretsDirectory, "neu.pfx");
        var (code, output, _) = Run(world, [KeyRingCommands.Setup, "--cert", certificate]);

        code.Should().Be(Ok);
        File.Exists(certificate).Should().BeTrue();
        File.Exists(certificate + ".password").Should().BeTrue();
        output.Should().Contain(KeyRingSwitch.CertificatePassword + FileSecret.Suffix,
            "der vorgeschlagene Weg ist der ohne Passwort in der Umgebung");
        output.Should().Contain(KeyRingSwitch.FileSecretValue);
    }

    [Fact]
    public async Task Check_warns_when_no_mode_was_declared()
    {
        using var world = new KeyRingWorld();
        await world.StartAsync();

        var (code, output, _) = Run(world, [KeyRingCommands.Check]);

        code.Should().Be(Warning);
        output.Should().Contain("keine Betriebsart erklaert");
    }

    [Fact]
    public async Task Check_is_green_for_a_file_secret_instance()
    {
        using var world = new KeyRingWorld();
        world.UseFileSecret();
        await world.StartAsync();

        var (code, output, _) = Run(world, [KeyRingCommands.Check]);

        code.Should().Be(Ok, "eine korrekt eingerichtete Instanz muss gruen werden koennen");
        output.Should().Contain("Leseprobe");
    }

    [Fact]
    public async Task Check_reports_a_ring_that_does_not_match_its_certificate()
    {
        using var world = new KeyRingWorld();
        world.UseFileSecret("erst");
        await world.StartAsync();

        var andere = world.CreateCertificate("andere");
        world.Configuration[KeyRingSwitch.CertificatePath] = andere.CertificatePath;
        world.Configuration[KeyRingSwitch.CertificatePassword + FileSecret.Suffix] = andere.PasswordPath;

        var (code, output, _) = Run(world, [KeyRingCommands.Check]);

        code.Should().Be(Finding);
        output.Should().Contain("nicht lesbar");
    }

    [Fact]
    public async Task Rotate_refuses_a_switch_that_would_make_the_instance_unreadable()
    {
        using var world = new KeyRingWorld();
        world.UseFileSecret("alt");
        await world.StartAsync();

        // Das alte Zertifikat wird bei diesem Wechsel NICHT weiterverwendet — der Wechsel, der die
        // Instanz unlesbar macht. Genau den soll die Probe vorher abfangen.
        var neu = world.CreateCertificate("neu");
        world.Configuration.Remove(KeyRingSwitch.CertificatePath);
        world.Configuration.Remove(KeyRingSwitch.CertificatePassword + FileSecret.Suffix);
        world.Configuration.Remove(KeyRingSwitch.Protection);

        var (code, output, _) = Run(
            world,
            [KeyRingCommands.Rotate, "--new-cert", neu.CertificatePath,
             "--new-password-file", neu.PasswordPath]);

        code.Should().Be(Finding);
        output.Should().Contain("NICHT UMSTELLEN");
    }

    [Fact]
    public async Task Rotate_clears_a_switch_that_keeps_the_previous_certificate()
    {
        using var world = new KeyRingWorld();
        world.UseFileSecret("alt");
        await world.StartAsync();
        var neu = world.CreateCertificate("neu");

        var (code, output, _) = Run(
            world,
            [KeyRingCommands.Rotate, "--new-cert", neu.CertificatePath,
             "--new-password-file", neu.PasswordPath]);

        code.Should().Be(Ok);
        output.Should().Contain(KeyRingSwitch.PreviousCertificatePath,
            "ohne das vorherige Zertifikat bliebe das Altmaterial nach dem Wechsel zu");
    }

    [Fact]
    public void Rotate_without_a_new_certificate_is_a_usage_error()
    {
        using var world = new KeyRingWorld();

        var (code, _, error) = Run(world, [KeyRingCommands.Rotate]);

        code.Should().Be(Usage);
        error.Should().Contain("--new-cert");
    }

    private static (int Code, string Output, string Error) Run(KeyRingWorld world, string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = KeyRingCommands.Run(
            args, world.Value, world.DataDirectory, TimeProvider.System, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
