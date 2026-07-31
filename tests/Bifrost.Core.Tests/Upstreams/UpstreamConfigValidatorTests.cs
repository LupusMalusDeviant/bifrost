using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

public class UpstreamConfigValidatorTests
{
    private static UpstreamServerConfig Cli(params CliToolSpec[] tools) => new(
        "cli", "CLI", UpstreamTransportKind.Cli, Enabled: true,
        Cli: new CliTransportOptions(Environment.ProcessPath!, tools));

    private static readonly string PublisherKey = Convert.ToBase64String(new byte[32]);

    private static UpstreamServerConfig Wasi(
        IReadOnlyList<string>? pinned = null,
        WasiExecutionLimits? limits = null,
        WasiCapabilityGrants? grants = null,
        IReadOnlyDictionary<string, string>? secrets = null,
        string? cacheDirectory = null,
        long? cacheMaxBytes = null) => new(
        "wasi", "WASI", UpstreamTransportKind.Wasi, Enabled: true,
        Wasi: new WasiTransportOptions(
            "host.exe", "component.wasm", "component.sig",
            pinned ?? [PublisherKey],
            Grants: grants,
            Limits: limits,
            Secrets: secrets,
            ModuleCacheDirectory: cacheDirectory,
            ModuleCacheMaxBytes: cacheMaxBytes));

    [Fact]
    public void Valid_wasi_config_passes()
    {
        var act = () => UpstreamConfigValidator.Validate(Wasi());

        act.Should().NotThrow();
    }

    [Fact]
    public void Wasi_without_a_configured_publisher_is_allowed_since_the_trust_store_supplies_them()
    {
        // Ab WP4 ist der Trust-Store die Vertrauensquelle; das Config-Feld ist nur noch der
        // Migrationspfad. Fail-closed bleibt es trotzdem — nur eine Ebene tiefer: Ist auch der
        // Store leer, gehen null Schlüssel an den Host und der lädt nichts.
        var act = () => UpstreamConfigValidator.Validate(Wasi(pinned: []));

        act.Should().NotThrow();
    }

    [Fact]
    public void Wasi_publisher_key_must_be_a_base64_32_byte_key()
    {
        var act = () => UpstreamConfigValidator.Validate(Wasi(pinned: ["nicht-base64!"]));

        act.Should().Throw<ArgumentException>().WithMessage("*32-Byte*");
    }

    [Fact]
    public void Wasi_rejects_non_positive_limits()
    {
        var act = () => UpstreamConfigValidator.Validate(
            Wasi(limits: new WasiExecutionLimits(MaxOutputBytes: 0)));

        act.Should().Throw<ArgumentException>().WithMessage("*MaxOutputBytes*");
    }

    [Fact]
    public void Wasi_grants_that_the_host_cannot_enforce_are_rejected_at_configuration_time()
    {
        // Relativer Preopen: zeigt je nach Arbeitsverzeichnis des Host-Prozesses woanders hin.
        var relativePreopen = () => UpstreamConfigValidator.Validate(
            Wasi(grants: new WasiCapabilityGrants(FilesystemPreopens: ["daten"])));
        // Netzwerkziel ohne Port lässt sich nicht zu einer Socket-Adresse auflösen.
        var portless = () => UpstreamConfigValidator.Validate(
            Wasi(grants: new WasiCapabilityGrants(NetworkAllow: ["api.example.com"])));

        relativePreopen.Should().Throw<ArgumentException>().WithMessage("*absoluter Pfad*");
        portless.Should().Throw<ArgumentException>().WithMessage("*host:port*");
    }

    [Fact]
    public void Wasi_secret_names_and_values_must_match()
    {
        // Ein gewährter Name ohne Wert laesst den Host fail-closed abweisen — das soll hier
        // auffallen und nicht erst, wenn der Upstream nicht hochkommt.
        var missingValue = () => UpstreamConfigValidator.Validate(
            Wasi(grants: new WasiCapabilityGrants(Secrets: ["db-password"])));
        // Ein Wert ohne Grant kaeme nie an — der Betreiber hat ihn in falscher Annahme hinterlegt.
        var ungranted = () => UpstreamConfigValidator.Validate(
            Wasi(secrets: new Dictionary<string, string> { ["db-password"] = "geheim" }));
        var matching = () => UpstreamConfigValidator.Validate(
            Wasi(grants: new WasiCapabilityGrants(Secrets: ["db-password"]),
                secrets: new Dictionary<string, string> { ["db-password"] = "geheim" }));

        missingValue.Should().Throw<ArgumentException>().WithMessage("*keinen Wert*");
        ungranted.Should().Throw<ArgumentException>().WithMessage("*nie an*");
        matching.Should().NotThrow();
    }

    [Fact]
    public void Wasi_module_cache_directory_must_be_absolute()
    {
        // Dort landen ausfuehrbare Kompilate — ein relativer Pfad zeigt je nach
        // Arbeitsverzeichnis des Host-Prozesses woanders hin.
        var relative = () => UpstreamConfigValidator.Validate(Wasi(cacheDirectory: "cache"));
        var absolute = () => UpstreamConfigValidator.Validate(
            Wasi(cacheDirectory: Path.Combine(Path.GetTempPath(), "wasi-cache")));

        relative.Should().Throw<ArgumentException>().WithMessage("*absoluter Pfad*");
        absolute.Should().NotThrow();
    }

    [Fact]
    public void Wasi_module_cache_budget_needs_a_directory_and_may_not_be_negative()
    {
        var withoutDirectory = () => UpstreamConfigValidator.Validate(Wasi(cacheMaxBytes: 1024));
        var negative = () => UpstreamConfigValidator.Validate(
            Wasi(cacheDirectory: Path.GetTempPath(), cacheMaxBytes: -1));
        // 0 heisst ausdruecklich unbegrenzt und ist damit gueltig.
        var unlimited = () => UpstreamConfigValidator.Validate(
            Wasi(cacheDirectory: Path.GetTempPath(), cacheMaxBytes: 0));

        withoutDirectory.Should().Throw<ArgumentException>().WithMessage("*keine Wirkung*");
        negative.Should().Throw<ArgumentException>().WithMessage("*nicht negativ*");
        unlimited.Should().NotThrow();
    }

    [Fact]
    public void Wasi_accepts_grants_the_host_enforces()
    {
        var act = () => UpstreamConfigValidator.Validate(Wasi(grants: new WasiCapabilityGrants(
            FilesystemPreopens: [Path.GetTempPath()],
            NetworkAllow: ["127.0.0.1:8080"],
            Environment: ["MCPMCP_SPIKE"],
            Clock: true,
            Random: true)));

        act.Should().NotThrow();
    }

    [Fact]
    public void Wasi_options_on_another_transport_are_rejected()
    {
        var config = new UpstreamServerConfig(
            "mix", "Mix", UpstreamTransportKind.Stdio, Enabled: true,
            Stdio: new StdioTransportOptions("echo", []),
            Wasi: new WasiTransportOptions("host.exe", "c.wasm", "c.sig", [PublisherKey]));

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*widersprüchliche Konfiguration*");
    }

    [Fact]
    public void Valid_stdio_config_passes()
    {
        var act = () => UpstreamConfigValidator.Validate(TestData.StdioConfig("github"));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("UPPER")]
    [InlineData("bad__slug")]
    [InlineData("-leading-dash")]
    [InlineData("umlaut-ä")]
    public void Invalid_slugs_are_rejected(string slug)
    {
        var config = TestData.StdioConfig() with { Slug = slug };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stdio_kind_without_stdio_options_is_rejected()
    {
        var config = TestData.StdioConfig() with { Stdio = null };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*Stdio*");
    }

    [Fact]
    public void Mismatched_extra_options_are_rejected()
    {
        var config = TestData.StdioConfig() with
        {
            Http = new HttpTransportOptions(new Uri("http://localhost:1234")),
        };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*widersprüchlich*");
    }

    [Fact]
    public void Empty_stdio_command_is_rejected()
    {
        var config = TestData.StdioConfig() with
        {
            Stdio = new StdioTransportOptions("  ", []),
        };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*Command*");
    }

    [Fact]
    public void NonPositive_call_timeout_is_rejected()
    {
        var config = TestData.StdioConfig() with { CallTimeout = TimeSpan.Zero };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*CallTimeout*");
    }

    [Theory]
    [InlineData(-1, 1, 2.0, 10)]
    [InlineData(3, 0, 2.0, 10)]
    [InlineData(3, 1, 0.5, 10)]
    [InlineData(3, 5, 2.0, 1)]
    public void Invalid_restart_policies_are_rejected(int maxRetries, int initialSeconds, double multiplier, int maxSeconds)
    {
        var config = TestData.StdioConfig() with
        {
            Restart = new RestartPolicy(
                maxRetries, TimeSpan.FromSeconds(initialSeconds), multiplier, TimeSpan.FromSeconds(maxSeconds)),
        };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*RestartPolicy*");
    }

    [Fact]
    public void Http_config_requires_http_options()
    {
        var config = new UpstreamServerConfig(
            "remote", "Remote", UpstreamTransportKind.StreamableHttp, Enabled: true);

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*Http*");
    }

    [Fact]
    public void Duplicate_cli_tool_names_are_rejected()
    {
        var config = Cli(new CliToolSpec("run"), new CliToolSpec("run"));

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*doppelt*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-starts-with-dash")]
    [InlineData("umlaut-ä")]
    public void Invalid_cli_tool_names_are_rejected(string name)
    {
        var act = () => UpstreamConfigValidator.Validate(Cli(new CliToolSpec(name)));

        act.Should().Throw<ArgumentException>().WithMessage("*Toolname*");
    }

    [Fact]
    public void Relative_cli_executable_is_rejected_unless_path_lookup_is_explicit()
    {
        var strict = Cli(new CliToolSpec("run")) with
        {
            Cli = new CliTransportOptions("dotnet", [new CliToolSpec("run")]),
        };
        var development = strict with
        {
            Cli = strict.Cli! with { AllowPathLookup = true },
        };

        Action strictAct = () => UpstreamConfigValidator.Validate(strict);
        Action developmentAct = () => UpstreamConfigValidator.Validate(development);

        strictAct
            .Should().Throw<ArgumentException>().WithMessage("*absolut*");
        developmentAct.Should().NotThrow();
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(1, 0)]
    public void Nonpositive_cli_limits_are_rejected(int concurrency, int outputBytes)
    {
        var config = Cli(new CliToolSpec("run")) with
        {
            Cli = Cli(new CliToolSpec("run")).Cli! with
            {
                MaxConcurrency = concurrency,
                MaxOutputBytes = outputBytes,
            },
        };

        Action act = () => UpstreamConfigValidator.Validate(config);

        act
            .Should().Throw<ArgumentException>().WithMessage("*Cli*");
    }

    [Fact]
    public void Contradictory_cli_parameters_are_rejected()
    {
        var config = Cli(new CliToolSpec(
            "run",
            Parameters:
            [
                new CliParameterSpec("target", Position: 0, Flag: "--target"),
                new CliParameterSpec("target"),
            ]));

        Action act = () => UpstreamConfigValidator.Validate(config);

        act
            .Should().Throw<ArgumentException>();
    }
}
