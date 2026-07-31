using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Packaging;
using Bifrost.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Der Paketteil von ADR-0016 am laufenden Gateway: ein signiertes Paket wird installiert, in
/// Quarantäne <b>wirklich</b> geprobt (echter Rust-Host, echtes WebAssembly), aktiviert — und
/// danach läuft ein Upstream darüber, der nur die Paket-Id kennt.
/// <para>
/// Ohne diesen Test wäre die Paketverwaltung eine Struktur ohne Anschluss: Man könnte installieren,
/// aber nichts damit betreiben.
/// </para>
/// <para>
/// <b>Der Klassenname traegt <c>WasiRealHost</c> mit Absicht:</b> Die CI waehlt danach aus. Der Job
/// <c>build-test</c> schliesst dieses Praefix aus (dort gibt es kein Rust-Binary, ein Skip wuerde
/// das Skip-Gate reissen), der Job <c>wasi-component-spike</c> waehlt genau danach aus. Ohne das
/// Praefix liefe dieser Nachweis in <em>keinem</em> Job — und niemand haette es gemerkt.
/// </para>
/// </summary>
public sealed class WasiRealHostPackageE2ETests : IClassFixture<GatewayFixture>
{
    private const string PackageId = "com.bifrost.fixture-guest";

    private readonly GatewayFixture _gw;

    public WasiRealHostPackageE2ETests(GatewayFixture gw) => _gw = gw;

    /// <summary>
    /// Der ganze Weg: bauen → installieren (mit Probe) → Upstream über die Paket-Id → Aufruf durch
    /// die volle Pipeline → aktualisieren → zurückrollen.
    /// </summary>
    [Fact]
    public async Task A_signed_package_becomes_a_running_upstream_and_can_be_rolled_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = WasiHostPaths.RequireHost();
        var previousHost = Environment.GetEnvironmentVariable("BIFROST_WASI_HOST");
        Environment.SetEnvironmentVariable("BIFROST_WASI_HOST", host);

        try
        {
            var maintainer = await PinMaintainerAsync(ct);
            var installer = _gw.Services.GetRequiredService<ConnectorPackageInstaller>();
            var resolver = _gw.Services.GetRequiredService<ConnectorPackageResolver>();

            // 1) Installieren. Die Probe startet den echten Host und fragt den Katalog ab; ein
            //    Paket, das das nicht übersteht, käme hier nicht durch.
            using var v1 = await BuildPackageAsync(maintainer, "1.0.0", ct);
            var installed = await installer.InstallAsync(
                v1, new ConnectorInstallOptions(AcceptedGrants: ["env:MCPMCP_SPIKE"]), null, ct);
            await resolver.RefreshAsync(ct);

            installed.Package.State.Should().Be(PackageState.Active);
            installed.Package.TrustLevel.Should().Be(ConnectorTrustLevel.Official);

            // 2) Ein Upstream, der nur die Paket-Id kennt — keine Pfade in der Konfiguration.
            var serverId = await _gw.Supervisor.AddAsync(
                new UpstreamServerConfig(
                    "pkgguest", "Guest aus dem Paket", UpstreamTransportKind.Wasi, Enabled: true,
                    Wasi: new WasiTransportOptions(
                        host,
                        ComponentPath: string.Empty,
                        SignaturePath: string.Empty,
                        PinnedPublishers: [],
                        Grants: new WasiCapabilityGrants(Environment: ["MCPMCP_SPIKE"]),
                        PackageId: PackageId)),
                ct);
            await IntegrationSupport.WaitUntilAsync(
                () => _gw.Supervisor.GetStatus(serverId)?.State == UpstreamState.Healthy,
                because: "der Upstream muss allein über die Paket-Id hochkommen");

            // 3) Aufruf durch die volle Governance-Pipeline.
            var (admin, _) = await _gw.SeedAdminAsync("pkg-admin");
            var tool = _gw.Supervisor.GetInventory(serverId)!.Tools[0].Name;
            var result = await _gw.Invoker.InvokeAsync(
                new ToolInvocationRequest(
                    admin, CallOrigin.Rest, new NamespacedToolName($"pkgguest__{tool}"),
                    JsonSerializer.Deserialize<JsonElement>("{}"), null),
                ct);

            result.Status.Should().Be(InvocationStatus.Success, result.ErrorMessage);

            // 4) Update und Rollback: Die Dateien wechseln, die Konfiguration bleibt unangetastet.
            using var v2 = await BuildPackageAsync(maintainer, "2.0.0", ct);
            await installer.InstallAsync(
                v2, new ConnectorInstallOptions(AcceptedGrants: ["env:MCPMCP_SPIKE"]), null, ct);
            await resolver.RefreshAsync(ct);
            resolver.ResolveActive(PackageId)!.Value.EntryPoint.Should().Contain("2.0.0");

            await installer.RollbackAsync(PackageId, null, ct);
            await resolver.RefreshAsync(ct);
            resolver.ResolveActive(PackageId)!.Value.EntryPoint.Should().Contain("1.0.0");

            await _gw.Supervisor.RemoveAsync(serverId, DrainPolicy.Immediate, ct);
            await installer.RemovePackageAsync(PackageId, null, ct);
            await resolver.RefreshAsync(ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BIFROST_WASI_HOST", previousHost);
        }
    }

    /// <summary>
    /// Pinnt den Herausgeber, der die <em>Manifeste</em> signiert, und den der Fixture-Component.
    /// Zwei Signaturen, zwei Prüfer: Das Manifest prüft das Gateway, die Component-Bytes der Host.
    /// </summary>
    private async Task<TestSigner> PinMaintainerAsync(CancellationToken ct)
    {
        var trust = _gw.Services.GetRequiredService<PublisherTrustStore>();
        var componentPublisher = (await File.ReadAllTextAsync(WasiHostPaths.PublisherPath, ct)).Trim();
        await trust.PinAsync(componentPublisher, "fixture-component", ct);

        var maintainer = new TestSigner();
        var key = await trust.PinAsync(maintainer.PublicKeyBase64, "paket-herausgeber", ct);
        await trust.SetTrustLevelAsync(key.KeyId, ConnectorTrustLevel.Official, ct);
        return maintainer;
    }

    private static async Task<MemoryStream> BuildPackageAsync(
        TestSigner maintainer, string version, CancellationToken ct)
    {
        var component = await File.ReadAllBytesAsync(WasiHostPaths.ComponentPath, ct);
        var signature = await File.ReadAllBytesAsync(WasiHostPaths.SignaturePath, ct);
        return new MemoryStream(ConnectorPackageBuilder.Build(
            new ConnectorManifest(
                ConnectorManifest.SchemaV1, PackageId, version,
                ConnectorManifest.SupportedContractVersion, maintainer.KeyId,
                "WASI-Fixture-Guest", UpstreamTransportKind.Wasi,
                "payload/guest.component.wasm", "payload/guest.component.sig",
                Payloads: [],
                // Der Guest importiert wasi:cli/environment — ohne diesen Grant startet er nicht,
                // und die Probe fiele durch, ohne dass am Paket etwas falsch wäre.
                Grants: new ConnectorGrantRequest(Environment: ["MCPMCP_SPIKE"])),
            new Dictionary<string, byte[]>
            {
                ["payload/guest.component.wasm"] = component,
                ["payload/guest.component.sig"] = signature,
            },
            maintainer.Sign));
    }

    /// <summary>Ein echtes Ed25519-Schlüsselpaar zum Signieren der Manifeste im Test.</summary>
    private sealed class TestSigner
    {
        private readonly Ed25519PrivateKeyParameters _private;

        public TestSigner()
        {
            _private = new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
            var publicKey = _private.GeneratePublicKey().GetEncoded();
            PublicKeyBase64 = Convert.ToBase64String(publicKey);
            KeyId = Convert.ToHexStringLower(SHA256.HashData(publicKey));
        }

        public string KeyId { get; }

        public string PublicKeyBase64 { get; }

        public byte[] Sign(byte[] message)
        {
            var signer = new Ed25519Signer();
            signer.Init(forSigning: true, _private);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }
    }
}
