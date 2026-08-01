using System.Text.Json;
using System.Text.Json.Nodes;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>
/// Das Ergebnis eines Rückwegs: ein Dokument im Format des Quellclients samt der Stellen, an denen
/// dabei etwas verloren geht.
/// <para>
/// <b>Ein leerer Befundsatz ist die Zusage „verlustfrei".</b> Alles andere wird benannt
/// (<see cref="ImportReason.Lossy"/>) — ein Export, der stillschweigend etwas weglässt, erzeugt beim
/// Empfänger eine Konfiguration, die anders ist als die hiesige, und niemand weiß worin.
/// </para>
/// </summary>
public sealed record ClientExportResult(string Document, IReadOnlyList<ImportFinding> Findings)
{
    /// <summary>Ob sich dieser Server ohne Verlust im Clientformat ausdrücken ließ.</summary>
    public bool Lossless => Findings.Count == 0;
}

/// <summary>
/// Die gemeinsamen Schreibhilfen der Rückwege ins Clientformat (WP4.2, Punkt 7).
/// <para>
/// <b>Was hier nicht passiert:</b> Es wird keine Datei angelegt und keine verschickt. Was
/// herauskommt, ist eine Zeichenkette, die ein Mensch in seine Clientkonfiguration kopiert.
/// </para>
/// </summary>
internal static class ClientExport
{
    public static JsonSerializerOptions Pretty { get; } = new() { WriteIndented = true };

    /// <summary>
    /// Die Transportarten, die keiner der vier Clients kennt. Sie zurückzuschreiben hieße, ein Feld
    /// zu erfinden, das der Zielclient ignoriert — der Server fehlte dann dort, und die Datei sähe
    /// vollständig aus.
    /// </summary>
    [NoHostExecution(
        "Liest die Transportart einer bereits vorhandenen Konfiguration und liefert einen Befund. "
        + "Startet nichts, persistiert nichts, erzeugt keine Konfiguration.")]
    public static ImportFinding? Unsupported(UpstreamServerConfig config, string client)
        => config.Kind is UpstreamTransportKind.Stdio or UpstreamTransportKind.StreamableHttp
            ? null
            : new ImportFinding(
                ImportReason.Lossy,
                ImportSeverity.Error,
                $"Ein Upstream der Art '{config.Kind}' laesst sich in einer {client}-Konfiguration "
                + "nicht ausdruecken; dieser Client kennt nur lokale Programme und HTTP-Server.",
                config.Slug,
                "Diesen Server im Ziel-Client weglassen oder ihn ueber dieses Gateway ansprechen.");

    /// <summary>Ein Objekt aus Zeichenketten, oder <c>null</c>, wenn nichts drinsteht.</summary>
    public static JsonObject? Map(IReadOnlyDictionary<string, string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var entry in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            result[entry.Key] = entry.Value;
        }

        return result;
    }

    public static JsonArray? List(IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return null;
        }

        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// Der Befund für ein Feld, das dieses Gateway führt und der Zielclient nicht kennt.
    /// </summary>
    public static ImportFinding Drops(string slug, string field, string client, string what)
        => new(
            ImportReason.Lossy,
            ImportSeverity.Warning,
            $"'{field}' gehoert nicht zum dokumentierten {client}-Schema und faellt beim Export weg: "
            + what,
            $"{slug}/{field}",
            $"Nach dem Einfuegen in {client} pruefen, ob der Server ohne diese Angabe startet.");
}
