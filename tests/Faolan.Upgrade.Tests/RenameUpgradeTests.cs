using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using Faolan.Abstractions;
using Xunit;

namespace Faolan.Upgrade.Tests;

/// <summary>
/// <b>Eine Bestandsinstallation übersteht die Umbenennung auf Faolan</b> (Vertrag
/// umbenennung-faolan §7): Sie startet mit ihren alten Variablennamen, findet ihre Datenbank unter
/// dem alten Dateinamen und entschlüsselt ihre gespeicherten Upstream-Konfigurationen mit ihrem
/// vorhandenen Schlüsselring.
/// <para>
/// <b>Geprüft am echten Serverprozess.</b> Die Übernahme der Variablen schreibt ins Prozessumfeld;
/// in einem Testhost liefe sie gegen die Umgebung des Testlaufs. Und nur der gestartete Server
/// zeigt, dass die drei Teile zusammen wirken: Kette, Dateiwahl und Schlüsselableitung.
/// </para>
/// <para>
/// <b>Was „0.12.0" hier heißt.</b> Ein Release-Artefakt von 0.12.0 liegt dem Test nicht vor. Der
/// Zustand wird deshalb hergestellt: Die Instanz entsteht mit dem aktuellen Server, danach trägt
/// ihre Datenbank den alten Namen und der zweite Start sieht ausschließlich alte Variablennamen.
/// Schlüsselring und kryptografische Zwecke sind dieselben wie in 0.12.0 — das sichert
/// <c>CryptographicNamesTests</c>.
/// </para>
/// </summary>
public sealed class RenameUpgradeTests
{
    [Theory]
    [InlineData("BIFROST_", "bifrost.db")]
    [InlineData("MCPMCP_", "mcpmcp.db")]
    public async Task An_instance_under_old_names_starts_and_decrypts_its_upstreams(string prefix, string databaseFile)
    {
        var ct = TestContext.Current.CancellationToken;
        var dataDir = Directory.CreateTempSubdirectory("faolan-rename-upgrade").FullName;
        try
        {
            // 1. Die Instanz entsteht und bekommt einen Upstream; dessen Konfiguration liegt
            //    verschlüsselt in der Datenbank.
            var slug = "bestand-" + Guid.NewGuid().ToString("N")[..8];
            var current = new Dictionary<string, string> { ["FAOLAN_DATA_DIR"] = dataDir };
            var apiKey = await IssueApiKeyAsync(current, ct);
            await using (var first = await ServerProcess.StartAsync(current, apiKey, ct))
            {
                var config = new UpstreamServerConfig(
                    slug,
                    "Bestand",
                    UpstreamTransportKind.StreamableHttp,
                    Enabled: false,
                    Http: new HttpTransportOptions(
                        new Uri("https://upstream.example.invalid/mcp"),
                        new Dictionary<string, string> { ["Authorization"] = "Bearer " + Guid.NewGuid().ToString("N") }));
                var created = await first.Client.PostAsJsonAsync("api/v1/servers", config, ct);
                created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync(ct));
            }

            // 2. Der Stand von vorher: die Datenbank unter dem alten Namen.
            foreach (var file in Directory.GetFiles(dataDir, "faolan.db*"))
            {
                File.Move(file, Path.Combine(dataDir, databaseFile + Path.GetFileName(file)["faolan.db".Length..]));
            }

            File.Exists(Path.Combine(dataDir, databaseFile)).Should().BeTrue();

            // 3. Neustart nur mit alten Variablennamen.
            var legacy = new Dictionary<string, string> { [prefix + "DATA_DIR"] = dataDir };
            await using var second = await ServerProcess.StartAsync(legacy, apiKey, ct);

            var servers = await second.Client.GetStringAsync("api/v1/servers", ct);
            servers.Should().Contain(
                $"\"slug\":\"{slug}\"",
                "der Slug steht nur in der verschlüsselten Konfiguration — er ist lesbar, also "
                + "hat der vorhandene Schlüsselring entschlüsselt, und der API-Key aus derselben "
                + "Datenbank hat angemeldet");
            File.Exists(Path.Combine(dataDir, "faolan.db")).Should().BeFalse(
                "neben der alten Datenbank darf keine leere neue entstehen");
            second.Output.Should().Contain(prefix + "DATA_DIR", "die Übernahme wird beim Start gemeldet");
        }
        finally
        {
            TryDelete(dataDir);
        }
    }

    /// <summary>
    /// Review-Befund H2 im echten Prozess: Das Image setzt <c>FAOLAN_DATA_DIR</c> selbst, der
    /// Betreiber hat noch <c>BIFROST_DATA_DIR</c> mit anderem Wert. Der Server darf nicht auf der
    /// Vorgabe starten, sondern bricht ab und nennt beide Namen — für beide alten Präfixe und für die
    /// Datei-Variante.
    /// <para>
    /// Mit Frist: Versagt die Sperre, läuft der Server einfach weiter. Ohne Frist hinge der Test dann,
    /// statt rot zu werden.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("BIFROST_DATA_DIR", false)]
    [InlineData("MCPMCP_DATA_DIR", false)]
    [InlineData("BIFROST_DATA_DIR", true)]
    public async Task An_image_default_hiding_an_old_setting_stops_the_start(string oldName, bool asFile)
    {
        var ct = TestContext.Current.CancellationToken;
        var imageDir = Directory.CreateTempSubdirectory("faolan-image-default").FullName;
        var operatorDir = Directory.CreateTempSubdirectory("faolan-operator-dir").FullName;
        var valueFile = Path.Combine(operatorDir, "datadir.txt");
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["FAOLAN_DATA_DIR"] = imageDir,
                ["FAOLAN_IMAGE_DEFAULTS"] = "FAOLAN_DATA_DIR=" + imageDir,
            };
            if (asFile)
            {
                await File.WriteAllTextAsync(valueFile, operatorDir + "\n", ct);
                environment[oldName + "_FILE"] = valueFile;
            }
            else
            {
                environment[oldName] = operatorDir;
            }

            using var process = Process.Start(ServerProcess.StartInfo(environment))
                ?? throw new InvalidOperationException("Server startet nicht.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException(
                    "Der Server lief nach 60 s noch — die Sperre hat den Start nicht verhindert.");
            }

            var error = await stderr;
            await stdout;

            process.ExitCode.Should().Be(2, error);
            error.Should().Contain(asFile ? oldName + "_FILE" : oldName).And.Contain("FAOLAN_DATA_DIR");
            error.Should().NotContain(operatorDir, "die Meldung nennt Namen, keine Werte");
            Directory.EnumerateFileSystemEntries(imageDir).Should().BeEmpty(
                "auf der Vorgabe des Images darf nichts angelegt worden sein");
        }
        finally
        {
            TryDelete(imageDir);
            TryDelete(operatorDir);
        }
    }

    /// <summary>
    /// Drittes Review: Die Brücke für den WASI-Hostnamen wird über den echten Weg gemeldet — eine
    /// gespeicherte Konfiguration in der Datenbank, ein Neustart, die Warnung im Startlog und der
    /// Befund BFR-UP-0002 im doctor-Bericht.
    /// </summary>
    [Fact]
    public async Task A_saved_upstream_with_the_old_host_name_is_reported_at_start_and_in_doctor()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataDir = Directory.CreateTempSubdirectory("faolan-legacy-host").FullName;
        try
        {
            var slug = "alt-host-" + Guid.NewGuid().ToString("N")[..8];
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var hostDirectory = Directory.CreateDirectory(Path.Combine(dataDir, "bin")).FullName;

            // Viertes Review: gemeldet wird gemessen am Host der Instanz. Ohne FAOLAN_WASI_HOST startet
            // kein WASI-Upstream — dann ist es keine Brücke, und es gibt nichts zu melden.
            var withoutHost = new Dictionary<string, string> { ["FAOLAN_DATA_DIR"] = dataDir };
            var environment = new Dictionary<string, string>(withoutHost)
            {
                ["FAOLAN_WASI_HOST"] = Path.Combine(hostDirectory, "faolan-wasi-host" + suffix),
            };
            var apiKey = await IssueApiKeyAsync(environment, ct);
            await using (var first = await ServerProcess.StartAsync(environment, apiKey, ct))
            {
                var config = new UpstreamServerConfig(
                    slug,
                    "Alter Host",
                    UpstreamTransportKind.Wasi,
                    Enabled: false,
                    Wasi: new WasiTransportOptions(
                        Path.Combine(hostDirectory, "bifrost-wasi-host" + suffix),
                        Path.Combine(dataDir, "c.wasm"),
                        Path.Combine(dataDir, "c.sig"),
                        []));
                var created = await first.Client.PostAsJsonAsync("api/v1/servers", config, ct);
                created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync(ct));
            }

            await using var second = await ServerProcess.StartAsync(environment, apiKey, ct);

            // Die Warnung entsteht nach dem Start (die Migrationen laufen als Hosted Service).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!second.Output.Contains("BFR-UP-0002", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
            }

            second.Output.Should().Contain("BFR-UP-0002").And.Contain(slug, "der Start nennt den Upstream");

            var doctor = await second.Client.GetStringAsync("api/v1/operations/doctor", ct);
            using var document = System.Text.Json.JsonDocument.Parse(doctor);
            var finding = document.RootElement.GetProperty("checks").EnumerateArray()
                .Single(check => check.GetProperty("code").GetString() == "BFR-UP-0002");
            finding.GetProperty("status").GetString().Should().Be("Warning");
            finding.GetRawText().Should().Contain(slug);
            await second.DisposeAsync();

            await using var third = await ServerProcess.StartAsync(withoutHost, apiKey, ct);
            var report = await third.Client.GetStringAsync("api/v1/operations/doctor", ct);
            using var withoutHostReport = System.Text.Json.JsonDocument.Parse(report);
            var withoutHostFinding = withoutHostReport.RootElement.GetProperty("checks").EnumerateArray()
                .Single(check => check.GetProperty("code").GetString() == "BFR-UP-0002");
            withoutHostFinding.GetProperty("status").GetString().Should().NotBe("Warning",
                "ohne eingerichteten Host läuft der Upstream nicht über die Brücke");
            withoutHostFinding.GetRawText().Should().NotContain(slug);
            third.Output.Should().NotContain("BFR-UP-0002");
        }
        finally
        {
            TryDelete(dataDir);
        }
    }

    /// <summary>
    /// Fünftes Review: Auf dem falschen, leeren Volume kommt der Betreiber an <c>doctor</c> nicht heran —
    /// der alte API-Schlüssel gilt dort nicht. Der Befund BFR-DB-0008 muss deshalb ohne Anmeldung
    /// sichtbar sein: im Startlog eines echten Prozesses, ohne dass ein Schlüssel ausgegeben wurde.
    /// </summary>
    [Fact]
    public async Task An_empty_instance_is_reported_in_the_startup_log_without_a_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataDir = Directory.CreateTempSubdirectory("faolan-wrong-volume").FullName;
        try
        {
            var environment = new Dictionary<string, string> { ["FAOLAN_DATA_DIR"] = dataDir };
            await using var server = await ServerProcess.StartAsync(environment, "kein-gueltiger-schluessel", ct);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!server.Output.Contains("BFR-DB-0008", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
            }

            server.Output.Should().Contain("BFR-DB-0008").And.Contain("FAOLAN_DATA_VOLUME");
            var unauthorized = await server.Client.GetAsync("api/v1/operations/doctor", ct);
            unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "genau deshalb muss der Befund im Startlog stehen");
        }
        finally
        {
            TryDelete(dataDir);
        }
    }

    private static async Task<string> IssueApiKeyAsync(IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var start = ServerProcess.StartInfo(environment);
        start.ArgumentList.Add("--issue-bootstrap-key");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Server startet nicht.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var line = stdout.Split('\n').FirstOrDefault(l => l.Contains("API-Key", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Kein API-Key ausgegeben.\n{stdout}\n{stderr}");
        return line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim();
    }

    private static void TryDelete(string directory)
    {
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

    private sealed class ServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly Lock _lock = new();
        private bool _disposed;

        private ServerProcess(Process process, HttpClient client)
        {
            _process = process;
            Client = client;
        }

        public HttpClient Client { get; }

        public string Output
        {
            get
            {
                lock (_lock)
                {
                    return _output.ToString();
                }
            }
        }

        public static async Task<ServerProcess> StartAsync(
            IReadOnlyDictionary<string, string> environment, string apiKey, CancellationToken ct)
        {
            var address = new Uri($"http://127.0.0.1:{FreePort()}/");
            var start = StartInfo(environment);
            start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');

            var process = Process.Start(start) ?? throw new InvalidOperationException("Server startet nicht.");
            var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
            var server = new ServerProcess(process, client);
            process.OutputDataReceived += (_, e) => server.Append(e.Data);
            process.ErrorDataReceived += (_, e) => server.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await server.WaitUntilReadyAsync(ct);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }

            return server;
        }

        /// <summary>
        /// Startinformation mit einer Umgebung, in der <b>keine</b> Generation des Präfixes aus dem
        /// Testlauf mitspielt — nur, was der Fall vorgibt.
        /// </summary>
        public static ProcessStartInfo StartInfo(IReadOnlyDictionary<string, string> environment)
        {
            var executable = Executable();
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };

            foreach (var name in start.Environment.Keys.ToList())
            {
                if (EnvironmentNameChain.Generations.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    start.Environment.Remove(name);
                }
            }

            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            foreach (var (key, value) in environment)
            {
                start.Environment[key] = value;
            }

            return start;
        }

        private async Task WaitUntilReadyAsync(CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"Server beendet mit {_process.ExitCode}.\n{Output}");
                }

                try
                {
                    using var response = await Client.GetAsync("healthz", ct);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(200, ct);
            }

            throw new TimeoutException($"Server wurde nicht bereit.\n{Output}");
        }

        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_lock)
            {
                _output.AppendLine(line);
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Zweimal aufrufbar: Ein Test beendet einen Prozess vorzeitig, um denselben Datenbestand
            // neu zu starten, und 'await using' ruft danach noch einmal.
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Client.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static string Executable()
        {
            var configuration = typeof(RenameUpgradeTests).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Faolan.slnx")))
            {
                root = root.Parent;
            }

            var path = Path.Combine(
                root?.FullName ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden."),
                "src", "Faolan.Server", "bin", configuration, "net10.0",
                "Faolan.Server" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            return File.Exists(path) ? path : throw new FileNotFoundException("Zuerst die Solution bauen.", path);
        }
    }
}
