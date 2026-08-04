using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Operations.SupportBundle;
using Bifrost.Security.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Operations;

/// <summary>
/// WP5.6: Das Support-Bundle ist standardmäßig weitergabefähig — und diese Tests sind der Grund,
/// warum das eine Zusicherung ist und keine Absicht.
/// </summary>
public sealed class SupportBundleTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"bifrost-bundle-{Guid.NewGuid():N}");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

    public SupportBundleTests() => Directory.CreateDirectory(_dataDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_preview_lists_what_would_go_in_and_names_what_would_not()
    {
        var service = CreateService();

        var preview = await service.PreviewAsync(new SupportBundleRequest(), TestContext.Current.CancellationToken);

        preview.Entries.Select(e => e.Path).Should().Contain(
            ["version.json", "configuration.json", "diagnostics.json", "health.json", "migrations.json", "runtime.json"]);
        preview.EstimatedBytes.Should().BeGreaterThan(0);

        // Diese Installation schreibt kein Anwendungslog — das ist eine Aussage und kein Loch.
        preview.SkippedCategories.Should().ContainSingle()
            .Which.Category.Should().Be(SupportBundleCategory.Logs);
        preview.SkippedCategories[0].Reason.Should().NotBeEmpty();
        preview.Entries.Should().Contain(e => e.Path == "logs/how-to-collect.txt");
    }

    [Fact]
    public async Task An_explicit_selection_takes_exactly_those_categories()
    {
        var service = CreateService();

        var preview = await service.PreviewAsync(
            new SupportBundleRequest(new HashSet<SupportBundleCategory>
                { SupportBundleCategory.Version, SupportBundleCategory.Runtime }),
            TestContext.Current.CancellationToken);

        preview.Entries.Select(e => e.Path).Should().BeEquivalentTo(["version.json", "runtime.json"]);
    }

    [Fact]
    public async Task The_archive_carries_a_manifest_whose_checksums_match_its_files()
    {
        var service = CreateService();
        var target = Path.Combine(_dataDirectory, "bundle.zip");

        var result = await service.CreateAsync(
            new SupportBundleRequest(OutputPath: target), TestContext.Current.CancellationToken);

        File.Exists(target).Should().BeTrue();
        result.Manifest.FormatVersion.Should().Be(SupportBundleService.FormatVersion);
        result.Manifest.InstanceId.Should().Be("instanz-1");
        result.Manifest.ChecksumAlgorithm.Should().Be("sha-256");

        using var archive = ZipFile.OpenRead(target);

        // Das Manifest liegt unverschluesselt im Archiv: Ein Empfaenger muss pruefen koennen, was er
        // vor sich hat, BEVOR er es oeffnet.
        var manifestEntry = archive.GetEntry("manifest.json");
        manifestEntry.Should().NotBeNull();

        foreach (var file in result.Manifest.Files)
        {
            var entry = archive.GetEntry(file.Path);
            entry.Should().NotBeNull($"das Manifest nennt '{file.Path}'");

            using var reader = new StreamReader(entry!.Open());
            var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Encoding.UTF8.GetByteCount(content).Should().Be((int)file.Bytes);
            Sha256(content).Should().Be(file.Sha256, $"die Pruefsumme von '{file.Path}' muss stimmen");
        }
    }

    /// <summary>
    /// <b>Die Regel, an der dieses Paket hängt</b> (M5-Vertrag §9.8): Jede Quelle trägt ein
    /// Canary-Secret. Erscheint eines im Bundle, ist der Lauf rot — nicht gelb, nicht kommentiert.
    /// </summary>
    /// <summary>
    /// Ohne Zielpfad landet das Bundle im Datenverzeichnis. <b>Befund aus dem Audit:</b> Bis hierher
    /// gab jeder Test einen Pfad vor — der Weg, den ein Betreiber ohne <c>--out</c> nimmt, war
    /// ungeprüft, samt des Verzeichnisses, das dabei erst entstehen muss.
    /// </summary>
    [Fact]
    public async Task Without_a_target_path_the_bundle_lands_in_the_data_directory()
    {
        var service = CreateService();

        var result = await service.CreateAsync(new SupportBundleRequest(), TestContext.Current.CancellationToken);

        result.ArchivePath.Should().StartWith(Path.Combine(_dataDirectory, "support"));
        File.Exists(result.ArchivePath).Should().BeTrue();
        result.Bytes.Should().BeGreaterThan(0);
        result.Sha256.Should().HaveLength(64);
    }

    /// <summary>
    /// <b>Befund aus dem Audit:</b> Die Kategorie <c>Logs</c> war nur in ihrer <em>abwesenden</em>
    /// Form geprüft. Findet sich eine Logdatei, muss sie beschnitten und maskiert im Bundle landen —
    /// und genau dieser Weg war ungeprüft, obwohl er der einzige ist, der Freitext einsammelt.
    /// </summary>
    [Fact]
    public async Task An_existing_log_file_is_tailed_and_masked()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_dataDirectory, "logs"));
        var lines = Enumerable.Range(0, 50).Select(i => $"Zeile {i}").ToList();
        lines.Add($"conn: Host=db;Password={SecretCorpus.ConnectionString}");
        await File.WriteAllLinesAsync(
            Path.Combine(logs.FullName, "bifrost.log"), lines, TestContext.Current.CancellationToken);

        var service = CreateService();
        var target = Path.Combine(_dataDirectory, "mit-log.zip");

        var result = await service.CreateAsync(
            new SupportBundleRequest(LogTailLines: 5, OutputPath: target), TestContext.Current.CancellationToken);

        result.SkippedCategories.Should().BeEmpty("mit vorhandener Logdatei wird nichts uebersprungen");
        result.Manifest.Files.Should().Contain(f => f.Path == "logs/bifrost.log");

        var content = ReadAll(target);
        content.Should().Contain("Zeile 49", "das Ende des Logs ist der interessante Teil");
        content.Should().NotContain("Zeile 10", "nur die letzten fuenf Zeilen waren verlangt");
        content.Should().NotContain(
            SecretCorpus.Marker, "auch Freitext geht durch den Maskierungslauf");
    }

    [Fact]
    public async Task No_canary_secret_from_any_source_reaches_the_bundle()
    {
        var service = CreateService(withCanaries: true);
        var target = Path.Combine(_dataDirectory, "canary.zip");

        await service.CreateAsync(new SupportBundleRequest(OutputPath: target), TestContext.Current.CancellationToken);

        var content = ReadAll(target);
        content.Should().NotContain(
            SecretCorpus.Marker,
            "ein Wert aus dem Negativkorpus im Bundle ist ein Leck, kein Hinweis");
    }

    [Fact]
    public async Task The_redaction_pass_reports_how_often_it_masked()
    {
        var service = CreateService(withCanaries: true);
        var target = Path.Combine(_dataDirectory, "counted.zip");

        var result = await service.CreateAsync(
            new SupportBundleRequest(OutputPath: target), TestContext.Current.CancellationToken);

        result.Manifest.Redaction.Pass.Should().Be(SupportBundleRedactor.PassName);
        result.Manifest.Redaction.RedactedFields.Should().BeGreaterThan(
            0, "die Quellen dieses Laufs tragen ausdruecklich Secret-Felder");

        // Was maskiert wurde, steht NICHT im Manifest — ein maskierter Wert in einer Inhaltsangabe
        // waere derselbe Fehler wie ein unmaskierter im Dokument.
        JsonSerializer.Serialize(result.Manifest).Should().NotContain(SecretCorpus.Marker);
    }

    /// <summary>
    /// Auditinhalte und Credentialmaterial sind keine Kategorie — auch nicht abschaltbar. Ohne
    /// diesen Test wäre das eine Aussage im Vertrag, die jemand versehentlich aufweicht.
    /// </summary>
    [Fact]
    public void Audit_payload_and_credentials_are_not_a_category_at_all()
    {
        var names = Enum.GetNames<SupportBundleCategory>();

        names.Should().NotContain(n => n.Contains("Audit", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("KeyRing", StringComparison.OrdinalIgnoreCase));
    }

    private SupportBundleService CreateService(bool withCanaries = false) => new(
        new FakeDiagnostics(withCanaries),
        new FakeConfigurationExport(withCanaries),
        new FakeEnvironment(_dataDirectory, withCanaries),
        _time);

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

    private static string Sha256(string content) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FakeDiagnostics(bool withCanaries) : IDiagnosticService
    {
        public Task<DiagnosticReport> RunAsync(DiagnosticScope scope, CancellationToken ct) =>
            Task.FromResult(new DiagnosticReport(
                scope,
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(1),
                [
                    new DiagnosticCheck(
                        "BFR-CFG-0001",
                        CheckStatus.Pass,
                        "Konfiguration gelesen",
                        SafeDetails: withCanaries
                            ? new Dictionary<string, string>
                            {
                                ["dataDirectory"] = "/data",
                                // Eine Sonde, die versehentlich ein Secret mitgibt — genau der Fall,
                                // fuer den es den zentralen Durchgang gibt.
                                ["apiKey"] = SecretCorpus.ApiKeyPlaintext,
                            }
                            : null),
                ]));
    }

    private sealed class FakeConfigurationExport(bool withCanaries) : IConfigurationExportService
    {
        public Task<ConfigurationExport> ExportAsync(ConfigurationExportRequest request, CancellationToken ct)
        {
            request.IncludeSecrets.Should().BeFalse("ein Support-Bundle holt den Export OHNE Secrets");

            var payload = withCanaries
                ? $$"""
                    {
                      "servers": [
                        { "slug": "github",
                          "environment": { "GITHUB_TOKEN": "{{SecretCorpus.StdioEnv}}" },
                          "connectionString": "Host=db;Password={{SecretCorpus.ConnectionString}}",
                          "headers": { "Authorization": "Bearer {{SecretCorpus.HttpHeader}}" } }
                      ]
                    }
                    """
                : """{ "servers": [ { "slug": "github" } ] }""";

            return Task.FromResult(new ConfigurationExport(1, "0.16.0", DateTimeOffset.UnixEpoch, false, payload));
        }

        public Task<ConfigurationImportPlan> PlanImportAsync(string payload, string? passphrase, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<ConfigurationImportPlan> PlanImportAsync(
            string payload, string? passphrase, ConfigurationImportMode mode, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<ConfigurationImportUndo> ApplyImportAsync(ConfigurationImportPlan plan, CancellationToken ct)
            => throw new NotSupportedException();

        public Task UndoImportAsync(ConfigurationImportUndo undo, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeEnvironment(string dataDirectory, bool withCanaries) : ISupportBundleEnvironment
    {
        public string InstanceId => "instanz-1";

        public string DataDirectory { get; } = dataDirectory;

        public Task<IReadOnlyList<string>> AppliedMigrationsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["20260731_Initial"]);

        public Task<IReadOnlyDictionary<string, string>> RuntimeFactsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["os"] = "Linux",
                ["dotnet"] = "10.0.0",
            });

        public Task<IReadOnlyDictionary<string, string>> HealthFactsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["databaseReachable"] = "True",
                ["upstream:github"] = "Healthy",
                ["connection"] = withCanaries
                    ? $"Host=db;Username=bifrost;Password={SecretCorpus.ConnectionString}"
                    : "Host=db;Username=bifrost",
            });
    }
}
