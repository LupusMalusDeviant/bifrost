using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Audit;
using Bifrost.Core.Configuration;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Upstreams;
using Bifrost.Security.Tests.Infrastructure;
using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// <b>Invariante 4 (M3-Vertrag §6.2):</b> Secrets werden vor Persistenz, Log, Export und Diagnose
/// redigiert — auf <b>jedem</b> Ausgabeweg.
/// <para>
/// Die Fehlerklasse, gegen die dieses Paket antritt, steht im Fortschrittsprotokoll: Der Redactor
/// kannte sechs Transporte, ein siebter kam dazu, sein Zugangsdatum ging im Klartext an die API —
/// und der Test, der „jeder Transport" im Namen trug, prueste genau einen. Die Lehre daraus ist
/// nicht „OpenRPC nachtragen", sondern eine Stelle zu haben, an der das Vergessen auffaellt.
/// </para>
/// </summary>
public class RedactionCorpusTests
{
    private static UpstreamServerConfig FullyLoadedConfig() => new(
        "alle", "Alle Transporte", UpstreamTransportKind.Stdio, true,
        Stdio: new StdioTransportOptions(
            "server", ["--serve"],
            new Dictionary<string, string> { ["TOKEN"] = SecretCorpus.StdioEnv }),
        Http: new HttpTransportOptions(
            new Uri("https://example.invalid/mcp"),
            Headers: new Dictionary<string, string> { ["Authorization"] = SecretCorpus.HttpHeader }),
        OpenApi: new OpenApiTransportOptions(
            new Uri("https://example.invalid/openapi.json"),
            Credential: SecretCorpus.OpenApiCredential),
        Cli: new CliTransportOptions(
            "tool", [new CliToolSpec("run")],
            EnvironmentVariables: new Dictionary<string, string> { ["PASSWORD"] = SecretCorpus.CliEnv }),
        Wasi: new WasiTransportOptions(
            "bifrost-wasi-host", "component.wasm", "component.sig", ["cHVibGlzaGVy"],
            Secrets: new Dictionary<string, string> { ["API_KEY"] = SecretCorpus.WasiSecret }),
        OpenRpc: new OpenRpcTransportOptions(
            new Uri("https://example.invalid/rpc"),
            Credential: SecretCorpus.OpenRpcCredential));

    // ───────────────────────── REST-/UI-Pfad ─────────────────────────

    /// <summary>
    /// Der Weg ueber <c>ApiEndpoints</c> an Oberflaeche und API. Erst der Nachweis, dass es
    /// ueberhaupt etwas zu maskieren gibt — sonst waere der folgende Vergleich auch dann gruen,
    /// wenn ein Transport gar nicht serialisiert wuerde.
    /// </summary>
    [Fact]
    public void The_rest_path_masks_every_transport_secret()
    {
        var config = FullyLoadedConfig();
        var plain = JsonSerializer.Serialize(config);
        foreach (var secret in Placed())
        {
            plain.Should().Contain(secret, "sonst prueft der Vergleich einen Wert, der nie ausgegeben wird");
        }

        var masked = JsonSerializer.Serialize(UpstreamConfigRedactor.Redact(config));

        SecretCorpus.FirstLeakIn(masked).Should().BeNull(
            "der Redactor sitzt in ApiEndpoints vor der Upstream-Liste — was hier durchkommt, "
            + "steht in der Oberflaeche");
    }

    /// <summary>
    /// Die Gegenrichtung, und die zweite Haelfte desselben Fehlers von damals: Wer einen
    /// maskierten Wert zurueckspeichert, darf nicht die Maske persistieren. Ein Upstream, dessen
    /// Zugangsdatum woertlich aus drei Sternen besteht, laeuft bis zum naechsten Neustart weiter.
    /// </summary>
    [Fact]
    public void Saving_a_masked_configuration_restores_every_secret()
    {
        var previous = FullyLoadedConfig();
        var edited = UpstreamConfigRedactor.Redact(previous);

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);
        var json = JsonSerializer.Serialize(merged);

        json.Should().NotContain(UpstreamConfigRedactor.Mask,
            "die Maske als echter Wert zerlegt den Upstream lautlos");
        foreach (var secret in Placed())
        {
            json.Should().Contain(secret, "der bestehende Wert muss zurueckkommen");
        }
    }

    /// <summary>
    /// <b>Der Waechter ueber alle Ausgabewege.</b> Er zaehlt nicht die Transporte auf, sondern
    /// jede Eigenschaft im Konfigurationsmodell, deren Name nach einem Geheimnis aussieht — und
    /// verlangt, dass der Typ, der sie traegt, im Korpus oben tatsaechlich befuellt ist.
    /// <para>
    /// <b>Wie er bei einer neuen Stelle rot wird:</b> Ein achter Transport, ein neues Feld
    /// <c>ClientSecret</c> an einem bestehenden Transport, ein zweiter Satz Umgebungsvariablen —
    /// alles drei erscheint hier von selbst. Der Test kennt die Fehlerklasse, nicht die Faelle.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_secret_bearing_field_in_the_config_model_is_covered_by_the_corpus()
    {
        string[] secretish =
        [
            "Credential", "Secret", "Secrets", "Password", "Passphrase", "Token", "ApiKey",
            "Headers", "EnvironmentVariables", "ClientSecret",
        ];

        var config = FullyLoadedConfig();
        var serialized = JsonSerializer.Serialize(config);
        var uncovered = new List<string>();

        foreach (var transport in typeof(UpstreamServerConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.Name.EndsWith("TransportOptions", StringComparison.Ordinal)))
        {
            var options = transport.GetValue(config);
            if (options is null)
            {
                uncovered.Add(
                    $"{transport.Name}: im Korpus gar nicht befuellt — ein Transport, dessen "
                    + "Secrets nie geprueft werden");
                continue;
            }

            foreach (var field in options.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => secretish.Contains(p.Name, StringComparer.Ordinal)))
            {
                var value = field.GetValue(options);
                var filled = value switch
                {
                    null => false,
                    string text => SecretCorpus.All.Contains(text, StringComparer.Ordinal),
                    IReadOnlyDictionary<string, string> map =>
                        map.Values.Any(v => SecretCorpus.All.Contains(v, StringComparer.Ordinal)),
                    _ => false,
                };

                if (!filled)
                {
                    uncovered.Add($"{transport.Name}.{field.Name}");
                }
            }
        }

        // Zweiter Beleg: Was der Korpus behauptet befuellt zu haben, muss auch wirklich in der
        // Serialisierung stehen. Ein Feld mit [JsonIgnore] waere sonst still abgedeckt.
        foreach (var secret in Placed())
        {
            serialized.Should().Contain(secret);
        }

        uncovered.Should().BeEmpty(
            "jedes dieser Felder traegt ein Geheimnis, das kein Test durch die Maskierung "
            + "schickt — genau die Luecke, durch die das OpenRPC-Credential ging. Gefunden:\n"
            + string.Join('\n', uncovered));
    }

    // ───────────────────────── Meta-/MCP-Pfad ─────────────────────────

    /// <summary>
    /// Der Weg, den Tool-Argumente ins Audit nehmen: <see cref="IRedactionService"/>. Er speist
    /// den MCP-Pfad (<c>tools/call</c>), den REST-Aufruf, den Webhook und die Meta-Tools —
    /// alle vier laufen durch denselben Invoker.
    /// </summary>
    [Theory]
    [InlineData("password")]
    [InlineData("api_key")]
    [InlineData("Authorization")]
    [InlineData("clientSecret")]
    [InlineData("access-token")]
    public void The_argument_path_masks_named_secret_fields(string fieldName)
    {
        var service = new RedactionService(new NoExtraRules());
        var args = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"harmlos":"ja","{{fieldName}}":"{{SecretCorpus.ToolArgument}}"}""");

        var redacted = service.RedactArguments(new NamespacedToolName("srv__tool"), args);

        SecretCorpus.FirstLeakIn(redacted.GetRawText()).Should().BeNull(
            $"'{fieldName}' traegt ein Geheimnis ins Audit-Log, das dort dauerhaft liegen bleibt");
    }

    /// <summary>
    /// Verschachtelt. Ein Redigierer, der nur die oberste Ebene ansieht, ist bei jedem realen
    /// Toolschema wirkungslos — Zugangsdaten stehen dort in einem Unterobjekt.
    /// </summary>
    [Fact]
    public void The_argument_path_reaches_into_nested_objects_and_arrays()
    {
        var service = new RedactionService(new NoExtraRules());
        var args = JsonSerializer.Deserialize<JsonElement>(
            $$$"""
            {"auth":{"nested":{"token":"{{{SecretCorpus.ToolArgument}}}"}},
             "liste":[{"password":"{{{SecretCorpus.ApiKeyPlaintext}}}"}]}
            """);

        var redacted = service.RedactArguments(new NamespacedToolName("srv__tool"), args);

        SecretCorpus.FirstLeakIn(redacted.GetRawText()).Should().BeNull();
    }

    // ───────────────────────── Support-/Diagnosepfad ─────────────────────────

    /// <summary>
    /// Der Diagnosebericht ist die Ausgabe, die ein Betreiber im Stoerungsfall weitergibt — an
    /// den Hersteller, in ein Ticket, in einen Chat. Was hier durchkommt, verlaesst das Haus.
    /// </summary>
    [Theory]
    [InlineData("Verbindung fehlgeschlagen: Password={0}")]
    [InlineData("BIFROST_KEYRING_CERT_PASSWORD={0}")]
    [InlineData("Host=db;Username=bifrost;Password={0};Database=bifrost")]
    [InlineData("Authorization: Bearer {0}")]
    [InlineData("https://benutzer:{0}@interner-host/pfad")]
    [InlineData("client_secret = \"{0}\"")]
    [InlineData("api-key: {0}")]
    public void The_support_path_masks_named_values(string template)
    {
        var text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, template, SecretCorpus.ConnectionString);

        text.Should().Contain(SecretCorpus.ConnectionString, "sonst prueft der Test nichts");

        SecretCorpus.FirstLeakIn(DiagnosticRedaction.Scrub(text)).Should().BeNull(
            $"'{template}' geht so in den Diagnosebericht");
    }

    /// <summary>
    /// Der ganze Befund, nicht nur sein Text: Zusammenfassung, Abhilfe und die Detailtabelle —
    /// samt ihrer Schluessel, denn einer davon kommt von aussen.
    /// </summary>
    [Fact]
    public void The_support_path_masks_the_whole_finding()
    {
        var check = new DiagnosticCheck(
            "BFR-CFG-0001",
            CheckStatus.Fail,
            $"Upstream antwortet nicht (token={SecretCorpus.OAuthToken})",
            $"Setze secret={SecretCorpus.WebhookSecret} neu",
            new Dictionary<string, string>
            {
                [$"apikey={SecretCorpus.ApiKeyPlaintext}"] = "im Schluessel",
                ["connectionString"] = $"Password={SecretCorpus.ConnectionString}",
            });

        var scrubbed = DiagnosticRedaction.Scrub(check);

        SecretCorpus.FirstLeakIn(JsonSerializer.Serialize(scrubbed)).Should().BeNull();
    }

    // ───────────────────────── Exportpfad ─────────────────────────

    /// <summary>
    /// Der Konfigurationsexport ist die einzige Ausgabe, die den ganzen Zustand am Stueck
    /// mitnimmt — und die einzige, die ein Betreiber gedankenlos in ein Repository legt.
    /// </summary>
    [Fact]
    public void The_export_path_carries_no_secret()
    {
        var scrubbed = ConfigurationSecretScrubber.Scrub("alle", FullyLoadedConfig());

        SecretCorpus.FirstLeakIn(JsonSerializer.Serialize(scrubbed)).Should().BeNull(
            "ein Export mit Klartext-Zugangsdaten landet im Zweifel in einem Git-Repository");
    }

    // ───────────────────────── Importpfad (WP4.3) ─────────────────────────

    /// <summary>
    /// Der Verbindungstest des Konfigurationsimports. Er ist der eine Ausgabeweg dieses Pakets, der
    /// <b>nicht</b> selbst gebaut wird: Die Meldung stammt aus einem fremden Prozess oder Dienst,
    /// und ein Prozess, der nicht startet, schreibt gern seine Kommandozeile hinein.
    /// <para>
    /// Der Scrubber raet nicht, was ein Geheimnis sein koennte — er kennt die Werte genau der
    /// Konfiguration, die gerade getestet wurde, und entfernt diese.
    /// </para>
    /// </summary>
    [Fact]
    public void The_import_probe_path_removes_the_values_of_the_tested_configuration()
    {
        var config = FullyLoadedConfig();
        var fremdmeldung = "Verbindungstest fehlgeschlagen. Uebergebene Umgebung: "
            + string.Join(' ', Placed())
            + " — Aufruf abgebrochen.";

        foreach (var secret in Placed())
        {
            fremdmeldung.Should().Contain(secret, "sonst prueft der Test nichts");
        }

        var scrubbed = Bifrost.Server.Importing.ImportValueScrubber.Scrub(fremdmeldung, config);

        SecretCorpus.FirstLeakIn(scrubbed).Should().BeNull(
            "die Fehlermeldung eines Verbindungstests geht unveraendert an Oberflaeche und CLI");
        scrubbed.Should().Contain("Verbindungstest fehlgeschlagen",
            "die Meldung soll lesbar bleiben — sonst hilft sie niemandem bei der Fehlersuche");
    }

    /// <summary>
    /// <b>Der Waechter ueber die Redigierer selbst.</b> Jede Stelle, die etwas maskiert, ist ein
    /// Ausgabeweg — und jeder Ausgabeweg muss gegen den Korpus laufen. Kommt ein Redigierer dazu,
    /// dessen Ergebnis kein Test gegen den Korpus haelt, wird der Test rot und verlangt eine
    /// Entscheidung.
    /// </summary>
    [Fact]
    public void A_new_redactor_forces_a_decision_about_the_corpus()
    {
        var known = new[]
        {
            // Die vier echten Ausgabewege — jeder hat oben einen Test mit dem Korpus.
            "Bifrost.Core.Audit.RedactionService",
            "Bifrost.Core.Configuration.ConfigurationSecretScrubber",
            "Bifrost.Core.Diagnostics.DiagnosticRedaction",
            "Bifrost.Core.Upstreams.UpstreamConfigRedactor",
            // Diese beiden geben nichts aus: Sie halten die tool-eigenen Zusatzmuster, die
            // RedactionService anwendet. Ihre Wirkung wird oben ueber den Argumentpfad geprueft.
            "Bifrost.Persistence.RedactionRuleRow",
            "Bifrost.Persistence.RedactionRuleStore",
            // WP4.3: Der Konfigurationsimport. Er hat zwei Ausgabewege, und nur einer davon ist ein
            // Redigierer — das Vorschaumodell ist eine Positivliste und maskiert nichts (der
            // Nachweis dafuer steht in ImportSecretSeparationTests). Der Scrubber hier raeumt die
            // eine Ausgabe auf, die NICHT dort entsteht: die Fehlermeldung eines fremden Prozesses
            // oder Dienstes beim Verbindungstest. Sein Korpustest steht unten.
            "Bifrost.Server.Importing.ImportValueScrubber",
        };

        var redactors = BifrostAssemblies.AllTypes()
            .Where(type => type.Name.Contains("Redact", StringComparison.Ordinal)
                || type.Name.Contains("Scrubber", StringComparison.Ordinal))
            .Where(type => !type.IsInterface && !type.Name.StartsWith('<'))
            .Select(type => type.FullName!)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        redactors.Should().BeEquivalentTo(known,
            "ein neuer Redigierer ist ein neuer Ausgabeweg. Er gehoert in diese Datei, mit einem "
            + "Test, der SecretCorpus.All durch ihn hindurchschickt");
    }

    /// <summary>Die Korpuswerte, die in <see cref="FullyLoadedConfig"/> tatsaechlich stehen.</summary>
    private static IEnumerable<string> Placed() =>
    [
        SecretCorpus.StdioEnv, SecretCorpus.HttpHeader, SecretCorpus.OpenApiCredential,
        SecretCorpus.CliEnv, SecretCorpus.WasiSecret, SecretCorpus.OpenRpcCredential,
    ];

    private sealed class NoExtraRules : IRedactionRules
    {
        public IReadOnlyList<string>? GetPatterns(NamespacedToolName tool) => null;

        public IReadOnlyDictionary<NamespacedToolName, IReadOnlyList<string>> All { get; }
            = new Dictionary<NamespacedToolName, IReadOnlyList<string>>();

        public Task SetAsync(NamespacedToolName tool, IReadOnlyList<string>? patterns, CancellationToken ct)
            => Task.CompletedTask;
    }
}
