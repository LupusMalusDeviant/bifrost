using System.Globalization;
using System.Text.Json;
using Bifrost.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bifrost.Server;

/// <summary>
/// Holt eine ausstehende Freigabe <b>im Moment des Aufrufs</b> beim Menschen ein, wenn der
/// anfragende Client danach fragen kann.
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
/// <para>
/// <b>Zwei Wege, dasselbe Ziel.</b> Seit der Spec-Revision 2026-07-28 gibt es die Rückfrage in zwei
/// Bauformen, und welche geht, entscheidet der Betriebsmodus:
/// <list type="bullet">
///   <item><b>MRTR</b> — der Aufruf endet mit <c>input_required</c>, der Client zeigt das Formular
///   und <em>wiederholt</em> den Aufruf mit der Antwort. Der einzige Weg ohne Sitzung; das SDK
///   verweigert die alte Rückfrage im stateless Betrieb ausdrücklich
///   („Elicitation is not supported in stateless mode").</item>
///   <item><b>Elicitation</b> — der Server fragt während des laufenden Aufrufs zurück. Braucht eine
///   stehende Sitzung, bedient dafür auch Clients, die MRTR nicht können.</item>
/// </list>
/// Die Auswertung der Antwort ist für beide dieselbe (<see cref="IsExplicitYes"/>) — der Unterschied
/// liegt allein im Transport.
/// </para>
/// </summary>
internal static class ApprovalElicitation
{
    /// <summary>Feldname im Bestaetigungsformular.</summary>
    private const string ApproveField = "approve";

    /// <summary>Schluessel der Rueckfrage in der MRTR-Antwort.</summary>
    internal const string InputKey = "approval";

    /// <summary>
    /// Zweck des Schutzes fuer den <c>requestState</c>. Die Versionsnummer steht drin, damit ein
    /// spaeteres Format alte Zustaende nicht stillschweigend akzeptiert.
    /// </summary>
    private const string StatePurpose = "Bifrost.Server.ApprovalElicitation.RequestState.v1";

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

    /// <summary>Kann dieser Client ueberhaupt gefragt werden — und wenn ja, wie?</summary>
    public static bool CanAsk(McpServer? server)
        => server is not null
        && (server.IsMrtrSupported || server.ClientCapabilities?.Elicitation is not null);

    /// <summary>
    /// Baut die Rueckfrage als MRTR-Ausnahme. Wer sie wirft, beendet den Aufruf mit
    /// <c>input_required</c>; der Client beantwortet sie und wiederholt den Aufruf, und dann laeuft
    /// <see cref="TryAcceptAnswerAsync"/>.
    /// <para>
    /// <b>Der <c>requestState</c> ist geschuetzt, nicht bloss serialisiert.</b> Er geht durch die
    /// Haende des Clients und kommt von dort zurueck — die Spec sagt dazu selbst, dass ein Server
    /// ihn verschluesseln soll, wenn Vertraulichkeit oder Unversehrtheit zaehlen. Hier zaehlt die
    /// Unversehrtheit: Ohne Schutz koennte ein Aufrufer eine beliebige Freigabe-Id einsetzen und
    /// damit die Antwort auf eine <em>andere</em> Frage als Zustimmung fuer seinen Aufruf ausgeben.
    /// Der Schutz bindet die Id an Identitaet und Werkzeug; beim Zurueckkommen wird beides erneut
    /// gegen den laufenden Aufruf geprueft.
    /// </para>
    /// <para>
    /// Liefert <c>null</c>, wenn es nichts zu fragen gibt (Anfrage steht nicht mehr auf wartend) —
    /// dann bleibt es beim Warteschlangen-Ergebnis.
    /// </para>
    /// </summary>
    public static async Task<InputRequiredException?> TryBuildInputRequiredAsync(
        IServiceProvider services,
        IdentityId identity,
        Guid approvalId,
        NamespacedToolName tool,
        CancellationToken ct)
    {
        var log = Logger(services);
        var pending = await FindPendingAsync(services, approvalId, tool, log, ct).ConfigureAwait(false);
        if (pending is null)
        {
            return null;
        }

        return new InputRequiredException(
            inputRequests: new Dictionary<string, InputRequest>
            {
                [InputKey] = InputRequest.ForElicitation(BuildForm(pending)),
            },
            requestState: Protect(services, approvalId, identity, tool));
    }

    /// <summary>
    /// Zweite Runde einer MRTR-Rueckfrage: Der Client hat geantwortet und den Aufruf wiederholt.
    /// Bei ausdruecklicher Zustimmung ist die Freigabe danach im Store erteilt, und der Aufruf kann
    /// unmittelbar durchlaufen.
    /// </summary>
    public static async Task<Outcome> TryAcceptAnswerAsync(
        IServiceProvider services,
        IdentityId identity,
        NamespacedToolName tool,
        string requestState,
        IDictionary<string, InputResponse>? responses,
        CancellationToken ct)
    {
        var log = Logger(services);

        if (Unprotect(services, requestState) is not { } state)
        {
            // Kein Grund zur Panik, aber auch keiner zum Schweigen: Entweder ist der Zustand alt
            // (Schluesselwechsel), oder jemand hat einen erfunden.
            Skipped(log, tool, "der mitgeschickte Vorgangszustand ist ungueltig");
            return Outcome.NotPossible;
        }

        if (state.Identity != identity || state.Tool != tool)
        {
            Skipped(log, tool,
                $"der Vorgangszustand gehoert zu '{state.Tool.Value}' und nicht zum laufenden Aufruf");
            return Outcome.NotPossible;
        }

        if (responses is null || !responses.TryGetValue(InputKey, out var response))
        {
            Skipped(log, tool, "die Wiederholung kam ohne Antwort auf die Rueckfrage");
            return Outcome.NotPossible;
        }

        ElicitResult? answer;
        try
        {
            answer = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        }
        catch (JsonException exception)
        {
            Skipped(log, tool, $"die Antwort war nicht lesbar: {exception.Message}");
            return Outcome.NotPossible;
        }

        if (answer is null || !IsExplicitYes(answer))
        {
            Skipped(log, tool, $"Antwort '{answer?.Action ?? "(leer)"}' ohne ausdrueckliche Zustimmung");
            return Outcome.NotPossible;
        }

        // Nur eine Anfrage, die noch wartet, darf entschieden werden — sonst liesse sich eine
        // laengst abgelaufene oder widerrufene Freigabe durch eine Wiederholung neu beleben.
        if (await FindPendingAsync(services, state.ApprovalId, tool, log, ct).ConfigureAwait(false) is null)
        {
            return Outcome.NotPossible;
        }

        await services.GetRequiredService<IApprovalStore>()
            .DecideAsync(state.ApprovalId, approved: true, ct).ConfigureAwait(false);
        return Outcome.Approved;
    }

    /// <summary>
    /// Fragt im laufenden Aufruf zurück (Elicitation) — der Weg für Clients ohne MRTR. Setzt eine
    /// stehende Sitzung voraus; im stateless Betrieb verweigert das SDK ihn.
    /// Bei Zustimmung ist die Freigabe im Store bereits erteilt.
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
        var log = Logger(services);

        if (server?.ClientCapabilities?.Elicitation is null)
        {
            Skipped(log, tool, "der Client meldet keine Elicitation-Faehigkeit");
            return Outcome.NotPossible;
        }

        // Die REDIGIERTEN Argumente aus der Warteschlange, nicht die des Aufrufs: Das Popup darf
        // nicht mehr zeigen als die Oberfläche, sonst wäre die Maskierung eine Frage des Weges.
        var pending = await FindPendingAsync(services, approvalId, tool, log, ct).ConfigureAwait(false);
        if (pending is null)
        {
            return Outcome.NotPossible;
        }

        ElicitResult answer;
        try
        {
            answer = await server.ElicitAsync(BuildForm(pending), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Ein Client, der die Fähigkeit meldet, sie aber nicht bedient, darf den Aufruf nicht
            // verlieren — er landet dann wie bisher in der Warteschlange. Der Grund gehoert aber
            // ins Log, sonst ist ein Fehler von einem fehlenden Feature nicht zu unterscheiden.
            Skipped(log, tool, $"Rueckfrage gescheitert: {exception.Message}");
            return Outcome.NotPossible;
        }

        if (IsExplicitYes(answer))
        {
            await services.GetRequiredService<IApprovalStore>()
                .DecideAsync(approvalId, approved: true, ct).ConfigureAwait(false);
            return Outcome.Approved;
        }

        Skipped(log, pending.Tool, $"Antwort '{answer.Action}' ohne ausdrueckliche Zustimmung");
        return Outcome.NotPossible;
    }

    /// <summary>
    /// NUR ein ausdrueckliches Ja zaehlt. Alles andere — auch ein glasklares <c>decline</c> — fuehrt
    /// zurueck in die Warteschlange, wo ein Mensch noch entscheiden kann.
    /// <para>
    /// Das war zweimal anders gedacht und zweimal falsch:
    /// <list type="number">
    ///   <item>Zuerst galt jede Nicht-Zustimmung als Ablehnung. Der Client lehnte selbst ab, weil
    ///   das Formular leer war, und im Audit stand „abgelehnt".</item>
    ///   <item>Danach galten <c>decline</c> und <c>accept</c>-ohne-Haekchen als menschliches Nein,
    ///   nur <c>cancel</c> nicht. Am 2026-07-30 hat derselbe Client dann <c>decline</c> geschickt,
    ///   ohne dass ein Formular je zu sehen war — nachgewiesen: Der Mensch bestaetigte in diesem
    ///   Aufruf ausschliesslich die Berechtigungsfrage seines Clients.</item>
    /// </list>
    /// Die Lehre steckt in der Wiederholung: Man kann einem Client nicht ansehen, ob hinter seiner
    /// Antwort ein Mensch stand. Nur die Zustimmung traegt ein Merkmal, das kein Automatismus
    /// nebenbei erzeugt — ein eigens gesetztes Haekchen. Der Preis ist klein und faellt auf die
    /// richtige Seite: Ein ECHTES Nein wird hier nicht mehr vermerkt, sondern bleibt wartend. Ein
    /// nicht verbuchtes Nein kostet einen Klick in der Oberflaeche; ein erfundenes Ja kostet die
    /// Freigabepflicht.
    /// </para>
    /// </summary>
    private static bool IsExplicitYes(ElicitResult answer)
        => string.Equals(answer.Action, "accept", StringComparison.Ordinal)
        && answer.Content?.TryGetValue(ApproveField, out var value) == true
        && value.ValueKind is JsonValueKind.True;

    /// <summary>
    /// Ein Formular mit EINEM Ja/Nein-Feld. Der erste Versuch schickte ein leeres Schema — daran
    /// hatte der Client nichts anzuzeigen und lehnte von sich aus ab, ohne dass ein Mensch etwas
    /// sah. Ein Dialog, den niemand sieht, ist keine Freigabe.
    /// </summary>
    private static ElicitRequestParams BuildForm(ApprovalRequest pending) => new()
    {
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
    };

    private static async Task<ApprovalRequest?> FindPendingAsync(
        IServiceProvider services, Guid approvalId, NamespacedToolName tool, ILogger? log, CancellationToken ct)
    {
        var store = services.GetService<IApprovalStore>();
        if (store is null)
        {
            Skipped(log, tool, "kein Approval-Store eingebunden");
            return null;
        }

        var pending = (await store.ListAsync(ApprovalState.Pending, ct).ConfigureAwait(false))
            .FirstOrDefault(r => r.Id == approvalId);
        if (pending is null)
        {
            Skipped(log, tool, $"Anfrage {approvalId} steht nicht (mehr) auf wartend");
        }

        return pending;
    }

    private static string Protect(
        IServiceProvider services, Guid approvalId, IdentityId identity, NamespacedToolName tool)
        => Protector(services).Protect(string.Join(
            '|', approvalId.ToString("D", CultureInfo.InvariantCulture), identity.Value, tool.Value));

    private static (Guid ApprovalId, IdentityId Identity, NamespacedToolName Tool)? Unprotect(
        IServiceProvider services, string state)
    {
        try
        {
            var parts = Protector(services).Unprotect(state).Split('|');
            if (parts.Length != 3
                || !Guid.TryParse(parts[0], CultureInfo.InvariantCulture, out var approvalId)
                || !Guid.TryParse(parts[1], CultureInfo.InvariantCulture, out var identity))
            {
                return null;
            }

            return (approvalId, new IdentityId(identity), new NamespacedToolName(parts[2]));
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static IDataProtector Protector(IServiceProvider services)
        => services.GetRequiredService<IDataProtectionProvider>().CreateProtector(StatePurpose);

    private static ILogger? Logger(IServiceProvider services)
        => services.GetService<ILoggerFactory>()?.CreateLogger("Bifrost.Server.ApprovalElicitation");

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
