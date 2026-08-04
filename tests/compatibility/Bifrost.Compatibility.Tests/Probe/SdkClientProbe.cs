using Bifrost.Abstractions;
using Bifrost.Compatibility.Tests.Matrix;
using Bifrost.Core.Invocation;
using Bifrost.Integration.Tests.Gateway;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Bifrost.Compatibility.Tests.Probe;

internal sealed record ProbeOutcome(
    IReadOnlyDictionary<string, string> Features,
    IReadOnlyList<string> Notes);

/// <summary>
/// Der automatisierbare Teil von WP5.1: das offizielle C#-SDK als echter Client gegen den echten
/// Gateway-Host, Fähigkeit für Fähigkeit.
/// <para>
/// <b>Was der Probelauf ausdrücklich nicht ist:</b> ein Beweis über TCP, TLS und Reverse Proxy.
/// Er fährt gegen den in-process gehosteten Server — die Netzstrecke ist Sache von WP5.3. Diese
/// Einschränkung steht als <c>limitations</c>-Eintrag in der Matrixzeile, damit sie im Bericht
/// erscheint und nicht nur hier im Kommentar.
/// </para>
/// </summary>
internal static class SdkClientProbe
{
    public static async Task<ProbeOutcome> RunAsync(GatewayFixture gw, CancellationToken ct)
    {
        var notes = new List<string>();
        var features = new Dictionary<string, string>(StringComparer.Ordinal);

        var (_, apiKey) = await gw.SeedAdminAsync($"compat-{Guid.NewGuid():N}");
        var slug = $"cmp{Guid.NewGuid():N}"[..10];
        await gw.AddEchoUpstreamAsync(slug);

        var assets = gw.Services.GetRequiredService<IAssetStore>();
        var assetName = $"compat-{Guid.NewGuid():N}";
        await assets.CreateAsync(assetName, "WP5.1-Probe", "Inhalt der Probe", metadata: null, ct);

        await using var client = await gw.ConnectClientAsync(apiKey);

        features[CompatibilityFeatures.Tools] = await ProbeAsync(
            CompatibilityFeatures.Tools, notes, async () =>
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                if (!tools.Any(t => t.Name == MetaToolService.SearchToolsName))
                {
                    return "search_tools fehlt in tools/list";
                }

                var result = await client.CallToolAsync(
                    $"{slug}__echo",
                    new Dictionary<string, object?> { ["message"] = "probe" },
                    cancellationToken: ct);

                if (result.IsError == true)
                {
                    return "tools/call meldet IsError";
                }

                var text = result.Content.OfType<TextContentBlock>().Single().Text;
                return text == "Echo: probe" ? null : $"unerwartete Antwort: '{text}'";
            });

        features[CompatibilityFeatures.Resources] = await ProbeAsync(
            CompatibilityFeatures.Resources, notes, async () =>
            {
                var uri = AssetDelivery.ResourceUri(assetName);
                var resources = await client.ListResourcesAsync(cancellationToken: ct);
                if (!resources.Any(r => r.Uri == uri))
                {
                    return "die angelegte Resource steht nicht in resources/list";
                }

                var read = await client.ReadResourceAsync(uri, cancellationToken: ct);
                var text = read.Contents.OfType<TextResourceContents>().Single().Text;
                return text == "Inhalt der Probe" ? null : $"unerwarteter Inhalt: '{text}'";
            });

        features[CompatibilityFeatures.Prompts] = await ProbeAsync(
            CompatibilityFeatures.Prompts, notes, async () =>
            {
                var name = AssetDelivery.PromptName(assetName);
                var prompts = await client.ListPromptsAsync(cancellationToken: ct);
                if (!prompts.Any(p => p.Name == name))
                {
                    return "der angelegte Prompt steht nicht in prompts/list";
                }

                var prompt = await client.GetPromptAsync(name, cancellationToken: ct);
                var text = prompt.Messages.Single().Content is TextContentBlock block ? block.Text : null;
                return text == "Inhalt der Probe" ? null : $"unerwarteter Prompt-Inhalt: '{text}'";
            });

        features[CompatibilityFeatures.Skills] = await ProbeAsync(
            CompatibilityFeatures.Skills, notes, async () =>
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                if (!tools.Any(t => t.Name == MetaToolService.ListSkillsName))
                {
                    return "list_skills fehlt in tools/list";
                }

                var result = await client.CallToolAsync(
                    MetaToolService.ListSkillsName,
                    new Dictionary<string, object?>(),
                    cancellationToken: ct);

                return result.IsError == true ? "list_skills meldet IsError" : null;
            });

        // Seit ADR-0023 gibt es ohne Sitzung kein tools/list_changed mehr — der Cache-Hinweis hat
        // es ersetzt. Der Probelauf liest deshalb die ausgehandelte Fähigkeit, statt auf eine
        // Benachrichtigung zu warten, die per Vertrag nicht kommt.
        var listChanged = client.ServerCapabilities?.Tools?.ListChanged == true;
        features[CompatibilityFeatures.ListChanged] =
            listChanged ? CompatibilityValues.Pass : CompatibilityValues.Unsupported;
        notes.Add($"listChanged aus ServerCapabilities: {(listChanged ? "angeboten" : "nicht angeboten")}.");

        // Bewusst ungeprüft in dieser Welle — und deshalb sichtbar 'untested' statt weggelassen.
        features[CompatibilityFeatures.Elicitation] = CompatibilityValues.Untested;
        features[CompatibilityFeatures.Approval] = CompatibilityValues.Untested;
        features[CompatibilityFeatures.OAuth] = CompatibilityValues.Untested;
        notes.Add("elicitation/approval/oauth werden von dieser Probe nicht ausgeübt (WP5.2 bzw. spätere Ausbaustufe).");

        return new ProbeOutcome(features, notes);
    }

    /// <summary>
    /// Führt eine Probe aus. Rückgabe der Prüfung: <c>null</c> = bestanden, sonst der Grund.
    /// Eine Ausnahme ist ein Fehlschlag mit Begründung — nicht ein „geht nicht" ohne Spur.
    /// </summary>
    private static async Task<string> ProbeAsync(
        string feature, List<string> notes, Func<Task<string?>> probe)
    {
        try
        {
            var failure = await probe();
            if (failure is null)
            {
                return CompatibilityValues.Pass;
            }

            notes.Add($"{feature}: {failure}");
            return CompatibilityValues.Fail;
        }
        catch (Exception ex)
        {
            notes.Add($"{feature}: {ex.GetType().Name} — {ex.Message}");
            return CompatibilityValues.Fail;
        }
    }
}
