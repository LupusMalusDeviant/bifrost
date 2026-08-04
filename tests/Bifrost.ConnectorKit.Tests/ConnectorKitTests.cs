using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Bifrost.ConnectorKit;
using Xunit;

namespace Bifrost.ConnectorKit.Tests;

/// <summary>
/// WP6.5: Das Kit gegen seine eigene Dokumentation.
/// <para>
/// <b>Der erste Test ist die DoD dieses Pakets</b> — „ein neues Beispielpaket entsteht und besteht
/// die Probe mit den dokumentierten Befehlen". Er fährt genau die Befehlsfolge aus
/// <c>docs/connectors/developer-kit.md</c>. Wird die Dokumentation falsch, wird er rot.
/// </para>
/// </summary>
public sealed class ConnectorKitTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"cdk-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public ConnectorKitTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string KeyPath => Path.Combine(_work, "publisher.key.json");

    private string ProjectPath => Path.Combine(_work, "beispiel");

    private Task<int> RunAsync(params string[] args) =>
        new ConnectorKitCli(_output, _error).RunAsync(args, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_documented_commands_produce_a_package_that_passes_the_probe()
    {
        (await RunAsync("init", ProjectPath, "--id", "beispiel", "--key", KeyPath))
            .Should().Be(ConnectorKitExit.Success, _error.ToString());

        File.Copy(
            Path.Combine(RepoRoot, "samples", "counter-connector", "payload", "connector.wasm"),
            Path.Combine(ProjectPath, "payload", "connector.wasm"));

        (await RunAsync("build", ProjectPath, "--key", KeyPath))
            .Should().Be(ConnectorKitExit.Success, _error.ToString());

        var package = Path.Combine(ProjectPath, "out", "beispiel-0.1.0.mcpkg");
        File.Exists(package).Should().BeTrue();

        (await RunAsync("test", package, "--publisher", KeyPath))
            .Should().Be(ConnectorKitExit.Success, _error.ToString());

        _output.ToString().Should().Contain("Probe bestanden");
    }

    /// <summary>
    /// Das Beispiel aus dem Repository — derselbe Weg, aber mit den Dateien, die ein Leser der
    /// Dokumentation vor sich hat. Ohne diesen Fall wäre `samples/` eine Behauptung.
    /// </summary>
    [Fact]
    public async Task The_sample_connector_in_the_repository_builds_and_passes()
    {
        var sample = Path.Combine(_work, "counter-connector");
        CopyDirectory(Path.Combine(RepoRoot, "samples", "counter-connector"), sample);

        (await RunAsync("init", Path.Combine(_work, "leer"), "--key", KeyPath))
            .Should().Be(ConnectorKitExit.Success, _error.ToString());

        (await RunAsync("build", sample, "--key", KeyPath))
            .Should().Be(ConnectorKitExit.Success, _error.ToString());

        (await RunAsync("test", Path.Combine(sample, "out", "counter-connector-0.1.0.mcpkg"),
                "--publisher", KeyPath))
            .Should().Be(ConnectorKitExit.Success, _error.ToString());
    }

    /// <summary>
    /// <b>Die Gegenprobe.</b> Ein verändertes Byte in der Nutzlast muss auffallen — sonst prüft die
    /// Probe oben nur, dass eine Datei existiert.
    /// </summary>
    [Fact]
    public async Task A_tampered_payload_is_refused()
    {
        var package = await BuildSampleAsync();
        Tamper(package, "payload/connector.wasm");

        (await RunAsync("test", package, "--publisher", KeyPath))
            .Should().Be(ConnectorKitExit.PackageInvalid);
        _error.ToString().Should().NotBeEmpty("ein abgelehntes Paket bekommt eine Begründung");
    }

    [Fact]
    public async Task A_package_from_another_publisher_is_refused()
    {
        var package = await BuildSampleAsync();
        var fremd = Path.Combine(_work, "fremd.key.json");

        (await RunAsync("init", Path.Combine(_work, "fremdprojekt"), "--key", fremd))
            .Should().Be(ConnectorKitExit.Success);

        (await RunAsync("verify", package, "--publisher", fremd))
            .Should().Be(ConnectorKitExit.PackageInvalid,
                "ein fremder Herausgeber darf ein Paket nicht beglaubigen");
    }

    /// <summary>
    /// Ein Paket für eine fremde Vertragsversion ist echt signiert und trotzdem unbrauchbar.
    /// <para>
    /// <b>Befund aus dem ersten Testlauf:</b> Der Test hiess urspruenglich „passes verify but fails
    /// the probe" — und war falsch. Die Vertragsversion prueft der Leser des <em>Produkts</em>,
    /// also schon <c>verify</c>. Meine Probe hatte dieselbe Pruefung ein zweites Mal, wo sie nie
    /// greifen konnte: toter Code, der Gruendlichkeit vortaeuscht. Er ist weg, und der Test haelt
    /// jetzt fest, wer wirklich urteilt.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_foreign_contract_version_is_refused_by_the_products_own_reader()
    {
        await RunAsync("init", ProjectPath, "--id", "beispiel", "--key", KeyPath);
        File.Copy(
            Path.Combine(RepoRoot, "samples", "counter-connector", "payload", "connector.wasm"),
            Path.Combine(ProjectPath, "payload", "connector.wasm"));

        var manifestPath = Path.Combine(ProjectPath, ConnectorProject.FileName);
        await File.WriteAllTextAsync(
            manifestPath,
            (await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken))
                .Replace("\"contractVersion\": \"1\"", "\"contractVersion\": \"99\"", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        await RunAsync("build", ProjectPath, "--key", KeyPath);
        var package = Path.Combine(ProjectPath, "out", "beispiel-0.1.0.mcpkg");

        (await RunAsync("verify", package, "--publisher", KeyPath))
            .Should().Be(ConnectorKitExit.PackageInvalid,
                "die Vertragsversion prueft der Leser des Produkts — nicht erst die Probe");
        _error.ToString().Should().Contain("Vertragsversion");

        (await RunAsync("test", package, "--publisher", KeyPath))
            .Should().Be(ConnectorKitExit.PackageInvalid);
    }

    /// <summary>
    /// <b>Was die Probe wirklich hinzufuegt.</b> Die abgetrennte Signatur des Einstiegspunkts
    /// prueft der Paketleser NICHT — er sieht nur, dass die Datei deklariert ist und ihr Hash
    /// stimmt. Ob sie eine gueltige Signatur ueber die Component ist, entscheidet sonst erst der
    /// WASI-Host auf dem Rechner des Betreibers.
    /// </summary>
    [Fact]
    public async Task A_wrong_component_signature_passes_verify_and_fails_the_probe()
    {
        var package = await BuildSampleAsync();

        // Die Signaturdatei durch eine gleich lange, aber falsche ersetzen — der Hash im Manifest
        // wird mitgezogen, damit NUR die Signatur selbst falsch ist.
        var (manifest, files) = ConnectorPackageSource.Read(
            await File.ReadAllBytesAsync(package, TestContext.Current.CancellationToken));
        var mutated = new Dictionary<string, byte[]>(files, StringComparer.Ordinal)
        {
            [manifest.SignaturePath] = new byte[files[manifest.SignaturePath].Length],
        };

        var key = PublisherKeyFile.Load(KeyPath);
        await File.WriteAllBytesAsync(
            package,
            Bifrost.Core.Packaging.ConnectorPackageBuilder.Build(manifest, mutated, key.Sign),
            TestContext.Current.CancellationToken);

        (await RunAsync("verify", package, "--publisher", KeyPath))
            .Should().Be(ConnectorKitExit.Success, "Manifest und Hashes sind in Ordnung");

        (await RunAsync("test", package, "--publisher", KeyPath))
            .Should().Be(ConnectorKitExit.PackageInvalid,
                "der WASI-Host wuerde diese Component ablehnen — das soll der Entwickler vorher wissen");
        _error.ToString().Should().Contain("Einstiegspunkt");
    }

    /// <summary>
    /// <b>Die Regel, an der dieses Werkzeug hängt:</b> Der private Schlüssel verlässt seine Datei
    /// nicht — auch nicht über eine Ausgabe.
    /// </summary>
    [Fact]
    public async Task The_private_key_never_appears_in_any_output()
    {
        await RunAsync("init", ProjectPath, "--id", "beispiel", "--key", KeyPath);
        File.Copy(
            Path.Combine(RepoRoot, "samples", "counter-connector", "payload", "connector.wasm"),
            Path.Combine(ProjectPath, "payload", "connector.wasm"));
        await RunAsync("build", ProjectPath, "--key", KeyPath);
        await RunAsync("test", Path.Combine(ProjectPath, "out", "beispiel-0.1.0.mcpkg"), "--publisher", KeyPath);

        var seed = System.Text.Json.JsonDocument
            .Parse(await File.ReadAllTextAsync(KeyPath, TestContext.Current.CancellationToken))
            .RootElement.GetProperty("seed").GetString()!;

        seed.Should().NotBeNullOrWhiteSpace("sonst prueft der Vergleich nichts");
        (_output.ToString() + _error.ToString()).Should().NotContain(
            seed, "ein privater Schluessel in einer Konsolenausgabe steht danach in der Shell-Historie");
    }

    [Fact]
    public async Task An_existing_key_is_never_overwritten()
    {
        (await RunAsync("init", ProjectPath, "--key", KeyPath)).Should().Be(ConnectorKitExit.Success);
        var original = await File.ReadAllTextAsync(KeyPath, TestContext.Current.CancellationToken);

        // Zweites Projekt, derselbe Schluessel: Er wird WIEDERVERWENDET, nicht ersetzt.
        (await RunAsync("init", Path.Combine(_work, "zweites"), "--key", KeyPath))
            .Should().Be(ConnectorKitExit.Success);

        (await File.ReadAllTextAsync(KeyPath, TestContext.Current.CancellationToken))
            .Should().Be(original, "ein ersetzter Schluessel entwertet jedes damit signierte Paket");
    }

    [Fact]
    public async Task A_typo_in_an_option_is_a_usage_error_and_not_a_silently_different_package()
    {
        await RunAsync("init", ProjectPath, "--key", KeyPath);

        (await RunAsync("build", ProjectPath, "--key", KeyPath, "--ausgabe", "x.mcpkg"))
            .Should().Be(ConnectorKitExit.UsageError);
        _error.ToString().Should().Contain("--ausgabe");
    }

    [Fact]
    public async Task Without_a_key_the_build_says_so_instead_of_guessing()
    {
        (await RunAsync("build", ProjectPath)).Should().Be(ConnectorKitExit.UsageError);
        _error.ToString().Should().Contain("--key");
    }

    private async Task<string> BuildSampleAsync()
    {
        await RunAsync("init", ProjectPath, "--id", "beispiel", "--key", KeyPath);
        File.Copy(
            Path.Combine(RepoRoot, "samples", "counter-connector", "payload", "connector.wasm"),
            Path.Combine(ProjectPath, "payload", "connector.wasm"));
        (await RunAsync("build", ProjectPath, "--key", KeyPath)).Should().Be(ConnectorKitExit.Success);
        return Path.Combine(ProjectPath, "out", "beispiel-0.1.0.mcpkg");
    }

    private static void Tamper(string package, string entryName)
    {
        using var archive = ZipFile.Open(package, ZipArchiveMode.Update);
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException(entryName);
        using var stream = entry.Open();
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(Encoding.ASCII.GetBytes("XX"));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bifrost.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Repo-Root nicht gefunden.");
        }
    }
}
