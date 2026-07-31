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
/// Die Paket-API ohne Laufzeit-Bedarf: Wer darf sie benutzen, und was passiert mit einem Paket, das
/// niemand kennt. Bewusst von <see cref="WasiRealHostPackageE2ETests"/> getrennt — diese Faelle
/// brauchen kein Rust-Binary und sollen deshalb im normalen CI-Job mitlaufen, nicht nur dort, wo der
/// Host gebaut wird.
/// </summary>
public sealed class ConnectorPackageApiTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public ConnectorPackageApiTests(GatewayFixture gw) => _gw = gw;

    /// <summary>
    /// Ein Paket von einem nicht gepinnten Herausgeber wird über die API abgewiesen — mit 400 und
    /// einem Grund, nicht mit 500.
    /// </summary>
    [Fact]
    public async Task An_unsigned_package_is_refused_by_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var stranger = new TestSigner();
        using var package = new MemoryStream(ConnectorPackageBuilder.Build(
            new ConnectorManifest(
                ConnectorManifest.SchemaV1, "com.fremd.paket", "1.0.0",
                ConnectorManifest.SupportedContractVersion, stranger.KeyId, "Fremd",
                UpstreamTransportKind.Wasi, "payload/c.wasm", "payload/c.wasm.sig", []),
            new Dictionary<string, byte[]>
            {
                ["payload/c.wasm"] = [0x00, 0x61, 0x73, 0x6D],
                ["payload/c.wasm.sig"] = new byte[64],
            },
            stranger.Sign));

        var (_, apiKey) = await _gw.SeedAdminAsync("pkg-api-admin");
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        using var content = new ByteArrayContent(package.ToArray());
        var response = await client.PostAsync("/api/v1/packages", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        payload.GetProperty("error").GetString().Should().Contain("Herausgeber");
    }

    /// <summary>Paketverwaltung ist Adminsache — wer nur Tools aufrufen darf, sieht sie nicht.</summary>
    [Fact]
    public async Task Package_management_is_admin_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedIdentityAsync("pkg-nicht-admin", grants: []);
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var response = await client.GetAsync("/api/v1/packages", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }


    /// <summary>Ein echtes Ed25519-Schluesselpaar zum Signieren der Manifeste im Test.</summary>
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
