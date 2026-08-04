using System.Globalization;
using System.Text;
using System.Text.Json;
using Bifrost.Abstractions;

namespace Bifrost.Core.Audit;

/// <summary>
/// Schreibt Exporteinträge nach JSONL oder CSV — <b>eine Zeile nach der anderen</b> (M6/WP6.2,
/// FR-P072).
/// <para>
/// <b>Warum je Zeile und nicht ein Dokument:</b> Ein JSON-Array oder ein DataTable muss vollständig
/// im Arbeitsspeicher stehen, bevor die erste Zeile hinausgeht. Bei einem Auditlog von Millionen
/// Einträgen ist das kein Detail, sondern der Unterschied zwischen „läuft" und „reißt die Instanz
/// mit". Beide Formate hier sind deshalb zeilenweise: Jede Zeile ist für sich vollständig.
/// </para>
/// </summary>
public static class AuditExportWriter
{
    /// <summary>
    /// Die Spaltenfolge des CSV. Sie ist <b>fest</b>: Wer sie ändert, zerlegt jede Auswertung, die
    /// jemand darauf gebaut hat.
    /// </summary>
    public static IReadOnlyList<string> CsvColumns { get; } =
    [
        "sequence", "timestamp", "kind", "origin", "caller", "callerRoles", "server", "tool",
        "status", "durationMs", "requestBytes", "responseBytes", "correlationId", "detail",
        "chainId", "chainIndex", "previousHash", "entryHash",
    ];

    public static string ContentType(AuditExportFormat format) => format switch
    {
        AuditExportFormat.Csv => "text/csv; charset=utf-8",
        _ => "application/x-ndjson; charset=utf-8",
    };

    public static string FileExtension(AuditExportFormat format)
        => format is AuditExportFormat.Csv ? "csv" : "jsonl";

    /// <summary>Die Kopfzeile — nur CSV hat eine.</summary>
    public static string? Header(AuditExportFormat format)
        => format is AuditExportFormat.Csv ? string.Join(',', CsvColumns) : null;

    public static string Line(AuditExportEntry entry, AuditExportFormat format)
        => format is AuditExportFormat.Csv ? Csv(entry) : Jsonl(entry);

    /// <summary>
    /// Eine Zeile JSONL. Die maskierten Argumente reisen als <b>eingebettetes JSON</b> mit, nicht
    /// als Zeichenkette: Wer den Export weiterverarbeitet, soll nicht zweimal parsen müssen.
    /// </summary>
    private static string Jsonl(AuditExportEntry entry)
    {
        var buffer = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var evt = entry.Event;
            writer.WriteStartObject();
            writer.WriteNumber("sequence", entry.Sequence);
            writer.WriteString("timestamp", evt.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("kind", evt.Kind.ToString());
            writer.WriteString("origin", evt.Origin.ToString());
            WriteOptional(writer, "caller", evt.Caller?.Value.ToString("D", CultureInfo.InvariantCulture));
            WriteOptional(writer, "callerRoles", evt.CallerRoles);
            WriteOptional(writer, "server", evt.Server?.Value.ToString("D", CultureInfo.InvariantCulture));
            WriteOptional(writer, "tool", evt.Tool);
            WriteOptional(writer, "status", evt.Status?.ToString());

            if (evt.Duration is { } duration)
            {
                writer.WriteNumber("durationMs", duration.TotalMilliseconds);
            }

            if (evt.RequestBytes is { } request)
            {
                writer.WriteNumber("requestBytes", request);
            }

            if (evt.ResponseBytes is { } response)
            {
                writer.WriteNumber("responseBytes", response);
            }

            WriteOptional(writer, "correlationId", evt.CorrelationId?.ToString("D", CultureInfo.InvariantCulture));
            WriteOptional(writer, "detail", evt.Detail);

            if (evt.RedactedArguments is { } args)
            {
                writer.WritePropertyName("redactedArguments");
                args.WriteTo(writer);
            }

            if (evt.RedactedResponse is { } payload)
            {
                writer.WritePropertyName("redactedResponse");
                payload.WriteTo(writer);
            }

            if (entry.IsChained)
            {
                writer.WriteString("chainVersion", AuditChain.Version);
                writer.WriteString("chainId", entry.ChainId!.Value.ToString("D", CultureInfo.InvariantCulture));
                writer.WriteNumber("chainIndex", entry.ChainIndex!.Value);
                writer.WriteString("previousHash", entry.PreviousHash);
                writer.WriteString("entryHash", entry.EntryHash);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Eine Zeile CSV nach RFC 4180. <b>Ohne die maskierten Argumente</b> — sie sind JSON und
    /// gehören nicht in eine Tabellenzelle; wer sie braucht, nimmt JSONL. Das steht auch in der
    /// Dokumentation, aber hier steht der Grund.
    /// </summary>
    private static string Csv(AuditExportEntry entry)
    {
        var evt = entry.Event;
        var text = new StringBuilder(256);
        var fields = new[]
        {
            entry.Sequence.ToString(CultureInfo.InvariantCulture),
            evt.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            evt.Kind.ToString(),
            evt.Origin.ToString(),
            evt.Caller?.Value.ToString("D", CultureInfo.InvariantCulture),
            evt.CallerRoles,
            evt.Server?.Value.ToString("D", CultureInfo.InvariantCulture),
            evt.Tool,
            evt.Status?.ToString(),
            evt.Duration?.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
            evt.RequestBytes?.ToString(CultureInfo.InvariantCulture),
            evt.ResponseBytes?.ToString(CultureInfo.InvariantCulture),
            evt.CorrelationId?.ToString("D", CultureInfo.InvariantCulture),
            evt.Detail,
            entry.ChainId?.ToString("D", CultureInfo.InvariantCulture),
            entry.ChainIndex?.ToString(CultureInfo.InvariantCulture),
            entry.PreviousHash,
            entry.EntryHash,
        };

        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(Quote(fields[i]));
        }

        return text.ToString();
    }

    /// <summary>
    /// Anführungszeichen nach RFC 4180 — und ein führendes Hochkomma vor allem, was eine
    /// Tabellenkalkulation als Formel läse.
    /// <para>
    /// <b>Das ist keine Kosmetik.</b> Ein Auditfeld enthält Text aus fremder Quelle; beginnt er mit
    /// <c>=</c>, <c>+</c>, <c>-</c> oder <c>@</c>, führt Excel ihn beim Öffnen aus (CSV Injection).
    /// Ein Auditexport, der beim Ansehen Code startet, ist die Umkehrung seines Zwecks.
    /// </para>
    /// </summary>
    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return text.Contains(',', StringComparison.Ordinal)
            || text.Contains('"', StringComparison.Ordinal)
            || text.Contains('\n', StringComparison.Ordinal)
            || text.Contains('\r', StringComparison.Ordinal)
                ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : text;
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
