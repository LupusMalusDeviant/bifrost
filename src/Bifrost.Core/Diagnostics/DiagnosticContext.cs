using System.Collections;
using System.Globalization;

namespace Bifrost.Core.Diagnostics;

/// <summary>
/// Alles, was die Checks vorfinden: die Umgebung, die Adressen, auf denen gelauscht wird, und die
/// Sonden zur Außenwelt.
/// <para>
/// <b>Der Kontext trägt Konfigurationswerte — die Befunde tun es nie.</b> Die Umgebung enthält
/// Passwörter (<c>BIFROST_KEYRING_CERT_PASSWORD</c>) und Verbindungszeichenfolgen. Ein Check liest
/// sie, um zu entscheiden, und schreibt danach <b>Namen, Pfade, Zahlen und Ja/Nein</b> in seine
/// Details. Das ist eine Positivliste; die Mustererkennung in <see cref="DiagnosticRedaction"/> ist
/// die zweite Linie dahinter, nicht die erste.
/// </para>
/// </summary>
public sealed record DiagnosticContext
{
    private readonly IReadOnlyList<string>? _listenAddresses;

    /// <summary>Die Umgebung, wie der Prozess sie sieht. Namen sollten ohne Rücksicht auf Groß/Klein vergleichen.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>
    /// <c>Development</c>, <c>Production</c>, … Entscheidet mit, ob das Sitzungs-Cookie 'Secure'
    /// trägt — im Development tut es das nicht, und dann ist BFR-NET-0002 gegenstandslos.
    /// </summary>
    public string? HostEnvironmentName { get; init; }

    /// <summary>
    /// Adressen, auf denen gelauscht wird. Vorgabe ist die Auswertung von <c>ASPNETCORE_URLS</c>;
    /// der laufende Server kennt die tatsächlichen und reicht sie durch (WP2.7).
    /// </summary>
    public IReadOnlyList<string> ListenAddresses
    {
        get => _listenAddresses ?? ParseUrls(Value("ASPNETCORE_URLS"));
        init => _listenAddresses = value;
    }

    /// <summary>
    /// Läuft die Diagnose <b>im</b> Gateway-Prozess? Dann ist ein belegter Port der Normalfall und
    /// kein Befund. Aus der CLI heraus (Gateway steht) ist er einer.
    /// </summary>
    public bool GatewayRunsInThisProcess { get; init; }

    /// <summary>
    /// Verlangt mindestens ein Upstream Container-Isolation? <c>null</c> heißt „unbekannt" — dann
    /// prüft BFR-RT-0001 zwar, wertet ein Fehlen der Runtime aber nur als <c>Skipped</c>. Erst der
    /// Server weiß es sicher (WP2.7). Eine Warnung, die beim korrekten Aufbau mitläuft, wird
    /// ignoriert; deshalb wird hier nicht auf Verdacht gewarnt.
    /// </summary>
    public bool? ContainerIsolationConfigured { get; init; }

    public IFileProbe Files { get; init; } = SystemFileProbe.Instance;

    public IPortProbe Ports { get; init; } = SystemPortProbe.Instance;

    public IProcessProbe Processes { get; init; } = SystemProcessProbe.Instance;

    /// <summary><c>null</c> = nicht verdrahtet; die BFR-DB-Checks melden dann Skipped.</summary>
    public IDatabaseDiagnosticProbe? Database { get; init; }

    /// <summary><c>null</c> = nicht verdrahtet; BFR-UP-0001 meldet dann Skipped.</summary>
    public IUpstreamDiagnosticProbe? Upstreams { get; init; }

    /// <summary>
    /// Der ermittelte Zustand der Ausführungs-Policy (ADR-0025, Codes BFR-POL). <c>null</c> heißt
    /// „nicht verdrahtet"; die BFR-POL-Checks melden dann Skipped.
    /// <para>
    /// Ausdrücklich der <b>ermittelte</b> Zustand und nicht die Umgebungsvariable: Die Instanz kann
    /// ihren Wert übernommen haben, und genau dieser Unterschied ist der Befund.
    /// </para>
    /// </summary>
    public Execution.HostExecutionState? HostExecution { get; init; }

    /// <summary>Ein Umgebungswert oder <c>null</c>, wenn er fehlt oder leer ist.</summary>
    public string? Value(string name)
        => Environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    public string DataDirectory => Value("BIFROST_DATA_DIR") ?? "data";

    public string KeyRingDirectory => Path.Combine(DataDirectory, "keys");

    public string DatabaseProvider => Value("BIFROST_DB_PROVIDER") ?? "sqlite";

    public bool HasDatabaseConnectionString => Value("BIFROST_DB_CONNECTION") is not null;

    public string? KeyRingCertificatePath => Value("BIFROST_KEYRING_CERT_PATH");

    public string? PublicBaseUrl => Value("BIFROST_PUBLIC_BASE_URL");

    public string? TrustedProxies => Value("BIFROST_TRUSTED_PROXIES");

    public string? OAuthIssuer => Value("BIFROST_OAUTH_ISSUER");

    public string? WasiHostPath => Value("BIFROST_WASI_HOST");

    /// <summary>
    /// Name der zu prüfenden Container-Runtime. Bewusst <b>keine</b> Umgebungsvariable: Die Runtime
    /// steht je Upstream in der Konfiguration (<c>CliIsolationOptions.Runtime</c>, Vorgabe
    /// <c>docker</c>). Der Server reicht den tatsächlich verlangten Namen durch (WP2.7); ohne ihn
    /// wird die Vorgabe geprüft, damit hier keine Einstellung erfunden wird, die es nicht gibt.
    /// </summary>
    public string ContainerRuntimeName { get; init; } = "docker";

    public bool IsDevelopment
        => string.Equals(HostEnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kontext aus dem echten Prozessumfeld. Die Umgebung wird <b>kopiert</b>, nicht laufend
    /// abgefragt: Ein Bericht soll denselben Zustand beschreiben, den sein erster Check gesehen hat.
    /// </summary>
    public static DiagnosticContext FromProcessEnvironment(
        string? hostEnvironmentName = null,
        IReadOnlyList<string>? listenAddresses = null,
        bool gatewayRunsInThisProcess = false)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                snapshot[key] = value;
            }
        }

        var context = new DiagnosticContext
        {
            Environment = snapshot,
            HostEnvironmentName = hostEnvironmentName
                ?? System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            GatewayRunsInThisProcess = gatewayRunsInThisProcess,
        };

        return listenAddresses is null ? context : context with { ListenAddresses = listenAddresses };
    }

    /// <summary>
    /// Die Ports aus den Lauschadressen. <c>http://+:8080</c>, <c>http://0.0.0.0:8080</c> und
    /// <c>https://gateway.example</c> ergeben alle eine Zahl; was keine hergibt, fällt weg.
    /// </summary>
    public IReadOnlyList<int> ListenPorts()
    {
        var ports = new List<int>();
        foreach (var address in ListenAddresses)
        {
            var port = ParsePort(address);
            if (port is > 0 && !ports.Contains(port.Value))
            {
                ports.Add(port.Value);
            }
        }

        return ports;
    }

    /// <summary>Lauscht der Gateway ausschließlich auf <c>http://</c>?</summary>
    public bool ListensOnlyOnPlainHttp()
        => ListenAddresses.Count > 0
            && ListenAddresses.All(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sind alle Adressen Loopback? Dann hält die Anmeldung auch über Klartext-HTTP — Browser
    /// behandeln <c>localhost</c> als sicheren Ursprung.
    /// </summary>
    public bool ListensOnlyOnLoopback()
        => ListenAddresses.Count > 0 && ListenAddresses.All(IsLoopback);

    private static bool IsLoopback(string address)
    {
        var host = AuthorityOf(address, out _);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("[::1]", StringComparison.Ordinal);
    }

    /// <summary>Trennt Host und Port einer Adresse, ohne an <c>+</c>, <c>*</c> oder IPv6 zu scheitern.</summary>
    private static string AuthorityOf(string address, out string? portText)
    {
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        var rest = schemeEnd >= 0 ? address[(schemeEnd + 3)..] : address;
        var pathStart = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = pathStart >= 0 ? rest[..pathStart] : rest;

        var colon = authority.LastIndexOf(':');
        if (colon > 0 && colon < authority.Length - 1)
        {
            var candidate = authority[(colon + 1)..];
            if (candidate.All(char.IsAsciiDigit))
            {
                portText = candidate;
                return authority[..colon];
            }
        }

        portText = null;
        return authority;
    }

    private static int? ParsePort(string address)
    {
        _ = AuthorityOf(address, out var portText);
        if (portText is not null
            && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return port;
        }

        // Ohne Portangabe gilt der Standardport des Schemas.
        return address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 443
            : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 80
            : null;
    }

    private static IReadOnlyList<string> ParseUrls(string? urls)
        => string.IsNullOrWhiteSpace(urls)
            ? []
            : [.. urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
