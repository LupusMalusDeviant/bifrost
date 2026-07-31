using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bifrost.Abstractions;

/// <summary>
/// Art einer Fähigkeit ([ADR-0015](../../docs/adr/0015-protokollneutrales-capability-modell.md)).
/// <para>
/// Die Zahlen werden persistiert — neue Werte gehören ans Ende.
/// </para>
/// </summary>
public enum CapabilityKind
{
    /// <summary>Ein Befehl mit Seiteneffekt oder Berechnung — der heutige Tool-Call.</summary>
    Tool = 0,

    /// <summary>Lesende Abfrage ohne Seiteneffekt.</summary>
    Query = 1,

    /// <summary>Schreibende Operation.</summary>
    Mutation = 2,

    /// <summary>Adressierbarer Inhalt (MCP-Resource).</summary>
    Resource = 3,

    /// <summary>Vorlage für ein Modell (MCP-Prompt).</summary>
    Prompt = 4,

    /// <summary>Langlaufender Vorgang; liefert eine Task-Id statt eines Ergebnisses (ADR-0019).</summary>
    Task = 5,

    /// <summary>Ein Ereignis aus einem Upstream. **Noch nicht öffentlich** — siehe <see cref="CapabilityKinds"/>.</summary>
    Event = 6,

    /// <summary>Ein Abonnement auf Ereignisse. **Noch nicht öffentlich.**</summary>
    Subscription = 7,

    /// <summary>Delegation an einen fremden Agenten. **Noch nicht öffentlich.**</summary>
    AgentDelegation = 8,
}

/// <summary>
/// Welche Arten der Katalog tatsächlich anbieten darf.
/// <para>
/// ADR-0015 macht das an ADR-0019 fest: Task-, Event- und Stream-Arten werden erst öffentlich, wenn
/// deren Persistenz und Berechtigungen stehen. Für <see cref="CapabilityKind.Task"/> ist das seit
/// dem 2026-07-26 der Fall. Für <see cref="CapabilityKind.Event"/> und
/// <see cref="CapabilityKind.Subscription"/> <b>nicht</b>: ADR-0019 hat EventV1 mit Zustellzusage
/// ausdrücklich vertagt, es gibt also keine Persistenz, auf die man sie stellen könnte.
/// <see cref="CapabilityKind.AgentDelegation"/> wartet auf ADR-0013 (A2A).
/// </para>
/// <para>
/// Die Arten stehen trotzdem schon im Enum: Ein Connector soll seine Fähigkeiten benennen können,
/// auch wenn das Gateway sie noch nicht anbietet. Was fehlt, ist die Freigabe — nicht das Wort.
/// </para>
/// </summary>
public static class CapabilityKinds
{
    /// <summary>Arten, die der Katalog anbieten darf.</summary>
    public static bool IsPubliclyOffered(CapabilityKind kind) => kind
        is CapabilityKind.Tool
        or CapabilityKind.Query
        or CapabilityKind.Mutation
        or CapabilityKind.Resource
        or CapabilityKind.Prompt
        or CapabilityKind.Task;

    /// <summary>Warum eine Art nicht angeboten wird — für Diagnose statt stillem Weglassen.</summary>
    public static string? WhyNotOffered(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Event or CapabilityKind.Subscription =>
            "EventV1 mit Zustellzusage ist in ADR-0019 vertagt — es gibt keine Event-Persistenz.",
        CapabilityKind.AgentDelegation =>
            "Agentendelegation wartet auf ADR-0013 (A2A): Delegationsbudgets und Loop-Erkennung fehlen.",
        _ => null,
    };
}

/// <summary>Wie eine Fähigkeit ausgeführt wird.</summary>
public enum CapabilityExecution
{
    /// <summary>Ergebnis kommt im Aufruf zurück.</summary>
    Synchronous = 0,

    /// <summary>Aufruf liefert eine Task-Id; das Ergebnis wird geholt (ADR-0019).</summary>
    Asynchronous = 1,

    /// <summary>
    /// Ergebnis kommt in Stücken. **Noch nirgends umgesetzt** — beim WASI-Host bewusst
    /// zurückgestellt, weil ein dynamischer Host Streams nur für fest einkompilierte Payload-Typen
    /// lesen kann.
    /// </summary>
    Streaming = 2,
}

/// <summary>
/// Herkunft und Fassung eines Schemas. Ohne diese Angaben wäre ein Schema im Katalog eine Behauptung
/// ohne Quelle: Man wüsste nicht, ob es aus dem Upstream stammt oder das Gateway es erzeugt hat, und
/// eine Vertragsänderung fiele niemandem auf.
/// </summary>
/// <param name="Dialect">JSON-Schema-Dialekt, z. B. <c>https://json-schema.org/draft/2020-12/schema</c>.</param>
/// <param name="Provenance">Woher es kommt — nativ aus dem Upstream oder vom Gateway abgeleitet.</param>
/// <param name="Hash">SHA-256 über das kanonische Schema; macht eine Änderung sichtbar.</param>
/// <param name="NativeVersion">Fassung, die der Upstream selbst nennt, wenn er eine nennt.</param>
public sealed record SchemaRef(
    string Dialect,
    SchemaProvenance Provenance,
    string Hash,
    string? NativeVersion = null)
{
    public const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// Der Aufruf nimmt keine Argumente. Eigener Wert statt <c>null</c>: „kein Schema" und
    /// „Schema unbekannt" sind verschiedene Aussagen, und nur die erste ist hier gemeint.
    /// </summary>
    public static SchemaRef None { get; } =
        new(Draft202012, SchemaProvenance.None, Sha256Hex("{}"));

    /// <summary>Baut die Referenz aus einem Schema; der Hash entsteht über den Rohtext.</summary>
    public static SchemaRef ForSchema(
        JsonElement schema,
        SchemaProvenance provenance,
        string? nativeVersion = null,
        string dialect = Draft202012)
        => new(dialect, provenance, Sha256Hex(schema.GetRawText()), nativeVersion);

    internal static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Woher ein Schema stammt.</summary>
public enum SchemaProvenance
{
    /// <summary>Der Upstream hat es so geliefert.</summary>
    Native = 0,

    /// <summary>Das Gateway hat es abgeleitet, etwa aus WIT-Typbäumen oder einem CLI-Manifest.</summary>
    Derived = 1,

    /// <summary>Es gibt keins — der Aufruf nimmt keine Argumente.</summary>
    None = 2,
}

/// <summary>
/// Stabile Kennung einer Fähigkeit.
/// <para>
/// Abgeleitet aus <see cref="ServerId"/> und <b>nativem</b> Namen. ADR-0015 nennt in der Aufzählung
/// zusätzlich Connector-Id und Schema-Version; beides bleibt hier bewusst draußen:
/// </para>
/// <list type="bullet">
/// <item>
/// Die <b>Schema-Version</b> widerspräche dem eigenen Satz „IDs nicht ändern" — jeder zusätzliche
/// Parameter am Upstream ergäbe eine neue Id, und RBAC-Grants sowie gepinnte Profile brächen bei
/// jeder Schema-Pflege. Die Fassung steht im <see cref="SchemaRef.Hash"/> daneben und bleibt
/// sichtbar, ohne die Adressierung zu zerreißen.
/// </item>
/// <item>
/// Die <b>Connector-Id</b> trägt nichts zur Eindeutigkeit bei: Die <see cref="ServerId"/> ist schon
/// eindeutig, und die Transportart ist eine Eigenschaft dieses Upstreams, keine eigene Achse. Sie
/// mitzuhashen hätte nur bedeutet, dass die Id ohne Konfigurationszugriff nicht mehr ableitbar ist.
/// </item>
/// </list>
/// <para>
/// Auch nicht Teil der Id: Anzeigename und Beschreibung. Die dürfen sich ändern.
/// </para>
/// </summary>
public readonly record struct CapabilityId
{
    public string Value { get; }

    public CapabilityId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>
    /// Leitet die Kennung deterministisch ab. Derselbe Upstream mit demselben nativen Namen ergibt
    /// über Prozessgrenzen und Neustarts hinweg dieselbe Id — sonst wäre „persistiert" wertlos.
    /// </summary>
    public static CapabilityId Derive(ServerId upstream, string nativeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeName);
        var seed = string.Create(CultureInfo.InvariantCulture, $"{upstream.Value:N}:{nativeName}");
        // Gekürzt auf 32 Hex-Zeichen: kollisionssicher genug für einen Gateway-Katalog und kurz
        // genug, um in einer URL und einer Logzeile lesbar zu bleiben.
        return new CapabilityId($"cap_{SchemaRef.Sha256Hex(seed)[..32]}");
    }

    public override string ToString() => Value;
}

/// <summary>
/// Eine Fähigkeit, protokollneutral beschrieben (ADR-0015, `CapabilityDescriptorV1`).
/// <para>
/// Additiv neben <see cref="ToolDescriptor"/>, nicht als Ersatz: Die bestehenden Deskriptoren und
/// Endpunkte bleiben, bis eine eigene Migration sie ablöst. Die doppelte Deskriptorwelt ist der
/// bewusst gewählte Preis dafür, dass MCP-Verträge und Connectoren nicht auf einmal umkippen.
/// </para>
/// </summary>
public sealed record CapabilityDescriptorV1(
    CapabilityId Id,
    /// <summary>Der Name, unter dem der Upstream sie kennt — unverändert, auch mit Sonderzeichen.</summary>
    string NativeName,
    /// <summary>Der namespaced Katalogname, unter dem das Gateway sie anbietet.</summary>
    NamespacedToolName CatalogName,
    /// <summary>Anzeigename; darf sich ändern, ohne die <see cref="Id"/> zu berühren.</summary>
    string DisplayName,
    string? Description,
    CapabilityKind Kind,
    CapabilityExecution Execution,
    ServerId Upstream,
    /// <summary>
    /// Transportart des Upstreams — rein informativ. Sie steht in der Konfiguration, nicht im
    /// Katalog; wo sie beim Projizieren nicht vorliegt, bleibt sie <c>null</c> statt geraten.
    /// </summary>
    UpstreamTransportKind? Connector,
    /// <summary>Eingabeschema samt Herkunft; <c>null</c>, wenn der Aufruf keine Argumente nimmt.</summary>
    SchemaRef? Input,
    /// <summary>Ausgabeschema, wenn der Upstream eins nennt. Die meisten tun es nicht.</summary>
    SchemaRef? Output,
    /// <summary>Seiteneffekt — dieselbe Achse, die RBAC und Risk Classification schon nutzen.</summary>
    CapabilityRisk SideEffect,
    bool RequiresApproval,
    /// <summary>Ob ein Wiederholen desselben Aufrufs denselben Zustand ergibt.</summary>
    bool Idempotent,
    bool SupportsCancellation,
    bool SupportsProgress,
    bool SupportsBinary,
    bool SupportsPagination,
    /// <summary>Grobe Erwartung, nicht Zusage — Grundlage für Timeout-Wahl und Task-Entscheidung.</summary>
    TimeSpan? ExpectedDuration = null)
{
    /// <summary>Ob der Katalog diese Fähigkeit anbieten darf (siehe <see cref="CapabilityKinds"/>).</summary>
    public bool IsPubliclyOffered => CapabilityKinds.IsPubliclyOffered(Kind);
}

/// <summary>
/// Ergebnis eines Aufrufs als diskriminierte Hülle (ADR-0015, `CapabilityResultV1`).
/// <para>
/// Genau eines der Felder ist gesetzt. Der Sinn der Hülle: Ein Task, ein Artifact-Verweis und ein
/// strukturierter Fehler sind keine Textvarianten desselben Dings — sie ununterscheidbar in einen
/// String zu legen war genau die Verarmung, die ADR-0015 abstellt.
/// </para>
/// </summary>
public sealed record CapabilityResultV1
{
    private CapabilityResultV1() { }

    public CapabilityResultKind Kind { get; private init; }

    /// <summary>Strukturierte Daten bei <see cref="CapabilityResultKind.Structured"/>.</summary>
    public JsonElement? Data { get; private init; }

    /// <summary>Text bei <see cref="CapabilityResultKind.Text"/>.</summary>
    public string? Text { get; private init; }

    /// <summary>Begrenzte Binärdaten bei <see cref="CapabilityResultKind.Binary"/>.</summary>
    public ReadOnlyMemory<byte> Binary { get; private init; }

    public string? MimeType { get; private init; }

    /// <summary>Verweis auf ein Artifact bei <see cref="CapabilityResultKind.Artifact"/>.</summary>
    public Uri? Artifact { get; private init; }

    /// <summary>Task-Id bei <see cref="CapabilityResultKind.Task"/> — das Ergebnis wird geholt.</summary>
    public Guid? TaskId { get; private init; }

    /// <summary>Fehler bei <see cref="CapabilityResultKind.Error"/>.</summary>
    public CapabilityError? Error { get; private init; }

    /// <summary>Gesetzt, wenn gekürzt wurde — strukturiert, nicht als Textsuffix.</summary>
    public ResultTruncation? Truncation { get; private init; }

    public static CapabilityResultV1 Structured(JsonElement data, ResultTruncation? truncation = null)
        => new() { Kind = CapabilityResultKind.Structured, Data = data, Truncation = truncation };

    public static CapabilityResultV1 FromText(string text, ResultTruncation? truncation = null)
        => new() { Kind = CapabilityResultKind.Text, Text = text, Truncation = truncation };

    public static CapabilityResultV1 FromBinary(ReadOnlyMemory<byte> bytes, string mimeType)
        => new() { Kind = CapabilityResultKind.Binary, Binary = bytes, MimeType = mimeType };

    public static CapabilityResultV1 ArtifactRef(Uri uri, string? mimeType = null)
        => new() { Kind = CapabilityResultKind.Artifact, Artifact = uri, MimeType = mimeType };

    public static CapabilityResultV1 Accepted(Guid taskId)
        => new() { Kind = CapabilityResultKind.Task, TaskId = taskId };

    public static CapabilityResultV1 Failed(CapabilityError error)
        => new() { Kind = CapabilityResultKind.Error, Error = error };
}

public enum CapabilityResultKind
{
    Structured = 0,
    Text = 1,
    Binary = 2,
    Artifact = 3,
    Task = 4,
    Error = 5,
}

/// <summary>
/// Strukturierter Fehler (ADR-0015). Zwei Codes, weil zwei Fragen dahinterstehen: Der
/// <paramref name="GatewayCode"/> ist stabil und für Automaten gedacht, der
/// <paramref name="ConnectorCode"/> sagt, was der Upstream selbst gemeldet hat. Ein einziges
/// Fehlerfeld hätte beides vermischt.
/// </summary>
/// <param name="Retryable">Ob ein Wiederholen Aussicht hat — sonst rät der Aufrufer.</param>
/// <param name="Detail">Redigierte Einzelheiten; nie rohe Upstream-Ausgabe.</param>
public sealed record CapabilityError(
    string GatewayCode,
    string? ConnectorCode,
    string Message,
    bool Retryable,
    string? Detail = null);
