using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Microsoft.Net.Http.Headers;

namespace Bifrost.Server.Importing;

/// <summary>
/// Die Eingangsprüfungen der Importendpunkte: Größe, Inhaltstyp, Häufigkeit.
///
/// <para>
/// <b>Warum eigene Prüfungen und nicht die Modellbindung.</b> Der Rumpf dieser Endpunkte ist ein
/// <em>fremdes Dokument</em>, kein Vertragstyp — er wird als Text entgegengenommen und erst danach
/// beurteilt. Damit fällt jede Absicherung weg, die aus einem Schema kommt: Ohne eigene Grenze läse
/// der Dienst so viel, wie der Aufrufer schickt, und ein Parser mit 30-facher Verschachtelung
/// beschäftigte ihn beliebig lange.
/// </para>
/// </summary>
public static class ImportRequestLimits
{
    /// <summary>
    /// Obergrenze des Quelldokuments. Eine Client-Konfiguration mit hundert Servern liegt bei
    /// wenigen zehn Kilobyte; ein Megabyte ist großzügig und trotzdem eine Grenze.
    /// </summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>
    /// Zugelassene Inhaltstypen. <c>application/json</c> ist der Regelfall,
    /// <c>text/plain</c> der Weg für eine Datei, die noch kein gültiges JSON sein muss — genau der
    /// Fall, für den es die Formaterkennung gibt. Alles andere wird abgewiesen, statt geraten.
    /// </summary>
    public static IReadOnlyList<string> AllowedContentTypes { get; } =
        ["application/json", "text/plain"];

    /// <summary>
    /// Liest das Quelldokument aus dem Rumpf — oder gibt die Absage zurück, die stattdessen
    /// hinausgeht.
    /// <para>
    /// Gelesen wird <b>mit</b> Grenze und nicht bis zum Ende: Eine <c>Content-Length</c> ist eine
    /// Behauptung des Aufrufers. Wer sie glaubt und danach unbegrenzt liest, hat keine Grenze,
    /// sondern eine Bitte.
    /// </para>
    /// </summary>
    public static async Task<(string? Document, IResult? Failure)> ReadDocumentAsync(
        HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var contentType = context.Request.ContentType;
        if (!IsAllowed(contentType))
        {
            return (null, ImportErrors.Result(
                StatusCodes.Status415UnsupportedMediaType,
                ImportErrors.ContentType,
                "Der Inhaltstyp '"
                + (string.IsNullOrWhiteSpace(contentType) ? "(keiner)" : contentType)
                + "' wird hier nicht angenommen. Zugelassen sind: "
                + string.Join(", ", AllowedContentTypes) + "."));
        }

        if (context.Request.ContentLength > MaxDocumentBytes)
        {
            return (null, TooLarge());
        }

        var buffer = new byte[8192];
        var collected = new MemoryStream();
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (collected.Length + read > MaxDocumentBytes)
            {
                return (null, TooLarge());
            }

            collected.Write(buffer, 0, read);
        }

        if (collected.Length == 0)
        {
            return (null, ImportErrors.Result(
                StatusCodes.Status400BadRequest,
                ImportErrors.Usage,
                "Der Rumpf ist leer. Erwartet wird die fremde Konfigurationsdatei selbst — nicht ein "
                + "Pfad auf sie: Der Quellpfad ist eine Herkunftsangabe, kein Leseauftrag."));
        }

        // UTF-8 ohne BOM-Toleranz waere hier falsch: Editoren unter Windows schreiben eines, und
        // ein BOM vor der oeffnenden Klammer laesst jeden JSON-Parser mit 'unexpected character'
        // scheitern — eine Meldung, die nach einer kaputten Datei aussieht.
        return (new UTF8Encoding(false).GetString(collected.ToArray()).TrimStart('﻿'), null);
    }

    private static IResult TooLarge() => ImportErrors.Result(
        StatusCodes.Status413PayloadTooLarge,
        ImportErrors.TooLarge,
        "Das Dokument ist groesser als "
        + (MaxDocumentBytes / 1024).ToString(CultureInfo.InvariantCulture)
        + " KiB. Diese Grenze steht, weil der Rumpf hier ein fremdes Dokument ist und nicht ein "
        + "geprueftes Modell.");

    private static bool IsAllowed(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || !MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return false;
        }

        var media = parsed.MediaType.Value;
        return media is not null
            && AllowedContentTypes.Contains(media, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Ein Zähler je Aufrufer über ein festes Zeitfenster.
///
/// <para>
/// <b>Warum nicht <c>IRateLimiter</c> aus dem RBAC.</b> Der begrenzt <em>Werkzeugaufrufe</em> je
/// Identität und nur dann, wenn eine Rolle ein Limit trägt — ohne gesetztes Limit ist er unbegrenzt.
/// Das ist für Werkzeuge richtig und hier falsch: Der Setup-Weg hat gar keine Identität, und ein
/// Import ist teuer, unabhängig davon, was in einer Rolle steht. Der Zähler hier ist deshalb
/// bedingungslos.
/// </para>
///
/// <para>
/// Der Schlüssel ist die Identität, auf dem Setup-Weg die Gegenstelle. Ein festes Fenster statt
/// eines Token-Buckets: Ein Import ist ein Vorgang, den ein Mensch auslöst, und die Frage lautet
/// „wie viele in der Minute?", nicht „wie gleichmäßig verteilt?".
/// </para>
/// </summary>
public sealed class ImportRateLimiter
{
    /// <summary>Anfragen je Schlüssel und Fenster.</summary>
    public const int PermitsPerWindow = 12;

    /// <summary>Die Länge des Fensters.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ImportRateLimiter(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Darf dieser Schlüssel jetzt? Der Aufruf zählt mit.</summary>
    public bool TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = _time.GetUtcNow();
        var counter = _counters.AddOrUpdate(
            key,
            _ => new Counter(now, 1),
            (_, current) => current.WindowStart + Window <= now
                ? new Counter(now, 1)
                : current with { Used = current.Used + 1 });

        return counter.Used <= PermitsPerWindow;
    }

    private sealed record Counter(DateTimeOffset WindowStart, int Used);
}

/// <summary>
/// Die Absagen der Importendpunkte in genau einer Form. Der Code im Rumpf ist das, worauf CLI und
/// Oberfläche sich stützen — nicht der HTTP-Status: <c>400</c> heißt hier „Dokument unbrauchbar"
/// oder „Bedienfehler", und das sind zwei verschiedene Aussagen.
/// </summary>
public static class ImportErrors
{
    public const string Usage = "usage";
    public const string ContentType = "content-type";
    public const string TooLarge = "too-large";
    public const string RateLimited = "rate-limited";
    public const string DocumentInvalid = "document-invalid";
    public const string HandleUnknown = "handle-unknown";
    public const string ConfirmationRequired = "confirmation-required";
    public const string Conflict = "conflict";

    public static IResult Result(int statusCode, string code, string message)
        => Results.Json(
            new { error = new { code, message } },
            statusCode: statusCode);
}
