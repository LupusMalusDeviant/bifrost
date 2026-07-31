using Bifrost.Abstractions;
using Bifrost.Upstream.Isolation;
using ModelContextProtocol.Client;

namespace Bifrost.Upstream;

/// <summary>
/// Startet lokale MCP-Server über stdio (ADR-0005) — seit WP3.2 wahlweise als Hostprozess oder
/// <b>in einem Container</b> (ADR-0018, ADR-0025 E5).
/// <para>
/// <b>Host-Modus ist keine Sandbox</b>, und das ist die dokumentierte Grenze: Das Programm läuft
/// mit den Rechten des Gateways, und das Gateway hält den Schlüsselring, mit dem sich sämtliche
/// Zugangsdaten aller anderen Upstreams entschlüsseln lassen. Was dort geht, ist, die Umgebung des
/// Kindprozesses klein zu halten (<see cref="StdioProcessEnvironment"/>).
/// </para>
/// <para>
/// <b>Container-Modus</b> setzt dieselbe Mindestpolicy durch wie der CLI-Transport, aus derselben
/// Stelle (<c>ContainerLaunchPolicy</c>). Der einzige Unterschied ist die Lebensdauer: Ein
/// stdio-Upstream ist eine <em>stehende Sitzung</em> — stdin bleibt offen, weil darüber das
/// Protokoll läuft. Deshalb ist das Abräumen hier eine eigene Handlung und kein Nebeneffekt von
/// <c>--rm</c>.
/// </para>
/// <para>
/// <b>Kein stiller Rückfall</b> (ADR-0018, ADR-0025 E6): Verlangt eine Konfiguration Container und
/// trägt die Runtime die Policy nicht, kommt der Upstream nicht hoch.
/// </para>
/// </summary>
/// <param name="gateway">
/// Die Kennung dieser Gateway-Instanz. Sie landet als Etikett auf jedem gestarteten Container,
/// damit ein Aufräumlauf die eigenen von fremden unterscheiden kann. Optional, damit die
/// Registrierung im Wirt unverändert bleibt; ohne sie gilt eine prozessweite Ersatzkennung.
/// </param>
public sealed class StdioUpstreamConnector(GatewayIdentity? gateway = null)
    : IUpstreamConnector, IAsyncDisposable
{
    private readonly string _instanceId = gateway?.InstanceId ?? ContainerIdentity.ProcessInstanceId;

    public UpstreamTransportKind Kind => UpstreamTransportKind.Stdio;

    /// <summary>
    /// Der Aufräumlauf beim Herunterfahren. Der Konnektor ist ein Singleton im Wirt; wird der
    /// beendet, räumt dieser Aufruf jeden Container ab, den <b>diese</b> Instanz gestartet hat.
    /// <para>
    /// <b>Warum hier und nicht nur je Verbindung:</b> Eine einzelne Verbindung räumt beim
    /// <c>DisposeAsync</c> ihren eigenen Container ab. Was das nicht abdeckt, ist der Fall, in dem
    /// eine Verbindung gar nicht mehr zum Aufräumen kommt — ein gescheiterter Abbau, ein Container,
    /// dessen Verbindung schon weg war. Der Lauf über das Instanz-Etikett fängt genau diesen Rest.
    /// </para>
    /// <para>
    /// Nach dem Instanz-Etikett, nicht nach dem Besitz-Etikett: Zwei Gateways am selben Daemon sind
    /// ein realer Betriebsfall.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
        => await ContainerLifecycle.SweepAllRuntimesAsync(_instanceId).ConfigureAwait(false);

    public async Task<IUpstreamConnection> ConnectAsync(
        ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Stdio
            ?? throw new ArgumentException(
                $"Config '{config.Slug}' hat keine Stdio-Optionen.", nameof(config));

        ProcessHygiene.EnsureInitialized();

        return options.Isolation is { Mode: IsolationMode.Container } isolation
            ? await ConnectInContainerAsync(id, config, options, isolation, ct).ConfigureAwait(false)
            : await ConnectOnHostAsync(id, config, options, ct).ConfigureAwait(false);
    }

    private static async Task<IUpstreamConnection> ConnectOnHostAsync(
        ServerId id, UpstreamServerConfig config, StdioTransportOptions options, CancellationToken ct)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Slug,
            Command = options.Command,
            Arguments = [.. options.Arguments],
            WorkingDirectory = options.WorkingDirectory,
            // Beides gehört zusammen: Ohne `InheritEnvironmentVariables = false` ERGÄNZT das SDK
            // die geerbte Umgebung nur, statt sie zu ersetzen — die Allowlist allein wäre dann
            // wirkungslos, weil alles Geerbte zusätzlich stehen bliebe. Die Vorgabe des SDK ist
            // `true`.
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioProcessEnvironment.Build(options.EnvironmentVariables),
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        return new SdkUpstreamConnection(id, client);
    }

    private async Task<IUpstreamConnection> ConnectInContainerAsync(
        ServerId id,
        UpstreamServerConfig config,
        StdioTransportOptions options,
        IsolationOptions isolation,
        CancellationToken ct)
    {
        // Zuerst fragen, ob die Runtime die Policy überhaupt durchsetzen kann — nicht bloß, ob sie
        // antwortet. Fällt das durch, kommt der Upstream nicht hoch; ein Ausweichen auf den Host
        // wäre eine stille Herabstufung genau der Eigenschaft, wegen der jemand den Container
        // gewählt hat.
        if (await ContainerLaunchPolicy.ProbeAsync(isolation, ct).ConfigureAwait(false) is { } problem)
        {
            throw new InvalidOperationException(
                ContainerLaunchPolicy.RefusalMessage(config.Slug, problem));
        }

        var identity = ContainerIdentity.ForUpstream(config.Slug, _instanceId);
        var environment = StdioProcessEnvironment.Build(options.EnvironmentVariables);

        // Ein stdio-Upstream hat keine Mount-Allowlisten: Sein Programm liegt im Image, und ein
        // Arbeitsverzeichnis des Wirts hätte im Container keine Entsprechung. Deshalb gar kein
        // Mount — nicht "vorsichtshalber der Ordner des Gateways".
        var arguments = new List<string>(ContainerLaunchPolicy.BuildRunArguments(
            new ContainerLaunchRequest(
                isolation,
                identity,
                ContainerLifetime.Session,
                WorkingDirectory: options.WorkingDirectory,
                // Nur die Namen: Der Wert steht in der Umgebung des Runtime-Prozesses, nie in
                // seiner Kommandozeile.
                EnvironmentNames: [.. (options.EnvironmentVariables ?? new Dictionary<string, string>()).Keys])))
        {
            // Hinter dem Image folgt das Programm — dieselbe literale Übergabe wie im Host-Modus,
            // nie über eine Shell.
            options.Command,
        };
        arguments.AddRange(options.Arguments);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Slug,
            Command = isolation.Runtime,
            Arguments = arguments,
            // Das Arbeitsverzeichnis gilt IM Container und wurde dort über `--workdir` gesetzt; der
            // Runtime-Client selbst startet neutral.
            WorkingDirectory = null,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
        });

        try
        {
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct)
                .ConfigureAwait(false);
            return new ContainerBackedUpstreamConnection(
                new SdkUpstreamConnection(id, client), isolation, identity);
        }
        catch
        {
            // Der Aufbau ist gescheitert — der Container kann trotzdem schon laufen. Ohne diesen
            // Schritt bliebe er stehen, und niemand wüsste, wem er gehört.
            await ContainerLifecycle.StopAsync(
                isolation.Runtime, identity.Name, isolation.StopTimeoutSeconds).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Eine stdio-Verbindung, deren Gegenstelle in einem Container läuft. Der Zusatz gegenüber der
/// nackten SDK-Verbindung sind Gesundheitsprüfung und Abräumen — beides Dinge, die im Host-Modus
/// das Betriebssystem erledigt.
/// <para>
/// <b>Warum das Abräumen hier nicht optional ist:</b> <c>docker run</c> ist ein Client zum Daemon,
/// kein Elternprozess. Beendet sich der Client, läuft der Container weiter — der Prozessbaum-Kill
/// aus <c>ProcessHygiene</c> hat hier kein Gegenstück.
/// </para>
/// </summary>
internal sealed class ContainerBackedUpstreamConnection(
    IUpstreamConnection inner, IsolationOptions isolation, ContainerIdentity identity)
    : IUpstreamConnection
{
    public ServerId Id => inner.Id;

    public bool PushesCatalogChanges => inner.PushesCatalogChanges;

    /// <summary>Der Name des Containers — die Grundlage jedes Aufräumens.</summary>
    public string ContainerName => identity.Name;

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add => inner.NotificationReceived += value;
        remove => inner.NotificationReceived -= value;
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct) => inner.DiscoverAsync(ct);

    public Task<System.Text.Json.JsonElement> CallToolAsync(
        string toolName, System.Text.Json.JsonElement args, CancellationToken ct)
        => inner.CallToolAsync(toolName, args, ct);

    public Task<System.Text.Json.JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => inner.ReadResourceAsync(uri, ct);

    public Task<System.Text.Json.JsonElement> GetPromptAsync(
        string promptName, System.Text.Json.JsonElement? args, CancellationToken ct)
        => inner.GetPromptAsync(promptName, args, ct);

    /// <summary>
    /// Erst der Container, dann das Protokoll. Ein Upstream, dessen Container weg ist, ist tot —
    /// auch wenn die Rohre noch offen aussehen und das <c>ping</c> in einen Puffer läuft.
    /// </summary>
    public async Task PingAsync(CancellationToken ct)
    {
        if (!await ContainerLifecycle.IsRunningAsync(isolation.Runtime, identity.Name, ct)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Der Container '{identity.Name}' des Upstreams '{identity.Slug}' läuft nicht mehr.");
        }

        await inner.PingAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drain, Stop, Cleanup: Erst wird die Verbindung geschlossen (stdin schließt, das Programm
    /// bekommt sein EOF und darf ordentlich enden), dann bekommt der Container die Gnadenfrist und
    /// danach den harten Schnitt.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await ContainerLifecycle.StopAsync(
                isolation.Runtime, identity.Name, isolation.StopTimeoutSeconds).ConfigureAwait(false);
        }
    }
}
