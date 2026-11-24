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

    /// <summary>Wie lange nginx bereit werden darf; ein Bild ziehen und starten dauert Sekunden.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Der ganze Start samt Ziehen des Bildes; ein hängender Pull endet sonst erst mit dem Lauf.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);

    private readonly ITestOutputHelper _output;

    public ReverseProxyHopTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task The_hop_through_a_real_reverse_proxy_is_measured_instead_of_assumed()
    {
        Assert.SkipUnless(Enabled, "Lastmessung nur mit BIFROST_RUN_BENCHMARK=1 (Referenz-Hardware).");
        await RequireRuntimeAsync();

        var ct = TestContext.Current.CancellationToken;
        var bridge = await ContainerBridgeAsync(ct);

        await using var gateway = await GatewayProcess.StartAsync(
            new GatewayProcessOptions(
                Transport: GatewayTransport.Http,
                Environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BIFROST_TRUSTED_PROXIES"] = bridge.TrustedProxies,
                },
                // Der Container muss den Gateway erreichen; auf Loopback allein geht das unter Linux
                // nicht (seit Oktober jeden Montag eine Stunde Hänger im geplanten ci-Lauf).
                AdditionalListenAddresses: bridge.ListenAddresses),
            ct);

        await gateway.AddStdioUpstreamAsync("slow", "SlowServer", ct);

        var port = gateway.BaseAddress.Port;
        var listening = string.Join(", ", bridge.ListenAddresses.Select(a => a.ToString()).Prepend("127.0.0.1"));
        await using var proxy = await StartNginxAsync(port, listening, ct);
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
        var required = ContainerRequired;
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
    private async Task<IContainer> StartNginxAsync(int gatewayPort, string listening, CancellationToken ct)
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
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPath("/healthz").ForPort(80),
                // Ohne Schranke wartet Testcontainers, bis der Testlauf nach einer Stunde abbricht —
                // und der Grund (nginx erreicht den Gateway nicht) steht nirgends.
                wait => wait.WithTimeout(ReadinessTimeout)))
            .Build();

        // Das Ziehen des Bildes deckt die Wartezeit oben nicht ab; diese Schranke deckt den ganzen Start.
        using var startLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startLimit.CancelAfter(StartTimeout);
        try
        {
            await container.StartAsync(startLimit.Token);
            return container;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            // Jeder Startfehler, nicht nur die Zeitüberschreitung: nginx kann auch sofort enden
            // ("host not found in upstream"). Das Log ist dann die einzige Auskunft.
            var log = await ReadLogAsync(container);
            await DisposeQuietlyAsync(container);
            throw new InvalidOperationException(
                $"nginx kam nicht in Betrieb ({exception.GetType().Name}: {exception.Message}). Erwartet: "
                + $"/healthz bis zum Gateway auf {HostAlias}:{gatewayPort} (der Gateway lauscht an {listening}) "
                + $"binnen {ReadinessTimeout.TotalSeconds:F0} s, "
                + $"Start samt Ziehen des Bildes binnen {StartTimeout.TotalMinutes:F0} min. nginx-Log:\n{log}",
                exception);
        }
        catch
        {
            // Abbruch durch den Testlauf: aufräumen, nicht umdeuten.
            await DisposeQuietlyAsync(container);
            throw;
        }
        finally
        {
            // Die Datei ist beim Start in den Container kopiert; das Verzeichnis braucht niemand mehr.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<string> ReadLogAsync(IContainer container)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (stdout, stderr) = await container.GetLogsAsync(ct: limit.Token);
            return $"{stdout}\n{stderr}";
        }
#pragma warning disable CA1031 // Ein unlesbares Log darf den eigentlichen Startfehler nicht verdecken.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return $"(Log nicht lesbar: {exception.Message})";
        }
    }

    /// <summary>
    /// Aufräumen, ohne den eigentlichen Fehler zu verdecken: Ist Docker im selben Moment weg, wäre
    /// sonst die Ausnahme des Aufräumens die einzige, die jemand zu sehen bekommt.
    /// </summary>
    private async Task DisposeQuietlyAsync(IContainer container)
    {
        try
        {
            await container.DisposeAsync();
        }
#pragma warning disable CA1031 // Der Startfehler ist die Auskunft; das Aufräumen wird nur vermerkt.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _output.WriteLine($"nginx-Container liess sich nicht aufräumen: {exception.Message}");
        }
    }

    /// <summary>Wie der Testgateway für einen Container erreichbar ist und wem er Forwarded-Header glaubt.</summary>
    private sealed record ContainerBridge(IReadOnlyList<IPAddress> ListenAddresses, string TrustedProxies);

    /// <summary>
    /// Unter Linux: das IPv4-Gateway des Docker-Netzes <c>bridge</c> — dorthin zeigt
    /// <c>host-gateway</c> —, an dem der Test zusätzlich zu <c>127.0.0.1</c> lauscht, und dessen
    /// Subnetz als einzige vertrauenswürdige Proxy-Quelle.
    /// <para>
    /// <b>Was das abgrenzt und was nicht.</b> Erreichbar ist der Testgateway damit vom Rechner selbst
    /// und aus Containern im Netz <c>bridge</c>. Linux nimmt Pakete für jede eigene Adresse auf jeder
    /// Schnittstelle an; ein Nachbar mit einer Route auf das Docker-Subnetz käme also ebenfalls an.
    /// Deshalb glaubt der Gateway Forwarded-Header nur Absendern aus dem Docker-Subnetz statt
    /// <c>any</c> — ein solcher Nachbar kann sich nicht als Loopback ausgeben. Vertraut wird damit
    /// allerdings <b>jedem</b> Container im Netz <c>bridge</c>, nicht nur nginx; auf einem geteilten
    /// Runner könnte ein anderer Container sich als Loopback ausgeben. Das betrifft nur die Wege,
    /// die an Loopback hängen (die Setup-Vorschau, die zusätzlich ein Token verlangt). Den Zugang
    /// selbst schützt weiter der zur Laufzeit erzeugte API-Schlüssel.
    /// </para>
    /// <para>
    /// <b>Annahme:</b> <c>host-gateway</c> ist das Gateway von <c>bridge</c>. Ein Daemon mit
    /// eigenem <c>host-gateway-ip</c> bricht sie; dann wird nginx nicht bereit, und die Meldung nennt
    /// die Adresse, an der der Gateway lauscht.
    /// </para>
    /// <para>
    /// Docker Desktop (Windows, macOS) leitet <c>host-gateway</c> auf die Loopback-Adresse des
    /// Rechners weiter. Dort lauscht der Gateway nur auf <c>127.0.0.1</c>; erreichen können ihn nur
    /// Prozesse dieses Rechners, und <c>any</c> glaubt damit nur ihnen.
    /// </para>
    /// </summary>
    private static async Task<ContainerBridge> ContainerBridgeAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new ContainerBridge([], "any");
        }

        var start = new System.Diagnostics.ProcessStartInfo(
            "docker",
            ["network", "inspect", "bridge", "--format", "{{range .IPAM.Config}}{{.Subnet}}|{{.Gateway}}\n{{end}}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("docker liess sich nicht starten.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // Kein docker-Programm (etwa Podman ohne Kompatibilitätsbefehl): wie eine fehlende
            // Runtime behandeln — übersprungen, es sei denn, ein Container ist Pflicht.
            Assert.SkipUnless(ContainerRequired, $"docker-Befehl nicht verfügbar: {exception.Message}");
            throw;
        }

        using (process)
        {
            var timeout = TimeSpan.FromSeconds(30);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);

            // Beide Ströme zugleich lesen: Ein voller stderr-Puffer hielte sonst den Prozess auf.
            var outputTask = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errorTask = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Ein hängender Docker-Daemon: den Befehl beenden, die Lesevorgänge abwarten (sonst
                // melden sie sich später als unbeobachtete Ausnahme) und sagen, was hing.
                process.Kill(entireProcessTree: true);
                await ((Task)Task.WhenAll(outputTask, errorTask)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw new InvalidOperationException(
                    $"'docker network inspect bridge' antwortete nicht binnen {timeout.TotalSeconds:F0} s — "
                    + "der Docker-Daemon hängt oder ist nicht erreichbar.");
            }

            var output = await outputTask;
            var error = await errorTask;

            // IPv4, denn darauf zeigt host-gateway; mit eingeschaltetem IPv6 kann der erste Eintrag
            // das v6-Subnetz sein. Das Subnetz wird so gelesen, wie der Gateway es liest
            // (IPNetwork.TryParse), und muss das Gateway enthalten — sonst bräche der Gateway erst beim
            // Start mit einer Meldung ab, die das Subnetz nicht nennt.
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|');
                if (parts.Length == 2
                    && IPAddress.TryParse(parts[1], out var gateway)
                    && gateway.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    if (!IPNetwork.TryParse(parts[0], out var subnet) || !subnet.Contains(gateway))
                    {
                        throw new InvalidOperationException(
                            $"Docker meldet für 'bridge' das Subnetz '{parts[0]}' mit Gateway {gateway}; das ist kein "
                            + "kanonisches Subnetz, das dieses Gateway enthält.");
                    }

                    return new ContainerBridge([gateway], subnet.ToString());
                }
            }

            throw new InvalidOperationException(
                $"Das IPv4-Gateway des Docker-Netzes 'bridge' liess sich nicht ermitteln (Exit {process.ExitCode}): "
                + $"{output} {error}");
        }
    }

    private static bool ContainerRequired
        => Environment.GetEnvironmentVariable("BIFROST_REQUIRE_CONTAINER") is "1" or "true";
}
