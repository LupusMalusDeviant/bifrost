using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Policy;
using Bifrost.Integration.Tests.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// WP6.1 am laufenden Gateway: der ganze Weg — planen, den Rechtezuwachs sehen, übernehmen,
/// zurückgehen.
/// <para>
/// <b>Die DoD dieses Pakets steht in <see cref="A_pack_can_be_examined_completely_before_a_single_right_changes"/>:</b>
/// Der Plan nennt den Zuwachs namentlich, und bis dahin hat sich nichts geändert.
/// </para>
/// </summary>
public sealed class PolicyPackApiTests : IClassFixture<GatewayFixture>
{
    // Der Server schreibt Enums als Text (JsonStringEnumConverter). Ohne denselben Konverter liest
    // dieser Test seine eigene Antwort nicht — und das saehe aus wie ein Fehler im Endpunkt.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly string[] UseToolOnly = ["UseTool"];

    private readonly GatewayFixture _fixture;

    public PolicyPackApiTests(GatewayFixture fixture) => _fixture = fixture;

    private IPublisherTrustStore Publishers => _fixture.Services.GetRequiredService<IPublisherTrustStore>();

    [Fact]
    public async Task A_pack_can_be_examined_completely_before_a_single_right_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var signer = await TrustedSignerAsync(ct);

        // Eine Rolle mit einem engen Recht, und eine Identität, die daran hängt.
        var roleName = $"entwickler-{Guid.NewGuid():N}"[..20];
        var identityName = $"agent-{Guid.NewGuid():N}"[..16];
        var role = new Role(RoleId.New(), roleName,
            [new Grant(new PermissionScope(null, new NamespacedToolName("github__read")), [ToolAction.UseTool])]);
        await _fixture.RbacStore.UpsertRoleAsync(role, ct);
        await _fixture.RbacStore.UpsertIdentityAsync(
            new Identity(IdentityId.New(), identityName, IdentityKind.Agent, [role.Id]), ct);

        // Das Pack erweitert dieselbe Rolle auf "alles" — der Objekt-Diff saehe hier nur
        // "eine Rolle geaendert".
        var pack = PackFor(signer, roleName, global: true);

        var response = await client.PostAsJsonAsync("/api/v1/policy/plan", new { pack }, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var plan = await response.Content.ReadFromJsonAsync<PolicyPackPlan>(Json, ct);
        plan!.RightsDelta.Added.Should().Contain(
            entry => entry.Identity == identityName && entry.Scope == "alles",
            "der Plan nennt jede Identität, die gewinnt — nicht nur die geänderte Rolle");
        plan.PublisherKeyId.Should().Be(signer.KeyId);
        plan.Token.Should().NotBeNullOrWhiteSpace("ohne Handle gibt es keine Übernahme");

        // Der interessante Fall eines Policy Packs ist genau dieser: dieselbe Rolle, mehr Rechte.
        // Er war bis M6 NICHT anwendbar, weil der Importweg nur anlegen konnte — der Befund steht
        // im Fortschrittsprotokoll. Seit dem Update-Modus ist er anwendbar, und die Ersetzung steht
        // getrennt von den Zugaengen da: "wird angelegt" und "wird ersetzt" sind zwei Saetze.
        plan.CanApply.Should().BeTrue("der Importweg ersetzt eine bestehende Rolle (M6/WP6.1)");
        plan.Conflicts.Should().BeEmpty();
        plan.Updates.Should().Contain(u => u.Contains(roleName, StringComparison.Ordinal));

        // Und bis hierher hat sich NICHTS geaendert — das ist die Zusage.
        var stored = (await _fixture.RbacStore.ListRolesAsync(ct)).Single(r => r.Name == roleName);
        stored.Grants.Should().ContainSingle()
            .Which.Scope.Tool!.Value.Value.Should().Be("github__read", "planen ändert nichts");
    }

    [Fact]
    public async Task A_pack_signed_by_an_unknown_publisher_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();

        // Ein Signierer, der NICHT im Trust-Store steht.
        var fremd = new TestSigner();
        var pack = PackFor(fremd, "egal", global: true);

        var response = await client.PostAsJsonAsync("/api/v1/policy/plan", new { pack }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("Herausgeber");
    }

    [Fact]
    public async Task Applying_without_a_plan_handle_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/policy/apply",
            new
            {
                canApply = true,
                manifest = new
                {
                    formatVersion = 1, id = "x", version = "1", publisherKeyId = "y",
                    description = "z", createdAt = DateTimeOffset.UtcNow,
                },
                publisherKeyId = "y",
                trustLevel = "Official",
                payloadHash = "hash",
                rightsDelta = new { added = Array.Empty<object>(), removed = Array.Empty<object>() },
                additions = Array.Empty<string>(),
                conflicts = Array.Empty<string>(),
                unchanged = Array.Empty<string>(),
                missingTools = Array.Empty<string>(),
                notes = Array.Empty<string>(),
                token = (string?)null,
            },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("nicht anwendbar");
    }

    /// <summary>Ein nicht-Administrator sieht keinen Plan — er nennt fremde Identitäten samt Rechten.</summary>
    [Fact]
    public async Task Only_an_admin_may_plan_or_apply()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, ohneRechte) = await _fixture.SeedIdentityAsync($"policy-fremd-{Guid.NewGuid():N}", grants: []);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ohneRechte);

        var response = await client.PostAsJsonAsync("/api/v1/policy/plan", new { pack = "{}" }, ct);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// <b>Der Rollback-Nachweis — und er fällt negativ aus.</b>
    /// <para>
    /// Ein Pack, das eine <em>neue</em> Rolle mitbringt, ist anwendbar. Der Planer schreibt vorher
    /// den Stand mit. Spielt man diesen Stand danach über denselben Importweg wieder ein, ist die
    /// neue Rolle <b>immer noch da</b>: Der Import legt nur an, er nimmt nichts weg.
    /// </para>
    /// <para>
    /// Damit ist belegt, dass der mitgeschriebene Stand <b>kein</b> Rollback ist, sondern eine
    /// Momentaufnahme. Der Test hält das fest, statt die Zusage stehen zu lassen — ein Rollback,
    /// den nie jemand gefahren ist, bricht genau dann, wenn man ihn braucht.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_snapshot_taken_before_applying_does_not_undo_the_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var signer = await TrustedSignerAsync(ct);

        // Eine Rolle, die es hier NICHT gibt — nur dann ist das Pack ueberhaupt anwendbar.
        var roleName = $"neu-{Guid.NewGuid():N}"[..14];
        var pack = PackFor(signer, roleName, global: true);

        var planned = await client.PostAsJsonAsync("/api/v1/policy/plan", new { pack }, ct);
        var plan = await planned.Content.ReadFromJsonAsync<PolicyPackPlan>(Json, ct);
        plan!.CanApply.Should().BeTrue("eine neue Rolle kollidiert mit nichts");

        var applied = await client.PostAsJsonAsync("/api/v1/policy/apply", plan, Json, ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        var application = await applied.Content.ReadFromJsonAsync<PolicyPackApplication>(Json, ct);

        (await _fixture.RbacStore.ListRolesAsync(ct)).Should().Contain(r => r.Name == roleName,
            "die Uebernahme hat wirklich gewirkt");

        // Jetzt der Rueckweg: derselbe Importweg, mit dem Stand von vorher.
        var import = _fixture.Services.GetRequiredService<IConfigurationExportService>();
        var rollbackPlan = await import.PlanImportAsync(application!.SnapshotBeforeApply, null, ct);
        if (rollbackPlan.CanApply)
        {
            await import.ApplyImportAsync(rollbackPlan, ct);
        }

        (await _fixture.RbacStore.ListRolesAsync(ct)).Should().Contain(
            r => r.Name == roleName,
            "der Importweg legt nur an — die neue Rolle bleibt. Das ist der Befund: Die "
            + "Momentaufnahme ist kein Rollback.");
    }

    /// <summary>
    /// <b>Der Rollback-Nachweis — und diesmal faellt er positiv aus.</b>
    /// <para>
    /// Nicht ueber die Momentaufnahme (die kann es nicht, siehe
    /// <see cref="The_snapshot_taken_before_applying_does_not_undo_the_change"/>), sondern ueber den
    /// Rueckweg, den die Uebernahme selbst erzeugt hat. Er beruehrt genau die Rolle, die das Pack
    /// beruehrt hat — und setzt sie auf ihren vorigen Inhalt zurueck, statt sie zu loeschen.
    /// </para>
    /// <para>
    /// <b>Der Unterschied ist der ganze Punkt:</b> Loeschen statt Zuruecksetzen wuerde jeder
    /// Identitaet, die an dieser Rolle haengt, saemtliche Rechte entziehen. Deshalb prueft dieser
    /// Test nicht nur, dass das enge Recht wieder da ist, sondern auch, dass die Rolle ueberhaupt
    /// noch existiert und ihre Id behalten hat.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_undo_from_applying_really_takes_a_widened_role_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var signer = await TrustedSignerAsync(ct);

        var roleName = $"rueckweg-{Guid.NewGuid():N}"[..18];
        var role = new Role(RoleId.New(), roleName,
            [new Grant(new PermissionScope(null, new NamespacedToolName("github__read")), [ToolAction.UseTool])]);
        await _fixture.RbacStore.UpsertRoleAsync(role, ct);

        var plan = await (await client.PostAsJsonAsync(
            "/api/v1/policy/plan", new { pack = PackFor(signer, roleName, global: true) }, ct))
            .Content.ReadFromJsonAsync<PolicyPackPlan>(Json, ct);
        plan!.CanApply.Should().BeTrue();

        var applied = await client.PostAsJsonAsync("/api/v1/policy/apply", plan, Json, ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        var application = await applied.Content.ReadFromJsonAsync<PolicyPackApplication>(Json, ct);

        // Der Rueckweg muss die Antwort ueberstehen: Er geht als JSON zum Aufrufer und kommt als
        // JSON zurueck. Ein Rueckweg, der dabei leer wird, meldet spaeter Erfolg und tut nichts —
        // die gefaehrlichste aller Antworten.
        application!.Undo.ReplacedRoles.Should().ContainSingle(
            "der Rueckweg traegt den vorigen Inhalt der ersetzten Rolle — auch nach dem JSON-Weg")
            .Which.Grants.Should().ContainSingle().Which.Scope.Tool!.Value.Value.Should().Be("github__read");

        var erweitert = (await _fixture.RbacStore.ListRolesAsync(ct)).Single(r => r.Name == roleName);
        erweitert.Grants.Should().ContainSingle().Which.Scope.Tool.Should().BeNull(
            "die Uebernahme hat die Rolle wirklich auf „alles\" erweitert");
        erweitert.Id.Should().Be(role.Id, "die Id der Zielinstanz bleibt — sonst zeigen Identitaeten ins Leere");

        // Und zurueck.
        var reverted = await client.PostAsJsonAsync("/api/v1/policy/revert", application, Json, ct);
        reverted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var zurueck = (await _fixture.RbacStore.ListRolesAsync(ct)).Should()
            .ContainSingle(r => r.Name == roleName, "die Rolle wurde zurueckgesetzt, nicht geloescht").Which;
        zurueck.Id.Should().Be(role.Id);
        zurueck.Grants.Should().ContainSingle().Which.Scope.Tool!.Value.Value.Should().Be("github__read");
    }

    private async Task<TestSigner> TrustedSignerAsync(CancellationToken ct)
    {
        var signer = new TestSigner();
        await Publishers.PinAsync(signer.PublicKeyBase64, "policy-test", ct);
        return signer;
    }

    /// <summary>
    /// Baut ein Pack, dessen Nutzlast ein Konfigurationsexport mit genau einer Rolle ist — mehr
    /// braucht der Rechte-Diff nicht, und weniger wäre kein gültiger Export.
    /// </summary>
    private static string PackFor(TestSigner signer, string roleName, bool global)
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
                    grants = new[]
                    {
                        new
                        {
                            server = (Guid?)null,
                            tool = global ? null : "github__read",
                            actions = UseToolOnly,
                        },
                    },
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

        var pack = PolicyPackReader.Build(
            new PolicyPackManifest(
                PolicyPackManifest.SupportedFormatVersion, "team-standard", "1.0.0",
                signer.KeyId, "Teamstandard", DateTimeOffset.UtcNow),
            payload,
            signer.Sign);

        return PolicyPackReader.Serialize(pack);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, key) = await _fixture.SeedAdminAsync("policy-admin-" + Guid.NewGuid().ToString("N")[..8]);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

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
