using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;
using Bifrost.Core.Importing;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// Der gemeinsame Aufbau der Importtests.
/// <para>
/// <b>Die Ausführungs-Policy steht hier ausdrücklich als Schalter.</b> Ob eine Host-Ausführung ein
/// Risiko oder ein Fehler ist, entscheidet nicht der Import, sondern die Instanz — genau das soll in
/// den Tests sichtbar sein und nicht in einer Vorgabe verschwinden.
/// </para>
/// </summary>
internal static class ImportWorld
{
    /// <summary>Eine Instanz, auf der native Ausführung ausdrücklich erlaubt ist (ADR-0025 E2-Ausnahme).</summary>
    public static ConfigurationImporter Permissive()
        => ConfigurationImporter.CreateDefault(HostExecutionPolicy.AllowedByOperator());

    /// <summary>Eine frische Instanz: native Ausführung ist verboten (ADR-0025 E2).</summary>
    public static ConfigurationImporter Strict()
        => ConfigurationImporter.CreateDefault(HostExecutionPolicy.FreshInstance());

    /// <summary>Die Policy einer Instanz, die native Ausführung erlaubt.</summary>
    public static IHostExecutionPolicy Allowing => HostExecutionPolicy.AllowedByOperator();

    /// <summary>Ein Dokument mit genau einem stdio-Server und den angegebenen Feldern.</summary>
    public static string Stdio(string name, string command, string? argumentsJson = null, string? extra = null)
        => $$"""
        {
          "mcpServers": {
            "{{name}}": {
              "command": "{{command}}"{{(argumentsJson is null ? string.Empty : $", \"args\": {argumentsJson}")}}{{(extra is null ? string.Empty : $", {extra}")}}
            }
          }
        }
        """;

    /// <summary>Ein Dokument mit genau einem HTTP-Server.</summary>
    public static string Http(string name, string url, string? extra = null)
        => $$"""
        {
          "mcpServers": {
            "{{name}}": {
              "type": "http",
              "url": "{{url}}"{{(extra is null ? string.Empty : $", {extra}")}}
            }
          }
        }
        """;

    /// <summary>Die Codes aller Befunde eines Plans — die des Plans und die der Kandidaten.</summary>
    public static IReadOnlyList<string> AllCodes(this ImportPlan plan)
        => [.. plan.Findings.Concat(plan.Candidates.SelectMany(c => c.Findings)).Select(f => f.Code)];

    /// <summary>Alle Befunde eines Plans, egal auf welcher Ebene sie stehen.</summary>
    public static IReadOnlyList<ImportFinding> AllFindings(this ImportPlan plan)
        => [.. plan.Findings.Concat(plan.Candidates.SelectMany(c => c.Findings))];
}
