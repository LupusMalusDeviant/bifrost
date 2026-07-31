using Bifrost.Abstractions;
using Bifrost.Core.Execution;
using Bifrost.Core.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Core.Tests.Configuration;

/// <summary>
/// Aufbauten für die Exporttests.
/// <para>
/// <b>Der Negativkorpus</b> ist der Kern: erfundene, paarweise verschiedene Zeichenfolgen, je eine
/// für jede Stelle im Konfigurationsbaum, an der ein Zugangsdatum stehen kann. Ein Standardexport,
/// in dem auch nur ein Bruchstück davon auftaucht, ist ein Leck — und genau das prüft
/// <see cref="ConfigurationSecretExportTests"/>.
/// </para>
/// </summary>
internal static class ConfigurationFixtures
{
    public const string ProductVersion = "0.11.0-test";

    // Erfunden, keine echten Zugangsdaten. Bewusst ohne gemeinsame Teilzeichenfolge, damit ein
    // Treffer im Export eindeutig einer Stelle zuzuordnen ist.
    public const string StdioEnvSecret = "QX7ZM2K9FTB4WR1DPSY6";
    public const string HttpHeaderSecret = "VJ3NC8HAL5GTQ2ZKMR7E";
    public const string OAuthClientSecret = "BD9WY4XPUF6SJ1AZOL3N";
    public const string OpenApiSecret = "TM5RK2QVZH8CDX7GNW4B";
    public const string OpenRpcSecret = "PL6EJ3YSFN9UAT2KVCX8";
    public const string CliEnvSecret = "GHW1QDZ7BMR4XKF5PSNJ";
    public const string WasiSecret = "ZCF8VUT3RA6EYN2LQKMD";

    public static DateTimeOffset Now { get; } = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<string> NegativeCorpus { get; } =
    [
        StdioEnvSecret,
        HttpHeaderSecret,
        OAuthClientSecret,
        OpenApiSecret,
        OpenRpcSecret,
        CliEnvSecret,
        WasiSecret,
    ];

    /// <summary>
    /// Der Dienst mit ausdruecklich erlaubter Host-Ausfuehrung. Die Beispielinstanz enthaelt an
    /// jeder moeglichen Stelle ein Zugangsdatum — darunter stdio- und CLI-Upstreams, die nativ
    /// laufen. Ohne die Erlaubnis pruefte jeder Import-Test nebenbei die Ausfuehrungs-Policy statt
    /// dessen, was er pruefen will; die Policy hat ihre eigenen Tests unter Execution/.
    /// </summary>
    public static ConfigurationExportService ServiceFor(FakeInstance instance)
        => new(instance, instance, new FakeTimeProvider(Now), ProductVersion,
            HostExecutionPolicy.AllowedByOperator());

    /// <summary>Derselbe Dienst auf einer Instanz, die native Ausfuehrung verbietet (ADR-0025 E2).</summary>
    public static ConfigurationExportService ForbiddingServiceFor(FakeInstance instance)
        => new(instance, instance, new FakeTimeProvider(Now), ProductVersion,
            HostExecutionPolicy.FreshInstance());

    /// <summary>Eine Instanz, in der an jeder möglichen Stelle ein Zugangsdatum steht.</summary>
    public static FakeInstance WithSecretsEverywhere()
    {
        var instance = new FakeInstance
        {
            NonPortable = new NonPortableInventory(Identities: 2, ApiKeys: 3, Webhooks: 1, UpstreamOAuthTokens: 1),
        };

        instance.Upstreams.Add(new UpstreamSnapshot(
            new ServerId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new UpstreamServerConfig(
                "mit-stdio", "Stdio-Server", UpstreamTransportKind.Stdio, Enabled: true,
                Stdio: new StdioTransportOptions(
                    "node",
                    ["server.js"],
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["GITHUB_TOKEN"] = StdioEnvSecret }))));

        instance.Upstreams.Add(new UpstreamSnapshot(
            new ServerId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new UpstreamServerConfig(
                "mit-http", "HTTP-Server", UpstreamTransportKind.StreamableHttp, Enabled: true,
                Http: new HttpTransportOptions(
                    new Uri("https://beispiel.invalid/mcp"),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = HttpHeaderSecret },
                    AllowLegacySse: true,
                    OAuth: new UpstreamOAuthOptions("client-1", OAuthClientSecret)))));

        instance.Upstreams.Add(new UpstreamSnapshot(
            new ServerId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            new UpstreamServerConfig(
                "mit-openapi", "OpenAPI-Quelle", UpstreamTransportKind.OpenApi, Enabled: true,
                OpenApi: new OpenApiTransportOptions(
                    new Uri("https://beispiel.invalid/openapi.json"),
                    AuthKind: OpenApiAuthKind.Bearer,
                    Credential: OpenApiSecret))));

        instance.Upstreams.Add(new UpstreamSnapshot(
            new ServerId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
            new UpstreamServerConfig(
                "mit-openrpc", "OpenRPC-Dienst", UpstreamTransportKind.OpenRpc, Enabled: true,
                OpenRpc: new OpenRpcTransportOptions(
                    new Uri("https://beispiel.invalid/rpc"),
                    AuthKind: OpenApiAuthKind.Bearer,
                    Credential: OpenRpcSecret))));

        instance.Upstreams.Add(new UpstreamSnapshot(
            new ServerId(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            new UpstreamServerConfig(
                "mit-cli", "CLI-Programm", UpstreamTransportKind.Cli, Enabled: true,
                Cli: new CliTransportOptions(
                    "/usr/bin/werkzeug",
                    [new CliToolSpec("lauf")],
                    EnvironmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["API_KEY"] = CliEnvSecret,
                    }))));

        instance.Upstreams.Add(new UpstreamSnapshot(
            new ServerId(Guid.Parse("66666666-6666-6666-6666-666666666666")),
            new UpstreamServerConfig(
                "mit-wasi", "WASI-Component", UpstreamTransportKind.Wasi, Enabled: true,
                Wasi: new WasiTransportOptions(
                    "bifrost-wasi-host",
                    "component.wasm",
                    "component.sig",
                    ["cHVibGlzaGVy"],
                    Secrets: new Dictionary<string, string>(StringComparer.Ordinal) { ["TOKEN"] = WasiSecret }))));

        return instance;
    }

    /// <summary>
    /// Eine vollständige, aber <b>zugangsdatenfreie</b> Instanz. Sie ist die Grundlage des
    /// Roundtrips: Erst ohne Geheimnisse lässt sich „semantisch gleich" überhaupt behaupten — mit
    /// Geheimnissen wäre der Standardexport ja absichtlich ärmer als die Quelle.
    /// </summary>
    public static FakeInstance SecretFree()
    {
        var serverId = new ServerId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var instance = new FakeInstance
        {
            Settings = new InstanceSettings(
                new GuardOptions { Enabled = true, MaxScanChars = 128 * 1024, AllowCustomPatterns = true },
                ApprovalEnforcement.Client),
        };

        instance.Upstreams.Add(new UpstreamSnapshot(
            serverId,
            new UpstreamServerConfig(
                "wetter", "Wetterdienst", UpstreamTransportKind.StreamableHttp, Enabled: true,
                Http: new HttpTransportOptions(new Uri("https://wetter.invalid/mcp")),
                CallTimeout: TimeSpan.FromSeconds(30))));

        instance.Roles.Add(new Role(
            new RoleId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            "Leser",
            [new Grant(new PermissionScope(serverId, null), [ToolAction.UseTool, ToolAction.ReadResource])],
            new RateLimit(60)));

        instance.Profiles.Add(new ToolProfile(
            new ProfileId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            "Standard",
            [NamespacedToolName.Create("wetter", "vorhersage")],
            LazyToolsEnabled: true));

        instance.GuardRules.Add(new GuardRule(
            "eigene-kundennummer",
            "Interne Kundennummern",
            "KD-[0-9]{6}",
            "KD-",
            GuardDirection.Both,
            GuardMode.Block,
            Enabled: true,
            IsCustom: true));

        instance.Approvals["wetter__unwetterwarnung_senden"] = ApprovalEnforcement.Queue;

        instance.Skills.Add(new SkillSnapshot(
            new AssetId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
            "wetter-abfragen",
            "Wie eine Vorhersage geholt wird.",
            "# Wetter abfragen\n\nZuerst den Ort klaeren, dann `wetter__vorhersage` aufrufen.",
            new SkillMetadata("Wenn nach dem Wetter gefragt wird.", null, ["wetter__vorhersage"])));

        return instance;
    }
}
