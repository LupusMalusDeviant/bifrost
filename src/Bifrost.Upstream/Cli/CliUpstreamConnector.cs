using System.Diagnostics;
using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Upstream.Isolation;

namespace Bifrost.Upstream.Cli;

/// <summary>
/// Shell-freie CLI-Brücke mit typisierten Manifesten, isoliertem Environment, kanonischer
/// Pfadprüfung, Parallelitätsgrenzen und während des Lesens begrenzten Ausgabestreams.
/// </summary>
/// <param name="gateway">
/// Die Kennung dieser Gateway-Instanz. Sie landet als Etikett auf jedem gestarteten Container,
/// damit ein Aufräumlauf die eigenen von fremden unterscheiden kann. Optional, damit die
/// Registrierung in <c>Program.cs</c> unverändert bleibt; ohne sie gilt eine prozessweite
/// Ersatzkennung.
/// </param>
public sealed class CliUpstreamConnector(GatewayIdentity? gateway = null)
    : IUpstreamConnector, IAsyncDisposable
{
    private readonly string _instanceId = gateway?.InstanceId ?? ContainerIdentity.ProcessInstanceId;

    public UpstreamTransportKind Kind => UpstreamTransportKind.Cli;

    /// <summary>
    /// Aufräumen beim Herunterfahren — dieselbe Begründung wie beim stdio-Konnektor: Ein Container
    /// je Aufruf endet zwar mit dem Kommando (<c>--rm</c>), aber ein abgebrochener Aufruf kann
    /// einen Rest hinterlassen, und der trägt das Instanz-Etikett.
    /// </summary>
    public async ValueTask DisposeAsync()
        => await ContainerLifecycle.SweepAllRuntimesAsync(_instanceId).ConfigureAwait(false);

    public async Task<IUpstreamConnection> ConnectAsync(
        ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();
        var options = config.Cli
            ?? throw new ArgumentException(
                $"Config '{config.Slug}' hat keine Cli-Optionen.", nameof(config));

        // Kein stiller Rückfall (ADR-0018, ADR-0025 E6): Wer Container verlangt und keine Runtime
        // hat, bekommt hier eine Absage. Auf den Host auszuweichen hiesse, die Isolation
        // abzuschalten, ohne dass es jemand merkt — der Upstream liefe weiter, nur ungeschützt.
        if (options.Isolation is { Mode: IsolationMode.Container } isolation
            && await ContainerLaunchPolicy.ProbeAsync(isolation, ct).ConfigureAwait(false)
                is { } problem)
        {
            throw new InvalidOperationException(
                ContainerLaunchPolicy.RefusalMessage(config.Slug, problem));
        }

        return new CliUpstreamConnection(id, options, config.Slug, _instanceId);
    }
}

internal sealed class CliUpstreamConnection : IUpstreamConnection
{
    private const int DefaultTimeoutSeconds = 30;
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly CliTransportOptions _options;
    private readonly ResolvedCliProcess _resolvedProcess;
    private readonly Dictionary<string, CliToolSpec> _tools;
    private readonly Dictionary<string, SemaphoreSlim> _commandGates;
    private readonly SemaphoreSlim _upstreamGate;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _slug;
    private readonly string _instanceId;

    public CliUpstreamConnection(
        ServerId id, CliTransportOptions options, string slug = "cli", string? instanceId = null)
    {
        Id = id;
        _options = options;
        _slug = slug;
        _instanceId = instanceId ?? ContainerIdentity.ProcessInstanceId;
        _resolvedProcess = CliProcessPolicy.Resolve(options);
        _tools = options.Tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        _upstreamGate = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        _commandGates = options.Tools
            .Where(tool => tool.MaxConcurrency is not null)
            .ToDictionary(
                tool => tool.Name,
                tool => new SemaphoreSlim(tool.MaxConcurrency!.Value, tool.MaxConcurrency.Value),
                StringComparer.Ordinal);
    }

    public ServerId Id { get; }

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new UpstreamInventory(
            [.. _options.Tools.Select(tool => new ToolDescriptor(
                tool.Name,
                tool.Description,
                CliArgumentBinder.BuildSchema(tool),
                tool.Risk,
                tool.Risk is CapabilityRisk.Destructive or CapabilityRisk.Privileged))],
            [],
            []));
    }

    public async Task<JsonElement> CallToolAsync(
        string toolName, JsonElement args, CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolName, out var spec))
        {
            throw new InvalidOperationException(
                $"Kommando '{toolName}' existiert nicht in diesem CLI-Upstream.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _upstreamGate.WaitAsync(linked.Token).ConfigureAwait(false);
        SemaphoreSlim? commandGate = null;
        var commandGateAcquired = false;
        try
        {
            if (_commandGates.TryGetValue(toolName, out commandGate))
            {
                await commandGate.WaitAsync(linked.Token).ConfigureAwait(false);
                commandGateAcquired = true;
            }

            return await ExecuteAsync(spec, args, ct, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (commandGateAcquired)
            {
                commandGate!.Release();
            }
            _upstreamGate.Release();
        }
    }

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => throw new NotSupportedException("CLI-Upstreams haben keine Resources.");

    public Task<JsonElement> GetPromptAsync(
        string promptName, JsonElement? args, CancellationToken ct)
        => throw new NotSupportedException("CLI-Upstreams haben keine Prompts.");

    public Task PingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = CliProcessPolicy.Resolve(_options);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        return ValueTask.CompletedTask;
    }

    private async Task<JsonElement> ExecuteAsync(
        CliToolSpec spec,
        JsonElement args,
        CancellationToken callerToken,
        CancellationToken executionToken)
    {
        // Ein eigener Containername je Aufruf. Er ist der einzige Griff, mit dem sich der Container
        // bei Zeitüberschreitung noch beenden lässt: Den Client zu töten liesse das Kommando im
        // Container weiterlaufen, und die Zeitgrenze wäre eine Zusage, die niemand einhält.
        var container = _options.Isolation is { Mode: IsolationMode.Container } isolation
            ? (Isolation: isolation, Identity: ContainerIdentity.ForUpstream(_slug, _instanceId))
            : default((IsolationOptions Isolation, ContainerIdentity Identity)?);

        var startInfo = CliProcessPolicy.CreateStartInfo(
            _options, _resolvedProcess, container?.Identity);
        foreach (var fixedArgument in spec.FixedArguments ?? [])
        {
            startInfo.ArgumentList.Add(fixedArgument);
        }
        foreach (var callerArgument in CliArgumentBinder.Bind(spec, args, _options))
        {
            startInfo.ArgumentList.Add(callerArgument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Prozess '{_resolvedProcess.Executable}' ließ sich nicht starten.");
        }

        using var captureCts = new CancellationTokenSource();
        var stdoutTask = BoundedProcessOutput.ReadAsync(
            process.StandardOutput.BaseStream,
            _options.MaxOutputBytes,
            _resolvedProcess.Encoding,
            captureCts.Token);
        var stderrTask = BoundedProcessOutput.ReadAsync(
            process.StandardError.BaseStream,
            _options.MaxOutputBytes,
            _resolvedProcess.Encoding,
            captureCts.Token);

        var timeout = TimeSpan.FromSeconds(
            _options.TimeoutSeconds is > 0 ? _options.TimeoutSeconds.Value : DefaultTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
            executionToken, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !callerToken.IsCancellationRequested;
            TryKill(process);

            // Und der Container dazu. `docker run` ist ein Client: Ohne diesen Schritt liefe das
            // Kommando nach dem Abbruch im Container weiter — sichtbar an nichts, nur teuer.
            if (container is { } running)
            {
                await ContainerLifecycle
                    .KillAsync(running.Isolation.Runtime, running.Identity.Name)
                    .ConfigureAwait(false);
            }
        }

        var (stdout, stderr) = await DrainOutputAsync(
            stdoutTask, stderrTask, captureCts).ConfigureAwait(false);
        stdout = RedactKnownSecrets(stdout);
        stderr = RedactKnownSecrets(stderr);

        if (callerToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerToken.IsCancellationRequested
                ? callerToken
                : executionToken);
        }

        if (timedOut)
        {
            return Result(
                exitCode: null,
                timedOut: true,
                stdout,
                stderr,
                $"CLI timeout after {timeout.TotalSeconds:0}s - process tree killed.",
                isError: true);
        }

        var body = BuildBody(process.ExitCode, stdout.Text, stderr.Text);
        return Result(
            process.ExitCode,
            timedOut: false,
            stdout,
            stderr,
            body,
            isError: process.ExitCode != 0);
    }

    private static async Task<(CapturedProcessStream Stdout, CapturedProcessStream Stderr)>
        DrainOutputAsync(
            Task<CapturedProcessStream> stdoutTask,
            Task<CapturedProcessStream> stderrTask,
            CancellationTokenSource captureCts)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(StreamDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            captureCts.Cancel();
        }

        return (
            await ObserveCaptureAsync(stdoutTask).ConfigureAwait(false),
            await ObserveCaptureAsync(stderrTask).ConfigureAwait(false));
    }

    private static async Task<CapturedProcessStream> ObserveCaptureAsync(
        Task<CapturedProcessStream> capture)
    {
        try
        {
            return await capture.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CapturedProcessStream(string.Empty, 0, 0, Truncated: true);
        }
    }

    private CapturedProcessStream RedactKnownSecrets(CapturedProcessStream stream)
    {
        var text = stream.Text;
        foreach (var secret in _options.EnvironmentVariables?.Values ?? [])
        {
            if (secret.Length >= 4)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }
        return stream with { Text = text };
    }

    private static string BuildBody(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 0)
        {
            return stdout.Length > 0
                ? stdout
                : stderr.Length > 0
                    ? stderr
                    : "(exit 0, no output)";
        }

        if (stdout.Length > 0 && stderr.Length > 0)
        {
            return $"{stdout}\n[stderr]\n{stderr}";
        }

        return stdout.Length > 0
            ? stdout
            : stderr.Length > 0
                ? stderr
                : $"(exit {exitCode}, no output)";
    }

    private JsonElement Result(
        int? exitCode,
        bool timedOut,
        CapturedProcessStream stdout,
        CapturedProcessStream stderr,
        string text,
        bool isError)
        => JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError,
            cli = new
            {
                exitCode,
                timedOut,
                maxOutputBytesPerStream = _options.MaxOutputBytes,
                stdout = StreamMetadata(stdout),
                stderr = StreamMetadata(stderr),
            },
        });

    private static object StreamMetadata(CapturedProcessStream stream) => new
    {
        text = stream.Text,
        totalBytes = stream.TotalBytes,
        capturedBytes = stream.CapturedBytes,
        truncated = stream.Truncated,
    };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}
