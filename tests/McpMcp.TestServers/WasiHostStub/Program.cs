using System.Buffers.Binary;
using System.Text.Json;

// Stub des WASI-Runtime-Hosts (ADR-0020, Plan 0003/WP2). Spricht denselben IPC-Vertrag wie das
// echte Rust-Binary — length-prefixed JSON über stdio —, führt aber kein WebAssembly aus.
//
// Zweck: den .NET-Connector deterministisch und ohne Rust-Toolchain testbar machen. Die echte
// Runtime-Semantik (Signaturprüfung, Grants, Limits) ist auf der Rust-Seite getestet; die
// Wire-Kompatibilität zwischen beiden Seiten belegen die WasiRealHostCompatibilityTests gegen das
// gebaute Rust-Binary (Plan 0003, WP6.2) — dieser Stub beweist sie ausdrücklich NICHT.
//
// Verhalten wird über das erste Argument gesteuert:
//   host              — normaler Vertragsablauf
//   host --bad-protocol — meldet eine fremde Protokollversion (Handshake muss scheitern)
//   host --reject-load  — weist load ab (simuliert eine ungültige Signatur)

var mode = args.Length > 1 ? args[1] : string.Empty;
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var loaded = false;
string? moduleSha256 = null;

while (true)
{
    var header = new byte[4];
    try
    {
        await stdin.ReadExactlyAsync(header);
    }
    catch (EndOfStreamException)
    {
        return 0; // sauberes EOF
    }

    var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
    await stdin.ReadExactlyAsync(body);

    using var request = JsonDocument.Parse(body);
    var type = request.RootElement.GetProperty("type").GetString();

    object response = type switch
    {
        "hello" => new
        {
            type = "hello",
            protocolVersion = mode == "--bad-protocol" ? "999" : "2",
            runtime = "stub",
            host = "wasi-host-stub/0.1.0",
        },
        "load" when mode == "--reject-load" => new
        {
            type = "error",
            code = "load-rejected",
            message = "component signature matches no pinned publisher",
        },
        "load" => Load(request.RootElement, ref loaded, ref moduleSha256),
        "discover" when !loaded => NotLoaded(),
        "discover" when mode == "--odd-names" => new { type = "discovered", tools = OddlyNamedTools() },
        "discover" => new { type = "discovered", tools = Tools() },
        "invoke" when !loaded => NotLoaded(),
        "invoke" => Invoke(request.RootElement),
        "health" => new
        {
            type = "health",
            status = "ok",
            loaded,
            moduleSha256 = moduleSha256,
            // Der Stub kompiliert nichts; die Felder existieren, damit die Wire-Form stimmt.
            cache = new { entries = loaded ? 1 : 0, hits = 0, misses = loaded ? 1 : 0, lastCompileMs = 0.0, totalCompileMs = 0.0 },
        },
        "shutdown" => new { type = "bye" },
        _ => new { type = "error", code = "bad-request", message = $"unbekannter Typ '{type}'" },
    };

    var payload = JsonSerializer.SerializeToUtf8Bytes(response);
    var length = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
    await stdout.WriteAsync(length);
    await stdout.WriteAsync(payload);
    await stdout.FlushAsync();

    if (type == "shutdown")
    {
        return 0;
    }
}

static object NotLoaded()
    => new { type = "error", code = "not-loaded", message = "kein Component geladen" };

// Typisierte Discovery wie im echten Host (Vertrag v2, WP6.1): ein Kommando-Einstiegspunkt, zwei
// typisierte Funktionen und ein Export, den der Host nicht aufrufen kann — letzterer darf im
// Katalog nicht erscheinen.
static object[] Tools() =>
[
    new
    {
        name = "wasi:cli/run@0.2.6",
        kind = "command",
        @params = Array.Empty<object>(),
        results = Array.Empty<string>(),
        supported = true,
    },
    new
    {
        name = "double",
        kind = "function",
        @params = new[] { new { name = "value", type = "s32" } },
        results = new[] { "s32" },
        supported = true,
    },
    // Gibt ein Secret auf stdout aus — Prüfpunkt für die eingehende Guardrail.
    new
    {
        name = "leak",
        kind = "function",
        @params = new[] { new { name = "value", type = "s32" } },
        results = new[] { "s32" },
        supported = true,
    },
    new
    {
        name = "grow",
        kind = "function",
        @params = new[] { new { name = "pages", type = "u32" } },
        results = new[] { "s32" },
        supported = false,
        unsupportedReason = "nur (s32) -> s32 wird aufgerufen, dieser Export ist (u32) -> (s32)",
    },
];

// Export-Namen, die ein Component tragen darf, ein Katalogname aber nicht: Sonderzeichen,
// Versionsanhänge und zwei Interfaces, die sich nach der Normalisierung überschneiden.
static object[] OddlyNamedTools() =>
[
    Named("ns:pkg/iface@1.0.0"),
    Named("ns:pkg/iface@2.0.0"),
    Named("weird name!!"),
    Named("@1.0.0"),
];

static object Named(string name) => new
{
    name,
    kind = "command",
    @params = Array.Empty<object>(),
    results = Array.Empty<string>(),
    supported = true,
};

static object Load(JsonElement request, ref bool loaded, ref string? loadedSha256)
{
    // Fail-closed wie der echte Host: ohne gepinnten Publisher wird nichts geladen.
    if (!request.TryGetProperty("pinnedPublishers", out var pinned)
        || pinned.ValueKind is not JsonValueKind.Array
        || pinned.GetArrayLength() == 0)
    {
        // Wie der echte Host: mit bereits geladenem Component ist ein Fehlschlag ein Rollback.
        return loaded
            ? new { type = "error", code = "load-rolled-back", message = "kein gepinnter Publisher" }
            : new { type = "error", code = "load-rejected", message = "kein gepinnter Publisher" };
    }

    loaded = true;
    var component = Convert.FromBase64String(request.GetProperty("component").GetString()!);
    var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(component));
    var cached = loadedSha256 == sha256;
    loadedSha256 = sha256;
    var grants = request.GetProperty("grants");
    return new
    {
        type = "loaded",
        compileMs = cached ? 0.0 : 1.0,
        cached,
        audit = new
        {
            moduleSha256 = sha256,
            publisherKeyId = "stub-publisher",
            runtime = "stub",
            grantedFilesystemPreopens = ToArray(grants, "filesystemPreopens"),
            grantedNetworkAllow = ToArray(grants, "networkAllow"),
            grantedEnvironment = ToArray(grants, "environment"),
            grantedSecrets = ToArray(grants, "secrets"),
            grantedClock = grants.GetProperty("clock").GetBoolean(),
            grantedRandom = grants.GetProperty("random").GetBoolean(),
        },
    };
}

static object Invoke(JsonElement request)
{
    var tool = request.GetProperty("tool").GetString();
    var arguments = request.TryGetProperty("args", out var args) && args.ValueKind is JsonValueKind.Array
        ? args.EnumerateArray().Select(item => item.GetInt32()).ToArray()
        : [];

    return tool switch
    {
        "wasi:cli/run@0.2.6" => new
        {
            type = "invoked",
            stdout = "stub-guest-ok",
            truncated = false,
            result = (int?)null,
        },
        // Typisierter Export: verdoppelt sein Argument — beweist die Argumentübergabe.
        "double" => new
        {
            type = "invoked",
            stdout = string.Empty,
            truncated = false,
            result = (int?)(arguments.Length > 0 ? arguments[0] * 2 : 0),
        },
        // Ein Guest darf alles auf stdout schreiben — auch ein Secret. Der Beispielschlüssel
        // stammt aus der AWS-Dokumentation und ist keiner.
        "leak" => new
        {
            type = "invoked",
            stdout = "token=AKIAIOSFODNN7EXAMPLE",
            truncated = false,
            result = (int?)null,
        },
        _ => new
        {
            type = "error",
            code = "invoke-failed",
            message = $"tool '{tool}' is not exported by this component",
        },
    };
}

static string[] ToArray(JsonElement grants, string name)
    => grants.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Array
        ? [.. value.EnumerateArray().Select(item => item.GetString() ?? string.Empty)]
        : [];
