using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bifrost.Abstractions;

namespace Bifrost.Performance.Tests.Harness;

internal enum GatewayTransport
{
    /// <summary>Kestrel über TCP, Klartext.</summary>
    Http,

    /// <summary>Kestrel über TCP mit TLS-Terminierung im Gateway.</summary>
    Https,
}

internal sealed record GatewayProcessOptions(
    bool Stateless = true,
    GatewayTransport Transport = GatewayTransport.Http,
    /// <summary>
    /// Legt zusätzlich einen UI-Benutzer über <c>--reset-ui-admin</c> an — der Weg, den ein
    /// Betreiber ohne Oberfläche geht. Für die Browser-Wege (WP5.5) ist das die einzige zulässige
    /// Art, an eine Anmeldung zu kommen: Ein Testschalter, der die Anmeldung überspringt, prüft
    /// eine Installation, die es nicht gibt (M5-Vertrag §8).
    /// </summary>
    bool WithUiAdmin = false,
    /// <summary>
    /// Andere Server-Executable als die aus <c>bin</c> — für die Browser-Wege das
    /// <b>Publish-Ergebnis</b>. Der Unterschied ist nicht kosmetisch: Im Entwicklungslayout liefert
    /// <c>_framework/blazor.web.js</c> 200 mit null Bytes, weil die Datei erst beim Veröffentlichen
    /// in <c>wwwroot</c> landet. Ein Browsertest gegen <c>bin</c> prüfte ein Layout, das niemand
    /// ausliefert — und zwar mit einer Oberfläche, die benutzbar aussieht und tot ist.
    /// </summary>
    string? ExecutablePath = null,
    /// <summary>
    /// Zusaetzliche Umgebungsvariablen — etwa <c>BIFROST_TRUSTED_PROXIES</c> fuer den
    /// Reverse-Proxy-Sprung. Sie werden NACH den Vorgaben gesetzt und koennen sie ueberschreiben.
    /// </summary>
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// Der Gateway als <b>echter Prozess</b> hinter einem echten Socket — nicht als in-process
/// gehosteter Testserver.
/// <para>
/// <b>Warum dieser Aufwand:</b> Die bisherige Referenzmessung lief gegen einen In-Memory-Host, und
/// damit stand die halbe Produktivstrecke nicht im Messwert: kein TCP, keine TLS-Terminierung,
/// keine Serialisierung über einen Socket, kein Kestrel-Scheduling
/// (<c>docs/acceptance/performance.md</c>, „Was diese Zahlen NICHT belegen"). Was hier gemessen
/// wird, hat denselben Weg genommen wie eine echte Anfrage.
/// </para>
/// <para>
/// Der Zugang entsteht über <c>--issue-bootstrap-key</c>, also über denselben Weg, den ein
/// Betreiber ohne Oberfläche nimmt. Kein Testschalter, keine aufgeweichte Authentifizierung
/// (M5-Vertrag §8).
/// </para>
/// </summary>
internal sealed class GatewayProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _dataDir;
    private readonly StringBuilder _output = new();
    private readonly Lock _outputLock = new();

    private readonly X509Certificate2? _certificate;

    private GatewayProcess(
        Process process, string dataDir, Uri baseAddress, string apiKey, X509Certificate2? certificate)
    {
        _process = process;
        _dataDir = dataDir;
        BaseAddress = baseAddress;
        ApiKey = apiKey;
        _certificate = certificate;
        Client = CreateClient(baseAddress, apiKey, certificate);
    }

    /// <summary>
    /// Ein eigener HTTP-Client je virtuellem Client. Ein gemeinsamer waere bequemer und wuerde
    /// genau die Eigenschaft verwischen, die hier interessiert: wie sich mehrere echte Verbindungen
    /// auf derselben Strecke verhalten.
    /// </summary>
    public HttpClient CreateHttpClient() => CreateClient(BaseAddress, ApiKey, _certificate);

    /// <summary>
    /// Ein Client auf eine andere Adresse — mit demselben Schluessel. Gebraucht fuer den
    /// Reverse-Proxy-Sprung: Dort steht ein Proxy davor, der Gateway ist derselbe.
    /// <para>
    /// Das Testzertifikat wird nur angelegt, wenn das Ziel der Gateway selbst ist. Vor einem Proxy
    /// steht dessen eigenes Zertifikat, und dem hier pauschal zu vertrauen hiesse, TLS zu umgehen
    /// statt es zu messen.
    /// </para>
    /// </summary>
    public HttpClient CreateHttpClient(Uri target)
        => CreateClient(target, ApiKey, target == BaseAddress ? _certificate : null);

    public Uri BaseAddress { get; }

    public string ApiKey { get; }

    /// <summary>Gesetzt, wenn <see cref="GatewayProcessOptions.WithUiAdmin"/> verlangt wurde.</summary>
    public string? UiUsername { get; init; }

    public string? UiPassword { get; init; }

    /// <summary>HTTP-Client mit API-Key und — bei TLS — Vertrauen auf genau unser Testzertifikat.</summary>
    public HttpClient Client { get; }

    public Process Process => _process;

    /// <summary>
    /// Das Datenverzeichnis dieser Instanz. Gebraucht wird es dort, wo ein Weg an Dateien muss, die
    /// der Gateway selbst anlegt — etwa das Setup-Token in <c>config/bootstrap-token.txt</c>, das
    /// bewusst NICHT im Log steht.
    /// </summary>
    public string DataDirectory => _dataDir;

    public string Output
    {
        get
        {
            lock (_outputLock)
            {
                return _output.ToString();
            }
        }
    }

    public static async Task<GatewayProcess> StartAsync(GatewayProcessOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dataDir = Path.Combine(Path.GetTempPath(), $"bifrost-perf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BIFROST_DATA_DIR"] = dataDir,
            ["BIFROST_DB_CONNECTION"] = $"Data Source={Path.Combine(dataDir, "perf.db")}",
            // ADR-0025 E2: stdio läuft nativ; ein Betreiber setzt diese Erlaubnis genauso.
            ["BIFROST_ALLOW_HOST_EXECUTION"] = "true",
            ["BIFROST_MCP_STATELESS"] = options.Stateless ? "1" : "0",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            // Der Lauf soll Anfragen messen und nicht das Schreiben von Logzeilen.
            ["Logging__LogLevel__Default"] = "Warning",
        };

        foreach (var (key, value) in options.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            environment[key] = value;
        }

        X509Certificate2? certificate = null;
        if (options.Transport == GatewayTransport.Https)
        {
            var pfxPath = Path.Combine(dataDir, "perf-tls.pfx");
            certificate = CreateSelfSignedCertificate();
            File.WriteAllBytes(pfxPath, certificate.Export(X509ContentType.Pfx, "perf"));
            environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = pfxPath;
            environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = "perf";
        }

        var executable = options.ExecutablePath ?? TestServerPaths.GatewayExecutable;
        var apiKey = await IssueApiKeyAsync(executable, environment, ct);
        (string Username, string Password)? uiAdmin =
            options.WithUiAdmin ? await CreateUiAdminAsync(executable, environment, ct) : null;

        var port = FreeTcpPort();
        var scheme = options.Transport == GatewayTransport.Https ? "https" : "http";
        var baseAddress = new Uri($"{scheme}://127.0.0.1:{port}/");
        environment["ASPNETCORE_URLS"] = baseAddress.ToString().TrimEnd('/');

        var process = StartServer(executable, environment, out var sink);
        var gateway = new GatewayProcess(process, dataDir, baseAddress, apiKey, certificate)
        {
            UiUsername = uiAdmin?.Username,
            UiPassword = uiAdmin?.Password,
        };
        sink(gateway);

        try
        {
            await gateway.WaitUntilReadyAsync(ct);
        }
        catch
        {
            await gateway.DisposeAsync();
            throw;
        }

        return gateway;
    }

    /// <summary>
    /// Legt einen stdio-Upstream an — über die REST-API, also den Weg eines Betreibers. Gibt die
    /// vergebene Id zurück; der Soak-Harness braucht sie, um denselben Upstream später zu stören.
    /// </summary>
    public async Task<Guid> AddStdioUpstreamAsync(string slug, string serverFolder, CancellationToken ct)
    {
        var config = new UpstreamServerConfig(
            slug,
            $"Perf {serverFolder}",
            UpstreamTransportKind.Stdio,
            Enabled: true,
            Stdio: new StdioTransportOptions(
                TestServerPaths.Executable(serverFolder),
                [],
                Isolation: new IsolationOptions(IsolationMode.Host)));

        var response = await Client.PostAsJsonAsync("api/v1/servers", config, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Upstream '{slug}' konnte nicht angelegt werden ({response.StatusCode}): "
                + await response.Content.ReadAsStringAsync(ct));
        }

        var created = await response.Content.ReadFromJsonAsync<CreatedServer>(ct);
        await WaitUntilHealthyAsync(slug, ct);
        return created?.Id ?? throw new InvalidOperationException(
            $"Die Anlage von '{slug}' hat keine Id geliefert.");
    }

    /// <summary>
    /// Wartet, bis der Upstream verbunden ist. Auf 'Healthy' warten statt auf den Anlagevorgang:
    /// Der Prozess startet asynchron, und ein Lastlauf gegen einen noch nicht verbundenen Upstream
    /// misst den Verbindungsaufbau mit.
    /// </summary>
    public async Task WaitUntilHealthyAsync(string slug, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await Client.GetStringAsync("api/v1/servers", ct);
            if (status.Contains("\"Healthy\"", StringComparison.OrdinalIgnoreCase)
                || status.Contains("\"healthy\"", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Upstream '{slug}' wurde nicht healthy.\n{Output}");
    }

    private sealed record CreatedServer(Guid Id);

    public ProcessSnapshot Snapshot()
    {
        _process.Refresh();
        return new ProcessSnapshot(
            _process.TotalProcessorTime,
            _process.WorkingSet64,
            _process.PeakWorkingSet64,
            _process.Threads.Count,
            SafeHandleCount(_process));
    }

    private static int SafeHandleCount(Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch (PlatformNotSupportedException)
        {
            return -1;
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Der Gateway-Prozess hat sich mit Code {_process.ExitCode} beendet.\n{Output}");
            }

            try
            {
                using var response = await Client.GetAsync("healthz", ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Der Gateway wurde nicht bereit: {last?.Message}\n{Output}");
    }

    private static async Task<string> IssueApiKeyAsync(
        string executable, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var start = ServerStartInfo(executable, environment);
        start.ArgumentList.Add("--issue-bootstrap-key");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Der Serverprozess ließ sich nicht starten.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        const string marker = "API-Key";
        var line = stdout.Split('\n').FirstOrDefault(l => l.Contains(marker, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Kein API-Key in der Ausgabe von --issue-bootstrap-key.\n{stdout}\n{stderr}");

        return line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim();
    }

    /// <summary>
    /// Legt den UI-Admin über das Recovery-Kommando an und liest das einmalig ausgegebene Passwort
    /// von der Konsole — genau so, wie es ein Betreiber täte.
    /// </summary>
    private static async Task<(string Username, string Password)> CreateUiAdminAsync(
        string executable, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        const string username = "e2e-admin";
        var start = ServerStartInfo(executable, environment);
        start.ArgumentList.Add("--reset-ui-admin");
        start.ArgumentList.Add(username);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Der Serverprozess ließ sich nicht starten.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var line = stdout.Split('\n').FirstOrDefault(l => l.Contains("Passwort", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Kein Passwort in der Ausgabe von --reset-ui-admin.\n{stdout}\n{stderr}");

        return (username, line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim());
    }

    private static Process StartServer(
        string executable, IReadOnlyDictionary<string, string> environment,
        out Action<GatewayProcess> attachOutput)
    {
        var start = ServerStartInfo(executable, environment);
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Der Gateway-Prozess ließ sich nicht starten.");

        attachOutput = gateway =>
        {
            process.OutputDataReceived += (_, e) => gateway.Append(e.Data);
            process.ErrorDataReceived += (_, e) => gateway.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        };

        return process;
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_outputLock)
        {
            _output.AppendLine(line);
        }
    }

    private static ProcessStartInfo ServerStartInfo(
        string executable, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };

        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        return start;
    }

    private static HttpClient CreateClient(Uri baseAddress, string apiKey, X509Certificate2? certificate)
    {
        var handler = new HttpClientHandler();
        if (certificate is not null)
        {
            // Genau unser eigenes Testzertifikat wird vertraut — kein pauschales Abschalten der
            // Kettenprüfung. Der Unterschied ist der zwischen „TLS gemessen" und „TLS umgangen".
            var expected = certificate.Thumbprint;
            handler.ServerCertificateCustomValidationCallback =
                (_, presented, _, _) => presented?.Thumbprint == expected;
        }

        var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return client;
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=bifrost-perf", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                // Der Gateway startet Upstream-Prozesse; ohne den Baum bleiben sie stehen und
                // verfälschen den nächsten Lauf (WP0.4 hat genau diese Klasse Fehler gefunden).
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
            // Prozess war bereits weg.
        }
        finally
        {
            _process.Dispose();
            TryDelete(_dataDir);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Ein liegengebliebenes Temp-Verzeichnis ist kein Grund, einen Messlauf rot zu machen.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record ProcessSnapshot(
    TimeSpan Cpu, long WorkingSetBytes, long PeakWorkingSetBytes, int Threads, int Handles);

internal static class TestServerPaths
{
    public static string GatewayExecutable => Executable(
        Path.Combine(RepoRoot, "src", "Bifrost.Server", "bin", Configuration, "net10.0"), "Bifrost.Server");

    public static string Executable(string serverFolder) => Executable(
        Path.Combine(RepoRoot, "tests", "Bifrost.TestServers", serverFolder, "bin", Configuration, "net10.0"),
        $"Bifrost.TestServers.{serverFolder}");

    private static string Executable(string directory, string name)
    {
        var path = Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Executable nicht gefunden: {path}. Zuerst die Solution bauen.", path);
    }

    private static string Configuration => typeof(TestServerPaths).Assembly
        .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bifrost.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Repo-Root nicht gefunden.");
        }
    }
}
