using System.Text.Json;
using Bifrost.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Bifrost.Upstream;

/// <summary>
/// Kapselt einen SDK-<see cref="McpClient"/> vollständig hinter <see cref="IUpstreamConnection"/> —
/// oberhalb dieser Klasse existieren keine SDK-Typen (DON'T Nr. 1). Discovery ist teil-tolerant:
/// Tools sind Pflicht, Resources/Prompts werden nur gelistet, wenn der Server die Capability meldet.
/// </summary>
internal sealed class SdkUpstreamConnection : IUpstreamConnection
{
    private static readonly string[] ForwardedNotifications =
    [
        NotificationMethods.ToolListChangedNotification,
        NotificationMethods.ResourceListChangedNotification,
        NotificationMethods.PromptListChangedNotification,
    ];

    /// <summary>Wie viele Namen aus einem offenen Wörterbuch höchstens im Bericht landen.</summary>
    private const int MaxOpenSetEntries = 16;

    /// <summary>Obergrenze für einen einzelnen Namen aus einem offenen Wörterbuch.</summary>
    private const int MaxNameLength = 120;

    private readonly McpClient _client;
    private readonly List<IAsyncDisposable> _registrations = [];

    public SdkUpstreamConnection(ServerId id, McpClient client)
    {
        Id = id;
        _client = client;
        foreach (var method in ForwardedNotifications)
        {
            _registrations.Add(_client.RegisterNotificationHandler(method, (notification, _) =>
            {
                NotificationReceived?.Invoke(this, new UpstreamNotificationEventArgs
                {
                    Server = Id,
                    Method = notification.Method,
                    Params = notification.Params is { } p ? JsonSerializer.SerializeToElement(p, McpJsonUtilities.DefaultOptions) : null,
                });
                return default;
            }));
        }
    }

    public ServerId Id { get; }

    /// <summary>
    /// Auf der Revision 2026-07-28 gibt es keine unaufgeforderten Nachrichten mehr — die
    /// registrierten Handler oben bleiben dann stumm, und der Supervisor muss turnusmäßig
    /// nachfragen. Auf älteren Ständen ist alles wie bisher.
    /// </summary>
    public bool PushesCatalogChanges => !SpeaksJuly2026OrLater(_client.NegotiatedProtocolVersion);

    /// <summary>
    /// Die ausgehandelte Fassung und die gemeldeten Fähigkeiten — hier, und nur hier, liegen sie.
    /// <para>
    /// Es wird <b>nichts nachgefragt</b>: Beide Werte stehen seit dem Verbindungsaufbau im Client.
    /// Die Eigenschaft ist damit auch dann beantwortbar, wenn die Gegenstelle gerade nicht
    /// antwortet — genau der Fall, in dem jemand die Diagnose aufruft.
    /// </para>
    /// </summary>
    public UpstreamProtocolInfo Protocol
    {
        get
        {
            var negotiated = _client.NegotiatedProtocolVersion;
            return string.IsNullOrWhiteSpace(negotiated)
                ? UpstreamProtocolInfo.Unknown(
                    "Die Verbindung steht, aber das SDK nennt keine ausgehandelte Fassung. Das ist "
                    + "kein Normalfall — erwartbar ist es nur, solange der Aufbau noch laeuft.")
                : UpstreamProtocolInfo.Negotiated(negotiated, DescribeCapabilities(_client.ServerCapabilities));
        }
    }

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived;

    public async Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
    {
        IList<McpClientTool> tools;
        try
        {
            tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            // Seit SDK 2.0 ist 'inputSchema' beim Werkzeug PFLICHT — fehlt es, bricht schon die
            // Deserialisierung. Vorher lief ein solcher Server durch. Die nackte JsonException
            // ("The JSON value could not be converted…") stuende dann als Ausfallgrund in der
            // Oberflaeche, und niemand kaeme von dort auf den eigentlichen Fehler: Der Upstream
            // haelt sich nicht ans Protokoll. Ein leeres '{}' genuegt ihm.
            throw new InvalidOperationException(
                "Der Upstream lieferte eine Werkzeugliste, die sich nicht lesen laesst. Haeufigste "
                + "Ursache seit der Spec-Revision 2026-07-28: Ein Werkzeug ohne 'inputSchema' — das "
                + "Feld ist inzwischen Pflicht, ein leeres Schema '{}' reicht aus. "
                + $"Urspruengliche Meldung: {exception.Message}",
                exception);
        }

        var toolDescriptors = tools
            .Select(t => new ToolDescriptor(t.Name, t.Description, t.JsonSchema.Clone()))
            .ToList();

        var resources = new List<ResourceDescriptor>();
        if (_client.ServerCapabilities?.Resources is not null)
        {
            var listed = await _client.ListResourcesAsync(cancellationToken: ct).ConfigureAwait(false);
            resources.AddRange(listed.Select(r => new ResourceDescriptor(
                new Uri(r.Uri, UriKind.RelativeOrAbsolute), r.Name, r.Description, r.MimeType)));
        }

        var prompts = new List<PromptDescriptor>();
        if (_client.ServerCapabilities?.Prompts is not null)
        {
            var listed = await _client.ListPromptsAsync(cancellationToken: ct).ConfigureAwait(false);
            prompts.AddRange(listed.Select(p => new PromptDescriptor(p.Name, p.Description)));
        }

        return new UpstreamInventory(toolDescriptors, resources, prompts);
    }

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await _client
                .CallToolAsync(toolName, JsonArguments.ToDictionary(args), cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("ElicitationHandler", StringComparison.Ordinal))
        {
            // Der Upstream will mitten im Aufruf etwas vom Menschen wissen (MRTR, 'input_required').
            // Wir beantworten das nicht — ADR-0010: Sampling und Elicitation werden NICHT
            // durchgereicht. Der Grund hat sich mit der neuen Revision nicht geaendert: Das
            // Protokoll traegt keine Korrelation, die sagt, WELCHER Mensch hier gemeint ist; der
            // Gateway steht zwischen vielen Aufrufern und einem Upstream.
            //
            // Der Aufruf scheitert also — aber mit einer Aussage. Ohne diesen Zweig stuende dort
            // "no ElicitationHandler is registered", was nach einem Fehler in der Zusammenstellung
            // des GATEWAYS klingt und nicht nach einer bewussten Grenze.
            throw new NotSupportedException(
                $"Das Werkzeug '{toolName}' verlangt eine Rueckfrage beim Menschen (MRTR). Der "
                + "Gateway reicht Rueckfragen eines Upstreams nicht durch (ADR-0010) — er koennte "
                + "nicht sagen, an welchen der vielen Aufrufer sie gehen soll.",
                exception);
        }

        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    public async Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var result = await _client.ReadResourceAsync(uri, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    public async Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
    {
        IReadOnlyDictionary<string, object?>? arguments = args is { } a ? JsonArguments.ToDictionary(a) : null;
        var result = await _client.GetPromptAsync(promptName, arguments, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    /// <summary>
    /// Lebenszeichen des Upstreams — die Grundlage der Gesundheitsanzeige und des Neustarts.
    /// <para>
    /// <b><c>ping</c> gibt es ab der Spec-Revision 2026-07-28 nicht mehr</b> („The method 'ping' is
    /// not available on protocol version '2026-07-28'"). Der Ersatz ist <c>server/discover</c>: die
    /// Anfrage, mit der ein Client auf dieser Revision ohnehin beginnt — leichtgewichtig, ohne
    /// Nebenwirkung und beim Server unvermeidlich implementiert.
    /// </para>
    /// <para>
    /// Die Weiche steht hier und nicht in der Konfiguration: Welche Revision gilt, hat die
    /// Gegenstelle ausgehandelt, nicht der Betreiber. Ein Schalter dafuer waere eine Einladung,
    /// ihn falsch zu stellen.
    /// </para>
    /// </summary>
    public async Task PingAsync(CancellationToken ct)
    {
        if (!SpeaksJuly2026OrLater(_client.NegotiatedProtocolVersion))
        {
            await _client.PingAsync(cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        await _client.SendRequestAsync<DiscoverRequestParams, DiscoverResult>(
            RequestMethods.ServerDiscover,
            new DiscoverRequestParams(),
            McpJsonUtilities.DefaultOptions,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Übersetzt das Capability-Objekt der Gegenstelle in <b>Namen</b>.
    /// <para>
    /// <b>Warum nur Namen:</b> <c>experimental</c> und <c>extensions</c> sind offene Wörterbücher —
    /// die Gegenstelle darf dort alles hineinschreiben, auch Werte, die niemand vorhergesehen hat.
    /// Ein Wert, den man nicht kennt, ist ein Wert, den man nicht anzeigen sollte; die Namen sagen,
    /// <em>dass</em> es etwas gibt, und das ist die Frage, um die es hier geht. Die Namen selbst
    /// kommen ebenfalls von aussen und laufen in der Diagnose durch die Redaktion.
    /// </para>
    /// <para>
    /// <b>Warum gedeckelt:</b> Ein Upstream kann zehntausend Erweiterungsnamen melden. Diese Liste
    /// landet in einem Diagnosebericht, den ein Mensch liest — sie ist eine Auskunft, kein Abbild.
    /// Wird gekürzt, steht das als eigener Eintrag da, statt still zu verschwinden.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> DescribeCapabilities(ServerCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return [];
        }

        var names = new List<string>();

        if (capabilities.Tools is { } tools)
        {
            names.Add("tools");
            if (tools.ListChanged is true)
            {
                names.Add("tools.listChanged");
            }
        }

        if (capabilities.Resources is { } resources)
        {
            names.Add("resources");
            if (resources.Subscribe is true)
            {
                names.Add("resources.subscribe");
            }

            if (resources.ListChanged is true)
            {
                names.Add("resources.listChanged");
            }
        }

        if (capabilities.Prompts is { } prompts)
        {
            names.Add("prompts");
            if (prompts.ListChanged is true)
            {
                names.Add("prompts.listChanged");
            }
        }

        // Abgekuendigt seit der Revision 2026-07-28 (SEP-2577) — und gerade deshalb eine Angabe,
        // die in eine Diagnose gehoert: Meldet eine Gegenstelle sie noch, sagt das etwas ueber
        // ihren Stand. Hier wird nichts benutzt, nur wiedergegeben, was sie selbst gesagt hat.
#pragma warning disable MCP9005
        if (capabilities.Logging is not null)
        {
            names.Add("logging");
        }
#pragma warning restore MCP9005

        if (capabilities.Completions is not null)
        {
            names.Add("completions");
        }

        AddOpenSet(names, "experimental", capabilities.Experimental?.Keys);
        AddOpenSet(names, "extensions", capabilities.Extensions?.Keys);
        return names;
    }

    /// <summary>Die offenen Wörterbücher: Namen, alphabetisch, gedeckelt — und die Kürzung sichtbar.</summary>
    private static void AddOpenSet(List<string> names, string prefix, IEnumerable<string>? keys)
    {
        if (keys is null)
        {
            return;
        }

        var ordered = keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
        foreach (var key in ordered.Take(MaxOpenSetEntries))
        {
            // Ein einzelner Name kann beliebig lang sein; er steht in einer Tabellenzelle.
            names.Add($"{prefix}:{(key.Length > MaxNameLength ? key[..MaxNameLength] + "…" : key)}");
        }

        if (ordered.Count > MaxOpenSetEntries)
        {
            names.Add($"{prefix}:… (+{ordered.Count - MaxOpenSetEntries} weitere)");
        }
    }

    /// <summary>
    /// Die Revisionen sind datumssortiert benannt — ein Stringvergleich reicht und bleibt richtig,
    /// wenn eine weitere dazukommt.
    /// </summary>
    internal static bool SpeaksJuly2026OrLater(string? negotiated)
        => negotiated is not null && string.CompareOrdinal(negotiated, "2026-07-28") >= 0;

    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations)
        {
            try
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Client bereits weg — irrelevant beim Abbau.
            }
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
