using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Bifrost.Abstractions;

namespace Bifrost.Upstream.Wasi;

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
    /// Protokollversion, die dieser Client spricht. Muss zum Host passen — <c>4</c> trägt eine
    /// <c>id</c> je Anfrage und Antwort, kennt <c>cancel</c> und erlaubt damit mehrere Aufrufe
    /// gleichzeitig (Plan 0003, Nebenläufigkeit und Abbruch).
    /// </summary>
    public const string ProtocolVersion = "4";

    private readonly IPublisherTrustStore _trust;
    private readonly IAuditSink? _audit;
    private readonly IConnectorPackageResolver? _packages;

    public WasiRuntimeConnector(
        IPublisherTrustStore trust,
        IAuditSink? audit = null,
        IConnectorPackageResolver? packages = null)
    {
        ArgumentNullException.ThrowIfNull(trust);
        _trust = trust;
        _audit = audit;
        _packages = packages;
    }

    public UpstreamTransportKind Kind => UpstreamTransportKind.Wasi;

    public async Task<IUpstreamConnection> ConnectAsync(
        ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Wasi
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Wasi-Optionen.", nameof(config));

        // Vertrauensquelle ist ab WP4 ausschließlich der Trust-Store — nicht die Konfiguration.
        // Ist er leer, gehen null Schlüssel an den Host, und der lehnt fail-closed ab.
        var pinned = _trust.ActivePublicKeys;

        var (componentPath, signaturePath) = ResolvePaths(config, options);

        // Component und Signatur werden hier gelesen, aber NICHT hier geprüft: die Verifikation
        // gegen die gepinnten Publisher passiert im Host, direkt vor dem Instanziieren.
        var component = await File.ReadAllBytesAsync(componentPath, ct).ConfigureAwait(false);
        var signature = await File.ReadAllBytesAsync(signaturePath, ct).ConfigureAwait(false);

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
        if (!string.IsNullOrWhiteSpace(options.ModuleCacheDirectory))
        {
            // Der Host legt dort seinen MAC-Schlüssel ab und prüft jedes Kompilat dagegen (WP5).
            startInfo.ArgumentList.Add("--cache-dir");
            startInfo.ArgumentList.Add(options.ModuleCacheDirectory);
            if (options.ModuleCacheMaxBytes is { } budget)
            {
                startInfo.ArgumentList.Add("--cache-max-bytes");
                startInfo.ArgumentList.Add(budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

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
            var audit = await connection
                .HandshakeAndLoadAsync(component, signature, pinned, startupCts.Token)
                .ConfigureAwait(false);
            RecordGrantAudit(id, config, options, audit);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    /// <summary>
    /// Woher Component und Signatur kommen: aus einem installierten Paket (ADR-0016) oder aus
    /// Pfaden in der Konfiguration.
    /// <para>
    /// Ein <c>PackageId</c>, zu dem es keine aktive Version gibt, wird <b>abgelehnt</b> statt auf
    /// die Pfade zurückzufallen. Ein stiller Rückfall führte dazu, dass nach einem misslungenen
    /// Update eine alte Datei liefe, während die Oberfläche das Paket als Quelle ausweist.
    /// </para>
    /// </summary>
    private (string ComponentPath, string SignaturePath) ResolvePaths(
        UpstreamServerConfig config, WasiTransportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PackageId))
        {
            return string.IsNullOrWhiteSpace(options.ComponentPath)
                ? throw new ArgumentException(
                    $"Config '{config.Slug}' nennt weder ein Paket noch einen ComponentPath.",
                    nameof(config))
                : (options.ComponentPath, options.SignaturePath);
        }

        if (_packages is null)
        {
            throw new InvalidOperationException(
                $"Config '{config.Slug}' verweist auf das Paket '{options.PackageId}', aber in dieser "
                + "Zusammenstellung ist keine Paketverwaltung eingebunden.");
        }

        return _packages.ResolveActive(options.PackageId)
            ?? throw new InvalidOperationException(
                $"Für das Paket '{options.PackageId}' gibt es keine aktive Version. Der Upstream "
                + $"'{config.Slug}' kommt deshalb nicht hoch — ein Rückfall auf andere Dateien wäre "
                + "eine stille Abweichung von dem, was konfiguriert ist.");
    }

    /// <summary>
    /// Schreibt den Grant-Audit-Datensatz des Hosts in den Audit-Pfad (WP4.3): welches Modul,
    /// welcher Publisher, welche Runtime, welche Grants. Ohne diese Zeile wüsste hinterher
    /// niemand, mit welchen Rechten ein Component tatsächlich gelaufen ist — der Host protokolliert
    /// nicht selbst, und die Konfiguration sagt nur, was gewünscht war.
    /// </summary>
    private void RecordGrantAudit(
        ServerId id, UpstreamServerConfig config, WasiTransportOptions options, JsonElement audit)
    {
        if (_audit is null)
        {
            return;
        }

        string? Text(string name) => audit.TryGetProperty(name, out var value) ? value.GetString() : null;
        string List(string name) => audit.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.Array
            ? string.Join(", ", value.EnumerateArray().Select(item => item.GetString()))
            : string.Empty;

        var granted = new List<string>();
        foreach (var (label, name) in new[]
        {
            ("Preopens", "grantedFilesystemPreopens"),
            ("Netz", "grantedNetworkAllow"),
            ("Env", "grantedEnvironment"),
            ("Secrets", "grantedSecrets"),
        })
        {
            if (List(name) is { Length: > 0 } values)
            {
                granted.Add($"{label}: {values}");
            }
        }

        if (audit.TryGetProperty("grantedClock", out var clock) && clock.ValueKind is JsonValueKind.True)
        {
            granted.Add("Clock");
        }

        if (audit.TryGetProperty("grantedRandom", out var random) && random.ValueKind is JsonValueKind.True)
        {
            granted.Add("Random");
        }

        _audit.Record(new AuditEvent(
            DateTimeOffset.UtcNow,
            Caller: null,
            CallOrigin.System,
            AuditEventKind.ServerLifecycle,
            Server: id,
            Tool: null,
            Status: null,
            RedactedArguments: null,
            RequestBytes: null,
            ResponseBytes: null,
            Duration: null,
            Detail: $"WASI-Component geladen: Upstream '{config.Slug}', Datei '{Path.GetFileName(options.ComponentPath)}', "
                + $"Modul-SHA256 {Text("moduleSha256")}, Publisher {Text("publisherKeyId")}, Runtime {Text("runtime")}, "
                + $"Grants [{(granted.Count > 0 ? string.Join("; ", granted) : "keine")}]"));
    }
}

/// <summary>
/// Eine laufende Host-Sitzung über stdin/stdout des Kindprozesses.
/// <para>
/// Ab Vertrag v4 trägt jede Anfrage eine <c>id</c> und jede Antwort gibt sie zurück. Deshalb liest
/// ein einzelner Pump-Task alle Frames und weckt darüber den jeweiligen Wartenden — Antworten
/// dürfen in anderer Reihenfolge kommen als die Anfragen. Serialisiert wird nur noch das
/// <b>Schreiben</b> eines Frames, nicht mehr der ganze Aufruf.
/// </para>
/// </summary>
internal sealed class WasiUpstreamConnection
    : IUpstreamConnection, ISignedUpstreamConnection, ICallerAwareUpstreamConnection
{
    private readonly Process _process;
    private readonly WasiTransportOptions _options;
    /// <summary>Nur noch die Schreibseite: Ein Frame muss am Stück hinausgehen.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<WasiTool> _tools = [];
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stopped = new();
    private readonly Task _pump;
    private long _nextId;
    private bool _disposed;

    public WasiUpstreamConnection(ServerId id, Process process, WasiTransportOptions options)
    {
        Id = id;
        _process = process;
        _options = options;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Liest Frames, solange der Host lebt, und weckt den Wartenden zur jeweiligen Id. Ein
    /// verwaister Frame (unbekannte Id) wird verworfen — er gehört zu einem Aufruf, den niemand
    /// mehr erwartet.
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            while (!_stopped.IsCancellationRequested)
            {
                var body = await ReadFrameAsync(_process.StandardOutput.BaseStream, _stopped.Token)
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement.Clone();
                if (!root.TryGetProperty("id", out var declared))
                {
                    // Ohne Korrelations-Id ist keine Zuordnung möglich — und Raten wäre schlimmer
                    // als Aufgeben. Praktisch heißt das fast immer: Der Host spricht noch Vertrag 3.
                    // Ohne diesen Abbruch würde der Handshake schlicht hängen, statt es zu sagen.
                    FailPending(new WasiHostException(
                        "WASI-Host antwortet ohne Korrelations-Id — er spricht Vertrag "
                        + $"{WasiRuntimeConnector.ProtocolVersion} nicht."));
                    return;
                }

                if (_pending.TryRemove(declared.GetInt64(), out var waiter))
                {
                    waiter.TrySetResult(root);
                }
            }
        }
        catch (Exception failure)
        {
            // Der Host ist weg oder die Leitung kaputt. Wartende jetzt scheitern zu lassen ist
            // besser, als sie bis zum Per-Call-Timeout hängen zu lassen.
            FailPending(failure);
            return;
        }

        FailPending(new WasiHostException("WASI-Host wurde beendet."));
    }

    private void FailPending(Exception failure)
    {
        foreach (var (id, waiter) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                waiter.TrySetException(failure);
            }
        }
    }

    public ServerId Id { get; }

    /// <summary>Publisher, dessen Signatur der Host akzeptiert hat — Ziel des Entzugs (WP4).</summary>
    public string PublisherKeyId { get; private set; } = string.Empty;

    // Der Host pusht keine Notifications — das Event bleibt bewusst leer verdrahtet.
    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Handshake, Component laden und die Tool-Liste holen — der Startup-Pfad. Liefert den
    /// Grant-Audit-Datensatz des Hosts zurück; der gehört ins Audit und nicht ins Nichts.
    /// </summary>
    public async Task<JsonElement> HandshakeAndLoadAsync(
        byte[] component, byte[] signature, IReadOnlyList<string> pinnedPublishers, CancellationToken ct)
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

        RequireFeatures(hello);

        var grants = _options.Grants ?? new WasiCapabilityGrants();
        var loadRequest = new
        {
            type = "load",
            component = Convert.ToBase64String(component),
            signature = Convert.ToBase64String(signature),
            pinnedPublishers = pinnedPublishers,
            grants = new
            {
                filesystemPreopens = grants.FilesystemPreopens ?? [],
                networkAllow = grants.NetworkAllow ?? [],
                environment = grants.Environment ?? [],
                secrets = grants.Secrets ?? (IReadOnlyList<string>)[],
                clock = grants.Clock,
                random = grants.Random,
            },
            // Getrennt von den Grants: Der Grant nennt die Namen, dieses Feld trägt die Werte.
            // Sie gehen nur hier über die Leitung und stehen in keiner Antwort und keinem Audit.
            secretValues = _options.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal),
            // Ohne persistente Instanz gibt es keine Handles — und damit keine Resources.
            persistentInstance = _options.PersistentInstance,
        };
        var loaded = await RequestAsync(loadRequest, ct).ConfigureAwait(false);
        var audit = loaded.GetProperty("audit");
        PublisherKeyId = audit.TryGetProperty("publisherKeyId", out var publisher)
            ? publisher.GetString() ?? string.Empty
            : string.Empty;

        var discovered = await RequestAsync(new { type = "discover" }, ct).ConfigureAwait(false);
        _tools.Clear();
        _tools.AddRange(WasiToolNormalizer.Normalize(discovered.GetProperty("tools")));
        return audit;
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new UpstreamInventory(
            [.. _tools.Select(tool => new ToolDescriptor(tool.Name, tool.Description, tool.InputSchema))],
            [],
            []));

    public Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
        => CallToolAsync(string.Empty, toolName, args, ct);

    /// <summary>
    /// Ruft mit Aufrufer-Identität auf (<see cref="ICallerAwareUpstreamConnection"/>). Nur bei
    /// <c>PersistentInstance</c> von Belang: Der Host schreibt jedes ausgegebene Handle auf diesen
    /// Namen, und nur derselbe Name kann es wieder einlösen.
    /// </summary>
    public async Task<JsonElement> CallToolAsync(
        string caller, string toolName, JsonElement args, CancellationToken ct)
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
            caller,
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
        if (response.TryGetProperty("result", out var result)
            && result.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            // Seit Vertrag 3 ist der Rückgabewert ein beliebiger JSON-Wert. Strings gehen roh
            // durch — sonst stünden Anführungszeichen im Ergebnis —, alles andere als JSON.
            var rendered = result.ValueKind is JsonValueKind.String
                ? result.GetString() ?? string.Empty
                : result.GetRawText();
            text = text.Length > 0 ? $"{text}\n{rendered}" : rendered;
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

    /// <summary>
    /// Pflichtfeatures, ohne die dieser Client nicht arbeiten kann (ADR-0016). Ein fehlendes
    /// Feature ist ein Kompatibilitätsfehler beim Handshake — nicht eine Überraschung beim ersten
    /// Aufruf. <c>streams</c> steht bewusst nicht darin: Der Host meldet dort <c>false</c>, und
    /// das Gateway braucht sie nicht.
    /// </summary>
    private static readonly string[] RequiredFeatures =
        ["typedDiscovery", "cancellation", "concurrency", "drain", "readiness"];

    private static void RequireFeatures(JsonElement hello)
    {
        if (!hello.TryGetProperty("features", out var features)
            || features.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "WASI-Host nennt beim Handshake keine Capability-Flags — er ist älter als der Vertrag verlangt.");
        }

        var missing = RequiredFeatures
            .Where(name => !features.TryGetProperty(name, out var flag)
                || flag.ValueKind is not JsonValueKind.True)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"WASI-Host unterstützt nicht: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// Health im Sinne von <b>Bereitschaft</b>, nicht bloß Leben (ADR-0016). Ein Host, der gerade
    /// drainiert, antwortet weiterhin — Aufrufe nimmt er nicht mehr an. Würde hier nur
    /// <c>status</c> geprüft, hielte der Supervisor ihn für gesund und schickte weiter Arbeit an
    /// eine sich schließende Tür.
    /// </summary>
    public async Task PingAsync(CancellationToken ct)
    {
        var health = await RequestAsync(new { type = "health" }, ct).ConfigureAwait(false);
        if (health.GetProperty("status").GetString() != "ok")
        {
            throw new InvalidOperationException("WASI-Host meldet keinen gesunden Zustand.");
        }

        if (!health.TryGetProperty("ready", out var ready) || ready.ValueKind is not JsonValueKind.True)
        {
            var phase = health.TryGetProperty("phase", out var declared) ? declared.GetString() : "unbekannt";
            throw new InvalidOperationException($"WASI-Host ist nicht bereit (Phase '{phase}').");
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
            // Erst drainieren, dann beenden (ADR-0016: … → drain → stop). Ohne den Schritt
            // schneidet `shutdown` laufende Aufrufe ab — seit Vertrag v4 können mehrere
            // gleichzeitig unterwegs sein, und der Host bricht sie beim Beenden ab.
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await RequestAsync(
                new { type = "drain", graceMs = 5000 }, drainCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or OperationCanceledException or ObjectDisposedException or WasiHostException)
        {
            // Drain ist eine Höflichkeit, kein Muss — der Shutdown folgt so oder so.
        }

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

        await _stopped.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException
            or IOException or ObjectDisposedException or WasiHostException)
        {
            // Der Pump hängt am toten Prozess — das Aufräumen darf daran nicht scheitern.
        }

        _process.Dispose();
        _gate.Dispose();
        _stopped.Dispose();
    }

    private static JsonElement Result(string text, bool isError)
        => JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError,
        });

    /// <summary>
    /// Sendet einen Frame und wartet auf die Antwort <b>zu dieser Id</b>. Eine <c>error</c>-Antwort
    /// wird zur <see cref="WasiHostException"/>.
    /// <para>
    /// Bricht <paramref name="ct"/> ab, geht ein <c>cancel</c> an den Host und es wird weiter auf
    /// dessen Antwort gewartet: Erst sie belegt, dass der Aufruf wirklich geendet hat. Einfach
    /// aufzugeben würde den Guest weiterlaufen lassen und den Abbruch nur behaupten.
    /// </para>
    /// </summary>
    private async Task<JsonElement> RequestAsync(object request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var envelope = JsonSerializer.SerializeToNode(request)?.AsObject()
            ?? throw new InvalidOperationException("Anfrage ließ sich nicht serialisieren.");
        envelope["id"] = id;

        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            await SendAsync(envelope, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        JsonElement root;
        await using (ct.Register(() => _ = CancelAsync(id)).ConfigureAwait(false))
        {
            root = await waiter.Task.ConfigureAwait(false);
        }

        if (root.GetProperty("type").GetString() == "error")
        {
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : "unknown";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : string.Empty;
            // Der Host hat den Abbruch eingelöst — nach außen ist das eine Abbruchmeldung und
            // kein Upstream-Fehler.
            if (code == "cancelled")
            {
                throw new OperationCanceledException($"WASI-Aufruf abgebrochen: {message}");
            }

            throw new WasiHostException($"{code}: {message}");
        }

        return root;
    }

    /// <summary>Schickt einen Abbruch für eine laufende Anfrage; best effort.</summary>
    private async Task CancelAsync(long target)
    {
        try
        {
            var envelope = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextId),
                ["type"] = "cancel",
                ["target"] = target,
            };
            await SendAsync(envelope, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or InvalidOperationException or OperationCanceledException)
        {
            // Host schon weg — dann ist der Aufruf ohnehin beendet.
        }
    }

    /// <summary>Schreibt einen Frame am Stück. Nur das muss serialisiert sein, nicht der Aufruf.</summary>
    private async Task SendAsync(System.Text.Json.Nodes.JsonObject envelope, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(length, ct).ConfigureAwait(false);
            await stdin.WriteAsync(payload, ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
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
