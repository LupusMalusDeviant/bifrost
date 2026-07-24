using System.Diagnostics;
using System.Text.Json;
using McpMcp.Abstractions;

namespace McpMcp.Upstream.Wasi;

/// <summary>
/// WASI→MCP-Brücke (ADR-0020, Plan 0003/WP2): ein signiertes WebAssembly-Component erscheint als
/// normaler Upstream. Die Ausführung läuft in einem eigenständigen Rust-Host-Prozess, den dieser
/// Connector als Kindprozess startet und über einen versionierten IPC-Vertrag (length-prefixed
/// JSON über stdio) ansteuert — .NET kann WASI-P2-Components nicht in-process ausführen.
/// <para>
/// Der Host prüft die Signatur gegen die gepinnten Publisher und setzt Grants und
/// Ausführungslimits durch; das Gateway bleibt für RBAC, Guardrails, Approval und Audit
/// zuständig. Kein Governance-Bypass: Aufrufe erreichen den Host nur über den
/// <c>IToolInvoker</c>.
/// </para>
/// </summary>
public sealed class WasiRuntimeConnector : IUpstreamConnector
{
    /// <summary>
    /// Protokollversion, die dieser Client spricht. Muss zum Host passen — <c>2</c> liefert
    /// typisierte Tool-Beschreibungen bei <c>discover</c> (Plan 0003, WP6.1).
    /// </summary>
    public const string ProtocolVersion = "2";

    public UpstreamTransportKind Kind => UpstreamTransportKind.Wasi;

    public async Task<IUpstreamConnection> ConnectAsync(
        ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Wasi
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Wasi-Optionen.", nameof(config));

        // Component und Signatur werden hier gelesen, aber NICHT hier geprüft: die Verifikation
        // gegen die gepinnten Publisher passiert im Host, direkt vor dem Instanziieren.
        var component = await File.ReadAllBytesAsync(options.ComponentPath, ct).ConfigureAwait(false);
        var signature = await File.ReadAllBytesAsync(options.SignaturePath, ct).ConfigureAwait(false);

        ProcessHygiene.EnsureInitialized();
        var startInfo = new ProcessStartInfo
        {
            FileName = options.HostExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false, // KEIN Shell — Argumente literal.
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("host");
        foreach (var argument in options.HostArguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"WASI-Host '{options.HostExecutable}' ließ sich nicht starten.");
        }

        var connection = new WasiUpstreamConnection(id, process, options);
        try
        {
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startupCts.CancelAfter(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
            await connection.HandshakeAndLoadAsync(component, signature, startupCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}

/// <summary>
/// Eine laufende Host-Sitzung. Alle Anfragen laufen serialisiert über stdin/stdout des
/// Kindprozesses — der Vertrag ist request/response, ein Frame nach dem anderen.
/// </summary>
internal sealed class WasiUpstreamConnection : IUpstreamConnection
{
    private readonly Process _process;
    private readonly WasiTransportOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<WasiTool> _tools = [];
    private bool _disposed;

    public WasiUpstreamConnection(ServerId id, Process process, WasiTransportOptions options)
    {
        Id = id;
        _process = process;
        _options = options;
    }

    public ServerId Id { get; }

    // Der Host pusht keine Notifications — das Event bleibt bewusst leer verdrahtet.
    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    /// <summary>Handshake, Component laden und die Tool-Liste holen — der Startup-Pfad.</summary>
    public async Task HandshakeAndLoadAsync(byte[] component, byte[] signature, CancellationToken ct)
    {
        var hello = await RequestAsync(
            new { type = "hello", protocolVersion = WasiRuntimeConnector.ProtocolVersion },
            ct).ConfigureAwait(false);
        var hostProtocol = hello.GetProperty("protocolVersion").GetString();
        if (hostProtocol != WasiRuntimeConnector.ProtocolVersion)
        {
            throw new InvalidOperationException(
                $"WASI-Host spricht Protokoll '{hostProtocol}', erwartet '{WasiRuntimeConnector.ProtocolVersion}'.");
        }

        var grants = _options.Grants ?? new WasiCapabilityGrants();
        var loadRequest = new
        {
            type = "load",
            component = Convert.ToBase64String(component),
            signature = Convert.ToBase64String(signature),
            pinnedPublishers = _options.PinnedPublishers,
            grants = new
            {
                filesystemPreopens = grants.FilesystemPreopens ?? [],
                networkAllow = grants.NetworkAllow ?? [],
                environment = grants.Environment ?? [],
                secrets = grants.Secrets ?? (IReadOnlyList<string>)[],
                clock = grants.Clock,
                random = grants.Random,
            },
        };
        await RequestAsync(loadRequest, ct).ConfigureAwait(false);

        var discovered = await RequestAsync(new { type = "discover" }, ct).ConfigureAwait(false);
        _tools.Clear();
        _tools.AddRange(WasiToolNormalizer.Normalize(discovered.GetProperty("tools")));
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new UpstreamInventory(
            [.. _tools.Select(tool => new ToolDescriptor(tool.Name, tool.Description, tool.InputSchema))],
            [],
            []));

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        // Der Katalogname ist die normalisierte Form; der Host kennt nur den rohen Export-Namen.
        if (_tools.FirstOrDefault(tool => tool.Name == toolName) is not { } target)
        {
            return Result($"WASI-Upstream kennt kein Tool '{toolName}'.", isError: true);
        }

        if (!target.TryBindArguments(args, out var positional, out var bindingError))
        {
            return Result(bindingError, isError: true);
        }

        var limits = _options.Limits ?? new WasiExecutionLimits();
        var request = new
        {
            type = "invoke",
            tool = target.Export,
            args = positional,
            limits = new
            {
                fuel = limits.Fuel,
                timeoutMs = limits.TimeoutMs,
                maxMemoryBytes = limits.MaxMemoryBytes,
                maxOutputBytes = limits.MaxOutputBytes,
            },
        };

        JsonElement response;
        try
        {
            response = await RequestAsync(request, ct).ConfigureAwait(false);
        }
        catch (WasiHostException failure)
        {
            // Fehler des Guests sind ein Ergebnis, kein Transportfehler — als isError zurückgeben.
            return Result(failure.Message, isError: true);
        }

        var text = response.TryGetProperty("stdout", out var stdout) ? stdout.GetString() ?? string.Empty : string.Empty;
        if (response.TryGetProperty("result", out var result) && result.ValueKind is JsonValueKind.Number)
        {
            text = text.Length > 0 ? $"{text}\n{result.GetInt32()}" : result.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var truncated = response.TryGetProperty("truncated", out var flag) && flag.GetBoolean();
        if (truncated)
        {
            text += "\n… [vom WASI-Host gekürzt]";
        }

        return Result(text.Length > 0 ? text : "(keine Ausgabe)", isError: false);
    }

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => throw new NotSupportedException("WASI-Upstreams haben keine Resources.");

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => throw new NotSupportedException("WASI-Upstreams haben keine Prompts.");

    public async Task PingAsync(CancellationToken ct)
    {
        var health = await RequestAsync(new { type = "health" }, ct).ConfigureAwait(false);
        if (health.GetProperty("status").GetString() != "ok")
        {
            throw new InvalidOperationException("WASI-Host meldet keinen gesunden Zustand.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RequestAsync(new { type = "shutdown" }, shutdownCts.Token).ConfigureAwait(false);
            _process.WaitForExit(2000);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or OperationCanceledException or ObjectDisposedException or WasiHostException)
        {
            // Ein toter oder klemmender Host wird gleich hart beendet — Shutdown ist best effort.
        }

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
        _gate.Dispose();
    }

    private static JsonElement Result(string text, bool isError)
        => JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError,
        });

    /// <summary>
    /// Sendet einen Frame und liest die Antwort. Serialisiert über <see cref="_gate"/>, weil der
    /// Vertrag strikt request/response ist. Eine <c>error</c>-Antwort wird zur
    /// <see cref="WasiHostException"/>.
    /// </summary>
    private async Task<JsonElement> RequestAsync(object request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(request);
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);

            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(length, ct).ConfigureAwait(false);
            await stdin.WriteAsync(payload, ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);

            var body = await ReadFrameAsync(_process.StandardOutput.BaseStream, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.Clone();

            if (root.GetProperty("type").GetString() == "error")
            {
                var code = root.TryGetProperty("code", out var c) ? c.GetString() : "unknown";
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : string.Empty;
                throw new WasiHostException($"{code}: {message}");
            }

            return root;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > 64 * 1024 * 1024)
        {
            throw new InvalidOperationException($"WASI-Host kündigte einen {length}-Byte-Frame an — zu groß.");
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }
}

/// <summary>Eine strukturierte Fehlerantwort des WASI-Hosts.</summary>
public sealed class WasiHostException : Exception
{
    public WasiHostException(string message) : base(message) { }

    public WasiHostException() { }

    public WasiHostException(string message, Exception innerException) : base(message, innerException) { }
}
