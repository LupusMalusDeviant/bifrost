using Bifrost.Performance.Tests.Harness;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Bifrost.Soak.Tests.Harness;

internal enum WorkloadKind
{
    List,
    Read,
    Write,
    Skill,
    Audit,
}

/// <summary>
/// Die Lastmischung — deterministisch, weil ein Soak-Lauf sonst nicht vergleichbar ist.
/// <para>
/// Jeder Arbeiter zieht aus seinem eigenen, fest gesäten Zufall. Zwei Läufe mit derselben Saat
/// führen dieselbe Folge von Aufrufen aus; ohne das wäre „gestern ging es, heute nicht" nie
/// beantwortbar.
/// </para>
/// <para>
/// <b>Nicht enthalten:</b> Freigaben (Approval). Der Weg braucht eine gesetzte Richtlinie und eine
/// zweite, entscheidende Identität; er gehört in die Lastmischung, ist hier aber nicht gebaut — und
/// steht deshalb als offener Punkt im Ergebnis statt als stille Lücke.
/// </para>
/// </summary>
internal sealed class WorkloadMix
{
    private static readonly (WorkloadKind Kind, int Weight)[] Weights =
    [
        (WorkloadKind.List, 30),
        (WorkloadKind.Read, 15),
        (WorkloadKind.Write, 35),
        (WorkloadKind.Skill, 10),
        (WorkloadKind.Audit, 10),
    ];

    private readonly Random _random;
    private readonly string _assetPromptName;
    private readonly string _assetResourceUri;

    public WorkloadMix(int seed, string assetPromptName, string assetResourceUri)
    {
        _random = new Random(seed);
        _assetPromptName = assetPromptName;
        _assetResourceUri = assetResourceUri;
    }

    public WorkloadKind Next()
    {
        var total = Weights.Sum(w => w.Weight);
        var roll = _random.Next(total);
        foreach (var (kind, weight) in Weights)
        {
            if (roll < weight)
            {
                return kind;
            }

            roll -= weight;
        }

        return WorkloadKind.List;
    }

    /// <summary>Führt eine Operation aus. Rückgabe <c>null</c> heißt bestanden, sonst der Grund.</summary>
    public async Task<string?> ExecuteAsync(
        WorkloadKind kind, McpClient client, HttpClient http, string writeTool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);

        switch (kind)
        {
            case WorkloadKind.List:
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                return tools.Count > 0 ? null : "tools/list war leer";

            case WorkloadKind.Read:
                var read = await client.ReadResourceAsync(_assetResourceUri, cancellationToken: ct);
                return read.Contents.OfType<TextResourceContents>().Any() ? null : "resources/read ohne Inhalt";

            case WorkloadKind.Write:
                var result = await client.CallToolAsync(
                    writeTool,
                    new Dictionary<string, object?> { ["message"] = "soak" },
                    cancellationToken: ct);
                return result.IsError == true ? "tools/call meldet IsError" : null;

            case WorkloadKind.Skill:
                var prompt = await client.GetPromptAsync(_assetPromptName, cancellationToken: ct);
                return prompt.Messages.Count > 0 ? null : "prompts/get ohne Nachricht";

            case WorkloadKind.Audit:
                using (var response = await http.GetAsync("api/v1/audit?pageSize=10", ct))
                {
                    return response.IsSuccessStatusCode ? null : $"GET /audit → {(int)response.StatusCode}";
                }

            default:
                return $"unbekannte Operation {kind}";
        }
    }

    /// <summary>Ein Client je Arbeiter — so wie ein Agent auch eine eigene Verbindung hätte.</summary>
    public static async Task<McpClient> ConnectAsync(GatewayProcess gateway, HttpClient http, int index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = $"soak-{index}",
                Endpoint = new Uri(gateway.BaseAddress, "/mcp"),
            },
            http);

        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }
}
