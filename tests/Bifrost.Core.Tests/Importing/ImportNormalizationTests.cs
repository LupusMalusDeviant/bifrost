using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;
using Bifrost.Core.Upstreams;

using Xunit;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// Die Normalisierung: Namen, Transporte, Argumente, Umgebung, Header, URLs — und die Zusage, dass
/// ein zweiter Blick auf dieselbe Datei denselben Plan ergibt.
/// </summary>
public sealed class ImportNormalizationTests
{
    // ── Namen ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GitHub Issues", "github-issues")]
    [InlineData("my.server", "my-server")]
    [InlineData("  Abstand  ", "abstand")]
    [InlineData("Server (alt)", "server-alt")]
    [InlineData("Ünter_strich", "nter_strich")]
    [InlineData("---", ImportSlug.Fallback)]
    [InlineData("", ImportSlug.Fallback)]
    public void Ein_fremder_name_wird_zu_einem_zulaessigen_slug(string quelle, string erwartet)
    {
        var result = ImportSlug.Normalize(quelle);

        result.Slug.Should().Be(erwartet);
        ImportSlug.IsValid(result.Slug).Should().BeTrue();
    }

    [Fact]
    public void Ein_bereits_zulaessiger_slug_bleibt_unveraendert()
    {
        var result = ImportSlug.Normalize("github-issues_2");

        result.Slug.Should().Be("github-issues_2");
        result.Changed.Should().BeFalse();
    }

    /// <summary>
    /// Die Abbildung ist idempotent — sonst wäre ein erneuter Import derselben Datei ein anderer
    /// Server, und der Agent verlöre seine Werkzeugnamen ohne dass sich etwas geändert hätte.
    /// </summary>
    [Theory]
    [InlineData("GitHub Issues")]
    [InlineData("my.server")]
    [InlineData("!!!")]
    [InlineData("ein sehr langer name der weit ueber vierundsechzig zeichen hinausgeht und deshalb gekuerzt werden muss")]
    public void Die_namensabbildung_ist_idempotent(string quelle)
    {
        var einmal = ImportSlug.Normalize(quelle).Slug;
        var zweimal = ImportSlug.Normalize(einmal).Slug;

        zweimal.Should().Be(einmal);
        ImportSlug.IsValid(einmal).Should().BeTrue();
        einmal.Length.Should().BeLessThanOrEqualTo(64);
    }

    [Fact]
    public void Eine_umbenennung_wird_gemeldet_und_der_urspruengliche_name_bleibt_sichtbar()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Stdio("GitHub Issues", "/usr/bin/server"));

        var candidate = plan.Candidates.Single();
        candidate.SourceName.Should().Be("GitHub Issues");
        candidate.Config.Slug.Should().Be("github-issues");
        candidate.Config.DisplayName.Should().Be("GitHub Issues");
        candidate.Findings.Should().Contain(f =>
            f.Code == ImportReason.Lossy && f.Summary.Contains("github-issues"));
    }

    [Fact]
    public void Ein_bereits_zulaessiger_name_erzeugt_keinen_befund()
        => ImportWorld.Permissive().Plan(ImportWorld.Stdio("github", "/usr/bin/server"))
            .Candidates.Single().Findings.Should().NotContain(f => f.Code == ImportReason.Lossy);

    // ── Umgebung und Header ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Ein_unzulaessiger_variablenname_wird_benannt_statt_verschluckt()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Stdio(
            "s", "/usr/bin/server", extra: "\"env\": {\"MEIN-WERT\": \"1\", \"GUT\": \"2\"}"));

        var candidate = plan.Candidates.Single();
        candidate.Config.Stdio!.EnvironmentVariables.Should().ContainKey("GUT");
        candidate.Config.Stdio.EnvironmentVariables.Should().NotContainKey("MEIN-WERT");
        candidate.Findings.Should().Contain(f =>
            f.Code == ImportReason.Lossy && f.Summary.Contains("MEIN-WERT"));
    }

    /// <summary>
    /// Headernamen unterscheiden laut RFC 9110 nicht zwischen Groß- und Kleinschreibung. Beide zu
    /// übernehmen hieße, sich auf die Reihenfolge einer fremden Datei zu verlassen.
    /// </summary>
    [Fact]
    public void Ein_doppelter_header_wird_gemeldet()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Http(
            "s",
            "https://api.example.com/mcp",
            "\"headers\": {\"X-Test\": \"a\", \"x-test\": \"b\"}"));

        var candidate = plan.Candidates.Single();
        candidate.Config.Http!.Headers.Should().HaveCount(1);
        candidate.Findings.Should().Contain(f =>
            f.Code == ImportReason.Lossy && f.Summary.Contains("X-Test"));
    }

    [Fact]
    public void Ein_kommando_in_anfuehrungszeichen_wird_entklammert()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Stdio("s", "\\\"/usr/bin/mein server\\\""));

        var candidate = plan.Candidates.Single();
        candidate.Config.Stdio!.Command.Should().Be("/usr/bin/mein server");
        candidate.Findings.Should().Contain(f => f.Code == ImportReason.Lossy);
    }

    [Fact]
    public void Nichttextliche_argumente_werden_als_text_uebergeben_und_das_wird_gesagt()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", "[\"--port\", 8080, true]"));

        var candidate = plan.Candidates.Single();
        candidate.Config.Stdio!.Arguments.Should().Equal("--port", "8080", "true");
        candidate.Findings.Should().Contain(f => f.Code == ImportReason.Lossy);
    }

    [Fact]
    public void Ein_argument_das_kein_wert_ist_wird_nicht_uebernommen_sondern_gemeldet()
    {
        var plan = ImportWorld.Permissive().Plan(
            ImportWorld.Stdio("s", "/usr/bin/server", "[\"--flagge\", {\"a\":1}]"));

        var candidate = plan.Candidates.Single();
        candidate.Config.Stdio!.Arguments.Should().Equal("--flagge");
        candidate.Findings.Should().Contain(f =>
            f.Code == ImportReason.Lossy && f.Severity == ImportSeverity.Warning);
    }

    // ── Die Zielform ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Was der Import liefert, muss der Validator dieses Gateways annehmen können. Wäre es anders,
    /// zeigte die Vorschau einen Server, dessen Anlegen an einer Formalie scheitert — und der Fehler
    /// stünde an einer Stelle, an der niemand mehr weiß, aus welcher Zeile er kam.
    /// </summary>
    [Theory]
    [InlineData("GitHub Issues", "/usr/bin/server")]
    [InlineData("my.server", "C:\\\\Programme\\\\server.exe")]
    [InlineData("???", "/opt/mcp/server")]
    public void Jeder_kandidat_haelt_die_aufbaupruefung_aus(string name, string command)
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Stdio(name, command));

        foreach (var candidate in plan.Candidates)
        {
            FluentActions.Invoking(() => UpstreamConfigValidator.Validate(candidate.Config))
                .Should().NotThrow();
        }
    }

    [Fact]
    public void Ein_http_kandidat_haelt_die_aufbaupruefung_aus()
    {
        var plan = ImportWorld.Permissive().Plan(ImportWorld.Http(
            "Entfernter Dienst", "https://api.example.com/mcp",
            "\"headers\": {\"Authorization\": \"Bearer abc\"}"));

        FluentActions.Invoking(() => UpstreamConfigValidator.Validate(plan.Candidates.Single().Config))
            .Should().NotThrow();
    }

    // ── Roundtrip ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Der Roundtrip: Plan → generisches MCP-JSON → Plan. Was beim zweiten Mal herauskommt, muss
    /// dasselbe sein. Eine Normalisierung, die beim zweiten Durchgang etwas anderes tut, wäre keine.
    /// </summary>
    [Fact]
    public void Ein_plan_ueberlebt_den_weg_durch_das_dateiformat()
    {
        var original = """
        {
          "mcpServers": {
            "GitHub Issues": {
              "command": "npx",
              "args": ["-y", "@scope/server-github"],
              "env": { "GITHUB_TOKEN": "${GITHUB_TOKEN}" }
            },
            "Entfernt": {
              "type": "http",
              "url": "https://api.example.com/mcp",
              "headers": { "Authorization": "Bearer abc123" }
            },
            "lokal": {
              "command": "/opt/mcp/server",
              "args": ["--port", "8080"],
              "cwd": "/opt/mcp"
            }
          }
        }
        """;

        var importer = ImportWorld.Permissive();
        var first = importer.Plan(original);
        var second = importer.Plan(Rewrite(first));

        second.Candidates.Select(c => c.Config)
            .Should().BeEquivalentTo(first.Candidates.Select(c => c.Config));

        second.Candidates.SelectMany(c => c.Findings).Select(f => f.Code)
            .Should().BeEquivalentTo(
                first.Candidates.SelectMany(c => c.Findings).Select(f => f.Code),
                "dieselbe Konfiguration traegt dieselben Risiken — auch nach einer Runde durch das "
                + "Dateiformat");
    }

    /// <summary>
    /// Schreibt die Kandidaten eines Plans zurück in generisches MCP-JSON.
    /// <para>
    /// <b>Bewusst hier im Test und nicht im Produktivcode.</b> Der Export in ein Clientformat ist
    /// WP4.2; ihn hier vorwegzunehmen hiesse, eine Schnittstelle zu bauen, deren Anforderungen noch
    /// niemand geschrieben hat.
    /// </para>
    /// </summary>
    private static string Rewrite(ImportPlan plan)
    {
        var servers = new JsonObject();

        foreach (var candidate in plan.Candidates)
        {
            var entry = new JsonObject();

            if (candidate.Config.Stdio is { } stdio)
            {
                entry["command"] = stdio.Command;
                entry["args"] = new JsonArray([.. stdio.Arguments.Select(a => (JsonNode)JsonValue.Create(a))]);
                if (stdio.EnvironmentVariables is { Count: > 0 } environment)
                {
                    var map = new JsonObject();
                    foreach (var pair in environment)
                    {
                        map[pair.Key] = pair.Value;
                    }

                    entry["env"] = map;
                }

                if (stdio.WorkingDirectory is { } cwd)
                {
                    entry["cwd"] = cwd;
                }
            }

            if (candidate.Config.Http is { } http)
            {
                entry["type"] = http.AllowLegacySse ? "sse" : "http";
                entry["url"] = http.Endpoint.ToString();
                if (http.Headers is { Count: > 0 } headers)
                {
                    var map = new JsonObject();
                    foreach (var pair in headers)
                    {
                        map[pair.Key] = pair.Value;
                    }

                    entry["headers"] = map;
                }
            }

            // Der Quellname, nicht der Slug: Genau so kommt die Datei beim naechsten Mal wieder an.
            servers[candidate.SourceName] = entry;
        }

        return new JsonObject { ["mcpServers"] = servers }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
