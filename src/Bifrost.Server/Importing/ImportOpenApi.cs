using System.Text.Json.Nodes;

namespace Bifrost.Server.Importing;

/// <summary>
/// Der Beitrag des Konfigurationsimports zum OpenAPI-Dokument (FR-18).
///
/// <para>
/// <b>Warum das hier steht und nicht im Generator.</b> Das Dokument beschreibt bisher genau eine
/// Sache: die Werkzeuge, die dieser Schlüssel sehen darf. Die Importendpunkte sind das erste
/// Management-Stück darin, und ihre Beschreibung gehört zu ihnen — nicht in eine Datei, die sonst
/// nichts vom Import weiß. Der Generator ruft eine Zeile auf; was drinsteht, entscheidet dieses
/// Paket.
/// </para>
///
/// <para>
/// <b>Nur für Schlüssel mit Global-Grant.</b> Das Dokument ist die RBAC-Sicht <em>dieses</em>
/// Schlüssels. Endpunkte hineinzuschreiben, die der Aufrufer mit 403 zurückbekäme, machte aus einer
/// Sicht eine Broschüre.
/// </para>
/// </summary>
public static class ImportOpenApi
{
    /// <summary>Trägt die Importendpunkte in die Pfadtabelle ein.</summary>
    public static void AddPaths(JsonObject paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        paths[ImportEndpoints.ApiBase + "/preview"] = new JsonObject
        {
            ["post"] = Operation(
                "importPreview",
                "Analysiert eine fremde MCP-Konfiguration und liefert das normalisierte "
                + "Vorschaumodell samt Handle. Der Rumpf ist die Datei SELBST — ein Pfad wird "
                + "hoechstens als Herkunftsangabe (Query 'originPath') entgegengenommen und nie "
                + "serverseitig gelesen. Die Antwort traegt keine Secretwerte: Von wertetragenden "
                + "Feldern reisen Namen und Anzahlen, nie Inhalte.",
                RawDocumentBody(),
                new JsonObject
                {
                    ["200"] = Response("Vorschaumodell mit Handle (30 Minuten, einmalig verwendbar)"),
                    ["400"] = Response("Leerer Rumpf oder unbrauchbares Dokument"),
                    ["403"] = Response("Kein Global-Grant"),
                    ["413"] = Response("Dokument groesser als 1 MiB"),
                    ["415"] = Response("Inhaltstyp weder application/json noch text/plain"),
                    ["429"] = Response("Zu viele Importanfragen"),
                },
                queryParameters: new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "originPath",
                        ["in"] = "query",
                        ["required"] = false,
                        ["description"] = "Herkunftsangabe fuer die Befunde. KEIN Leseauftrag.",
                        ["schema"] = new JsonObject { ["type"] = "string" },
                    })),
        };

        paths[ImportEndpoints.ApiBase + "/probe"] = new JsonObject
        {
            ["post"] = Operation(
                "importProbe",
                "Verbindet einen einzelnen Server aus dem vorgemerkten Plan, ohne ihn anzulegen. "
                + "Das Handle wird dabei NICHT verbraucht. Eine Fehlermeldung des fremden Dienstes "
                + "wird um die Werte dieser Konfiguration bereinigt.",
                JsonBody(new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("token", "sourceName"),
                    ["properties"] = new JsonObject
                    {
                        ["token"] = new JsonObject { ["type"] = "string" },
                        ["sourceName"] = new JsonObject { ["type"] = "string" },
                    },
                }),
                new JsonObject
                {
                    ["200"] = Response("Ergebnis des Verbindungstests"),
                    ["403"] = Response("Kein Global-Grant"),
                    ["404"] = Response("Der Plan kennt diesen Server nicht"),
                    ["409"] = Response("Handle unbekannt, abgelaufen oder fremd"),
                    ["429"] = Response("Zu viele Importanfragen"),
                }),
        };

        paths[ImportEndpoints.ApiBase + "/commit"] = new JsonObject
        {
            ["post"] = Operation(
                "importCommit",
                "Uebernimmt die ausgewaehlten Server aus dem vorgemerkten Plan. Atomar: Scheitert "
                + "einer, werden die bereits angelegten wieder entfernt. Das Handle gilt genau "
                + "einmal. Befunde der Stufe 'Risk' verlangen confirmRisks=true; ein Server, der "
                + "ein fremdes Programm startet, verlangt eine ausdrueckliche Isolationsangabe "
                + "(ADR-0025 E2/E5).",
                JsonBody(new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("token"),
                    ["properties"] = new JsonObject
                    {
                        ["token"] = new JsonObject { ["type"] = "string" },
                        ["confirmRisks"] = new JsonObject { ["type"] = "boolean" },
                        ["isolation"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("Host", "Container"),
                            ["description"] = "Gilt fuer alle Server ohne eigene Angabe.",
                        },
                        ["containerImage"] = new JsonObject { ["type"] = "string" },
                        ["servers"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Auswahl. Ohne Angabe gilt der ganze Plan.",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["required"] = new JsonArray("sourceName"),
                                ["properties"] = new JsonObject
                                {
                                    ["sourceName"] = new JsonObject { ["type"] = "string" },
                                    ["isolation"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new JsonArray("Host", "Container"),
                                    },
                                    ["containerImage"] = new JsonObject { ["type"] = "string" },
                                },
                            },
                        },
                    },
                }),
                new JsonObject
                {
                    ["200"] = Response("Uebernommen; Liste der angelegten Server"),
                    ["400"] = Response("Plan nicht anwendbar oder Auswahl leer"),
                    ["403"] = Response("Kein Global-Grant"),
                    ["409"] = Response(
                        "Handle unbekannt/verbraucht, Bestaetigung fehlt, Isolationsangabe fehlt "
                        + "oder Slug bereits vergeben"),
                    ["429"] = Response("Zu viele Importanfragen"),
                }),
        };
    }

    private static JsonObject Operation(
        string operationId,
        string summary,
        JsonObject requestBody,
        JsonObject responses,
        JsonArray? queryParameters = null)
    {
        var operation = new JsonObject
        {
            ["operationId"] = operationId,
            ["summary"] = summary,
            ["tags"] = new JsonArray("import"),
            ["requestBody"] = requestBody,
            ["responses"] = responses,
            ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
        };

        if (queryParameters is not null)
        {
            operation["parameters"] = queryParameters;
        }

        return operation;
    }

    private static JsonObject RawDocumentBody() => new()
    {
        ["required"] = true,
        ["description"] = "Die fremde Konfigurationsdatei, hoechstens 1 MiB.",
        ["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject
            {
                ["schema"] = new JsonObject { ["type"] = "object" },
            },
            ["text/plain"] = new JsonObject
            {
                ["schema"] = new JsonObject { ["type"] = "string" },
            },
        },
    };

    private static JsonObject JsonBody(JsonObject schema) => new()
    {
        ["required"] = true,
        ["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject { ["schema"] = schema },
        },
    };

    private static JsonObject Response(string description)
        => new() { ["description"] = description };
}
