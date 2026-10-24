using System.Globalization;
using System.Net;
using AwesomeAssertions;
using Bifrost.Performance.Tests.Harness;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Bifrost.Performance.Tests;

/// <summary>
/// WP5.3, der letzte offene Punkt der netzrealistischen Messung: <b>der Reverse-Proxy-Sprung</b>.
/// <para>
/// Die bisherige Messung terminiert TLS im Gateway selbst. Im Produktivaufbau steht davor ein
/// nginx oder Traefik — eine zweite Verbindung, ein zweiter Prozess, ein zweites Mal Serialisieren.
/// Was das kostet, stand bisher unter „Was auch diese Messung NICHT belegt".
/// </para>
/// <para>
/// <b>Ein echter Proxy in einem Container, kein In-Process-Ersatz.</b> Ein vorgeschalteter Handler
/// im selben Prozess wäre kein Sprung: Es ginge kein Paket über eine zweite Verbindung, und genau
/// die ist die Frage.
/// </para>
/// <para>
/// <b>Gemessen wird der Unterschied, nicht der absolute Wert.</b> Derselbe Lastlauf geht einmal
/// direkt und einmal durch den Proxy; die Differenz ist der Anteil des Proxy. Ein einzelner
/// Proxy-Lauf für sich sagte nichts — die Fremdlatenz des Upstreams steckt in beiden Zahlen.
/// </para>
/// </summary>
public sealed class ReverseProxyHopTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIFROST_RUN_BENCHMARK") == "1";

    /// <summary>
    /// Auf Linux erreicht ein Container den Host nicht von allein. Testcontainers legt dafür einen
    /// Namen an; unter Docker Desktop existiert er ohnehin.
    /// </summary>
    private const string HostAlias = "host.docker.internal";

    private readonly ITestOutputHelper _output;

    public ReverseProxyHopTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task The_hop_through_a_real_reverse_proxy_is_measured_instead_of_assumed()
    {
        Assert.SkipUnless(Enabled, "Lastmessung nur mit BIFROST_RUN_BENCHMARK=1 (Referenz-Hardware).");
        await RequireRuntimeAsync();

        var ct = TestContext.Current.CancellationToken;

        // 'any' ist hier zulässig und sonst nicht: Diese Instanz steht in einem Testlauf, und die
        // Zeile im Diagnosebericht (BFR-NET-0003) sagt genau das — jedem Absender wird geglaubt.
        await using var gateway = await GatewayProcess.StartAsync(
            new GatewayProcessOptions(
                Transport: GatewayTransport.Http,
                Environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BIFROST_TRUSTED_PROXIES"] = "any",
                }),
            ct);

        await gateway.AddStdioUpstreamAsync("slow", "SlowServer", ct);

        var port = gateway.BaseAddress.Port;
        await using var proxy = await StartNginxAsync(port, ct);
        var proxyAddress = new Uri($"http://{proxy.Hostname}:{proxy.GetMappedPublicPort(80)}/");
        _output.WriteLine($"Gateway: {gateway.BaseAddress} · Proxy: {proxyAddress}");

        // Funktioniert der Weg überhaupt? Erst danach lohnt es, ihn zu messen.
        using (var probe = gateway.CreateHttpClient(proxyAddress))
        {
            var health = await probe.GetAsync("healthz", ct);
            health.StatusCode.Should().Be(HttpStatusCode.OK,
                "ohne einen funktionierenden Sprung misst der Rest nur Fehlermeldungen");
        }

        var options = new LoadOptions(
            ToolName: "slow__sleep_measured",
            UpstreamLatencyMs: 20,
            Concurrency: 16,
            WarmupSeconds: 3,
            DurationSeconds: 15);

        var direct = await LoadDriver.RunAsync(gateway, options, ct);
        var hopped = await LoadDriver.RunAsync(gateway, options with { Endpoint = proxyAddress }, ct);

        Report("direkt", direct);
        Report("über nginx", hopped);

        var delta = hopped.GatewayOverheadP95 - direct.GatewayOverheadP95;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Anteil des Proxy an der p95: {delta:F2} ms"));

        direct.Errors.Should().Be(0);
        hopped.Errors.Should().Be(0, "ein Sprung, der Fehler erzeugt, ist kein Messergebnis");

        // NFR-01 gilt für die Strecke, die ein Betreiber betreibt — und das ist die mit Proxy.
        hopped.P95.Should().BeLessThan(50 + options.UpstreamLatencyMs * 2.0,
            "NFR-01 (p95 ≤ 50 ms über dem Fremdanteil) muss auch hinter einem Proxy halten");

        // Die eigentliche Aussage: Der Sprung kostet etwas, aber nichts in der Größenordnung des
        // Gateways selbst. Eine feste Millisekundenschranke wäre auf geteilter Hardware eine
        // Wette; geprüft wird deshalb die Größenordnung.
        delta.Should().BeLessThan(direct.GatewayOverheadP95 + 25,
            "der Proxy darf den Gateway-Anteil nicht um ein Vielfaches überschreiten");
    }

    /// <summary>
    /// Ohne Container-Runtime gibt es keinen Proxy und damit keinen Sprung. Übersprungen wird das
    /// mit Begründung — es sei denn, jemand hat <c>BIFROST_REQUIRE_CONTAINER</c> gesetzt: Dort, wo
    /// Docker da sein muss, ist ein Skip ein stiller Ausfall des Nachweises.
    /// </summary>
    private static async Task RequireRuntimeAsync()
    {
        var required = Environment.GetEnvironmentVariable("BIFROST_REQUIRE_CONTAINER") is "1" or "true";
        try
        {
            var probe = new ContainerBuilder("nginx:1.27-alpine").WithCommand("true").Build();
            await probe.DisposeAsync();
        }
        catch (Exception exception)
        {
            Assert.SkipUnless(required,
                "Keine Container-Runtime erreichbar — Docker starten oder BIFROST_REQUIRE_CONTAINER setzen.");
            Assert.Fail(
                "BIFROST_REQUIRE_CONTAINER ist gesetzt, aber keine Container-Runtime erreichbar: "
                + exception.Message);
        }
    }

    private void Report(string label, LoadMeasurement measurement)
        => _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label,-12} Aufrufe {measurement.Calls,6} · gesamt p50 {measurement.P50,6:F1} ms · "
            + $"p95 {measurement.P95,6:F1} ms · Upstream p50 {measurement.UpstreamP50,6:F1} ms · "
            + $"Anteil Gateway+Netz p50 {measurement.GatewayOverheadP50,5:F2} ms · "
            + $"p95 {measurement.GatewayOverheadP95,5:F2} ms · Fehler {measurement.Errors}"));

    /// <summary>
    /// nginx mit genau der Konfiguration, die in <c>docs/operations.md</c> empfohlen wird:
    /// <c>Host</c> durchreichen, <c>X-Forwarded-Proto</c> und <c>X-Forwarded-For</c> setzen.
    /// <para>
    /// <b>Kein Puffern.</b> <c>proxy_buffering off</c> ist für einen MCP-Gateway keine
    /// Feinabstimmung: Server-Sent Events kämen sonst blockweise an, und der Sitzungsbetrieb sähe
    /// aus, als hinge er.
    /// </para>
    /// </summary>
    private static async Task<IContainer> StartNginxAsync(int gatewayPort, CancellationToken ct)
    {
        var configuration = $$"""
            events {}
            http {
              access_log off;
              upstream bifrost { server {{HostAlias}}:{{gatewayPort}}; keepalive 32; }
              server {
                listen 80;
                location / {
                  proxy_pass http://bifrost;
                  proxy_http_version 1.1;
                  proxy_set_header Host $http_host;
                  proxy_set_header Connection "";
                  proxy_set_header X-Forwarded-Proto https;
                  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                  proxy_buffering off;
                  proxy_read_timeout 300s;
                }
              }
            }
            """;

        // Die Datei muss im Container 'nginx.conf' heissen — das Ziel der Zuordnung ist ein
        // Verzeichnis, nicht ein Dateiname. Deshalb ein eigenes Verzeichnis mit dem richtigen Namen
        // darin, statt einen Zufallsnamen an eine Stelle zu legen, an der nginx nicht sucht.
        var directory = Path.Combine(Path.GetTempPath(), $"bifrost-nginx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "nginx.conf");
        await File.WriteAllTextAsync(file, configuration, ct);

        var container = new ContainerBuilder("nginx:1.27-alpine")
            .WithResourceMapping(new FileInfo(file), "/etc/nginx/")
            .WithExtraHost(HostAlias, "host-gateway")
            .WithPortBinding(80, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPath("/healthz").ForPort(80)))
            .Build();

        await container.StartAsync(ct);
        return container;
    }
}
