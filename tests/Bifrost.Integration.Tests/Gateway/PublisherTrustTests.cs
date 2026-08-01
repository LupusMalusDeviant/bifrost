using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Plan 0003, WP4: Der Publisher-Trust-Store ist ab jetzt die einzige Vertrauensquelle für
/// WASI-Components. Geprüft wird, was daran sicherheitsrelevant ist — dass ein leerer Store nichts
/// lädt, dass ein Entzug einen <b>laufenden</b> Upstream stoppt (festgelegte Entscheidung 2) und
/// dass die Verwaltung nur Admins offensteht.
/// </summary>
public sealed class PublisherTrustTests : IClassFixture<GatewayFixture>, IAsyncLifetime
{
    private static readonly string StubPublisher = Convert.ToBase64String(new byte[32]);

    private readonly GatewayFixture _gw;
    private string _componentPath = string.Empty;
    private string _signaturePath = string.Empty;

    public PublisherTrustTests(GatewayFixture gw) => _gw = gw;

    private PublisherTrustStore Trust => _gw.Services.GetRequiredService<PublisherTrustStore>();

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _componentPath = Path.Combine(Path.GetTempPath(), $"bifrost-trust-{Guid.NewGuid():N}.wasm");
        _signaturePath = Path.ChangeExtension(_componentPath, ".sig");
        await File.WriteAllBytesAsync(_componentPath, [0x00, 0x61, 0x73, 0x6D], ct);
        await File.WriteAllBytesAsync(_signaturePath, new byte[64], ct);
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(_componentPath);
        File.Delete(_signaturePath);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_pinned_key_survives_a_reload_and_keeps_its_fingerprint()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(b => (byte)b).ToArray());

        var pinned = await Trust.PinAsync(publicKey, "acme", ct);
        await Trust.LoadAsync(ct); // aus der DB neu hydratisieren

        // Die KeyId ist der SHA-256 des Public Keys — dieselbe Id, die der Rust-Host auditiert.
        pinned.KeyId.Should().Be(PublisherTrustStore.ComputeKeyId(publicKey));
        Trust.All.Should().ContainSingle(key => key.KeyId == pinned.KeyId && key.Label == "acme");
        Trust.ActivePublicKeys.Should().Contain(publicKey);
    }

    [Fact]
    public async Task Pinning_the_same_key_twice_does_not_resurrect_a_revoked_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicKey = Convert.ToBase64String(Enumerable.Range(40, 32).Select(b => (byte)b).ToArray());
        var pinned = await Trust.PinAsync(publicKey, "wieder-weg", ct);
        await Trust.RevokeAsync(pinned.KeyId, ct);

        await Trust.PinAsync(publicKey, "erneut", ct);

        // Sonst hübe ein Import oder ein unbedachtes erneutes Pinnen den Entzug still auf.
        Trust.ActivePublicKeys.Should().NotContain(publicKey);
        Trust.All.Single(key => key.KeyId == pinned.KeyId).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_empty_trust_store_lets_no_wasi_upstream_start()
    {
        var ct = TestContext.Current.CancellationToken;
        // Der Stub-Host lädt nur mit mindestens einem gepinnten Publisher — genau wie das echte
        // Binary. Ohne Eintrag im Store schickt der Connector eine leere Liste.
        foreach (var key in Trust.All.Where(key => key.IsActive))
        {
            await Trust.RevokeAsync(key.KeyId, ct);
        }

        var id = await _gw.Supervisor.AddAsync(Config("wasi-leer"), ct);

        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(id)?.State is UpstreamState.Failed,
            because: "ohne vertrauenswürdigen Publisher darf nichts geladen werden (fail-closed)");
        _gw.Supervisor.GetStatus(id)!.LastError.Should().Contain("load-rejected");
    }

    [Fact]
    public async Task Revoking_a_key_stops_the_running_upstream_that_used_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var pinned = await Trust.PinAsync(StubPublisher, "stub", ct);
        var id = await _gw.Supervisor.AddAsync(Config("wasi-entzug"), ct);
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "der Upstream muss erst laufen, damit der Entzug etwas zu stoppen hat");

        await Trust.RevokeAsync(pinned.KeyId, ct);

        // Festgelegte Entscheidung 2: sofort, nicht erst beim nächsten Laden.
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(id) is null,
            because: "ein Entzug, der erst beim nächsten Neustart greift, ist kein Entzug");
        await IntegrationSupport.WaitUntilAsync(
            async () => (await _gw.AuditQuery.QueryAsync(new AuditFilter(Kind: AuditEventKind.ServerLifecycle), ct))
                .Items.Any(e => e.Detail?.Contains(pinned.KeyId, StringComparison.Ordinal) == true),
            because: "der Stopp gehört ins Audit");

        await Trust.ReinstateAsync(pinned.KeyId, ct);
    }

    [Fact]
    public async Task Publisher_management_needs_an_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = _gw.CreateDefaultClient();

        var listed = await anonymous.GetAsync("/api/v1/publishers", ct);

        listed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unusable_public_key_is_rejected_as_a_bad_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedAdminAsync("publisher-admin");
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var tooShort = await client.PostAsync("/api/v1/publishers",
            new StringContent("""{"publicKey":"YWJj","label":"zu kurz"}""", Encoding.UTF8, "application/json"), ct);
        var notBase64 = await client.PostAsync("/api/v1/publishers",
            new StringContent("""{"publicKey":"kein base64!","label":"kaputt"}""", Encoding.UTF8, "application/json"), ct);

        tooShort.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        notBase64.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_admin_can_pin_and_revoke_over_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicKey = Convert.ToBase64String(Enumerable.Range(80, 32).Select(b => (byte)b).ToArray());
        var (_, apiKey) = await _gw.SeedAdminAsync("publisher-rest-admin");
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var pinned = await client.PostAsJsonAsync("/api/v1/publishers", new { publicKey, label = "rest" }, ct);
        var keyId = (await pinned.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("keyId").GetString();
        var revoked = await client.PostAsync($"/api/v1/publishers/{keyId}/revoke", content: null, ct);
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/publishers", ct);

        pinned.StatusCode.Should().Be(HttpStatusCode.OK);
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
        // Entzogene Schlüssel bleiben sichtbar — sonst wären ältere Audit-Zeilen nicht zuordenbar.
        listed.EnumerateArray().Should().Contain(entry =>
            entry.GetProperty("keyId").GetString() == keyId
            && entry.GetProperty("revokedAt").ValueKind != JsonValueKind.Null);
    }

    private UpstreamServerConfig Config(string slug) => new(
        slug, "WASI (Trust)", UpstreamTransportKind.Wasi, Enabled: true,
        Wasi: new WasiTransportOptions(
            TestPaths.Executable("WasiHostStub"), _componentPath, _signaturePath, PinnedPublishers: []),
        Restart: new RestartPolicy(0, TimeSpan.FromMilliseconds(50), 2.0, TimeSpan.FromSeconds(1)));
}
