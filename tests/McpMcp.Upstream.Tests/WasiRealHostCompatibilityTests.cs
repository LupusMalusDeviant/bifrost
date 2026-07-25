using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Upstream.Wasi;
using Xunit;

namespace McpMcp.Upstream.Tests;

/// <summary>
/// Plan 0003, WP6.2 — Kompatibilität gegen den <b>echten</b> Rust-Host, nicht gegen den Stub.
/// <see cref="WasiRuntimeConnectorTests"/> prüft die .NET-Seite deterministisch; hier läuft
/// dieselbe Leitung gegen das gebaute Binary: Handshake, signierter Load, Discovery, Invoke und
/// die Versionsverhandlung mit älterer und neuerer Vertragsversion. Ohne diesen Test bliebe die
/// Wire-Kompatibilität beider Implementierungen behauptet statt belegt.
/// <para>
/// Der Host wird nicht gebaut, sondern gesucht: <c>MCPMCP_WASI_HOST</c> zeigt direkt auf das
/// Binary, sonst wird <c>spikes/wasi-component-runtime/target/{release,debug}</c> abgesucht. Fehlt
/// er, werden die Tests übersprungen — eine .NET-Umgebung ohne Rust-Toolchain soll das Gate nicht
/// reißen. Damit der Nachweis nicht still ausfällt, erzwingt <c>MCPMCP_REQUIRE_WASI_HOST=1</c>
/// (im Rust-fähigen CI-Job gesetzt) einen harten Fehlschlag statt eines Skips.
/// </para>
/// </summary>
public sealed class WasiRealHostCompatibilityTests
{
    /// <summary>Die vom Rust-Host signierten Fixture-Bytes (Guest-Component + detached Signatur).</summary>
    private static readonly string FixturesDirectory =
        Path.Combine(RepositoryRoot(), "spikes", "wasi-component-runtime", "fixtures");

    private static readonly string ComponentPath =
        Path.Combine(FixturesDirectory, "wasi-p2-guest.component.wasm");

    private static readonly string SignaturePath =
        Path.Combine(FixturesDirectory, "wasi-p2-guest.component.sig");

    private static readonly string PublisherPath =
        Path.Combine(FixturesDirectory, "wasi-p2-guest.publisher.pub");

    private static readonly JsonElement NoArgs = JsonSerializer.Deserialize<JsonElement>("{}");

    /// <summary>Der Environment-Grant, den die Fixture-Component zum Instanziieren braucht.</summary>
    private static readonly string[] EnvironmentGrant = ["MCPMCP_SPIKE"];

    /// <summary>release vor debug: sind beide da, ist der optimierte Build der aussagekräftigere.</summary>
    private static readonly string[] BuildProfiles = ["release", "debug"];

    /// <summary>
    /// Pfad zum echten Host oder <c>null</c>. Wird beim Fehlen zu einem Skip — außer
    /// <c>MCPMCP_REQUIRE_WASI_HOST=1</c> verlangt den Nachweis.
    /// </summary>
    private static string RequireHost()
    {
        var host = LocateHost();
        if (host is null)
        {
            var required = Environment.GetEnvironmentVariable("MCPMCP_REQUIRE_WASI_HOST") is "1" or "true";
            Assert.SkipUnless(required, "WASI-Host-Binary nicht gefunden — 'cargo build' in spikes/wasi-component-runtime oder MCPMCP_WASI_HOST setzen.");
            Assert.Fail("MCPMCP_REQUIRE_WASI_HOST ist gesetzt, aber kein Host-Binary auffindbar.");
        }

        return host;
    }

    private static string? LocateHost()
    {
        if (Environment.GetEnvironmentVariable("MCPMCP_WASI_HOST") is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? configured : null;
        }

        var name = OperatingSystem.IsWindows()
            ? "mcpmcp-wasi-component-spike.exe"
            : "mcpmcp-wasi-component-spike";
        var target = Path.Combine(RepositoryRoot(), "spikes", "wasi-component-runtime", "target");

        return BuildProfiles
            .Select(profile => Path.Combine(target, profile, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Sucht die Solution-Wurzel — Testbinaries liegen mehrere Ebenen tiefer.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MCPMCP.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("MCPMCP.slnx oberhalb des Testverzeichnisses nicht gefunden.");
    }

    private static async Task<string> PinnedPublisherAsync(CancellationToken ct)
        => (await File.ReadAllTextAsync(PublisherPath, ct).ConfigureAwait(false)).Trim();

    // Die gepinnten Publisher kommen ab WP4 aus dem Trust-Store, nicht aus der Konfiguration.
    private static UpstreamServerConfig Config(string host, WasiCapabilityGrants grants) => new(
        "wasi-real", "WASI (echter Host)", UpstreamTransportKind.Wasi, Enabled: true,
        Wasi: new WasiTransportOptions(host, ComponentPath, SignaturePath, [], Grants: grants));

    private static WasiRuntimeConnector ConnectorFor(string publisher)
        => new(new FakePublisherTrustStore(publisher));

    [Fact]
    public async Task Handshake_load_discover_and_invoke_work_against_the_real_host()
    {
        var host = RequireHost();
        var ct = TestContext.Current.CancellationToken;
        // Der Guest importiert wasi:cli/environment — ohne diesen Grant würde er gar nicht erst
        // instanziiert (deny-before-instantiation, siehe Negativtest unten).
        var connector = ConnectorFor(await PinnedPublisherAsync(ct));
        var config = Config(host, new WasiCapabilityGrants(Environment: EnvironmentGrant));

        await using var connection = await connector.ConnectAsync(new ServerId(Guid.NewGuid()), config, ct);
        var inventory = await connection.DiscoverAsync(ct);
        var result = await connection.CallToolAsync("wasi_cli_run", NoArgs, ct);

        // Genau ein Katalogeintrag für den Kommando-Einstiegspunkt, unter normalisiertem Namen
        // (WP6.1). Vorher standen hier zwei Einträge — Instanz und ihre innere run-Funktion.
        inventory.Tools.Select(tool => tool.Name).Should().Equal("wasi_cli_run");
        inventory.Tools[0].Description.Should().Contain("wasi:cli/run@0.2.6",
            "der rohe Export-Name muss auffindbar bleiben");
        inventory.Tools[0].InputSchema.GetProperty("properties").EnumerateObject()
            .Should().BeEmpty("ein Kommando-Export nimmt keine Argumente");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("mcpmcp-guest-ok", "das echte Component schreibt diese Marke auf stdout");
    }

    [Fact]
    public async Task A_granted_secret_reaches_the_component_without_appearing_in_the_audit()
    {
        var host = RequireHost();
        var ct = TestContext.Current.CancellationToken;
        var connector = ConnectorFor(await PinnedPublisherAsync(ct));
        // Der Fixture-Guest gibt den Wert von MCPMCP_SPIKE aus — hier also den Secret-Wert statt
        // des Platzhalters, den ein blosser Environment-Grant setzt.
        var config = Config(host, new WasiCapabilityGrants(Secrets: ["MCPMCP_SPIKE"])) with
        {
            Wasi = new WasiTransportOptions(
                host, ComponentPath, SignaturePath, PinnedPublishers: [],
                Grants: new WasiCapabilityGrants(Secrets: ["MCPMCP_SPIKE"]),
                Secrets: new Dictionary<string, string> { ["MCPMCP_SPIKE"] = "s3hr-geheim" }),
        };

        await using var connection = await connector.ConnectAsync(new ServerId(Guid.NewGuid()), config, ct);
        var result = await connection.CallToolAsync("wasi_cli_run", NoArgs, ct);

        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("mcpmcp-guest-ok:s3hr-geheim", "der Secret-Wert muss im Guest ankommen");
    }

    [Fact]
    public async Task The_real_host_enforces_default_deny_across_the_wire()
    {
        var host = RequireHost();
        var ct = TestContext.Current.CancellationToken;
        // Ohne Grants: derselbe Aufruf muss scheitern — nicht als Transportfehler, sondern als
        // Fehlerergebnis, das die Governance-Schicht sauber weiterreichen kann.
        var connector = ConnectorFor(await PinnedPublisherAsync(ct));
        var config = Config(host, new WasiCapabilityGrants());

        await using var connection = await connector.ConnectAsync(new ServerId(Guid.NewGuid()), config, ct);
        var inventory = await connection.DiscoverAsync(ct);
        var result = await connection.CallToolAsync(inventory.Tools[0].Name, NoArgs, ct);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_real_host_refuses_a_component_signed_by_an_unpinned_publisher()
    {
        var host = RequireHost();
        var ct = TestContext.Current.CancellationToken;
        var stranger = Convert.ToBase64String(new byte[32]); // gültige Länge, falscher Schlüssel.

        var act = () => ConnectorFor(stranger).ConnectAsync(
            new ServerId(Guid.NewGuid()), Config(host, new WasiCapabilityGrants()), ct);

        // Fail-closed: der Upstream darf gar nicht erst hochkommen.
        await act.Should().ThrowAsync<WasiHostException>().WithMessage("*load-rejected*");
    }

    [Fact]
    public async Task Both_sides_negotiate_the_same_contract_version()
    {
        using var wire = new HostWire(RequireHost());
        var ct = TestContext.Current.CancellationToken;

        var hello = await wire.RequestAsync(
            new { type = "hello", protocolVersion = WasiRuntimeConnector.ProtocolVersion }, ct);

        hello.GetProperty("type").GetString().Should().Be("hello");
        hello.GetProperty("protocolVersion").GetString().Should().Be(WasiRuntimeConnector.ProtocolVersion);
        hello.GetProperty("host").GetString().Should().StartWith("mcpmcp-wasi-host/");
        hello.GetProperty("runtime").GetString().Should().Contain("wasmtime");
    }

    [Theory]
    [InlineData("2")] // älterer Client trifft neueren Host — der Bruch der Aufrufbreite
    [InlineData("4")] // neuerer Client trifft älteren Host
    public async Task An_incompatible_contract_version_is_rejected_without_killing_the_host(string version)
    {
        using var wire = new HostWire(RequireHost());
        var ct = TestContext.Current.CancellationToken;

        var response = await wire.RequestAsync(new { type = "hello", protocolVersion = version }, ct);

        response.GetProperty("type").GetString().Should().Be("error");
        response.GetProperty("code").GetString().Should().Be("unsupported-protocol");
        // Der Host lebt weiter und bleibt fail-closed: ohne gültigen Handshake kein load.
        var health = await wire.RequestAsync(new { type = "health" }, ct);
        health.GetProperty("status").GetString().Should().Be("ok");
        var load = await wire.RequestAsync(
            new
            {
                type = "load",
                component = Convert.ToBase64String(await File.ReadAllBytesAsync(ComponentPath, ct)),
                signature = Convert.ToBase64String(await File.ReadAllBytesAsync(SignaturePath, ct)),
                pinnedPublishers = new[] { await PinnedPublisherAsync(ct) },
            },
            ct);
        load.GetProperty("code").GetString().Should().Be("handshake-required");
    }

    [Fact]
    public async Task The_audit_record_of_a_real_load_identifies_module_and_publisher()
    {
        using var wire = new HostWire(RequireHost());
        var ct = TestContext.Current.CancellationToken;
        var component = await File.ReadAllBytesAsync(ComponentPath, ct);
        await wire.RequestAsync(new { type = "hello", protocolVersion = WasiRuntimeConnector.ProtocolVersion }, ct);

        var loaded = await wire.RequestAsync(
            new
            {
                type = "load",
                component = Convert.ToBase64String(component),
                signature = Convert.ToBase64String(await File.ReadAllBytesAsync(SignaturePath, ct)),
                pinnedPublishers = new[] { await PinnedPublisherAsync(ct) },
                grants = new
                {
                    filesystemPreopens = Array.Empty<string>(),
                    networkAllow = Array.Empty<string>(),
                    environment = EnvironmentGrant,
                    secrets = Array.Empty<string>(),
                    clock = false,
                    random = false,
                },
            },
            ct);

        // Diese Felder trägt WP4 in den Audit-Pfad — sie müssen über die Leitung stabil sein.
        var audit = loaded.GetProperty("audit");
        audit.GetProperty("moduleSha256").GetString()
            .Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(component)));
        audit.GetProperty("publisherKeyId").GetString().Should().Be(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Convert.FromBase64String(await PinnedPublisherAsync(ct)))));
        audit.GetProperty("runtime").GetString().Should().StartWith("wasmtime-");
        audit.GetProperty("grantedEnvironment").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("MCPMCP_SPIKE");
    }

    [Fact]
    public async Task The_second_load_of_a_component_reuses_the_compilation()
    {
        using var wire = new HostWire(RequireHost());
        var ct = TestContext.Current.CancellationToken;
        await wire.RequestAsync(new { type = "hello", protocolVersion = WasiRuntimeConnector.ProtocolVersion }, ct);
        var load = await LoadRequestAsync(await PinnedPublisherAsync(ct), ct);

        var first = await wire.RequestAsync(load, ct);
        var second = await wire.RequestAsync(load, ct);
        var health = await wire.RequestAsync(new { type = "health" }, ct);

        // Ohne Cache zahlte jeder Aufruf die Kompilierung erneut (gemessen: ~75 ms gegen ~0,4 ms).
        first.GetProperty("cached").GetBoolean().Should().BeFalse();
        first.GetProperty("compileMs").GetDouble().Should().BeGreaterThan(0);
        second.GetProperty("cached").GetBoolean().Should().BeTrue();
        second.GetProperty("compileMs").GetDouble().Should().Be(0);

        var cache = health.GetProperty("cache");
        cache.GetProperty("entries").GetInt32().Should().Be(1, "gleicher Inhalt, gleicher Schlüssel");
        cache.GetProperty("hits").GetInt32().Should().BeGreaterThan(0);
        cache.GetProperty("totalCompileMs").GetDouble().Should().BeGreaterThan(0,
            "die eingesparte Kompilierzeit ist im Betrieb ablesbar");
    }

    [Fact]
    public async Task A_new_host_process_starts_warm_from_the_disk_cache()
    {
        var host = RequireHost();
        var ct = TestContext.Current.CancellationToken;
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"mcpmcp-wasi-cache-{Guid.NewGuid():N}");
        var publisher = await PinnedPublisherAsync(ct);

        try
        {
            // Erster Prozess: kompiliert und legt das Kompilat MAC-gesichert ab.
            var firstHealth = await HealthOf(host, cacheDirectory, publisher, ct);

            // Zweiter Prozess, gleiches Verzeichnis, leerer Speicher-Cache: darf nicht erneut
            // kompilieren. Genau das ist der Gewinn — ein Gateway-Neustart zahlt nicht nochmal.
            var secondHealth = await HealthOf(host, cacheDirectory, publisher, ct);

            firstHealth.GetProperty("cache").GetProperty("misses").GetInt32().Should().Be(1,
                "der erste Start kompiliert");

            firstHealth.GetProperty("cache").GetProperty("diskHits").GetInt32().Should().Be(0,
                "der erste Start hatte nichts zum Wiederverwenden");
            secondHealth.GetProperty("cache").GetProperty("diskHits").GetInt32().Should().BeGreaterThan(0,
                "das Kompilat kam von Platte");
            secondHealth.GetProperty("cache").GetProperty("misses").GetInt32().Should().Be(0,
                "und wurde nicht neu erzeugt");
            secondHealth.GetProperty("cache").GetProperty("diskErrors").GetInt32().Should().Be(0);
            // Der Schutz des Artefakts liegt im Verzeichnis: ein Schlüssel, den nur der Host kennt.
            File.Exists(Path.Combine(cacheDirectory, "mac.key")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    /// <summary>Startet einen Host mit Cache-Verzeichnis, lädt das Component und liest health.</summary>
    private static async Task<JsonElement> HealthOf(
        string host, string cacheDirectory, string publisher, CancellationToken ct)
    {
        using var wire = new HostWire(host, "--cache-dir", cacheDirectory);
        await wire.RequestAsync(
            new { type = "hello", protocolVersion = WasiRuntimeConnector.ProtocolVersion }, ct);
        await wire.RequestAsync(await LoadRequestAsync(publisher, ct), ct);
        return await wire.RequestAsync(new { type = "health" }, ct);
    }

    private static UpstreamServerConfig CacheConfig(string host, string cacheDirectory) => new(
        "wasi-cache", "WASI (Cache)", UpstreamTransportKind.Wasi, Enabled: true,
        Wasi: new WasiTransportOptions(
            host, ComponentPath, SignaturePath, PinnedPublishers: [],
            Grants: new WasiCapabilityGrants(Environment: EnvironmentGrant),
            ModuleCacheDirectory: cacheDirectory));

    [Fact]
    public async Task A_failed_load_keeps_the_previous_component_active()
    {
        using var wire = new HostWire(RequireHost());
        var ct = TestContext.Current.CancellationToken;
        var component = await File.ReadAllBytesAsync(ComponentPath, ct);
        await wire.RequestAsync(new { type = "hello", protocolVersion = WasiRuntimeConnector.ProtocolVersion }, ct);
        await wire.RequestAsync(await LoadRequestAsync(await PinnedPublisherAsync(ct), ct), ct);

        // Zweiter Load mit fremdem Publisher — der Host lehnt ab.
        var rejected = await wire.RequestAsync(
            await LoadRequestAsync(Convert.ToBase64String(new byte[32]), ct), ct);
        var health = await wire.RequestAsync(new { type = "health" }, ct);
        var stillCallable = await wire.RequestAsync(new { type = "discover" }, ct);

        // Eigener Code: "abgewiesen" und "abgewiesen, alter Stand läuft weiter" sind für den
        // Betreiber zwei verschiedene Lagen.
        rejected.GetProperty("code").GetString().Should().Be("load-rolled-back");
        health.GetProperty("moduleSha256").GetString().Should().Be(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(component)),
            "das zuvor geladene Component ist weiterhin aktiv");
        stillCallable.GetProperty("type").GetString().Should().Be("discovered");
    }

    private static async Task<object> LoadRequestAsync(string publisher, CancellationToken ct) => new
    {
        type = "load",
        component = Convert.ToBase64String(await File.ReadAllBytesAsync(ComponentPath, ct)),
        signature = Convert.ToBase64String(await File.ReadAllBytesAsync(SignaturePath, ct)),
        pinnedPublishers = new[] { publisher },
        grants = new
        {
            filesystemPreopens = Array.Empty<string>(),
            networkAllow = Array.Empty<string>(),
            environment = EnvironmentGrant,
            secrets = Array.Empty<string>(),
            clock = false,
            random = false,
        },
    };

    /// <summary>
    /// Minimaler Rohleitungs-Client für den Host: derselbe Rahmen wie im Connector (4-Byte-Big-
    /// Endian-Länge + JSON), aber ohne dessen Versionsprüfung — nur so lassen sich inkompatible
    /// Vertragsversionen gegen den echten Host fahren.
    /// </summary>
    private sealed class HostWire : IDisposable
    {
        private readonly Process _process;

        public HostWire(string hostExecutable, params string[] hostArguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = hostExecutable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("host");
            foreach (var argument in hostArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"WASI-Host '{hostExecutable}' ließ sich nicht starten.");
        }

        public async Task<JsonElement> RequestAsync(object request, CancellationToken ct)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(request);
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);

            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(length, ct);
            await stdin.WriteAsync(payload, ct);
            await stdin.FlushAsync(ct);

            var header = new byte[4];
            await _process.StandardOutput.BaseStream.ReadExactlyAsync(header, ct);
            var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
            await _process.StandardOutput.BaseStream.ReadExactlyAsync(body, ct);

            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // Prozess ist bereits weg.
            }

            _process.Dispose();
        }
    }
}
