using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Compatibility.Tests.Matrix;

/// <summary>
/// Die Kompatibilitätsmatrix aus <c>tests/compatibility/matrix.json</c> — die einzige Quelle für
/// jede öffentliche Kompatibilitätsaussage (M5-Vertrag §5).
/// <para>
/// Das Modell ist bewusst als Text-in-Text gehalten (Werte als <c>string</c> statt <c>enum</c>):
/// Ein unbekannter Wert soll als <b>Validierungsfehler mit Fundstelle</b> auftauchen und nicht als
/// Deserialisierungsausnahme ohne Zeilenbezug. Wer die Matrix von Hand pflegt, bekommt sonst
/// „JsonException" statt „Zeile 3: Feature 'tols' gibt es nicht".
/// </para>
/// </summary>
public sealed record CompatibilityMatrix
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<CompatibilityEntry> Entries { get; init; } = [];
}

public sealed record CompatibilityEntry
{
    [JsonPropertyName("client")]
    public string Client { get; init; } = string.Empty;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; init; } = string.Empty;

    /// <summary>linux | windows | macos</summary>
    [JsonPropertyName("os")]
    public string Os { get; init; } = string.Empty;

    /// <summary>stdio | http</summary>
    [JsonPropertyName("transport")]
    public string Transport { get; init; } = string.Empty;

    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = string.Empty;

    /// <summary>stateless | session (ADR-0023)</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("features")]
    public IReadOnlyDictionary<string, string> Features { get; init; } =
        new Dictionary<string, string>();

    [JsonPropertyName("evidence")]
    public CompatibilityEvidence? Evidence { get; init; }

    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>Der Schlüssel, unter dem eine Zeile eindeutig ist — und den ein Lauf sucht.</summary>
    [JsonIgnore]
    public string Key => $"{Client}/{ClientVersion}/{Os}/{Transport}/{Mode}";

    [JsonIgnore]
    public bool IsPlaceholder =>
        Features.Values.All(v => v == CompatibilityValues.Untested);
}

public sealed record CompatibilityEvidence
{
    /// <summary>automated | manual</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Lauf-Referenz (CI-URL, Job) oder Pfad des handgeführten Protokolls.</summary>
    [JsonPropertyName("ref")]
    public string Ref { get; init; } = string.Empty;

    /// <summary>ISO-Datum (yyyy-MM-dd) des Laufs bzw. der Handprüfung.</summary>
    [JsonPropertyName("date")]
    public string Date { get; init; } = string.Empty;

    /// <summary>Bei <c>manual</c> Pflicht: wer geprüft hat (M5-Vertrag §1.4).</summary>
    [JsonPropertyName("operator")]
    public string? Operator { get; init; }
}

/// <summary>Der geschlossene Feature-Wortschatz (M5-Vertrag §5).</summary>
public static class CompatibilityFeatures
{
    public const string Tools = "tools";
    public const string Resources = "resources";
    public const string Prompts = "prompts";
    public const string Elicitation = "elicitation";
    public const string Approval = "approval";
    public const string ListChanged = "listChanged";
    public const string OAuth = "oauth";
    public const string Skills = "skills";

    /// <summary>
    /// Alle acht Schlüssel sind in jeder Zeile Pflicht. Ein fehlender Schlüssel wäre genau das,
    /// was <c>untested</c> verhindern soll: eine Lücke, die wie eine Nichtaussage aussieht.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
        [Tools, Resources, Prompts, Elicitation, Approval, ListChanged, OAuth, Skills];
}

public static class CompatibilityValues
{
    public const string Pass = "pass";
    public const string Partial = "partial";
    public const string Fail = "fail";
    public const string Unsupported = "unsupported";
    public const string Untested = "untested";

    public static IReadOnlyList<string> All { get; } = [Pass, Partial, Fail, Unsupported, Untested];
}

public static class CompatibilityVocabulary
{
    public static IReadOnlyList<string> OperatingSystems { get; } = ["linux", "windows", "macos"];

    public static IReadOnlyList<string> Transports { get; } = ["stdio", "http"];

    public static IReadOnlyList<string> Modes { get; } = ["stateless", "session"];

    public static IReadOnlyList<string> EvidenceKinds { get; } = ["automated", "manual"];

    /// <summary>
    /// FR-P061: Diese Clients werden regelmäßig geprüft. Fehlt einer, fehlt er nicht in der
    /// Matrix, sondern steht dort auf <c>untested</c> — die Lücke wird benannt, nicht verschwiegen.
    /// </summary>
    public static IReadOnlyList<string> MandatoryClients { get; } =
        ["claude-code", "claude-desktop", "cursor", "vscode", "codex", "mcp-inspector"];
}

public static class CompatibilityMatrixFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Ohne das stehen Umlaute und Gedankenstriche als \uXXXX in einer Datei, die von Hand
        // gepflegt wird — eine Einschraenkung, die niemand liest, ist keine Einschraenkung.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CompatibilityMatrix Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<CompatibilityMatrix>(stream, Options)
            ?? throw new InvalidOperationException($"Matrix '{path}' ist leer.");
    }

    public static CompatibilityMatrix Parse(string json) =>
        JsonSerializer.Deserialize<CompatibilityMatrix>(json, Options)
            ?? throw new InvalidOperationException("Matrix ist leer.");

    public static void Write(string path, CompatibilityMatrix matrix) =>
        File.WriteAllText(path, JsonSerializer.Serialize(matrix, Options) + Environment.NewLine);
}
