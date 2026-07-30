using System.Text.Json;
using McpMcp.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMcp.Server;

/// <summary>
/// Holt eine ausstehende Freigabe <b>im Moment des Aufrufs</b> beim Menschen ein, wenn der
/// anfragende Client danach fragen kann (MCP-Elicitation).
/// <para>
/// <b>Warum das hier steht und nicht im Invoker:</b> Der Invoker kennt kein MCP — er stellt fest,
/// dass eine Freigabe fehlt, und legt die Anfrage in die Warteschlange (ADR-0012). Ob der Aufrufer
/// gerade gefragt werden <em>kann</em>, ist eine Eigenschaft der Protokoll-Sitzung und gehört
/// deshalb in die Protokollschicht.
/// </para>
/// <para>
/// <b>Warum überhaupt:</b> Die Warteschlange verlangt einen Wechsel in Oberfläche oder CLI. Bei
/// einem Werkzeug, das man mehrmals am Tag braucht, führt das dazu, dass jemand die Freigabepflicht
/// abschaltet — und dann schützt sie gar nicht mehr. Eine Rückfrage im laufenden Gespräch kostet
/// einen Klick.
/// </para>
/// <para>
/// <b>Was sie NICHT ist:</b> eine Selbstfreigabe des Agenten. Die Frage geht an den Client, die
/// Antwort kommt vom Menschen davor. Der Agent hält keinen Freigabe-Schlüssel und kann die Antwort
/// nicht erfinden — er sieht nur, was danach passiert.
/// </para>
/// </summary>
internal static class ApprovalElicitation
{
    /// <summary>Feldname im Bestaetigungsformular.</summary>
    private const string ApproveField = "approve";

    /// <summary>
    /// Zwei Ausgaenge, nicht drei. Es gibt kein „abgelehnt" mehr.
    /// <para>
    /// Eine Ablehnung waere nur dann etwas wert, wenn man ihr ansehen koennte, dass ein Mensch sie
    /// ausgesprochen hat — und genau das kann man einem Client nicht ansehen. Zweimal wurde hier
    /// eine Ablehnung verbucht, die niemand geaeussert hatte. Beim dritten Mal faellt die
    /// Unterscheidung weg.
    /// </para>
    /// </summary>
    internal enum Outcome
    {
        /// <summary>
        /// Keine ausdrueckliche Zustimmung — aus welchem Grund auch immer. Die Warteschlange
        /// bleibt der Weg, ein Mensch kann dort noch entscheiden.
        /// </summary>
        NotPossible,

        /// <summary>Ein Mensch hat zugestimmt: ausdruecklich, mit gesetztem Haekchen.</summary>
        Approved,
    }

    /// <summary>
    /// Fragt nach, wenn möglich. Bei Zustimmung ist die Freigabe im Store bereits erteilt — der
    /// Aufrufer kann den Call unmittelbar wiederholen.
    /// </summary>
    public static async Task<Outcome> TryObtainAsync(
        IServiceProvider services,
        McpServer? server,
        Guid approvalId,
        NamespacedToolName tool,
        CancellationToken ct)
    {
        // Jede Absage bekommt eine Spur. Ein stiller Rueckfall auf die Warteschlange sieht von
        // aussen aus wie "der Client kann es nicht" — auch dann, wenn in Wahrheit etwas kaputt ist.
        // Genau so ist der erste Versuch untergegangen: Ein ungueltiger Modus warf, der catch
        // schluckte, und im Log stand nichts.
        var log = services.GetService<ILoggerFactory>()?.CreateLogger("McpMcp.Server.ApprovalElicitation");

        if (server?.ClientCapabilities?.Elicitation is null)
        {
            Skipped(log, tool, "der Client meldet keine Elicitation-Faehigkeit");
            return Outcome.NotPossible;
        }

        var store = services.GetService<IApprovalStore>();
        if (store is null)
        {
            Skipped(log, tool, "kein Approval-Store eingebunden");
            return Outcome.NotPossible;
        }

        // Die REDIGIERTEN Argumente aus der Warteschlange, nicht die des Aufrufs: Das Popup darf
        // nicht mehr zeigen als die Oberfläche, sonst wäre die Maskierung eine Frage des Weges.
        var pending = (await store.ListAsync(ApprovalState.Pending, ct).ConfigureAwait(false))
            .FirstOrDefault(r => r.Id == approvalId);
        if (pending is null)
        {
            Skipped(log, tool, $"Anfrage {approvalId} steht nicht (mehr) auf wartend");
            return Outcome.NotPossible;
        }

        ElicitResult answer;
        try
        {
            answer = await server.ElicitAsync(
                new ElicitRequestParams
                {
                    // Ein Formular mit EINEM Ja/Nein-Feld. Der erste Versuch schickte ein leeres
                    // Schema — daran hatte der Client nichts anzuzeigen und lehnte von sich aus ab,
                    // ohne dass ein Mensch etwas sah. Ein Dialog, den niemand sieht, ist keine
                    // Freigabe.
                    Mode = "form",
                    Message = Describe(pending),
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties =
                        {
                            [ApproveField] = new ElicitRequestParams.BooleanSchema
                            {
                                Title = $"'{pending.Tool.Value}' einmalig ausfuehren?",
                                Description = "Ja lässt genau diesen einen Aufruf durch.",
                            },
                        },
                        Required = [ApproveField],
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Ein Client, der die Fähigkeit meldet, sie aber nicht bedient, darf den Aufruf nicht
            // verlieren — er landet dann wie bisher in der Warteschlange. Der Grund gehoert aber
            // ins Log, sonst ist ein Fehler von einem fehlenden Feature nicht zu unterscheiden.
            Skipped(log, tool, $"Rueckfrage gescheitert: {exception.Message}");
            return Outcome.NotPossible;
        }

        // NUR ein ausdrueckliches Ja zaehlt. Alles andere — auch ein glasklares 'decline' — fuehrt
        // zurueck in die Warteschlange, wo ein Mensch noch entscheiden kann.
        //
        // Das war zweimal anders gedacht und zweimal falsch:
        //   1. Zuerst galt jede Nicht-Zustimmung als Ablehnung. Der Client lehnte selbst ab, weil
        //      das Formular leer war, und im Audit stand "abgelehnt".
        //   2. Danach galten 'decline' und 'accept'-ohne-Haekchen als menschliches Nein, nur
        //      'cancel' nicht. Am 2026-07-30 hat derselbe Client dann 'decline' geschickt, ohne
        //      dass ein Formular je zu sehen war — nachgewiesen: Der Mensch bestaetigte in diesem
        //      Aufruf ausschliesslich die Berechtigungsfrage seines Clients.
        //
        // Die Lehre steckt in der Wiederholung: Man kann einem Client nicht ansehen, ob hinter
        // seiner Antwort ein Mensch stand. Nur die Zustimmung traegt ein Merkmal, das kein
        // Automatismus nebenbei erzeugt — ein eigens gesetztes Haekchen. Der Preis ist klein und
        // faellt auf die richtige Seite: Ein ECHTES Nein wird hier nicht mehr vermerkt, sondern
        // bleibt wartend. Ein nicht verbuchtes Nein kostet einen Klick in der Oberflaeche; ein
        // erfundenes Ja kostet die Freigabepflicht.
        if (string.Equals(answer.Action, "accept", StringComparison.Ordinal)
            && answer.Content?.TryGetValue(ApproveField, out var value) == true
            && value.ValueKind is JsonValueKind.True)
        {
            await store.DecideAsync(approvalId, approved: true, ct).ConfigureAwait(false);
            return Outcome.Approved;
        }

        Skipped(log, pending.Tool, $"Antwort '{answer.Action}' ohne ausdrueckliche Zustimmung");
        return Outcome.NotPossible;
    }

#pragma warning disable CA1848 // Selten: nur bei freigabepflichtigen Aufrufen.
    private static void Skipped(ILogger? log, NamespacedToolName tool, string reason)
    {
        if (log?.IsEnabled(LogLevel.Information) == true)
        {
            log.LogInformation(
                "Keine Rueckfrage fuer {Tool} — {Reason}. Der Aufruf bleibt in der Warteschlange.",
                tool.Value, reason);
        }
    }
#pragma warning restore CA1848

    /// <summary>
    /// Der Text im Popup. Er muss allein tragen: Wer ihn liest, sieht sonst nichts vom Vorgang.
    /// </summary>
    private static string Describe(ApprovalRequest pending)
    {
        var arguments = pending.RedactedArguments is { } redacted
            ? Shorten(redacted.GetRawText())
            : "(keine)";

        return $"Freigabe nötig für '{pending.Tool.Value}'.{Environment.NewLine}"
            + $"Aufrufer: {pending.CallerDescription}{Environment.NewLine}"
            + $"Argumente (maskiert): {arguments}{Environment.NewLine}{Environment.NewLine}"
            + "Zustimmen lässt genau diesen einen Aufruf durch.";
    }

    /// <summary>
    /// Ein Popup mit zehn Kilobyte JSON liest niemand — und was niemand liest, ist keine Freigabe.
    /// </summary>
    private static string Shorten(string text)
        => text.Length <= 500 ? text : text[..500] + $"… (+{text.Length - 500} Zeichen)";
}
