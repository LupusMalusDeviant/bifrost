using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Operations.SupportBundle;

/// <summary>
/// Sammelt, maskiert und packt die Angaben, mit denen sich ein Pilotfehler ohne Vollzugriff
/// analysieren lässt (WP5.6, FR-P075).
/// <para>
/// Das Bundle ist <b>standardmäßig weitergabefähig</b>: Es enthält keine Auditinhalte und kein
/// Credentialmaterial, und jedes Dokument geht durch
/// <see cref="SupportBundleRedactor"/>. Was fehlt, steht als übersprungene Kategorie mit Begründung
/// darin — ein Bundle ohne Logs soll nicht aussehen wie eine Installation ohne Vorfälle.
/// </para>
/// </summary>
public sealed class SupportBundleService : ISupportBundleService
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly SupportBundleCategory[] DefaultCategories =
    [
        SupportBundleCategory.Version,
        SupportBundleCategory.Configuration,
        SupportBundleCategory.Diagnostics,
        SupportBundleCategory.Health,
        SupportBundleCategory.Logs,
        SupportBundleCategory.Migrations,
        SupportBundleCategory.Runtime,
    ];

    private readonly IDiagnosticService _diagnostics;
    private readonly IConfigurationExportService _configuration;
    private readonly ISupportBundleEnvironment _environment;
    private readonly TimeProvider _time;

    public SupportBundleService(
        IDiagnosticService diagnostics,
        IConfigurationExportService configuration,
        ISupportBundleEnvironment environment,
        TimeProvider time)
    {
        _diagnostics = diagnostics;
        _configuration = configuration;
        _environment = environment;
        _time = time;
    }

    public async Task<SupportBundlePreview> PreviewAsync(SupportBundleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (documents, skipped, _) = await CollectAsync(request, ct);
        return new SupportBundlePreview(
            [.. documents.Select(d => new SupportBundleEntry(d.Path, d.Category, Encoding.UTF8.GetByteCount(d.Content)))],
            skipped,
            documents.Sum(d => (long)Encoding.UTF8.GetByteCount(d.Content)));
    }

    public async Task<SupportBundleResult> CreateAsync(SupportBundleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (documents, skipped, redactedFields) = await CollectAsync(request, ct);
        var createdAt = _time.GetUtcNow();
        var path = request.OutputPath ?? DefaultPath(createdAt);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var files = documents
            .Select(d => new SupportBundleFile(
                d.Path,
                d.Category.ToString().ToLowerInvariant(),
                Encoding.UTF8.GetByteCount(d.Content),
                Sha256(Encoding.UTF8.GetBytes(d.Content))))
            .ToList();

        var manifest = new SupportBundleManifest(
            FormatVersion,
            BifrostProductInfo.Version,
            createdAt,
            _environment.InstanceId,
            [.. documents.Select(d => d.Category.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal)],
            files,
            new SupportBundleRedaction(SupportBundleRedactor.PassName, redactedFields),
            "sha-256");

        await using (var archive = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create))
        {
            // Das Manifest liegt unverschlüsselt und zuerst im Archiv — genau wie beim Backup:
            // Sonst kann ein Empfänger nicht prüfen, was er vor sich hat, bevor er es öffnet.
            await WriteEntryAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, Json), ct);
            foreach (var document in documents)
            {
                await WriteEntryAsync(zip, document.Path, document.Content, ct);
            }
        }

        var bytes = new FileInfo(path).Length;
        return new SupportBundleResult(path, bytes, Sha256(await File.ReadAllBytesAsync(path, ct)), manifest, skipped);
    }

    private async Task<(List<Document> Documents, List<SupportBundleSkip> Skipped, int Redacted)> CollectAsync(
        SupportBundleRequest request, CancellationToken ct)
    {
        var categories = request.Categories is { Count: > 0 }
            ? request.Categories.ToHashSet()
            : [.. DefaultCategories];

        var redactor = new SupportBundleRedactor();
        var documents = new List<Document>();
        var skipped = new List<SupportBundleSkip>();

        foreach (var category in DefaultCategories.Where(categories.Contains))
        {
            switch (category)
            {
                case SupportBundleCategory.Version:
                    documents.Add(Document.Json(redactor, "version.json", category, new
                    {
                        product = BifrostProductInfo.Version,
                        runtime = Environment.Version.ToString(),
                        collectedAt = _time.GetUtcNow(),
                    }));
                    break;

                case SupportBundleCategory.Configuration:
                    // Derselbe Weg wie der Konfigurationsexport, ausdrücklich OHNE Secrets — eine
                    // zweite Sammelstelle für dieselben Daten wäre eine zweite Wahrheit darüber,
                    // was als Secret gilt.
                    var export = await _configuration.ExportAsync(
                        new ConfigurationExportRequest(IncludeSecrets: false), ct);
                    documents.Add(Document.RawJson(redactor, "configuration.json", category, export.Payload));
                    break;

                case SupportBundleCategory.Diagnostics:
                    var report = await _diagnostics.RunAsync(DiagnosticScope.All, ct);
                    documents.Add(Document.Json(redactor, "diagnostics.json", category, report));
                    break;

                case SupportBundleCategory.Health:
                    documents.Add(Document.Json(
                        redactor, "health.json", category, await _environment.HealthFactsAsync(ct)));
                    break;

                case SupportBundleCategory.Migrations:
                    documents.Add(Document.Json(redactor, "migrations.json", category, new
                    {
                        applied = await _environment.AppliedMigrationsAsync(ct),
                    }));
                    break;

                case SupportBundleCategory.Runtime:
                    documents.Add(Document.Json(
                        redactor, "runtime.json", category, await _environment.RuntimeFactsAsync(ct)));
                    break;

                case SupportBundleCategory.Logs:
                    var log = CollectLog(redactor, request.LogTailLines);
                    if (log is null)
                    {
                        skipped.Add(new SupportBundleSkip(
                            category,
                            "Diese Installation schreibt kein Anwendungslog in das Datenverzeichnis; "
                            + "die Ausgabe gehört dem Prozessverwalter. Der Hinweis unter "
                            + "'logs/how-to-collect.txt' nennt die Befehle."));
                        documents.Add(Document.Text(
                            redactor, "logs/how-to-collect.txt", category, LogCollectionHint()));
                    }
                    else
                    {
                        documents.Add(log);
                    }

                    break;
            }
        }

        return (documents, skipped, redactor.RedactedFields);
    }

    /// <summary>
    /// Sucht das jüngste Anwendungslog im Datenverzeichnis. Findet es keines, ist das kein Fehler,
    /// sondern eine Aussage über die Installation — und die gehört ins Bundle.
    /// </summary>
    private Document? CollectLog(SupportBundleRedactor redactor, int tailLines)
    {
        var directory = Path.Combine(_environment.DataDirectory, "logs");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var newest = new DirectoryInfo(directory)
            .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            return null;
        }

        var lines = File.ReadLines(newest.FullName).TakeLast(Math.Max(1, tailLines));
        return Document.Text(redactor, $"logs/{newest.Name}", SupportBundleCategory.Logs, string.Join('\n', lines));
    }

    private static string LogCollectionHint() =>
        """
        Das Gateway schreibt sein Anwendungslog auf die Standardausgabe. Wer es beilegen will,
        holt es dort, wo der Prozess läuft:

          docker compose logs --no-color --tail 500 bifrost > bifrost.log
          journalctl -u bifrost --no-pager -n 500 > bifrost.log

        Bitte VOR dem Weitergeben durchsehen: Diese Ausgabe geht nicht durch den Maskierungslauf
        dieses Bundles.
        """;

    private string DefaultPath(DateTimeOffset createdAt) => Path.Combine(
        _environment.DataDirectory,
        "support",
        $"bifrost-support-{createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip");

    private static async Task WriteEntryAsync(ZipArchive zip, string path, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private sealed record Document(string Path, SupportBundleCategory Category, string Content)
    {
        public static Document Json(
            SupportBundleRedactor redactor, string path, SupportBundleCategory category, object payload) =>
            new(path, category, redactor.RedactJson(JsonSerializer.Serialize(payload, SupportBundleService.Json)));

        public static Document RawJson(
            SupportBundleRedactor redactor, string path, SupportBundleCategory category, string json) =>
            new(path, category, redactor.RedactJson(json));

        public static Document Text(
            SupportBundleRedactor redactor, string path, SupportBundleCategory category, string text) =>
            new(path, category, redactor.RedactText(text));
    }
}
