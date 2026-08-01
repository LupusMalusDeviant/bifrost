using AwesomeAssertions;

using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// Je Risikoindikator ein Positiv- und ein Negativfall.
/// <para>
/// <b>Der Negativfall ist der wichtigere Test.</b> Ein Melder, der immer meldet, ist keine Warnung,
/// sondern Rauschen — und Rauschen wird abgeschaltet. Deshalb steht neben jedem „das ist ein Risiko"
/// ein „und das hier ausdrücklich nicht".
/// </para>
/// </summary>
public sealed class ImportRiskIndicatorTests
{
    // ── BFR-IMP-0100 Hostausführung ───────────────────────────────────────────────────────────

    /// <summary>
    /// ADR-0025 E4: Der Importpfad ist ein Erzeugungsweg und fragt dieselbe Policy wie jeder andere.
    /// Auf einer frischen Instanz ist native Ausführung verboten — und ein Plan, der beim Anwenden
    /// scheitern würde, gilt nicht als anwendbar.
    /// </summary>
    [Fact]
    public void Ein_stdio_server_auf_einer_frischen_instanz_ist_ein_fehler()
    {
        var plan = ImportWorld.Strict().Plan(ImportWorld.Stdio("s", "/usr/bin/server"));

        var finding = plan.AllFindings().Should()
            .ContainSingle(f => f.Code == ImportReason.HostExecution).Subject;
        finding.Severity.Should().Be(ImportSeverity.Error);
        finding.Summary.Should().Contain("BFR-POL-", "der stabile Code der Policy bleibt sichtbar");
        plan.CanApply.Should().BeFalse();
    }

    /// <summary>
    /// Erlaubt die Instanz native Ausführung, bleibt es ein Risiko — sichtbar, aber nicht
    /// blockierend. Der Unterschied kommt aus der Policy und nicht aus dem Import.
    /// </summary>
    [Fact]
    public void Ein_stdio_server_auf_einer_erlaubenden_instanz_ist_ein_risiko()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", "/usr/bin/server"));

        plan.AllFindings().Should()
            .ContainSingle(f => f.Code == ImportReason.HostExecution)
            .Which.Severity.Should().Be(ImportSeverity.Risk);
        plan.CanApply.Should().BeTrue();
    }

    [Fact]
    public void Ein_http_server_beruehrt_die_hostpolicy_nicht()
        => ImportWorld.Strict().Plan(ImportWorld.Http("web", "https://api.example.com/mcp"))
            .AllCodes().Should().NotContain(ImportReason.HostExecution);

    // ── BFR-IMP-0101 PATH-Auflösung ───────────────────────────────────────────────────────────

    [Fact]
    public void Ein_kommando_ohne_verzeichnis_haengt_an_der_PATH_variablen()
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", "mcp-server"))
            .AllCodes().Should().Contain(ImportReason.PathLookup);

    [Theory]
    [InlineData("/usr/local/bin/mcp-server")]
    [InlineData("C:\\\\Programme\\\\mcp\\\\server.exe")]
    public void Ein_absolutes_kommando_haengt_an_nichts(string command)
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", command))
            .AllCodes().Should().NotContain(ImportReason.PathLookup);

    // ── BFR-IMP-0102 relative Pfade ───────────────────────────────────────────────────────────

    /// <summary>
    /// Plattformpfade, beide Richtungen. Der Prüfer läuft auf <em>einem</em> Betriebssystem, die
    /// geprüfte Datei kommt von einem beliebigen — ein Befund, der nur vom Betriebssystem des
    /// Prüfers handelt, ist keiner.
    /// </summary>
    [Theory]
    [InlineData("/opt/mcp/server")]
    [InlineData("/usr/bin/server")]
    [InlineData("C:\\\\Program Files\\\\mcp\\\\server.exe")]
    [InlineData("D:/werkzeuge/server.exe")]
    [InlineData("\\\\\\\\dateiserver\\\\freigabe\\\\server.exe")]
    public void Absolute_pfade_beider_plattformen_sind_kein_befund(string command)
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", command))
            .AllCodes().Should().NotContain(ImportReason.RelativePath);

    [Theory]
    [InlineData("./bin/server")]
    [InlineData("..\\\\bin\\\\server.exe")]
    [InlineData("~/werkzeuge/server")]
    [InlineData("${HOME}/werkzeuge/server")]
    [InlineData("%USERPROFILE%\\\\mcp\\\\server.exe")]
    [InlineData("\\\\laufwerksrelativ\\\\server.exe")]
    public void Relative_und_umgebungsabhaengige_pfade_werden_gemeldet(string command)
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", command))
            .AllCodes().Should().Contain(ImportReason.RelativePath);

    /// <summary>Ein Pfad mit Umgebungsvariablen wird gemeldet und ausdrücklich nicht aufgelöst.</summary>
    [Fact]
    public void Ein_umgebungsabhaengiger_pfad_wird_nicht_aufgeloest()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", "${HOME}/bin/server"));

        plan.Candidates.Single().Config.Stdio!.Command.Should().Be("${HOME}/bin/server");
    }

    [Fact]
    public void Ein_relatives_arbeitsverzeichnis_wird_gemeldet()
        => ImportWorld.Permissive()
            .Plan(ImportWorld.Stdio("s", "/usr/bin/server", extra: "\"cwd\": \"projekte/mcp\""))
            .AllCodes().Should().Contain(ImportReason.RelativePath);

    [Fact]
    public void Ein_absolutes_arbeitsverzeichnis_wird_nicht_gemeldet()
        => ImportWorld.Permissive()
            .Plan(ImportWorld.Stdio("s", "/usr/bin/server", extra: "\"cwd\": \"/opt/projekte/mcp\""))
            .AllCodes().Should().NotContain(ImportReason.RelativePath);

    // ── BFR-IMP-0103 Nachladen beim Start ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("npx", "[\"-y\",\"@scope/server\"]")]
    [InlineData("uvx", "[\"mcp-server\"]")]
    [InlineData("/usr/local/bin/npx", "[\"@scope/server\"]")]
    [InlineData("C:\\\\Program Files\\\\nodejs\\\\npx.cmd", "[\"@scope/server\"]")]
    [InlineData("pnpm", "[\"dlx\",\"@scope/server\"]")]
    [InlineData("bun", "[\"x\",\"@scope/server\"]")]
    [InlineData("uv", "[\"tool\",\"run\",\"mcp-server\"]")]
    public void Nachladende_starter_werden_erkannt(string command, string argumentsJson)
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", command, argumentsJson))
            .AllCodes().Should().Contain(ImportReason.FetchesCodeAtStart);

    [Theory]
    [InlineData("/usr/bin/node", "[\"/opt/mcp/server.js\"]")]
    [InlineData("/usr/bin/python3", "[\"/opt/mcp/server.py\"]")]
    [InlineData("npm", "[\"run\",\"start\"]")]
    public void Ein_gewoehnlicher_start_ist_kein_nachladen(string command, string argumentsJson)
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", command, argumentsJson))
            .AllCodes().Should().NotContain(ImportReason.FetchesCodeAtStart);

    /// <summary>Die Selbstbestätigung <c>-y</c> steht in der Meldung — sie ist der eigentliche Punkt.</summary>
    [Fact]
    public void Die_selbstbestaetigung_wird_benannt()
        => ImportWorld.Permissive()
            .Plan(ImportWorld.Stdio("s", "npx", "[\"-y\",\"@scope/server\"]"))
            .AllFindings().Single(f => f.Code == ImportReason.FetchesCodeAtStart)
            .Summary.Should().Contain("-y");

    // ── BFR-IMP-0104 Image ohne Digest ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[\"run\",\"-i\",\"--rm\",\"ghcr.io/scope/server:1.2\"]")]
    [InlineData("[\"run\",\"--rm\",\"-e\",\"TOKEN\",\"ghcr.io/scope/server\"]")]
    [InlineData("[\"run\",\"--rm\",\"-v\",\"/daten:/daten\",\"ghcr.io/scope/server:latest\"]")]
    [InlineData("[\"run\",\"--env=A=b\",\"registry.example.com:5000/scope/server:2\"]")]
    public void Ein_image_ohne_digest_wird_gemeldet(string argumentsJson)
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", "docker", argumentsJson))
            .AllCodes().Should().Contain(ImportReason.UnpinnedImage);

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public void Ein_image_mit_digest_wird_nicht_gemeldet(string runtime)
    {
        const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", runtime, $"[\"run\",\"--rm\",\"-e\",\"TOKEN\",\"ghcr.io/scope/server@{Digest}\"]"));

        plan.AllCodes().Should().NotContain(ImportReason.UnpinnedImage);
    }

    [Fact]
    public void Ein_gewoehnliches_programm_traegt_kein_image()
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", "/usr/bin/node", "[\"/opt/x.js\"]"))
            .AllCodes().Should().NotContain(ImportReason.UnpinnedImage);

    /// <summary>
    /// Lässt sich das Image nicht bestimmen, wird das gesagt — und nicht geraten. Eine Warnung ohne
    /// Behauptung ist die ehrlichere Antwort als ein Befund über ein Image, das gar nicht gemeint war.
    /// </summary>
    [Fact]
    public void Ein_unbestimmbares_image_wird_als_unbestimmbar_gemeldet()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "docker", "[\"exec\",\"-i\",\"behaelter\",\"server\"]"));

        plan.AllFindings().Should().ContainSingle(f => f.Code == ImportReason.UnpinnedImage)
            .Which.Severity.Should().Be(ImportSeverity.Warning);
    }

    // ── BFR-IMP-0105 privates Netzwerkziel ────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1:8080/mcp")]
    [InlineData("http://localhost:3000/mcp")]
    [InlineData("https://192.168.178.61:5100/mcp")]
    [InlineData("http://10.0.0.5/mcp")]
    [InlineData("http://172.16.4.4/mcp")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://mcp.internal/dienst")]
    [InlineData("https://badwolf/mcp")]
    [InlineData("http://[::1]:8080/mcp")]
    [InlineData("http://[fd00::1]/mcp")]
    public void Ein_privates_ziel_wird_gemeldet(string url)
        => ImportWorld.Permissive().Plan(ImportWorld.Http("s", url))
            .AllCodes().Should().Contain(ImportReason.PrivateTarget);

    [Theory]
    [InlineData("https://api.example.com/mcp")]
    [InlineData("https://mcp.githubusercontent.test/v1")]
    [InlineData("https://8.8.8.8/mcp")]
    public void Ein_oeffentliches_ziel_wird_nicht_gemeldet(string url)
        => ImportWorld.Permissive().Plan(ImportWorld.Http("s", url))
            .AllCodes().Should().NotContain(ImportReason.PrivateTarget);

    /// <summary>
    /// Der Import trifft die SSRF-Entscheidung nicht: <c>AllowPrivateTargets</c> bleibt offen. Ein
    /// Import, der <c>null</c> in <c>false</c> umschriebe, klemmte einen Upstream ab, der vorher
    /// lief — dieselbe stille Verhaltensänderung, die ADR-0025 E3 ablehnt.
    /// </summary>
    [Fact]
    public void Der_import_entscheidet_nichts_ueber_private_ziele()
        => ImportWorld.Permissive().Plan(ImportWorld.Http("s", "http://127.0.0.1:8080/mcp"))
            .Candidates.Single().Config.Http!.AllowPrivateTargets.Should().BeNull();

    // ── Abbildung ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Der_abgeloeste_sse_transport_wird_als_verlustbehaftet_gemeldet()
    {
        var document = """
        {
          "mcpServers": {
            "alt": { "type": "sse", "url": "https://api.example.com/sse" }
          }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.AllCodes().Should().Contain(ImportReason.Lossy);
        plan.Candidates.Single().Config.Http!.AllowLegacySse.Should().BeTrue();
    }

    [Fact]
    public void Eine_kaputte_url_wird_nicht_zurechtgebogen()
    {
        var document = """
        {
          "mcpServers": { "s": { "type": "http", "url": "example.com/mcp" } }
        }
        """;

        var plan = ImportWorld.Permissive().Plan(document);

        plan.Candidates.Should().BeEmpty();
        plan.CanApply.Should().BeFalse();
    }
}
