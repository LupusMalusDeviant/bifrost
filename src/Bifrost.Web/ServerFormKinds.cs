using Bifrost.Abstractions;

namespace Bifrost.Web;

/// <summary>
/// Welche Upstream-Arten das Formular auf <c>/servers</c> abbilden kann — und welche nicht.
/// <para>
/// <b>Warum das nicht einfach im Formular steht:</b> Die Auswahlliste bot vier Arten an, der
/// „Bearbeiten"-Knopf stand aber an <em>jeder</em> Zeile. Wer ihn an einem WASI-Upstream drückte,
/// bekam ein Formular, das dessen Konfiguration nicht enthält — und beim Speichern hätte der
/// Auffangzweig daraus eine OpenAPI-Konfiguration gebaut. Die Liste hier ist die eine Stelle, gegen
/// die sich das prüfen lässt: Kommt eine neue Art dazu, fällt der Test darüber, statt dass sie
/// stillschweigend im Auffangzweig landet.
/// </para>
/// </summary>
public static class ServerFormKinds
{
    /// <summary>Arten, für die das Formular Felder hat und eine Konfiguration bauen kann.</summary>
    public static IReadOnlyList<UpstreamTransportKind> Supported { get; } =
    [
        UpstreamTransportKind.Stdio,
        UpstreamTransportKind.StreamableHttp,
        UpstreamTransportKind.OpenApi,
        UpstreamTransportKind.Cli,
    ];

    public static bool CanEdit(UpstreamTransportKind kind) => Supported.Contains(kind);

    /// <summary>
    /// Warum eine Art nicht im Formular steht. Nicht „geht nicht", sondern der Grund und der Weg,
    /// der stattdessen gilt — sonst sucht jemand einen Knopf, den es absichtlich nicht gibt.
    /// </summary>
    public static string ReasonNotEditable(UpstreamTransportKind kind) => kind switch
    {
        UpstreamTransportKind.Wasi =>
            "WASI-Upstreams werden über ihr Connector-Paket konfiguriert (ADR-0016). Die "
            + "Konfiguration zeigt auf die Paket-Id; geändert wird sie durch ein Paket-Update.",
        UpstreamTransportKind.OpenRpc =>
            "OpenRPC-Upstreams werden bisher nur über die API oder die Konfigurationsdatei "
            + "angelegt. Ein Formular dafür gibt es noch nicht.",
        _ => "Diese Art kennt das Formular nicht.",
    };
}
