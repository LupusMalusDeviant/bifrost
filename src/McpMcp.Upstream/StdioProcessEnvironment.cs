namespace McpMcp.Upstream;

/// <summary>
/// Die Umgebung, die ein stdio-Kindprozess sieht (ADR-0005, Nachtrag 2026-07-28).
/// <para>
/// Bis dahin erbte ein stdio-Server die <b>vollständige</b> Umgebung des Gateways — also auch
/// <c>MCPMCP_DB_CONNECTION</c> mit dem Datenbankpasswort und
/// <c>MCPMCP_KEYRING_CERT_PASSWORD</c>. Der CLI-Transport räumt seine Umgebung seit ADR-0014 auf;
/// beim ältesten Transport fehlte derselbe Schritt schlicht.
/// </para>
/// <para>
/// <b>Warum eine Allowlist und kein vollständiges Leeren:</b> Ein stdio-Server wird typischerweise
/// über <c>npx</c> oder <c>uvx</c> gestartet, und die brauchen <c>PATH</c> und <c>HOME</c>, um
/// überhaupt zu laufen. Eine leere Umgebung wäre kein Sicherheitsgewinn, sondern ein kaputter
/// Transport — und die naheliegende Reaktion darauf wäre, die Härtung wieder abzuschalten.
/// </para>
/// </summary>
public static class StdioProcessEnvironment
{
    /// <summary>
    /// Variablen, die durchgereicht werden, wenn sie gesetzt sind. Bewusst kurz und
    /// namentlich — eine Präfix-Regel („alles außer MCPMCP_*") ließe jede neue Variable
    /// stillschweigend durch.
    /// </summary>
    private static readonly string[] Inherited =
    [
        // Ohne PATH findet kein `npx`/`uvx` sein Binary.
        "PATH",
        // npm/uv legen Cache und Konfiguration unter HOME ab; ohne HOME lädt npx bei jedem Start neu.
        "HOME",
        "USERPROFILE",
        // Windows-Grundlagen: ohne die startet dort praktisch kein Prozess.
        "SystemRoot",
        "WINDIR",
        "SystemDrive",
        "PATHEXT",
        "COMSPEC",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
        // Node und Python beachten diese für Zertifikate und Proxies; ohne sie scheitern
        // Server hinter einem Firmenproxy mit einer Meldung, die niemand auf uns zurückführt.
        "NODE_EXTRA_CA_CERTS",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "no_proxy",
    ];

    /// <summary>
    /// Baut die Umgebung für einen stdio-Kindprozess: eine kurze Allowlist aus der Umgebung des
    /// Gateways, plus das, was die Upstream-Konfiguration ausdrücklich mitgibt.
    /// </summary>
    /// <remarks>
    /// Die Werte aus der Konfiguration gewinnen: Wer <c>PATH</c> je Upstream setzt, meint das so.
    /// </remarks>
    public static Dictionary<string, string?> Build(IReadOnlyDictionary<string, string>? configured)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in Inherited)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        var temp = Path.GetFullPath(Path.GetTempPath());
        environment["TEMP"] = temp;
        environment["TMP"] = temp;
        environment["TMPDIR"] = temp;

        foreach (var (key, value) in configured ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return environment;
    }

    /// <summary>
    /// Namen, die <b>nie</b> durchgereicht werden, auch wenn sie in der Umgebung stehen. Nur für
    /// Tests und Dokumentation — die Allowlist oben ist die Durchsetzung.
    /// </summary>
    public static bool IsWithheldByDefault(string name)
        => !Inherited.Contains(name, StringComparer.OrdinalIgnoreCase)
            && name is not ("TEMP" or "TMP" or "TMPDIR");
}
