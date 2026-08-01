using Bifrost.Abstractions;

namespace Bifrost.Core.Diagnostics.Upstreams;

/// <summary>Die Einordnung einer Fehlermeldung: welche Stufe, und was der Betreiber tun soll.</summary>
/// <param name="Confident">
/// <c>false</c> heisst: kein Muster hat gegriffen, die Einordnung ist der Rückfall. Der Bericht sagt
/// das dann auch — eine geratene Zuordnung, die sich sicher gibt, schickt jemanden in die falsche
/// Richtung, und das ist schlimmer als eine ehrliche Unsicherheit.
/// </param>
public sealed record UpstreamFailureVerdict(
    UpstreamStage Stage,
    string Remediation,
    bool Confident = true);

/// <summary>
/// Ordnet die Fehlermeldung eines Verbindungsversuchs einer Stufe zu und hängt eine konkrete
/// Abhilfe daran (WP4.6, Punkt 5).
/// <para>
/// <b>Warum überhaupt eine Einordnung und keine Instrumentierung im Connector:</b> Anmeldung,
/// Handshake und Discovery passieren <i>innerhalb</i> von <c>ConnectAsync</c>/<c>DiscoverAsync</c>.
/// Sie einzeln zu messen hiesse, die Connectoren nachzubauen — und ein nachgebauter Connector ist
/// eine zweite Wahrheit darüber, ob eine Konfiguration funktioniert. Der Verbindungstest läuft
/// deshalb den <b>echten</b> Weg, und was er zurückbringt, wird hier eingeordnet. Eine Einordnung
/// kann danebenliegen; ein zweiter Connector liegt irgendwann grundsätzlich daneben.
/// </para>
/// <para>
/// Die Muster stehen in einer Tabelle und nicht in einer Kette von <c>if</c>s: Sie sind Daten, die
/// wachsen — jede neue bekannte Störung ist eine Zeile, kein Eingriff in die Logik.
/// </para>
/// </summary>
public static class UpstreamFailureCatalog
{
    private sealed record Rule(UpstreamStage Stage, string[] Markers, string Remediation);

    /// <summary>
    /// Geprüft wird in dieser Reihenfolge; die erste passende Zeile gewinnt. Spezifisches steht vor
    /// Allgemeinem — „401" vor „HTTP", Schemafehler vor Verbindungsfehlern.
    /// </summary>
    private static readonly Rule[] Rules =
    [
        // ── Stufe 5: Anmeldung ──────────────────────────────────────────────────────────────────
        new(UpstreamStage.Auth,
            ["401", "unauthorized", "nicht autorisiert", "authentication failed", "invalid_token",
             "invalid_client", "access denied"],
            "Die Gegenstelle hat die Anmeldung abgelehnt. Zugangsdaten neu hinterlegen — bei "
            + "Bearer/API-Key den Wert, bei Basic die Form 'benutzer:passwort'. Ein gespeichertes "
            + "Zugangsdatum wird beim Bearbeiten NICHT vorbefüllt; ein leer gelassenes Feld behält "
            + "den alten Wert."),
        new(UpstreamStage.Auth,
            ["403", "forbidden", "verboten", "insufficient_scope"],
            "Die Anmeldung wurde erkannt, aber nicht zugelassen. Das ist kein falsches Passwort, "
            + "sondern eine fehlende Berechtigung: die Rechte des benutzten Kontos bzw. die Scopes "
            + "des Tokens bei der Gegenstelle prüfen."),
        new(UpstreamStage.Auth,
            ["credential", "auth-art", "authkind", "access_token", "token_endpoint",
             "token-antwort", "client_secret"],
            "Die Anmeldung ist unvollständig oder die Gegenstelle liefert kein Token. Auth-Art und "
            + "Credential gehören zusammen: Wer eine Auth-Art wählt, hinterlegt auch einen Wert. "
            + "Bei OAuth zusätzlich Issuer und Client-Angaben prüfen."),

        // ── Stufe 4: Zielschutz ─────────────────────────────────────────────────────────────────
        new(UpstreamStage.TargetGuard,
            ["zeigt auf die interne adresse", "allowprivatetargets", "interne dienste"],
            "Das Ziel liegt im internen Netz (Loopback, privates Netz, Link-Local). Wenn der Dienst "
            + "wirklich dort steht, 'Ziele im internen Netz erlauben' ausdrücklich setzen — sonst "
            + "die Adresse korrigieren. Ohne diese Prüfung wäre das Gateway ein Weg zu internen "
            + "Diensten."),

        // ── Stufe 3: Runtime/DNS ────────────────────────────────────────────────────────────────
        new(UpstreamStage.Runtime,
            ["liess sich nicht auflösen", "ließ sich nicht auflösen", "no such host",
             "name or service not known", "kein solcher host", "getaddrinfo", "nodename nor servname"],
            "Der Name löst nicht auf. Schreibweise prüfen; wenn er nur intern bekannt ist, den "
            + "DNS-Server des Gateway-Containers prüfen — der Name des Hostrechners gilt im "
            + "Container nicht automatisch."),
        new(UpstreamStage.Runtime,
            ["wurde nicht gefunden", "not found: the system cannot find the file",
             "no such file or directory", "kein connector für transport", "datei nicht gefunden",
             "cannot find the path"],
            "Das zu startende Programm liegt nicht am angegebenen Pfad. Im sicheren Modus ist ein "
            + "absoluter Pfad Pflicht. Läuft das Gateway im Container, muss das Programm IM Container "
            + "liegen — ein Pfad des Hostrechners zeigt dort ins Leere."),
        new(UpstreamStage.Runtime,
            ["docker", "podman", "container-runtime", "cannot connect to the docker daemon"],
            "Die Container-Runtime antwortet nicht. Es gibt keinen stillen Rückfall auf den Host "
            + "(ADR-0018): Ohne Runtime kommt ein Upstream im Container-Modus nicht hoch. Runtime "
            + "installieren bzw. den Zugriff auf den Socket freigeben; siehe BFR-RT-0001."),

        // ── Stufe 7: Discovery ──────────────────────────────────────────────────────────────────
        new(UpstreamStage.Discovery,
            ["kein gültiges json", "ist kein json-objekt", "spec-wurzel", "'paths'", "'methods'",
             "keine importierbaren operationen", "beschreibt keine methoden", "swagger 2.0",
             "zeigt ins leere", "$ref", "operationid", "überschreitet", "rpc.discover",
             "methode ohne", "parameter ohne"],
            "Der Katalog kam an, war aber nicht verwertbar. Das ist ein Fehler der Beschreibung, "
            + "nicht der Verbindung: Das Dokument an der genannten Stelle prüfen (OpenAPI 3.x, "
            + "OpenRPC bzw. MCP-Toolschema). Die Meldung nennt die Fundstelle."),

        // ── Stufe 6: Handshake ──────────────────────────────────────────────────────────────────
        new(UpstreamStage.Handshake,
            ["zeitüberschreitung", "timeout", "timed out", "abgelaufen"],
            "Die Gegenstelle hat innerhalb der Frist nicht geantwortet. Erreichbarkeit von Hand "
            + "prüfen (Port, Firewall, Proxy). Bei einem lokal gestarteten Programm: Es muss auf "
            + "stdio sprechen und darf beim Start nichts auf stdout schreiben, was kein "
            + "JSON-RPC ist."),
        new(UpstreamStage.Handshake,
            ["connection refused", "verbindung verweigert", "actively refused", "econnrefused",
             "connection reset", "host unreachable", "netzwerk"],
            "Der Name löst auf, aber auf dem Port nimmt niemand an. Port und Pfad der Adresse "
            + "prüfen; bei einem Dienst im eigenen Netz zusätzlich, ob das Gateway ihn erreichen "
            + "darf."),
        new(UpstreamStage.Handshake,
            ["ssl", "tls", "certificate", "zertifikat", "trust"],
            "Der Transport kam wegen der TLS-Prüfung nicht zustande. Zertifikatskette und "
            + "Gültigkeit des Ziels prüfen. Eine abgeschaltete Prüfung ist keine Lösung — sie "
            + "macht jede Verbindung zum Ziel angreifbar."),
        new(UpstreamStage.Handshake,
            ["protokoll", "protocol", "handshake", "initialize", "wasi-host", "sse",
             "unexpected end", "weiterleitung"],
            "Der Transport steht, aber das Protokoll passt nicht. Bei MCP über HTTP prüfen, ob die "
            + "Gegenstelle Streamable HTTP spricht (der Rückfall auf HTTP+SSE ist abschaltbar); bei "
            + "WASI, ob Host-Binary und Gateway aus derselben Auslieferung stammen."),
        new(UpstreamStage.Handshake,
            ["404", "405", "500", "502", "503", "504", "http"],
            "Die Gegenstelle antwortet, aber nicht als MCP-/Spec-Endpunkt. Die Adresse zeigt "
            + "erfahrungsgemäss auf die Startseite statt auf den Endpunkt — den vollständigen Pfad "
            + "prüfen (etwa '/mcp' statt '/')."),
    ];

    /// <summary>
    /// Die Einordnung. <paramref name="message"/> ist Fremdtext und wird nur <b>gelesen</b> — in den
    /// Bericht geht er über den Weg, der durch die Redaktion läuft.
    /// </summary>
    public static UpstreamFailureVerdict Classify(string? message, UpstreamTransportKind kind)
    {
        var text = message ?? string.Empty;
        foreach (var rule in Rules)
        {
            if (rule.Markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return new UpstreamFailureVerdict(rule.Stage, rule.Remediation);
            }
        }

        // Rückfall auf die FRÜHESTE der drei Stufen, die im echten Versuch stecken. Die späteren
        // als gescheitert zu melden hiesse zu behaupten, die früheren seien durchgelaufen — und
        // genau diese Behauptung ist unbelegt.
        return new UpstreamFailureVerdict(
            UpstreamStage.Handshake,
            $"Die Meldung liess sich keiner bekannten Störung zuordnen. Sie steht unverändert oben; "
            + $"der Transport ist {kind}. Für den vollen Verlauf hilft der Serverlog zur Request-Id "
            + "dieses Laufs.",
            Confident: false);
    }
}
