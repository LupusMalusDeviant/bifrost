using System.Net;
using System.Text;

using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;
using Bifrost.Security.Tests.Infrastructure;

using Xunit;

namespace Bifrost.Security.Tests.KeyRing;

/// <summary>
/// Was der Diagnosebericht über den Key-Ring sagt — und was er bewusst nicht sagt (WP3.3, Auftrag 5).
/// <para>
/// Die Linie verläuft hier nicht zwischen „Pfad" und „kein Pfad", sondern zwischen den
/// Verzeichnissen <b>dieser Instanz</b> und dem Ort von <b>Schlüsselmaterial</b>. Das
/// Datenverzeichnis muss im Bericht stehen — ohne es wäre er nutzlos, und es ist die Angabe, die der
/// Betreiber selbst gesetzt hat. Wo das PFX und seine Passwortdatei liegen, ist dagegen die erste
/// Auskunft, die jemand braucht, der an den Key-Ring will.
/// </para>
/// </summary>
public class KeyRingDiagnosticsDisclosureTests
{
    private const string Password = "streng-geheim-4711";
    private const string SecretsDirectory = "/run/secrets/bifrost-intern";
    private const string CertificatePath = SecretsDirectory + "/keyring.pfx";
    private const string PasswordFilePath = SecretsDirectory + "/keyring.password";

    [Fact]
    public async Task The_report_names_neither_the_secret_location_nor_the_password()
    {
        var report = await RunAsync();
        var text = Flatten(report);

        text.Should().NotContain(Password, "das Passwort gehört in keinen Bericht");
        text.Should().NotContain(SecretsDirectory,
            "wo das Schlüsselmaterial liegt, ist die erste Angabe, die ein Angreifer braucht");
        text.Should().NotContain(CertificatePath);
        text.Should().NotContain(PasswordFilePath);
    }

    [Fact]
    public async Task The_report_still_says_which_file_is_meant()
    {
        // Ohne jede Angabe wäre der Bericht nicht mehr benutzbar: Ein Betreiber mit zwei
        // Zertifikaten muss erkennen können, welches gemeint ist. Der Dateiname genügt dafür — den
        // Weg dorthin hat er selbst konfiguriert.
        var report = await RunAsync();
        var text = Flatten(report);

        text.Should().Contain("keyring.pfx");
        report.Checks.Should().Contain(check => check.Code == DiagnosticCodes.KeyRingPasswordSource);
    }

    [Fact]
    public async Task A_password_from_a_file_passes_where_a_password_from_the_environment_warns()
    {
        var fromFile = await RunAsync();
        var fromEnvironment = await RunAsync(passwordInEnvironment: true);

        Check(fromFile, DiagnosticCodes.KeyRingPasswordSource).Status.Should().Be(CheckStatus.Pass);
        var warned = Check(fromEnvironment, DiagnosticCodes.KeyRingPasswordSource);
        warned.Status.Should().Be(CheckStatus.Warning);
        warned.Remediation.Should().Contain(
            KeyRingLayout.CertificatePasswordSetting + KeyRingLayout.FileSuffix);
    }

    [Fact]
    public async Task An_explicitly_unprotected_ring_passes_while_an_undeclared_one_warns()
    {
        // Der dritte Betriebsmodus ist eine Wahl. Eine Diagnose, die auf einer korrekt
        // eingerichteten Instanz nie grün wird, liest nach kurzer Zeit niemand mehr.
        var declared = await RunAsync(environment: new Dictionary<string, string>
        {
            [KeyRingLayout.ProtectionSetting] = KeyRingLayout.NoneMode,
        });
        var undeclared = await RunAsync(environment: new Dictionary<string, string>());

        Check(declared, DiagnosticCodes.KeyRingUnprotected).Status.Should().Be(CheckStatus.Pass);
        Check(undeclared, DiagnosticCodes.KeyRingUnprotected).Status.Should().Be(CheckStatus.Warning);
    }

    [Fact]
    public async Task A_witness_without_key_material_is_reported_as_a_loss()
    {
        var files = new StubFileProbe();
        files.Directories.Add("/data");
        files.Directories.Add("/data/keys");
        files.AddFile(KeyRingLayout.WitnessPathFor("/data"));

        var report = await RunAsync(environment: new Dictionary<string, string>(), files: files);
        var loss = Check(report, DiagnosticCodes.KeyRingLoss);

        loss.Status.Should().Be(CheckStatus.Fail);
        loss.Remediation.Should().Contain("KEINEN neuen Ring");
    }

    [Fact]
    public async Task The_diagnostic_report_is_admin_only()
    {
        // „Ohne Pfad- oder Secret-Leck fuer Unberechtigte" hat zwei Hälften. Die eine ist der Inhalt
        // des Berichts, die andere ist, wer ihn überhaupt bekommt.
        await using var fixture = new SecurityGatewayFixture();
        var (_, plainKey) = await fixture.SeedPlainAsync("keyring-plain");

        using var anonymous = fixture.CreateApiClient(null);
        using var plain = fixture.CreateApiClient(plainKey);

        var withoutKey = await anonymous.GetAsync(
            new Uri("/api/v1/operations/doctor", UriKind.Relative), TestContext.Current.CancellationToken);
        var withoutGrant = await plain.GetAsync(
            new Uri("/api/v1/operations/doctor", UriKind.Relative), TestContext.Current.CancellationToken);

        withoutKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withoutGrant.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Aufbau ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<DiagnosticReport> RunAsync(
        bool passwordInEnvironment = false,
        IReadOnlyDictionary<string, string>? environment = null,
        StubFileProbe? files = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIFROST_DATA_DIR"] = "/data",
        };

        if (environment is null)
        {
            values[KeyRingLayout.CertificatePathSetting] = CertificatePath;
            if (passwordInEnvironment)
            {
                values[KeyRingLayout.CertificatePasswordSetting] = Password;
            }
            else
            {
                values[KeyRingLayout.CertificatePasswordSetting + KeyRingLayout.FileSuffix] = PasswordFilePath;
            }
        }
        else
        {
            foreach (var (key, value) in environment)
            {
                values[key] = value;
            }
        }

        var probe = files ?? DefaultFiles();
        var context = new DiagnosticContext
        {
            Environment = values,
            HostEnvironmentName = "Production",
            Files = probe,
        };

        return await DiagnosticService.CreateDefault(context, TimeProvider.System)
            .RunAsync(DiagnosticScope.KeyRing, TestContext.Current.CancellationToken);
    }

    private static StubFileProbe DefaultFiles()
    {
        var files = new StubFileProbe();
        files.Directories.Add("/data");
        files.Directories.Add("/data/keys");
        files.AddFile("/data/keys/key-11111111-2222-3333-4444-555555555555.xml");
        files.AddFile(CertificatePath);
        files.AddFile(PasswordFilePath);
        return files;
    }

    private static DiagnosticCheck Check(DiagnosticReport report, string code)
        => report.Checks.Single(check => check.Code == code);

    /// <summary>Alles, was der Bericht ausgibt, als ein Text — inklusive der Detailtabellen.</summary>
    private static string Flatten(DiagnosticReport report)
    {
        var text = new StringBuilder();
        foreach (var check in report.Checks)
        {
            text.AppendLine(check.Code).AppendLine(check.Summary).AppendLine(check.Remediation);
            foreach (var (key, value) in check.SafeDetails ?? new Dictionary<string, string>())
            {
                text.Append(key).Append('=').AppendLine(value);
            }
        }

        return text.ToString();
    }

    /// <summary>Ein Dateisystem, das es nicht gibt — sonst liesse sich keine dieser Lagen herstellen.</summary>
    private sealed class StubFileProbe : IFileProbe
    {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Legt eine Datei an — mit normalisiertem Pfad, damit '\' und '/' dasselbe meinen.</summary>
        public void AddFile(string path) => Files.Add(Normalize(path));

        public bool DirectoryExists(string path) => Directories.Contains(Normalize(path));

        public bool FileExists(string path) => Files.Contains(Normalize(path));

        public IReadOnlyList<string> ListFiles(string path, string searchPattern)
        {
            var prefix = Normalize(path) + "/";
            var suffix = searchPattern.TrimStart('*');
            return [.. Files.Where(file =>
                file.StartsWith(prefix, StringComparison.Ordinal)
                && file.EndsWith(suffix, StringComparison.Ordinal))];
        }

        public string? ProbeWritable(string path) => null;

        private static string Normalize(string path) => path.Replace('\\', '/');
    }
}
