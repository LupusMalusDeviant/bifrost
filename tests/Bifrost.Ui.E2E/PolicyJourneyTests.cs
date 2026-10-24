using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Policy;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP6.1 über die Oberfläche: Ein Administrator übernimmt ein Policy Pack — und sieht
/// <b>vorher</b>, wer danach mehr darf.
/// <para>
/// Der Weg prüft die Zusage, an der dieses Paket hängt: <b>keine stille Rechteausweitung</b>. Die
/// Identität steht namentlich auf der Seite, der Knopf bleibt bis zur ausdrücklichen Bestätigung
/// gesperrt, und die Rücknahme setzt die Rolle wirklich zurück statt sie zu löschen.
/// </para>
/// </summary>
public sealed class PolicyJourneyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] UseToolOnly = ["UseTool"];

    private readonly ITestOutputHelper _output;

    public PolicyJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task An_admin_sees_who_gains_before_applying_a_pack_and_can_take_it_back()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(An_admin_sees_who_gains_before_applying_a_pack_and_can_take_it_back), ct);

        var signer = new TestSigner();
        var roleName = $"ui-rolle-{Guid.NewGuid():N}"[..16];
        var identityName = $"ui-agent-{Guid.NewGuid():N}"[..16];
        var roleId = Guid.NewGuid();

        // Der Aufbau geht über die API, nicht über die Oberfläche: Geprüft wird hier der Weg des
        // Packs, nicht die Rollenverwaltung — die hat ihre eigenen Wege.
        await PostAsync(session, "api/v1/rbac/roles", new
        {
            id = new { value = roleId },
            name = roleName,
            grants = new[]
            {
                new
                {
                    scope = new { server = (object?)null, tool = new { value = "echo__ping" } },
                    actions = UseToolOnly,
                },
            },
            rateLimit = (object?)null,
        }, ct);

        await PostAsync(session, "api/v1/rbac/identities", new
        {
            id = new { value = Guid.NewGuid() },
            name = identityName,
            kind = "Agent",
            roles = new[] { new { value = roleId } },
        }, ct);

        await PostAsync(session, "api/v1/publishers", new
        {
            publicKey = signer.PublicKeyBase64,
            label = "ui-journey",
        }, ct);

        await session.LoginAsync();
        await session.Page.GotoAsync("policy");
        await session.WaitForInteractivityAsync("[data-testid=policy-pack]");

        await session.Page.FillAsync("[data-testid=policy-pack]", PackFor(signer, roleName));
        await session.Page.ClickAsync("[data-testid=policy-plan]");

        // **Der Kern.** Nicht „eine Rolle geändert", sondern: diese Identität darf danach alles.
        var added = session.Page.Locator("[data-testid=policy-right-added]");
        await Assertions.Expect(added).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });
        (await added.TextContentAsync()).Should().Contain(identityName).And.Contain("alles");
        _output.WriteLine($"Rechtezuwachs auf der Seite: {await added.TextContentAsync()}");

        // Der Knopf bleibt gesperrt, bis jemand ausdrücklich bestätigt, den Zuwachs gesehen zu
        // haben. Ohne diese Sperre wäre die Anzeige eine Dekoration.
        var apply = session.Page.Locator("[data-testid=policy-apply]");
        (await apply.IsDisabledAsync()).Should().BeTrue(
            "ohne Bestätigung wird nicht übernommen — sonst ist der Rechte-Diff nur Zierde");

        await session.Page.CheckAsync("[data-testid=policy-confirm]");
        await apply.ClickAsync();
        await Assertions.Expect(session.Page.Locator("[data-testid=policy-applied]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        (await ScopeOfAsync(session, roleId, ct)).Should().BeNull(
            "die Übernahme hat wirklich gewirkt: die Rolle darf jetzt alles");

        // Und zurück — über den Rückweg der Übernahme, nicht über einen Stand von gestern.
        await session.Page.ClickAsync("[data-testid=policy-revert]");
        await Assertions.Expect(session.Page.Locator("[data-testid=policy-message]"))
            .ToContainTextAsync("zurückgenommen", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });

        (await ScopeOfAsync(session, roleId, ct)).Should().Be("echo__ping",
            "die Rolle wurde auf ihren vorigen Inhalt zurückgesetzt und nicht gelöscht");
    }

    /// <summary>
    /// Der Werkzeugname im einzigen Grant der Rolle — <c>null</c>, wenn sie global darf.
    /// <para>
    /// Gelesen aus der <b>Datenbank</b> über die Verwaltungs-API, nicht von der Seite: Was die
    /// Seite anzeigt, ist hier ausdrücklich nicht der Beweis. Die Grants stehen dort als
    /// eingebettetes JSON in der Zeile — deshalb der zweite Parse.
    /// </para>
    /// </summary>
    private static async Task<string?> ScopeOfAsync(UiSession session, Guid roleId, CancellationToken ct)
    {
        var body = await session.Gateway.Client.GetStringAsync("api/v1/rbac/roles", ct);
        var role = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("roles").EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == roleId);
        // Die Ablage schreibt die Grants flach und in PascalCase (RbacJson.GrantDto) — bewusst
        // nicht die Vertragsform: Was hier gelesen wird, ist die Zeile in der Datenbank.
        var grants = JsonSerializer.Deserialize<JsonElement>(role.GetProperty("grantsJson").GetString()!);

        return grants[0].GetProperty("Tool") is { ValueKind: JsonValueKind.String } tool
            ? tool.GetString()
            : null;
    }

    private static async Task PostAsync(UiSession session, string path, object body, CancellationToken ct)
    {
        var response = await session.Gateway.Client.PostAsJsonAsync(path, body, Json, ct);
        response.IsSuccessStatusCode.Should().BeTrue(
            $"Aufbau über {path} muss gelingen: {await response.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Ein Pack, das dieselbe Rolle auf „alles" erweitert.</summary>
    private static string PackFor(TestSigner signer, string roleName)
    {
        var payload = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            productVersion = "0.17.0",
            createdAt = DateTimeOffset.UtcNow,
            containsSecrets = false,
            upstreams = Array.Empty<object>(),
            roles = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    name = roleName,
                    grants = new[] { new { server = (Guid?)null, tool = (string?)null, actions = UseToolOnly } },
                    rateLimitPerMinute = (int?)null,
                },
            },
            profiles = Array.Empty<object>(),
            guardRules = Array.Empty<object>(),
            approvals = Array.Empty<object>(),
            skills = Array.Empty<object>(),
            settings = new { },
            secretReferences = Array.Empty<object>(),
            notExportable = Array.Empty<object>(),
        }, Json);

        return PolicyPackReader.Serialize(PolicyPackReader.Build(
            new PolicyPackManifest(
                PolicyPackManifest.SupportedFormatVersion, "ui-journey-pack", "1.0.0",
                signer.KeyId, "Pack für den Browserweg", DateTimeOffset.UtcNow),
            payload,
            signer.Sign));
    }

    /// <summary>Ein echtes Schlüsselpaar — eine Attrappe würde die Prüfung umgehen, nicht belegen.</summary>
    private sealed class TestSigner
    {
        private readonly Ed25519PrivateKeyParameters _private = new(RandomNumberGenerator.GetBytes(32), 0);

        public TestSigner()
        {
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
