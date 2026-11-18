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

            // Abgelesen, SOLANGE die Verbindung noch steht. Danach ist die Angabe weg — der Test
            // raeumt seine Verbindung ab, und genau daran scheiterte die Auskunft bisher. Es wird
            // nichts nachgefragt: Die Werte stehen seit dem Handshake im Client.
            return new UpstreamTestResult(true, inventory.Tools.Count, null, connection.Protocol);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new UpstreamTestResult(false, 0, $"Zeitüberschreitung nach {TestTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex)
        {
            // Derselbe Weg wie Status, Audit und Log (M3): ohne Antwortkörper der Gegenstelle,
            // ohne Geheimwerte, eine Zeile.
            return new UpstreamTestResult(false, 0, UpstreamFailure.Describe(ex, config));
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
    /// <para>
    /// <b>Auch Anfangsstücke werden maskiert.</b> Ein Upstream, der einen abgelehnten Schlüssel
    /// „zur Hilfe" gekürzt zitiert (<c>invalid key sk-live-ab12…</c>), verrät damit genug, um ihn
    /// zuzuordnen — und die Kürzung gleicht dem ganzen Wert nicht mehr. Maskiert wird deshalb jedes
    /// Vorkommen der ersten <see cref="MinimumPrefixLength"/> oder mehr Zeichen eines Geheimwerts.
    /// Das trifft gelegentlich harmlosen Text; ein Stern zu viel in einer Fehlermeldung ist der
    /// billigere Irrtum.
    /// </para>
    /// </summary>
    [NoHostExecution("Textersetzung auf einer Fehlermeldung; berührt weder Start noch Persistenz.")]
    public static string ScrubSecrets(string message, UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        // Längste zuerst: Sonst zerlegt ein kurzer Wert einen längeren, der ihn enthält, und der
        // Rest des längeren bleibt stehen.
        var candidates = Secrets(config)
            .Where(value => !string.IsNullOrEmpty(value))
            .SelectMany(Variants)
            .Concat(SwitchSecrets(config))
            .Distinct()
            .OrderByDescending(candidate => candidate.Value.Length)
            .ToList();

        foreach (var (value, _) in candidates)
        {
            // Sehr kurze Werte würden halbe Fehlermeldungen zerschießen und sind ohnehin keine Secrets.
            if (value.Length >= 4)
            {
                message = message.Replace(value, "***", StringComparison.Ordinal);
            }
        }

        foreach (var (value, prefixable) in candidates)
        {
            if (prefixable && value.Length > MinimumPrefixLength)
            {
                message = MaskPrefixes(message, value);
            }
        }

        return message;
    }

    /// <summary>Ab so vielen übereinstimmenden Anfangszeichen gilt ein Textstück als Teil eines Geheimwerts.</summary>
    public const int MinimumPrefixLength = 6;

    /// <summary>
    /// Ein Kopfzeilenwert der Form „Bearer abc…" ist zweierlei: der ganze Wert und der Teil nach
    /// dem Schema. Das Anfangsstück wird nur am zweiten gemessen — sonst maskierte „Bearer" als
    /// Anfang von „Bearer abc…" das Wort in jeder Meldung, die es benutzt.
    /// </summary>
    private static IEnumerable<(string Value, bool Prefixable)> Variants(string secret)
    {
        var space = secret.IndexOf(' ', StringComparison.Ordinal);
        var hasScheme = space is > 0 and <= 10
            && space < secret.Length - 1
            && secret.AsSpan(0, space).IndexOfAnyExceptInRange('A', 'z') < 0;
        if (!hasScheme)
        {
            yield return (secret, true);
            yield break;
        }

        yield return (secret, false);
        yield return (secret[(space + 1)..].TrimStart(), true);
    }

    private static string MaskPrefixes(string message, string secret)
    {
        var probe = secret[..MinimumPrefixLength];
        System.Text.StringBuilder? masked = null;
        var position = 0;
        while (position < message.Length)
        {
            var hit = message.IndexOf(probe, position, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            var length = MinimumPrefixLength;
            while (hit + length < message.Length
                && length < secret.Length
                && message[hit + length] == secret[length])
            {
                length++;
            }

            masked ??= new System.Text.StringBuilder(message.Length);
            masked.Append(message, position, hit - position).Append("***");
            position = hit + length;
        }

        if (masked is null)
        {
            return message;
        }

        return masked.Append(message, position, message.Length - position).ToString();
    }

    private static IEnumerable<string> Secrets(UpstreamServerConfig config)
    {
        if (config.OpenRpc?.Credential is { } rpcCredential)
        {
            yield return rpcCredential;
        }

        foreach (var value in config.Wasi?.Secrets?.Values ?? [])
        {
            yield return value;
        }

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

        if (config.Http?.OAuth?.ClientSecret is { } clientSecret)
        {
            yield return clientSecret;
        }
    }

    /// <summary>
    /// Geheimwerte in den Argumenten eines Stdio-Programms (M3): der Wert eines Schalters, dessen
    /// Name nach einem Geheimnis klingt — <c>--token=…</c> ebenso wie <c>--api-key …</c>. Die
    /// übrigen Argumente bleiben stehen: Paketnamen und Dateipfade machen eine Fehlermeldung erst
    /// verständlich.
    /// </summary>
    private static IEnumerable<(string Value, bool Prefixable)> SwitchSecrets(UpstreamServerConfig config)
    {
        var arguments = config.Stdio?.Arguments ?? [];
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (string.IsNullOrEmpty(argument) || !argument.StartsWith('-'))
            {
                continue;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals > 0 ? argument[..equals] : argument;
            if (!SecretSwitch(name))
            {
                continue;
            }

            if (equals > 0 && equals < argument.Length - 1)
            {
                yield return (argument[(equals + 1)..], true);
            }
            else if (equals < 0 && i + 1 < arguments.Count && !string.IsNullOrEmpty(arguments[i + 1]))
            {
                yield return (arguments[i + 1], true);
            }
        }
    }

    private static readonly string[] SecretSwitchWords =
        ["token", "key", "secret", "password", "passwd", "pwd", "auth", "credential", "bearer"];

    private static bool SecretSwitch(string name)
        => SecretSwitchWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
}
