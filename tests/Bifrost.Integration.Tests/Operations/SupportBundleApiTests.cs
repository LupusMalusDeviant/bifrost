using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Integration.Tests.Gateway;
using Bifrost.Security.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// WP5.6 am laufenden Gateway: Vorschau, Erzeugung, Audit — und die eine Frage, an der alles hängt,
/// ob das Archiv weitergegeben werden kann.
/// </summary>
public sealed class SupportBundleApiTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;

    public SupportBundleApiTests(GatewayFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_preview_comes_before_the_archive_and_names_what_is_missing()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/operations/support-bundle/preview", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("diagnostics.json").And.Contain("runtime.json");
        body.Should().Contain("logs", "eine übersprungene Kategorie wird benannt, nicht weggelassen");
    }

    /// <summary>
    /// <b>Die Canary-Regel des M5-Vertrags an der echten Installation.</b> Ein Upstream mit
    /// Zugangsdaten in Umgebung und Kopfzeile steht in der Konfiguration; im Bundle darf davon
    /// nichts ankommen — auch nicht gekürzt.
    /// </summary>
    [Fact]
    public async Task No_credential_of_a_real_upstream_reaches_the_bundle()
    {
        var client = await AdminClientAsync();
        var slug = $"canary{Guid.NewGuid():N}"[..12];

        await _fixture.Supervisor.AddAsync(
            new UpstreamServerConfig(
                slug,
                "Canary",
                UpstreamTransportKind.Stdio,
                Enabled: false,
                Stdio: new StdioTransportOptions(
                    TestPaths.Executable("EchoServer"),
                    [],
                    EnvironmentVariables: new Dictionary<string, string>
                    {
                        ["GITHUB_TOKEN"] = SecretCorpus.StdioEnv,
                    },
                    Isolation: new IsolationOptions(IsolationMode.Host))),
            TestContext.Current.CancellationToken);

        var target = Path.Combine(Path.GetTempPath(), $"bifrost-bundle-{Guid.NewGuid():N}.zip");
        try
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/operations/support-bundle",
                new { outputPath = target },
                TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            File.Exists(target).Should().BeTrue();

            ReadAll(target).Should().NotContain(
                SecretCorpus.Marker,
                "ein Wert aus dem Negativkorpus im Bundle ist ein Leck, kein Hinweis");
        }
        finally
        {
            File.Delete(target);
            await _fixture.Supervisor.RemoveAsync(
                _fixture.Supervisor.Statuses.First(s => s.Slug == slug).Id,
                DrainPolicy.Immediate,
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Ein Archiv, das die Installation verlässt, ist ein Vorgang. Wer es wann erzeugt hat, muss
    /// danach nachvollziehbar sein — sonst ist die einzige Spur der Zeitstempel der Datei.
    /// </summary>
    [Fact]
    public async Task Creating_a_bundle_is_audited()
    {
        var client = await AdminClientAsync();
        var target = Path.Combine(Path.GetTempPath(), $"bifrost-bundle-{Guid.NewGuid():N}.zip");

        try
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/operations/support-bundle",
                new { outputPath = target },
                TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await IntegrationSupport.WaitUntilAsync(
                async () =>
                {
                    var page = await _fixture.AuditQuery.QueryAsync(
                        new AuditFilter(PageSize: 200), TestContext.Current.CancellationToken);
                    // Verwaltungsereignisse tragen ihren Gegenstand im Tool-Feld (siehe
                    // ApiEndpoints.AuditManagement) — dieselbe Stelle wie bei 'backup-created'.
                    return page.Items.Any(e =>
                        e.Tool?.StartsWith("support-bundle-created:", StringComparison.Ordinal) == true);
                },
                because: "die Erzeugung eines Support-Bundles gehört ins Audit");
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>Admin-only: Eine Identität ohne Verwaltungsrechte bekommt kein Archiv.</summary>
    [Fact]
    public async Task A_non_admin_identity_cannot_create_a_bundle()
    {
        var (_, key) = await _fixture.SeedIdentityAsync($"kein-admin-{Guid.NewGuid():N}", grants: []);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var response = await client.PostAsJsonAsync(
            "/api/v1/operations/support-bundle", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    private static string ReadAll(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var text = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, key) = await _fixture.SeedAdminAsync("bundle-admin-" + Guid.NewGuid().ToString("N")[..8]);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}
