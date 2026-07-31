using Bifrost.Abstractions;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Upstreams;

/// <summary>
/// Die Vorgaben für eine <b>neu angelegte</b> Upstream-Konfiguration (ADR-0025 E2/E5, WP3.2).
/// <para>
/// <b>Der ganze Punkt dieser Klasse ist das Wort „neu".</b> Eine Vorgabe, die beim <em>Lesen</em>
/// wirkt, ändert das Verhalten bestehender Installationen beim nächsten Start — genau die stille
/// Verhaltensänderung, die ADR-0025 E3 ablehnt. Eine Vorgabe, die beim <em>Anlegen</em> wirkt,
/// ändert nur, was ab jetzt entsteht. Deshalb wird hier und nur hier ergänzt, und deshalb ruft das
/// ausschließlich der Weg auf, auf dem jemand eine Konfiguration <em>erzeugt</em> — nicht der Weg,
/// auf dem eine vorhandene wiederhergestellt oder importiert wird.
/// </para>
/// <para>
/// <b>Ausdrücklich gesetzt schlägt Vorgabe.</b> Wer <c>Mode = Host</c> hinschreibt, meint das so;
/// ob er es darf, entscheidet die Ausführungs-Policy aus WP3.1 und nicht diese Klasse. Die zwei
/// Fragen — „was gilt, wenn nichts dasteht?" und „darf das?" — getrennt zu halten, ist der Grund,
/// warum hier nichts verboten wird.
/// </para>
/// </summary>
public static class SecureUpstreamDefaults
{
    /// <summary>
    /// Ergänzt die sicheren Vorgaben einer neu angelegten Konfiguration.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Wenn ein neu angelegter nativer Upstream weder ein Image noch einen ausdrücklichen Modus
    /// mitbringt. Fail-closed statt still: Ihn ohne Image in den Container-Modus zu heben würde
    /// beim Start scheitern, ihn im Host-Modus zu lassen wäre die stille Herabstufung, die dieses
    /// Paket abschafft.
    /// </exception>
    [NoHostExecution(
        "Ergaenzt Vorgaben und gibt eine neue Konfiguration zurueck. Startet nichts und persistiert "
        + "nichts: Der Aufrufer reicht das Ergebnis an den Supervisor weiter, und DER fragt die "
        + "Policy. Die Trennung ist Absicht — 'was gilt, wenn nichts dasteht?' und 'darf das?' sind "
        + "verschiedene Fragen.")]
    public static UpstreamServerConfig ForNewUpstream(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Stdio is { Isolation: null })
        {
            throw new ArgumentException(MissingIsolation(config.Slug, "Stdio"), nameof(config));
        }

        if (config.Cli is { Isolation: null })
        {
            throw new ArgumentException(MissingIsolation(config.Slug, "Cli"), nameof(config));
        }

        return config with
        {
            Http = config.Http is { } http
                // Der Kern der nachgeholten SSRF-Entscheidung: `null` heisst "nicht entschieden"
                // und damit heute "erlaubt". Fuer Bestand ist das richtig (ADR-0025 E3), fuer eine
                // Neuanlage falsch — hier wird der Wert deshalb GESETZT, nicht offengelassen.
                ? http with { AllowPrivateTargets = http.AllowPrivateTargets ?? false }
                : null,
        };
    }

    /// <summary>
    /// Trägt diese Konfiguration die Entscheidung über private Ziele ausdrücklich? Der Prüfgriff
    /// für den Test — und für die Diagnose, die den Altbestand benennt.
    /// </summary>
    [NoHostExecution("Liest ein Feld und liefert einen Wahrheitswert. Startet nichts.")]
    public static bool DecidesPrivateTargets(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Http is null || config.Http.AllowPrivateTargets is not null;
    }

    private static string MissingIsolation(string slug, string field)
        => $"Der neu angelegte Upstream '{slug}' startet ein fremdes Programm und braucht deshalb "
            + $"eine Isolationsangabe in {field}.Isolation (ADR-0025 E2/E5). Sichere Vorgabe ist "
            + "Mode=Container mit einem Image; wer das Programm ausdruecklich als vertrauenswuerdig "
            + $"einstuft, setzt {field}.Isolation.Mode=Host — dann entscheidet die "
            + "Ausfuehrungs-Policy dieser Instanz, ob das erlaubt ist. Weggelassen wird die Angabe "
            + "nicht: Eine fehlende Isolationsangabe hiess frueher stillschweigend 'kein Schutz'.";
}
