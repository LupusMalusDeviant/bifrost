using System.Diagnostics;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Guardrails;
using Xunit;

namespace McpMcp.Core.Tests.Guardrails;

/// <summary>
/// ADR-0011. Geprüft werden die Zusagen, die die Entscheidung tragen — nicht nur, dass
/// irgendetwas gefunden wird.
/// </summary>
public class SecretGuardTests
{
    private static SecretGuard Default(GuardOptions? options = null)
        => new(BuiltInGuardRules.All, options);

    // Bewusst konstruierte, keine echten Zugangsdaten.
    private const string FakeAwsKey = "AKIAIOSFODNN7EXAMPLE";
    private const string FakeGitHubToken = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    [Theory]
    [InlineData(FakeAwsKey)]
    [InlineData(FakeGitHubToken)]
    [InlineData("glpat-abcdefghij0123456789")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuv")]
    [InlineData("npm_abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Known_credential_shapes_are_detected(string secret)
    {
        var verdict = Default().Inspect($$"""{"value":"{{secret}}"}""", GuardDirection.Inbound);

        verdict.Findings.Should().NotBeEmpty();
        verdict.Blocked.Should().BeTrue("der kuratierte Regelsatz blockt ab Werk");
    }

    [Fact]
    public void Finding_never_contains_the_secret_in_clear_text()
    {
        // Der Punkt, an dem LiteLLM einen Vorfall hatte: Eine Secret-Erkennung, die ihre Funde
        // protokolliert, kopiert Secrets in ein zweites System.
        var verdict = Default().Inspect($$"""{"key":"{{FakeAwsKey}}"}""", GuardDirection.Inbound);

        var finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Fingerprint.Should().NotContain(FakeAwsKey);
        finding.RuleDescription.Should().NotContain(FakeAwsKey);
        finding.Should().NotBeNull();

        // Auch die serialisierte Form darf den Wert nicht enthalten — Findings landen im Audit.
        System.Text.Json.JsonSerializer.Serialize(finding).Should().NotContain(FakeAwsKey);
    }

    [Fact]
    public void Same_secret_yields_the_same_fingerprint_for_correlation()
    {
        var guard = Default();

        var a = guard.Inspect($$"""{"a":"{{FakeAwsKey}}"}""", GuardDirection.Inbound).Findings[0];
        var b = guard.Inspect($$"""{"b":"{{FakeAwsKey}}"}""", GuardDirection.Outbound).Findings[0];

        a.Fingerprint.Should().Be(b.Fingerprint, "gleiche Funde müssen sich zusammenführen lassen");
    }

    [Theory]
    [InlineData("""{"sha":"e83c5163316f89bfbde7d9ab23ca2e25604af290"}""")]      // Git-Commit-SHA
    [InlineData("""{"id":"550e8400-e29b-41d4-a716-446655440000"}""")]           // UUID
    [InlineData("""{"hash":"5d41402abc4b2a76b9719d911017c592"}""")]             // MD5
    [InlineData("""{"key":"pk_live_abcdefghijklmnop"}""")]                      // Stripe PUBLIC key
    [InlineData("""{"note":"siehe -----BEGIN PRIVATE KEY----- in der Doku"}""")] // Erwähnung ohne Rumpf
    public void Common_false_positive_shapes_are_not_flagged(string payload)
    {
        // Genau diese Formen haben den Ausschlag gegen die Entropie-Heuristik gegeben (ADR-0011, E3):
        // Hex-Entropie schlägt auf Git-SHAs und UUIDs zu 100 % an. Unter "blockieren" wäre jeder
        // Treffer ein abgebrochener Arbeitsschritt.
        Default().Inspect(payload, GuardDirection.Inbound).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Jwt_is_observed_not_blocked()
    {
        var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop";

        var verdict = Default().Inspect($$"""{"token":"{{jwt}}"}""", GuardDirection.Inbound);

        verdict.Findings.Should().ContainSingle().Which.Mode.Should().Be(GuardMode.Observe);
        verdict.Blocked.Should().BeFalse(
            "JWT-Header und -Payload sind nur Base64; öffentliche ID-Tokens matchen identisch");
    }

    [Fact]
    public void Direction_is_respected()
    {
        var outboundOnly = new SecretGuard(
        [
            new GuardRule("t", "Test", @"\bSEKRET-\d{4}\b", "SEKRET-", GuardDirection.Outbound, GuardMode.Block),
        ]);

        outboundOnly.Inspect("""{"v":"SEKRET-1234"}""", GuardDirection.Outbound).Blocked.Should().BeTrue();
        outboundOnly.Inspect("""{"v":"SEKRET-1234"}""", GuardDirection.Inbound).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_rules_and_global_kill_switch_are_honoured()
    {
        var rule = new GuardRule("t", "Test", @"\bSEKRET-\d{4}\b", "SEKRET-", GuardDirection.Both, GuardMode.Block);
        const string payload = """{"v":"SEKRET-1234"}""";

        new SecretGuard([rule with { Enabled = false }])
            .Inspect(payload, GuardDirection.Inbound).Findings.Should().BeEmpty("Regel ist deaktiviert");

        new SecretGuard([rule], new GuardOptions { Enabled = false })
            .Inspect(payload, GuardDirection.Inbound).Findings.Should().BeEmpty("Not-Aus greift");
    }

    [Fact]
    public void Reload_swaps_rules_at_runtime()
    {
        var guard = new SecretGuard([]);
        const string payload = """{"v":"SEKRET-1234"}""";

        guard.Inspect(payload, GuardDirection.Inbound).Findings.Should().BeEmpty();

        guard.Reload([new GuardRule("t", "Test", @"\bSEKRET-\d{4}\b", "SEKRET-", GuardDirection.Both, GuardMode.Block)]);

        guard.Inspect(payload, GuardDirection.Inbound).Blocked.Should().BeTrue("hot-swappable ohne Neustart");
    }

    [Fact]
    public void A_broken_rule_does_not_take_down_the_rest()
    {
        // Ein Tippfehler in der UI darf nicht die gesamte Guardrail stilllegen.
        var guard = new SecretGuard(
        [
            new GuardRule("kaputt", "Ungültig", "(?<=x)y", null, GuardDirection.Both, GuardMode.Block),
            new GuardRule("gut", "Gültig", @"\bSEKRET-\d{4}\b", "SEKRET-", GuardDirection.Both, GuardMode.Block),
        ]);

        guard.Inspect("""{"v":"SEKRET-1234"}""", GuardDirection.Inbound).Blocked.Should().BeTrue();
    }

    [Theory]
    [InlineData("(?<=x)y", "Lookbehind")]
    [InlineData("(?=x)y", "Lookahead")]
    [InlineData(@"(\w)\1", "Rückwärtsreferenz")]
    public void Unsupported_constructs_are_rejected_at_save_time(string pattern, string what)
    {
        // Die Ablehnung passiert im Konstruktor — der Editor kann sie sofort anzeigen, statt
        // dass es später im Hot Path knallt.
        var act = () => SecretGuard.ValidatePattern(pattern, TimeSpan.FromMilliseconds(50));

        act.Should().Throw<ArgumentException>($"{what} kann die NonBacktracking-Engine nicht");
    }

    [Fact]
    public void Classic_redos_pattern_does_not_hang()
    {
        // NonBacktracking garantiert lineare Laufzeit in der Eingabelänge — mit Compiled würde
        // dieses Muster hier hängen bzw. in den Timeout laufen.
        SecretGuard.ValidatePattern("^(a+)+$", TimeSpan.FromMilliseconds(50));
        var guard = new SecretGuard([new GuardRule("redos", "ReDoS", "^(a+)+$", null, GuardDirection.Both, GuardMode.Block)]);

        var sw = Stopwatch.StartNew();
        guard.Inspect(new string('a', 40) + "!", GuardDirection.Inbound);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500, "lineare Laufzeit statt katastrophalem Backtracking");
    }

    [Fact]
    public void Overlong_patterns_are_rejected()
    {
        var act = () => SecretGuard.ValidatePattern(new string('a', SecretGuard.MaxPatternLength + 1), TimeSpan.FromSeconds(1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Clean_payload_stays_well_under_the_latency_budget()
    {
        // NFR-01: Der Gateway liegt bei p95 = 7 ms. Die Prüfung darf davon nur einen Bruchteil kosten.
        var guard = Default();
        var payload = $$"""{"items":[{{string.Join(",", Enumerable.Range(0, 200).Select(i => $$"""{"id":{{i}},"name":"eintrag-{{i}}","sha":"e83c5163316f89bfbde7d9ab23ca2e25604af290"}"""))}}]}""";
        payload.Length.Should().BeGreaterThan(10_000, "aussagekräftige Größenordnung");

        // Warmup (JIT + Regex-Kompilierung).
        for (var i = 0; i < 20; i++)
        {
            guard.Inspect(payload, GuardDirection.Inbound);
        }

        // Gemessen wird ein VERHÄLTNIS, keine Millisekundenzahl.
        //
        // Eine absolute Schwelle taugt hier nicht: Lokal kostet die Prüfung ~0,05 ms, auf einem
        // geteilten CI-Runner wurden 1,04 ms gemessen — Faktor 20 allein aus Rauschen. Jede
        // absolute Grenze, die dieses Rauschen übersteht, liegt damit oberhalb dessen, was eine
        // echte Regression auszeichnet, und schützt gegen nichts mehr. Genau deshalb liegt der
        // Durchsatz-Benchmark in einem eigenen CI-Job („geteilte Runner sind keine
        // Referenz-Hardware").
        //
        // Wovor der Test wirklich schützen soll, ist ein Sprung um Größenordnungen: ein
        // backtrackendes Muster oder ein entfallener Keyword-Prefilter (ADR-0011). Das lässt sich
        // maschinenunabhängig prüfen, indem dieselbe Nutzlast auf derselben Maschine gegen einen
        // naiven Einzel-Scan normiert wird.
        static double BestPerCallMs(Action work)
        {
            var best = double.MaxValue;
            for (var batch = 0; batch < 5; batch++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 100; i++)
                {
                    work();
                }

                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds / 100);
            }

            return best;
        }

        var guardMs = BestPerCallMs(() => guard.Inspect(payload, GuardDirection.Inbound));
        // Grundlinie: ein einziger ordinaler Durchlauf über dieselbe Nutzlast, ohne Treffer.
        var baselineMs = BestPerCallMs(() => payload.Contains("kommt-hier-nicht-vor", StringComparison.Ordinal));
        var ratio = guardMs / Math.Max(baselineMs, double.Epsilon);

        ratio.Should().BeLessThan(500,
            "die Prüfung darf ein Vielfaches eines Einzel-Scans kosten, aber keine Größenordnung mehr — "
            + $"ist: {ratio:0} × (Prüfung {guardMs:0.000} ms, Grundlinie {baselineMs:0.000} ms)");
    }
}
