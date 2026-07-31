using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

public class DataDirectoryCheckTests
{
    private static (DiagnosticContext Context, FakeFileProbe Files) World(string dataDir)
    {
        var files = new FakeFileProbe();
        var context = DiagnosticWorld.Context(
            new Dictionary<string, string> { ["BIFROST_DATA_DIR"] = dataDir },
            files);
        return (context, files);
    }

    [Fact]
    public async Task Existing_and_writable_passes()
    {
        var (context, files) = World("/data");
        files.Directories.Add("/data");

        var result = await new DataDirectoryCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.Code.Should().Be(DiagnosticCodes.DataDirectory);
    }

    [Fact]
    public async Task Not_writable_fails_with_a_remediation()
    {
        var (context, files) = World("/data");
        files.Directories.Add("/data");
        files.NotWritable["/data"] = "Zugriff verweigert";

        var result = await new DataDirectoryCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        result.Remediation.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Missing_directory_warns_about_the_volume()
    {
        // Der teuerste dokumentierte Ausfall: Der Gateway richtet sich in einem leeren Verzeichnis
        // ein und meldet sich fehlerfrei als bereit.
        var (context, _) = World("/data");

        var result = await new DataDirectoryCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("Volume");
    }

    [Fact]
    public async Task A_file_where_a_directory_belongs_fails()
    {
        var (context, files) = World("/data");
        files.Files.Add("/data");

        var result = await new DataDirectoryCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
    }
}

public class LegacyEnvironmentVariablesCheckTests
{
    [Fact]
    public async Task No_legacy_names_passes()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["BIFROST_DATA_DIR"] = "/data",
        });

        var result = await new LegacyEnvironmentVariablesCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task Legacy_names_warn_and_are_listed_by_name_only()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["MCPMCP_DATA_DIR"] = "/alt",
            ["MCPMCP_KEYRING_CERT_PASSWORD"] = "streng-geheim-4711",
        });

        var result = await new LegacyEnvironmentVariablesCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.SafeDetails!["variablen"].Should().Contain("MCPMCP_DATA_DIR").And.Contain("MCPMCP_KEYRING_CERT_PASSWORD");
        // Die Werte gehören NICHT dazu — unter den alten Namen steckt auch das PFX-Passwort.
        Flatten(result).Should().NotContain("streng-geheim-4711").And.NotContain("/alt");
    }

    [Fact]
    public async Task An_empty_legacy_value_is_not_in_use()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["MCPMCP_DATA_DIR"] = "   ",
        });

        var result = await new LegacyEnvironmentVariablesCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task A_legacy_name_shadowed_by_the_new_one_is_named_as_such()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["MCPMCP_DATA_DIR"] = "/alt",
            ["BIFROST_DATA_DIR"] = "/neu",
        });

        var result = await new LegacyEnvironmentVariablesCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.SafeDetails!["ueberschrieben_durch_neuen_namen"].Should().Contain("MCPMCP_DATA_DIR");
    }

    internal static string Flatten(DiagnosticCheck check)
        => string.Join(
            "\n",
            [check.Summary, check.Remediation ?? string.Empty,
             .. (check.SafeDetails ?? new Dictionary<string, string>()).Select(p => $"{p.Key}={p.Value}")]);
}

public class PublicBaseUrlCheckTests
{
    [Fact]
    public async Task Without_a_proxy_and_without_oauth_the_address_is_not_needed()
    {
        var context = DiagnosticWorld.Context();

        var result = await new PublicBaseUrlCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
        result.Summary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Behind_a_declared_proxy_a_missing_address_warns()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["BIFROST_TRUSTED_PROXIES"] = "172.17.0.1",
        });

        var result = await new PublicBaseUrlCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("callback");
    }

    [Fact]
    public async Task An_absolute_https_address_passes()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["BIFROST_TRUSTED_PROXIES"] = "172.17.0.1",
            ["BIFROST_PUBLIC_BASE_URL"] = "https://gateway.example.com",
        });

        var result = await new PublicBaseUrlCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task A_relative_address_fails()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["BIFROST_PUBLIC_BASE_URL"] = "gateway.example.com",
        });

        var result = await new PublicBaseUrlCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
    }

    [Fact]
    public async Task Plain_http_behind_a_proxy_warns()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["BIFROST_TRUSTED_PROXIES"] = "any",
            ["BIFROST_PUBLIC_BASE_URL"] = "http://gateway.example.com",
        });

        var result = await new PublicBaseUrlCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
    }
}
