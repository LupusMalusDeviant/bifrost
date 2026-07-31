using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Upstreams;

/// <summary>Transienter Verbindungstest (FR-34): connect → discover → dispose, mit kurzem Timeout, ohne Registrierung.</summary>
public sealed class UpstreamConnectionTester : IUpstreamConnectionTester
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly Dictionary<UpstreamTransportKind, IUpstreamConnector> _connectors;
    private readonly IHostExecutionPolicy _hostExecution;

    /// <param name="hostExecution">
    /// Die Ausführungs-Policy (ADR-0025 E4). „Verbindung testen" ist ein Startweg: Er führt das
    /// Programm wirklich aus. Ohne Policy wird nichts nativ gestartet.
    /// </param>
    public UpstreamConnectionTester(
        IEnumerable<IUpstreamConnector> connectors, IHostExecutionPolicy? hostExecution = null)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        _connectors = connectors.ToDictionary(c => c.Kind);
        _hostExecution = hostExecution ?? HostExecutionPolicy.Unresolved;
    }

    [HostExecutionChecked]
    public async Task<UpstreamTestResult> TestAsync(UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            UpstreamConfigValidator.Validate(config, _hostExecution);
        }
        catch (ArgumentException ex)
        {
            return new UpstreamTestResult(false, 0, ex.Message);
        }

        if (!_connectors.TryGetValue(config.Kind, out var connector))
        {
            return new UpstreamTestResult(false, 0, $"Kein Connector für Transport {config.Kind}.");
        }

        using var timeoutCts = new CancellationTokenSource(TestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await using var connection = await connector.ConnectAsync(ServerId.New(), config, linked.Token).ConfigureAwait(false);
            var inventory = await connection.DiscoverAsync(linked.Token).ConfigureAwait(false);
            return new UpstreamTestResult(true, inventory.Tools.Count, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new UpstreamTestResult(false, 0, $"Zeitüberschreitung nach {TestTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex)
        {
            return new UpstreamTestResult(false, 0, ScrubSecrets(ex.Message, config));
        }
    }

    /// <summary>
    /// Entfernt die Secrets der getesteten Konfiguration aus einer Fehlermeldung, bevor sie in der
    /// UI landet (NFR-04). Fremde Fehlertexte sind unkontrolliert — ein HTTP-Client, der die
    /// angefragte URL mitliefert, oder ein Upstream, der seinen Header zitiert, würde die
    /// Zugangsdaten sonst direkt anzeigen.
    ///
    /// Bewusst exakter Wertabgleich statt Mustererkennung: Die konkreten Geheimnisse sind hier
    /// bekannt, raten muss also niemand.
    /// </summary>
    [NoHostExecution("Textersetzung auf einer Fehlermeldung; berührt weder Start noch Persistenz.")]
    public static string ScrubSecrets(string message, UpstreamServerConfig config)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        foreach (var secret in Secrets(config))
        {
            // Sehr kurze Werte würden halbe Fehlermeldungen zerschießen und sind ohnehin keine Secrets.
            if (secret.Length >= 4)
            {
                message = message.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return message;
    }

    private static IEnumerable<string> Secrets(UpstreamServerConfig config)
    {
        foreach (var value in config.Stdio?.EnvironmentVariables?.Values ?? [])
        {
            yield return value;
        }

        foreach (var value in config.Http?.Headers?.Values ?? [])
        {
            yield return value;
        }

        foreach (var value in config.Cli?.EnvironmentVariables?.Values ?? [])
        {
            yield return value;
        }

        if (config.OpenApi?.Credential is { } credential)
        {
            yield return credential;
        }
    }
}
