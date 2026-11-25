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

    /// <summary>
    /// Vierzehnte Runde, Befund L: nginx wird an die Adresse gebunden, die Testcontainers anspricht —
    /// sonst wartete die Bereitschaft vergeblich, obwohl nginx läuft. Ein entfernter Daemon oder ein
    /// Override auf eine Nicht-Loopback-Adresse bindet nicht um. Braucht kein Docker.
    /// </summary>
    [Theory]
    [InlineData(null, null, "127.0.0.1")]
    [InlineData("npipe://./pipe/docker_engine", null, "127.0.0.1")]
    [InlineData("unix:///var/run/docker.sock", null, "127.0.0.1")]
    [InlineData("tcp://localhost:2375", null, "127.0.0.1")]
    [InlineData("tcp://127.0.0.1:2375", null, "127.0.0.1")]
    [InlineData("tcp://127.0.0.2:2375", null, "127.0.0.2")]
    [InlineData("tcp://[::1]:2375", null, "::1")]
    [InlineData("tcp://192.168.178.61:2375", null, null)]
    [InlineData("tcp://docker.example.invalid:2376", null, null)]
    [InlineData(null, "127.0.0.3", "127.0.0.3")]
    [InlineData(null, "localhost", "127.0.0.1")]
    [InlineData(null, "::1", "::1")]
    [InlineData(null, "192.168.178.61", null)]
    [InlineData("tcp://127.0.0.2:2375", "127.0.0.4", "127.0.0.4")]
    [InlineData("tcp://192.168.178.61:2375", "127.0.0.1", null)]
    public void Nginx_is_published_on_the_address_testcontainers_probes(string? dockerHost, string? hostOverride, string? expected)
        => LoopbackPublishAddress(dockerHost, hostOverride).Should().Be(expected);

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

        builder = builder.WithPortBinding(80, assignRandomHostPort: true);

        // Nur auf Loopback veröffentlichen, nicht auf allen Schnittstellen des Rechners: Unter
        // Docker Desktop glaubt der Gateway Forwarded-Header von überall ("any"), und nginx wäre
        // sonst für die Dauer des Tests aus dem LAN erreichbar (Review-Befund L-1, elfte Runde). Der
        // Lastlauf spricht nginx ohnehin über Loopback an.
        // Nur bei einem Daemon auf diesem Rechner (zwölfte Runde, L-4): Mit einem entfernten
        // DOCKER_HOST ist proxy.Hostname der entfernte Rechner, und dort wäre nginx an Loopback
        // gebunden unerreichbar. Ein entfernter Host aus ~/.testcontainers.properties wird hier
        // nicht erkannt; dann scheitert die Bereitschaft mit der Meldung, die den Proxy nennt.
        // Gebunden wird an genau die Loopback-Adresse, die Testcontainers für Bereitschaft und
        // Lastlauf anspricht (vierzehnte Runde, Befund L): bei DOCKER_HOST=tcp://127.0.0.2 also
        // 127.0.0.2, nicht 127.0.0.1.
        if (LoopbackPublishAddress(
                Environment.GetEnvironmentVariable("DOCKER_HOST"),
                Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE")) is { } publishAddress)
        {
            builder = builder.WithCreateParameterModifier(parameters =>
            {
                var rewritten = 0;
                foreach (var binding in (parameters.HostConfig?.PortBindings?.Values ?? []).SelectMany(list => list))
                {
                    binding.HostIP = publishAddress;
                    rewritten++;
                }

                // Setzte eine künftige Testcontainers-Fassung die Bindungen erst nach diesem Schritt,
                // liefe das still ins Leere und nginx lauschte wieder überall.
                if (rewritten == 0)
                {
                    throw new InvalidOperationException(
                        $"Keine Portbindung zum Umschreiben auf {publishAddress} gefunden — Testcontainers setzt die "
                        + "Bindungen offenbar erst nach dem Create-Parameter-Modifier.");
                }
            });
        }

        var container = builder
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
        string? NetworkName,
        string GatewayHost,
        Action<string> Log) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (Network is not null && !await DisposeNetworkQuietlyAsync(Network, NetworkName!, Log))
            {
                await RemoveNetworkByNameAsync(NetworkName!, Log);
            }
        }
    }

    /// <summary>
    /// Das Testnetz entfernen, ohne je zu werfen und ohne unbegrenzt zu warten (Review-Befunde M-1
    /// und L-1, neunte Runde). Es wird zuletzt entsorgt; eine Ausnahme hier ersetzte bei
    /// <c>await using</c> die des Testrumpfs — den Startfehler von nginx samt Log, eine
    /// fehlgeschlagene Prüfung. Hängt nginx noch im Netz („active endpoints") oder hängt der Daemon,
    /// steht der Befehl zum Entfernen im Protokoll; Ryuk räumt das Netz sonst über das
    /// Sitzungs-Label ab.
    /// <para>
    /// Der Name wird übergeben, nicht vom Netzobjekt gelesen: Gibt es das Netz für Testcontainers
    /// nicht, wirft schon das Lesen von <c>Name</c> — und verdeckte im catch wieder den
    /// eigentlichen Fehler (Review-Befund L-1, zehnte Runde).
    /// </para>
    /// </summary>
    /// <returns>Ob Testcontainers das Netz entfernt hat; sonst versucht der Aufrufer es per Name.</returns>
    private static async Task<bool> DisposeNetworkQuietlyAsync(INetwork network, string name, Action<string> log)
    {
        try
        {
            await network.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            return true;
        }
#pragma warning disable CA1031 // Das Aufräumen darf den eigentlichen Befund nicht verdecken.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            log($"Docker-Netz {name} liess sich über Testcontainers nicht entfernen ({exception.GetType().Name}: "
                + $"{exception.Message}); neuer Versuch über 'docker network rm'.");
            return false;
        }
    }

    /// <summary>
    /// Das Netz über den Namen entfernen, am Docker-Befehl vorbei an Testcontainers. Für den Abbruch
    /// mitten im Anlegen: Dann kann das Netz beim Daemon schon stehen, ohne dass Testcontainers es
    /// kennt — sein <c>DisposeAsync</c> täte nichts, und ohne Ryuk bliebe das Netz stehen
    /// (Review-Befund L-2, zehnte Runde). Leise und mit Schranke.
    /// <para>
    /// Der Ausgang zählt (Review-Befund L-2, elfte Runde): Ein echter Fehler („active endpoints")
    /// steht mit seiner Meldung im Protokoll. „Gibt es nicht" kann heissen, dass der abgebrochene
    /// Anlege-Auftrag beim Daemon noch läuft; dann wird nach einer kurzen Pause ein zweites Mal
    /// entfernt. Ein Netz, das danach erst entsteht, kann nur Ryuk abräumen — das Protokoll nennt
    /// den Namen.
    /// </para>
    /// <para>
    /// Aufgerufen nur, wo Testcontainers das Netz nicht entfernt hat oder es nie kannte (zwölfte
    /// Runde, L-5) — nicht nach jedem sauberen Aufräumen, das sonst 2 s Pause und eine irreführende
    /// Zeile kostete. „Nicht gefunden" heisst dann meist: Das Anlegen ist wirklich gescheitert; die
    /// Zeile dazu ist entsprechend neutral. Erkannt wird es am englischen Text der Docker-CLI, die
    /// ihre Meldungen nicht übersetzt.
    /// </para>
    /// </summary>
    private static async Task RemoveNetworkByNameAsync(string name, Action<string> log)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var (exitCode, error) = await RunDockerAsync(["network", "rm", name], log);
            if (exitCode == 0)
            {
                return;
            }

            var notFound = error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("No such network", StringComparison.OrdinalIgnoreCase);
            if (!notFound)
            {
                log($"'docker network rm {name}' scheiterte (Exit {exitCode}): {error.Trim()} — von Hand entfernen.");
                return;
            }

            if (attempt == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        log($"Docker-Netz {name}: beim Daemon nicht vorhanden, nichts zu entfernen. (Entstünde es nach einem "
            + "Abbruch noch nachträglich, räumte es Ryuk ab.)");
    }

    /// <summary>Ein docker-Befehl mit Schranke (30 s); Ausgang und stderr, wirft nie.</summary>
    private static async Task<(int ExitCode, string Error)> RunDockerAsync(string[] arguments, Action<string> log)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return (-1, "docker liess sich nicht starten");
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                // Endet der Prozess genau jetzt, wirft Kill; das ist kein Fehler des Befehls.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                await ((Task)Task.WhenAll(output, error)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                return (-1, $"'docker {string.Join(' ', arguments)}' antwortete nicht binnen 30 s");
            }

            await ((Task)Task.WhenAll(output, error)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return (process.ExitCode, error.IsCompletedSuccessfully ? error.Result : string.Empty);
        }
#pragma warning disable CA1031 // Aufräumen; der eigentliche Befund steht anderswo.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            log($"'docker {string.Join(' ', arguments)}' liess sich nicht ausführen: {exception.Message}");
            return (-1, exception.Message);
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
    /// zur Laufzeit erzeugte API-Schlüssel. Dasselbe gilt für Container anderer Netze auf dem
    /// Runner: Dockers Trennung der Netze wirkt nur auf weitergeleiteten Verkehr, nicht auf Pakete an
    /// den Rechner selbst — sie erreichen die Adresse, aber mit einer Quelle ausserhalb des
    /// vertrauten Subnetzes.
    /// </para>
    /// <para>
    /// <b>Bewusst kein <c>internal</c>-Netz</b> (Review-Befund L-4, zehnte Runde, optional): Ein
    /// internes Netz hätte keinen Weg nach aussen, nginx braucht aber genau den Weg zum Gateway auf
    /// dem Rechner. Ob ein internes Netz die Gateway-Adresse auf dem Rechner überhaupt erhält, hängt
    /// an der Docker-Fassung; der Test liefe dann wieder in die Bereitschafts-Schranke. Der Gewinn —
    /// fremde Container erreichten die Adresse nicht mehr — ändert am Vertrauen nichts.
    /// </para>
    /// <para>
    /// Docker Desktop (Windows, macOS) leitet <c>host-gateway</c> auf die Loopback-Adresse des
    /// Rechners weiter. Dort lauscht der Gateway nur auf <c>127.0.0.1</c>, ein eigenes Netz braucht
    /// es nicht; erreichen können ihn nur Prozesse dieses Rechners, und <c>any</c> glaubt damit nur
    /// ihnen.
    /// </para>
    /// </summary>
    private async Task<ContainerBridge> ContainerBridgeAsync(CancellationToken ct)
    {
        void Log(string line) => _output.WriteLine(line);

        if (!OperatingSystem.IsLinux())
        {
            return new ContainerBridge([], "any", null, null, "host-gateway", Log);
        }

        var name = $"bifrost-bench-{Guid.NewGuid():N}";
        var network = new NetworkBuilder().WithName(name).Build();
        try
        {
            await network.CreateAsync(ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            // Nach dem Token, nicht nach der Ausnahmeart: Docker.DotNet meldet eine Zeitüberschreitung
            // des Clients als TaskCanceledException, ohne dass der Test abgebrochen wurde
            // (Review-Befund L-3, zehnte Runde). Auch das ist „Docker nicht nutzbar".
            // Testcontainers kennt das Netz hier nicht (kein Anlegen abgeschlossen); entfernt wird
            // es deshalb nur über den Namen, falls der Daemon es doch angelegt hat.
            await RemoveNetworkByNameAsync(name, Log);
            Assert.SkipUnless(ContainerRequired, $"Docker-Netz liess sich nicht anlegen: {exception.Message}");
            throw;
        }
        catch
        {
            // Abbruch des Tests mitten im Anlegen: Das Netz kann beim Daemon schon stehen.
            await RemoveNetworkByNameAsync(name, Log);
            throw;
        }

        try
        {
            var (gateway, subnet) = await InspectNetworkAsync(name, ct);
            return new ContainerBridge([gateway], subnet, network, name, gateway.ToString(), Log);
        }
        catch
        {
            if (!await DisposeNetworkQuietlyAsync(network, name, Log))
            {
                await RemoveNetworkByNameAsync(name, Log);
            }

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

    /// <summary>
    /// An welche Loopback-Adresse nginx gebunden wird — oder <c>null</c>, wenn nicht umgebunden wird
    /// und nginx bei der Vorgabe von Testcontainers bleibt.
    /// <para>
    /// Umgebunden wird nur bei einem Docker-Daemon auf diesem Rechner (Socket, Named Pipe oder TCP auf
    /// Loopback; Review-Befund L-4, dreizehnte Runde) und an <b>dieselbe</b> Adresse, unter der
    /// Testcontainers den Container anspricht (vierzehnte Runde, Befund L): zuerst
    /// <c>TESTCONTAINERS_HOST_OVERRIDE</c>, sonst der Host aus <c>DOCKER_HOST</c>, bei Socket und
    /// Pipe <c>127.0.0.1</c>. <c>localhost</c> wird zu <c>127.0.0.1</c>, eine IPv6-Loopback-Adresse
    /// bleibt <c>::1</c>. Zeigt eins davon nicht auf Loopback, wird nicht umgebunden.
    /// </para>
    /// <para>
    /// Ein entfernter Host aus <c>~/.testcontainers.properties</c> oder einem <c>docker context</c>
    /// wird nicht erkannt; dann scheitert die Bereitschaft, und die Meldung nennt die Proxy-Adresse.
    /// </para>
    /// </summary>
    internal static string? LoopbackPublishAddress(string? dockerHost, string? hostOverride)
    {
        string? daemonHost = null;
        if (!string.IsNullOrWhiteSpace(dockerHost)
            && !dockerHost.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            && !dockerHost.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
        {
            daemonHost = LoopbackHost(dockerHost);
            if (daemonHost is null)
            {
                return null; // entfernter Daemon
            }
        }

        if (!string.IsNullOrWhiteSpace(hostOverride))
        {
            return LoopbackHost(hostOverride);
        }

        return daemonHost ?? "127.0.0.1";
    }

    /// <summary>Die Loopback-Adresse eines Endpunkts als Bindeadresse, sonst <c>null</c>.</summary>
    private static string? LoopbackHost(string value)
    {
        var host = Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : value;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "127.0.0.1";
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)
            ? address.ToString()
            : null;
    }

    private static bool IsLocalAddress(IPAddress address)
        => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));
}
