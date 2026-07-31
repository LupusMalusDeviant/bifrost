using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

/// <summary>
/// Negativkorpus: erfundene Zugangsdaten in genau den Formen, in denen sie in dieser Anwendung
/// vorkommen. Keiner dieser Werte darf je in einem <c>Summary</c>, einer <c>Remediation</c> oder
/// einem <c>SafeDetails</c> stehen — <b>auch nicht gekürzt</b>. Ein halbes Secret in einer
/// Diagnoseausgabe ist ein Secret in einer Diagnoseausgabe.
/// </summary>
internal static class SecretCorpus
{
    public const string AwsAccessKey = "AKIAIOSFODNN7EXAMPLE";
    public const string GitHubToken = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
    public const string AnthropicKey = "sk-ant-api03-"
        + "aaaaaaaaaabbbbbbbbbbccccccccccddddddddddeeeeeeeeeeffffffffffgggggggggghhhhhhhhhhiiiiiiiiiiaaa"
        + "AA";
    public const string SlackToken = "xox" + "b-1234567890-1234567890-erfundenerslacktoken";
    public const string GatewayApiKey = "mcpk_9f3c2a7e5b1d4086cafe";
    public const string Passphrase = "Tr0ub4dor-3-erfunden";
    public const string PostgresConnection =
        "Host=db;Port=5432;Database=bifrost;Username=bifrost;Password=" + Passphrase;
    public const string UpstreamUrlWithCredentials = "https://bifrost:" + Passphrase + "@upstream.example/mcp";
    public const string BearerHeader = "Authorization: Bearer erfundenes-bearer-token-4711";
    public const string BearerValue = "erfundenes-bearer-token-4711";
    public const string PrivateKey =
        "-----BEGIN PRIVATE KEY-----\n"
        + "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQerfundenerschluessel\n"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n"
        + "-----END PRIVATE KEY-----";

    /// <summary>Die Werte, die nach der Redaktion nirgends mehr auftauchen dürfen.</summary>
    public static IReadOnlyList<string> Forbidden { get; } =
    [
        AwsAccessKey,
        GitHubToken,
        AnthropicKey,
        SlackToken,
        GatewayApiKey,
        Passphrase,
        BearerValue,
    ];
}

public class DiagnosticRedactionTests
{
    [Theory]
    [InlineData("Verbindung fehlgeschlagen: " + SecretCorpus.PostgresConnection, SecretCorpus.Passphrase)]
    [InlineData("BIFROST_KEYRING_CERT_PASSWORD=" + SecretCorpus.Passphrase, SecretCorpus.Passphrase)]
    [InlineData("Upstream antwortet nicht: " + SecretCorpus.UpstreamUrlWithCredentials, SecretCorpus.Passphrase)]
    [InlineData(SecretCorpus.BearerHeader, SecretCorpus.BearerValue)]
    [InlineData("token: " + SecretCorpus.GitHubToken, SecretCorpus.GitHubToken)]
    [InlineData("api_key=" + SecretCorpus.AwsAccessKey, SecretCorpus.AwsAccessKey)]
    [InlineData("client_secret: \"" + SecretCorpus.Passphrase + "\"", SecretCorpus.Passphrase)]
    public void Named_values_are_masked(string input, string secret)
    {
        var scrubbed = DiagnosticRedaction.Scrub(input);

        scrubbed.Should().NotContain(secret);
        scrubbed.Should().Contain(DiagnosticRedaction.Mask);
    }

    [Theory]
    [InlineData(SecretCorpus.AwsAccessKey)]
    [InlineData(SecretCorpus.GitHubToken)]
    [InlineData(SecretCorpus.AnthropicKey)]
    [InlineData(SecretCorpus.SlackToken)]
    [InlineData(SecretCorpus.GatewayApiKey)]
    public void Anchored_token_shapes_are_masked_even_without_a_field_name(string secret)
    {
        var scrubbed = DiagnosticRedaction.Scrub($"Der Upstream meldete: {secret} (unbrauchbar)");

        scrubbed.Should().NotContain(secret);
    }

    [Fact]
    public void A_pem_block_is_masked()
    {
        var scrubbed = DiagnosticRedaction.Scrub("Fehler im Zertifikat:\n" + SecretCorpus.PrivateKey);

        scrubbed.Should().NotContain("erfundenerschluessel");
    }

    [Fact]
    public void The_key_ring_path_survives_because_a_bare_key_is_not_masked()
    {
        // Sonst verschwände genau die Angabe, wegen der jemand die Diagnose aufruft.
        var scrubbed = DiagnosticRedaction.Scrub("Der Key-Ring liegt unter /data/keys.");

        scrubbed.Should().Be("Der Key-Ring liegt unter /data/keys.");
    }

    [Fact]
    public void Details_are_masked_in_key_and_value()
    {
        var details = new Dictionary<string, string>
        {
            ["verbindung"] = SecretCorpus.PostgresConnection,
            ["Password=" + SecretCorpus.Passphrase] = "egal",
        };

        var scrubbed = DiagnosticRedaction.Scrub(details)!;

        string.Join("|", scrubbed.Select(pair => $"{pair.Key}={pair.Value}"))
            .Should().NotContain(SecretCorpus.Passphrase);
    }

    [Fact]
    public void Very_long_texts_are_truncated()
    {
        var scrubbed = DiagnosticRedaction.Scrub(new string('x', DiagnosticRedaction.MaxLength * 2));

        scrubbed.Should().HaveLength(DiagnosticRedaction.MaxLength + " … (gekürzt)".Length);
    }

    [Fact]
    public void A_whole_check_is_scrubbed_in_every_field()
    {
        var check = new DiagnosticCheck(
            DiagnosticCodes.DatabaseReachable,
            CheckStatus.Fail,
            "Verbindung fehlgeschlagen: " + SecretCorpus.PostgresConnection,
            "Prüfe " + SecretCorpus.UpstreamUrlWithCredentials,
            new Dictionary<string, string> { ["token"] = SecretCorpus.GitHubToken });

        var scrubbed = DiagnosticRedaction.Scrub(check);

        Flatten(scrubbed).Should().NotContainAny(SecretCorpus.Forbidden);
        scrubbed.Code.Should().Be(DiagnosticCodes.DatabaseReachable);
        scrubbed.Status.Should().Be(CheckStatus.Fail);
    }

    internal static string Flatten(DiagnosticCheck check)
        => string.Join(
            "\n",
            [check.Summary, check.Remediation ?? string.Empty,
             .. (check.SafeDetails ?? new Dictionary<string, string>()).Select(p => $"{p.Key}={p.Value}")]);

    internal static string Flatten(DiagnosticReport report)
        => string.Join("\n", report.Checks.Select(Flatten));
}

/// <summary>
/// Der Nachweis über den <b>ganzen</b> Bericht: Der Korpus wird an jeder Stelle eingespeist, an der
/// echte Zugangsdaten stehen — Umgebung, Datenbankfehler, Upstream-Fehler — und danach steht
/// nichts davon in der Ausgabe.
/// </summary>
public class DiagnosticReportLeakTests
{
    private static DiagnosticContext PoisonedWorld()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIFROST_DATA_DIR"] = "/data",
            ["ASPNETCORE_URLS"] = "http://+:8080",
            ["BIFROST_DB_PROVIDER"] = "postgres",
            ["BIFROST_DB_CONNECTION"] = SecretCorpus.PostgresConnection,
            ["BIFROST_KEYRING_CERT_PATH"] = "/secrets/keyring.pfx",
            ["BIFROST_KEYRING_CERT_PASSWORD"] = SecretCorpus.Passphrase,
            ["BIFROST_PUBLIC_BASE_URL"] = "https://gateway.example.com",
            ["BIFROST_TRUSTED_PROXIES"] = "172.17.0.1",
            ["BIFROST_WASI_HOST"] = "/usr/local/bin/bifrost-wasi-host",
            // Alt benannte Variablen: Der Check nennt ihre NAMEN — ihre Werte dürfen nie mitkommen.
            ["MCPMCP_KEYRING_CERT_PASSWORD"] = SecretCorpus.Passphrase,
            ["MCPMCP_UPSTREAM_TOKEN"] = SecretCorpus.GitHubToken,
        };

        return DiagnosticWorld.Context(environment) with
        {
            Database = new FakeDatabaseProbe(new DatabaseDiagnosticFacts(
                false,
                $"Npgsql konnte sich nicht verbinden ({SecretCorpus.PostgresConnection})")),
            Upstreams = new FakeUpstreamProbe(
                new UpstreamDiagnosticFact(
                    "github", "Failed", false,
                    $"401 beim Handshake mit {SecretCorpus.UpstreamUrlWithCredentials}, "
                    + $"{SecretCorpus.BearerHeader}, Key {SecretCorpus.GitHubToken}"),
                new UpstreamDiagnosticFact(
                    "slack", "Failed", false, $"Token {SecretCorpus.SlackToken} abgelehnt")),
        };
    }

    [Fact]
    public async Task No_corpus_entry_shows_up_anywhere_in_the_report()
    {
        var report = await DiagnosticService.CreateDefault(PoisonedWorld())
            .RunAsync(DiagnosticScope.All, TestContext.Current.CancellationToken);

        DiagnosticRedactionTests.Flatten(report).Should().NotContainAny(SecretCorpus.Forbidden);
    }

    [Fact]
    public async Task The_report_is_still_useful_after_scrubbing()
    {
        // Eine Redaktion, die den Befund unlesbar macht, hilft niemandem: Die Codes, die Namen und
        // die Handlungsanweisungen müssen stehen bleiben.
        var report = await DiagnosticService.CreateDefault(PoisonedWorld())
            .RunAsync(DiagnosticScope.All, TestContext.Current.CancellationToken);

        report.Checks.Select(c => c.Code).Should().BeEquivalentTo(DiagnosticCodes.All);
        report.Checks.Single(c => c.Code == DiagnosticCodes.DatabaseReachable)
            .Status.Should().Be(CheckStatus.Fail);
        report.Checks.Single(c => c.Code == DiagnosticCodes.LegacyEnvironmentVariables)
            .SafeDetails!["variablen"].Should().Contain("MCPMCP_UPSTREAM_TOKEN");
        report.Checks.Single(c => c.Code == DiagnosticCodes.UpstreamStates)
            .Summary.Should().Contain("github");
    }
}
