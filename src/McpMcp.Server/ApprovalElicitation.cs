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
    /// <summary>Antwort des Clients: zugestimmt, abgelehnt, oder gar nicht erst gefragt.</summary>
    internal enum Outcome
    {
        /// <summary>Der Client kann nicht fragen — die Warteschlange bleibt der Weg.</summary>
        NotPossible,

        /// <summary>Ein Mensch hat zugestimmt.</summary>
        Approved,

        /// <summary>Ein Mensch hat abgelehnt.</summary>
        Declined,
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
                    // "form" mit LEEREM Schema ist die Bestaetigungsfrage: Es wird nichts erhoben,
                    // die Antwort steckt allein in der Aktion (accept/decline/cancel). Ein erster
                    // Versuch mit Mode = "confirmation" warf eine ArgumentException — das Protokoll
                    // kennt nur "form" und "url".
                    Mode = "form",
                    Message = Describe(tool, pending),
                    RequestedSchema = new ElicitRequestParams.RequestSchema(),
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

        // Alles außer ausdrücklicher Zustimmung gilt als Nein. "cancel" ist der Vorgabewert des
        // Protokolls, wenn ein Client nichts setzt — daraus ein Ja zu machen wäre die gefährlichste
        // mögliche Auslegung.
        var approved = string.Equals(answer.Action, "accept", StringComparison.Ordinal);
        await store.DecideAsync(approvalId, approved, ct).ConfigureAwait(false);
        return approved ? Outcome.Approved : Outcome.Declined;
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
    private static string Describe(NamespacedToolName tool, ApprovalRequest pending)
    {
        var arguments = pending.RedactedArguments is { } redacted
            ? Shorten(redacted.GetRawText())
            : "(keine)";

        return $"Freigabe nötig für '{tool.Value}'.{Environment.NewLine}"
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
