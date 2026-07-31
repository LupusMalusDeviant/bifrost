using Bifrost.Abstractions;
using ModelContextProtocol.Client;

namespace Bifrost.Upstream;

/// <summary>
/// Startet lokale MCP-Server als Kindprozesse via stdio (ADR-0005).
/// <para>
/// <b>Keine Sandbox</b> — das ist die dokumentierte Grenze dieses Transports. Was hier geht, ist,
/// die Umgebung des Kindprozesses klein zu halten: Er sieht seit dem 2026-07-28 nur noch eine kurze
/// Allowlist statt der vollständigen Umgebung des Gateways
/// (<see cref="StdioProcessEnvironment"/>). Vorher standen dort Datenbankpasswort und
/// Key-Ring-Passwort für jeden gestarteten Server lesbar.
/// </para>
/// </summary>
public sealed class StdioUpstreamConnector : IUpstreamConnector
{
    public UpstreamTransportKind Kind => UpstreamTransportKind.Stdio;

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Stdio
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Stdio-Optionen.", nameof(config));

        ProcessHygiene.EnsureInitialized();

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
}
