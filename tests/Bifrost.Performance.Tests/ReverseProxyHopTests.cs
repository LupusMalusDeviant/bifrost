using System.Globalization;
using System.Net;
using AwesomeAssertions;
using Bifrost.Performance.Tests.Harness;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
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
    /// Der Name, unter dem nginx den Gateway erreicht. Unter Docker Desktop zeigt er auf
    /// <c>host-gateway</c>, unter Linux auf das Gateway des eigenen Testnetzes
    /// (<see cref="ContainerBridgeAsync"/>).
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
        // Zuerst angelegt, zuletzt entfernt: nginx muss das Netz vor ihm verlassen haben.
        await using var bridge = await ContainerBridgeAsync(ct);

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
        await using var proxy = await StartNginxAsync(port, listening, bridge, ct);
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
    /// mit Begründung — es sei denn, ein Container ist Pflicht (<see cref="ContainerRequired"/>): Dort,
    /// wo Docker da sein muss, ist ein Skip ein stiller Ausfall des Nachweises.
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
                "Ein Container ist Pflicht (BIFROST_REQUIRE_CONTAINER oder Benchmark auf GitHub Actions unter Linux), "
                + "aber keine Container-Runtime erreichbar: "
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
    private async Task<IContainer> StartNginxAsync(int gatewayPort, string listening, ContainerBridge bridge, CancellationToken ct)
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

        var builder = new ContainerBuilder("nginx:1.27-alpine")
            .WithResourceMapping(new FileInfo(file), "/etc/nginx/")
            .WithExtraHost(HostAlias, bridge.GatewayHost);
        if (bridge.Network is not null)
        {
            builder = builder.WithNetwork(bridge.Network);
        }

        var container = builder
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
    /// sonst die Ausnahme des Aufräumens die einzige, die jemand zu sehen bekommt. Mit Schranke: Ein
    /// hängender Daemon soll den Bericht des Startfehlers nicht wieder unbegrenzt aufhalten.
    /// </summary>
    private async Task DisposeQuietlyAsync(IContainer container)
    {
        try
        {
            await container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }
#pragma warning disable CA1031 // Der Startfehler ist die Auskunft; das Aufräumen wird nur vermerkt.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Mit ID: Ohne Ryuk (TESTCONTAINERS_RYUK_DISABLED) bleibt der Container sonst unauffindbar
            // stehen (Review-Befund L-5, achte Runde).
            _output.WriteLine(
                $"nginx-Container {container.Id} liess sich nicht aufräumen ({exception.Message}); "
                + $"von Hand entfernen: docker rm -f {container.Id}");
        }
    }

    /// <summary>
    /// Wie der Testgateway für nginx erreichbar ist und wem er Forwarded-Header glaubt. Unter Linux
    /// mit einem eigenen Docker-Netz nur für diesen Test, das am Ende wieder entfernt wird.
    /// </summary>
    private sealed record ContainerBridge(
        IReadOnlyList<IPAddress> ListenAddresses,
        string TrustedProxies,
        INetwork? Network,
        string GatewayHost) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (Network is not null)
            {
                await Network.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>
    /// Unter Linux: ein <b>eigenes</b> Docker-Netz nur für nginx. Der Gateway lauscht zusätzlich zu
    /// <c>127.0.0.1</c> an dessen IPv4-Gateway, nginx erreicht ihn dort direkt (ohne
    /// <c>host-gateway</c>), und nur dessen Subnetz gilt als vertrauenswürdige Proxy-Quelle.
    /// <para>
    /// <b>Was das abgrenzt und was nicht.</b> Im eigenen Netz steht nur nginx — nicht Ryuk, keine
    /// Service-Container des Jobs, nichts sonst aus dem Standardnetz <c>bridge</c>. Nur nginx kann
    /// sich damit als Loopback ausgeben (Review-Befund L-4, achte Runde). Linux nimmt Pakete für
    /// jede eigene Adresse auf jeder Schnittstelle an; ein Nachbar mit einer Route auf das Subnetz
    /// käme an die Adresse heran, gilt aber nicht als Proxy. Den Zugang selbst schützt weiter der
    /// zur Laufzeit erzeugte API-Schlüssel.
    /// </para>
    /// <para>
    /// Docker Desktop (Windows, macOS) leitet <c>host-gateway</c> auf die Loopback-Adresse des
    /// Rechners weiter. Dort lauscht der Gateway nur auf <c>127.0.0.1</c>, ein eigenes Netz braucht
    /// es nicht; erreichen können ihn nur Prozesse dieses Rechners, und <c>any</c> glaubt damit nur
    /// ihnen.
    /// </para>
    /// </summary>
    private static async Task<ContainerBridge> ContainerBridgeAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new ContainerBridge([], "any", null, "host-gateway");
        }

        var name = $"bifrost-bench-{Guid.NewGuid():N}";
        var network = new NetworkBuilder().WithName(name).Build();
        try
        {
            await network.CreateAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Assert.SkipUnless(ContainerRequired, $"Docker-Netz liess sich nicht anlegen: {exception.Message}");
            throw;
        }

        try
        {
            var (gateway, subnet) = await InspectNetworkAsync(name, ct);
            return new ContainerBridge([gateway], subnet, network, gateway.ToString());
        }
        catch
        {
            await network.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// IPv4-Gateway und Subnetz eines Docker-Netzes, gelesen über <c>docker network inspect</c> —
    /// mit Schranke, und so geprüft, wie der Gateway das Subnetz später liest.
    /// </summary>
    private static async Task<(IPAddress Gateway, string Subnet)> InspectNetworkAsync(string networkName, CancellationToken ct)
    {
        var start = new System.Diagnostics.ProcessStartInfo(
            "docker",
            ["network", "inspect", networkName, "--format", "{{range .IPAM.Config}}{{.Subnet}}|{{.Gateway}}\n{{end}}"])
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
                throw new InvalidOperationException(
                    $"'docker network inspect {networkName}' antwortete nicht binnen {timeout.TotalSeconds:F0} s — "
                    + "der Docker-Daemon hängt oder ist nicht erreichbar.");
            }
            finally
            {
                // Schranke oder Testabbruch: den Befehl beenden und die Lesevorgänge abwarten — sonst
                // läuft docker weiter und die Lesevorgänge melden sich später als unbeobachtete
                // Ausnahme. Auf beiden Abbruchwegen gleich.
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await ((Task)Task.WhenAll(outputTask, errorTask)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }

            var output = await outputTask;
            var error = await errorTask;

            // IPv4, denn daran lauscht der Gateway für nginx; mit eingeschaltetem IPv6 kann der erste
            // Eintrag das v6-Subnetz sein. Das Subnetz wird so gelesen, wie der Gateway es liest
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
                            $"Docker meldet für '{networkName}' das Subnetz '{parts[0]}' mit Gateway {gateway}; das ist kein "
                            + "kanonisches Subnetz, das dieses Gateway enthält.");
                    }

                    // Spricht docker mit einem anderen Daemon (DOCKER_HOST, docker context, rootless),
                    // liegt dieses Gateway nicht auf diesem Rechner; der Gateway könnte dort nicht
                    // lauschen und bräche mit "Cannot assign requested address" ab.
                    if (!IsLocalAddress(gateway))
                    {
                        var reason =
                            $"Das Gateway {gateway} des Docker-Netzes '{networkName}' ist keine Adresse dieses Rechners — "
                            + "docker spricht vermutlich mit einem entfernten oder rootless Daemon (DOCKER_HOST, docker context).";
                        Assert.SkipUnless(ContainerRequired, reason);
                        throw new InvalidOperationException(reason);
                    }

                    return (gateway, subnet.ToString());
                }
            }

            // Kein solches Netz oder keines mit IPv4 (etwa Podman mit docker-Befehl): wie eine
            // fehlende Runtime — übersprungen, es sei denn, ein Container ist Pflicht.
            var missing =
                $"Das IPv4-Gateway des Docker-Netzes '{networkName}' liess sich nicht ermitteln (Exit {process.ExitCode}): "
                + $"{output} {error}";
            Assert.SkipUnless(ContainerRequired, missing);
            throw new InvalidOperationException(missing);
        }
    }

    /// <summary>
    /// Ob ein fehlender oder unpassender Container den Test scheitern lässt statt ihn zu überspringen.
    /// Ausdrücklich über <c>BIFROST_REQUIRE_CONTAINER</c> — und immer im Benchmark-Lauf auf einem
    /// Linux-Runner von GitHub Actions: Dort ist Docker vorhanden, und ein Überspringen färbte den
    /// Lauf grün, ohne den Proxy-Sprung gemessen zu haben. Ein Schalter nur im Testgerüst, nicht im
    /// Produkt.
    /// </summary>
    private static bool ContainerRequired
        => Environment.GetEnvironmentVariable("BIFROST_REQUIRE_CONTAINER") is "1" or "true"
            || (Enabled && OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true");

    private static bool IsLocalAddress(IPAddress address)
        => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));
}
