using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

public class KeyRingCheckTests
{
    private static readonly string DataDir = "/data";
    private static readonly string KeyDir = Path.Combine(DataDir, "keys");

    private static (DiagnosticContext Context, FakeFileProbe Files) World(
        IDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIFROST_DATA_DIR"] = DataDir,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        var files = new FakeFileProbe();
        files.Directories.Add(DataDir);
        return (DiagnosticWorld.Context(environment, files), files);
    }

    [Fact]
    public async Task A_populated_key_ring_passes()
    {
        var (context, files) = World();
        files.Directories.Add(KeyDir);
        files.Files.Add(Path.Combine(KeyDir, "key-11111111-2222-3333-4444-555555555555.xml"));

        var result = await new KeyRingPresenceCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["schluesseldateien"].Should().Be("1");
    }

    [Fact]
    public async Task A_missing_key_ring_warns_that_the_credentials_are_unusable()
    {
        var (context, _) = World();

        var result = await new KeyRingPresenceCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("Sicherung");
    }

    [Fact]
    public async Task An_empty_key_ring_directory_warns()
    {
        var (context, files) = World();
        files.Directories.Add(KeyDir);

        var result = await new KeyRingPresenceCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
    }

    [Fact]
    public async Task An_unprotected_key_ring_is_a_warning_with_a_remediation()
    {
        // Vertragsgemäss eine Warnung: Der Gateway läuft so, aber die Entscheidung soll getroffen
        // und nicht vorgefunden werden.
        var (context, _) = World();

        var result = await new KeyRingProtectionCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("BIFROST_KEYRING_CERT_PATH");
        result.SafeDetails!["geschuetzt"].Should().Be("nein");
    }

    [Fact]
    public async Task A_configured_certificate_makes_the_key_ring_protected()
    {
        var (context, _) = World(new Dictionary<string, string>
        {
            ["BIFROST_KEYRING_CERT_PATH"] = "/run/secrets/bifrost-keyring-pfx",
        });

        var result = await new KeyRingProtectionCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task Without_a_certificate_the_certificate_check_is_skipped()
    {
        var (context, _) = World();

        var result = await new KeyRingCertificateCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task A_missing_certificate_file_fails_because_the_start_aborts()
    {
        var (context, _) = World(new Dictionary<string, string>
        {
            ["BIFROST_KEYRING_CERT_PATH"] = "/secrets/keyring.pfx",
            ["BIFROST_KEYRING_CERT_PASSWORD"] = "streng-geheim-4711",
        });

        var result = await new KeyRingCertificateCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        LegacyEnvironmentVariablesCheckTests.Flatten(result).Should().NotContain("streng-geheim-4711");
    }

    [Fact]
    public async Task A_present_certificate_file_passes()
    {
        var (context, files) = World(new Dictionary<string, string>
        {
            ["BIFROST_KEYRING_CERT_PATH"] = "/secrets/keyring.pfx",
        });
        files.Files.Add("/secrets/keyring.pfx");

        var result = await new KeyRingCertificateCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }
}
