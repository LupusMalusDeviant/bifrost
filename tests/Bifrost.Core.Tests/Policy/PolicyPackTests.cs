using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Policy;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace Bifrost.Core.Tests.Policy;

/// <summary>
/// WP6.1: Policy Packs — Signatur, Vertrauen und vor allem der <b>Rechte-Diff</b>.
/// <para>
/// Der wichtigste Test ist <see cref="A_pack_that_widens_a_role_names_every_identity_that_gains"/>:
/// Ein Objekt-Diff hätte dort „Rolle geändert" gesagt. Was wirklich passiert, ist, dass drei
/// Agenten auf einmal mehr dürfen — und genau das muss dastehen, bevor irgendetwas gilt.
/// </para>
/// </summary>
public sealed class PolicyPackTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly TestSigner _publisher = new();

    // ── Signatur und Vertrauen ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pack_signed_by_a_known_publisher_is_accepted()
    {
        var pack = Pack("payload");

        var signer = PolicyPackReader.Verify(pack, [_publisher.Key]);

        signer.KeyId.Should().Be(_publisher.KeyId);
    }

    [Fact]
    public void A_tampered_payload_breaks_the_signature()
    {
        var pack = Pack("payload") with { Payload = "payload-manipuliert" };

        var verify = () => PolicyPackReader.Verify(pack, [_publisher.Key]);

        verify.Should().Throw<PolicyPackException>().WithMessage("*Herausgeberkennung*");
    }

    /// <summary>
    /// Signiert wird über Manifest <b>und</b> Nutzlast. Nur das Manifest zu signieren hieße, eine
    /// fremde Nutzlast unter einem echten Namen ausliefern zu können.
    /// </summary>
    [Fact]
    public void A_swapped_manifest_breaks_the_signature_too()
    {
        var pack = Pack("payload");
        var fremd = pack with { Manifest = pack.Manifest with { Description = "etwas ganz anderes" } };

        var verify = () => PolicyPackReader.Verify(fremd, [_publisher.Key]);

        verify.Should().Throw<PolicyPackException>();
    }

    [Fact]
    public void A_revoked_publisher_no_longer_carries_a_pack()
    {
        var pack = Pack("payload");
        var revoked = _publisher.Key with { RevokedAt = Now };

        var verify = () => PolicyPackReader.Verify(pack, [revoked]);

        verify.Should().Throw<PolicyPackException>();
    }

    /// <summary>
    /// Ein Pack aus einer neueren Generation wird <b>nicht teilweise</b> angewendet: Es kann Felder
    /// tragen, die dieser Gateway nicht kennt — und still zu ignorieren wäre bei Rechten die
    /// schlechteste aller Antworten.
    /// </summary>
    [Fact]
    public void A_pack_from_a_newer_generation_is_refused()
    {
        var pack = Pack("payload", formatVersion: PolicyPackManifest.SupportedFormatVersion + 1);

        var verify = () => PolicyPackReader.Verify(pack, [_publisher.Key]);

        verify.Should().Throw<PolicyPackException>().WithMessage("*Generation*");
    }

    // ── Der Rechte-Diff ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Der Kern von WP6.1.</b> Die Rolle heißt gleich, das Pack gibt ihr mehr — und der Diff
    /// nennt jede Identität, die dadurch gewinnt, samt Wirkungsebene.
    /// </summary>
    [Fact]
    public void A_pack_that_widens_a_role_names_every_identity_that_gains()
    {
        var entwickler = new Role(RoleId.New(), "entwickler",
            [new Grant(new PermissionScope(null, new NamespacedToolName("github__read")), [ToolAction.UseTool])]);
        var identities = new[]
        {
            Identity("anna", entwickler.Id),
            Identity("bernd", entwickler.Id),
        };

        var before = RightsProjection.Project(
            identities, [entwickler], Roles((entwickler.Name, entwickler.Grants)));

        // Das Pack erweitert dieselbe Rolle auf "alles".
        var after = RightsProjection.Project(
            identities, [entwickler],
            Roles((entwickler.Name, [new Grant(new PermissionScope(null, null), [ToolAction.UseTool])])));

        var delta = RightsProjection.Diff(before, after);

        delta.GrantsMore.Should().BeTrue();
        delta.Added.Should().HaveCount(2, "beide Identitäten hängen an dieser Rolle");
        delta.Added.Select(e => e.Identity).Should().BeEquivalentTo(["anna", "bernd"]);
        delta.Added.Should().OnlyContain(e => e.Scope == "alles");

        // Und die Wegnahme steht daneben: Das enge Recht gibt es danach nicht mehr.
        delta.Removed.Should().HaveCount(2).And.OnlyContain(e => e.Scope == "github__read");
    }

    [Fact]
    public void A_pack_that_changes_nothing_produces_an_empty_delta()
    {
        var rolle = new Role(RoleId.New(), "leser",
            [new Grant(new PermissionScope(null, null), [ToolAction.ReadResource])]);
        var identities = new[] { Identity("anna", rolle.Id) };

        var projection = RightsProjection.Project(identities, [rolle], Roles((rolle.Name, rolle.Grants)));
        var delta = RightsProjection.Diff(projection, projection);

        delta.GrantsMore.Should().BeFalse();
        delta.Added.Should().BeEmpty();
        delta.Removed.Should().BeEmpty();
    }

    /// <summary>
    /// Eine Identität ohne die betroffene Rolle gewinnt nichts. Ohne diesen Fall wäre nicht belegt,
    /// dass der Diff die <em>lokale</em> Zuordnung benutzt statt alle über einen Kamm zu scheren.
    /// </summary>
    [Fact]
    public void An_identity_without_the_role_gains_nothing()
    {
        var entwickler = new Role(RoleId.New(), "entwickler", []);
        var gast = new Role(RoleId.New(), "gast", []);
        var identities = new[] { Identity("anna", entwickler.Id), Identity("gustav", gast.Id) };

        var before = RightsProjection.Project(identities, [entwickler, gast], Roles(
            (entwickler.Name, []), (gast.Name, [])));
        var after = RightsProjection.Project(identities, [entwickler, gast], Roles(
            (entwickler.Name, [new Grant(new PermissionScope(null, null), [ToolAction.UseTool])]),
            (gast.Name, [])));

        var delta = RightsProjection.Diff(before, after);

        delta.Added.Should().ContainSingle().Which.Identity.Should().Be("anna");
    }

    /// <summary>
    /// Eine Rolle, die das Pack nicht kennt, verschwindet nicht still — sie erscheint als Wegnahme.
    /// Ein Pack, das versehentlich eine Rolle auslässt, entzieht sonst unbemerkt Rechte.
    /// </summary>
    [Fact]
    public void A_role_the_pack_does_not_mention_shows_up_as_removal()
    {
        var rolle = new Role(RoleId.New(), "entwickler",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool])]);
        var identities = new[] { Identity("anna", rolle.Id) };

        var before = RightsProjection.Project(identities, [rolle], Roles((rolle.Name, rolle.Grants)));
        var after = RightsProjection.Project(identities, [rolle], Roles());

        RightsProjection.Diff(before, after).Removed.Should().ContainSingle()
            .Which.Identity.Should().Be("anna");
    }

    /// <summary>
    /// Ein globaler Grant erscheint als „alles" und nicht als leere Zelle — eine leere Zelle wird
    /// beim Lesen übersehen, und das ist bei Rechten die teuerste Art von Übersehen.
    /// </summary>
    [Fact]
    public void A_global_grant_is_spelled_out()
    {
        var rolle = new Role(RoleId.New(), "admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool])]);

        RightsProjection.Project([Identity("root", rolle.Id)], [rolle], Roles((rolle.Name, rolle.Grants)))
            .Single().Scope.Should().Be("alles");
    }

    private static Identity Identity(string name, RoleId role) =>
        new(IdentityId.New(), name, IdentityKind.Agent, [role]);

    private static Dictionary<string, IReadOnlyList<Grant>> Roles(
        params (string Name, IReadOnlyList<Grant> Grants)[] roles) =>
        roles.ToDictionary(r => r.Name, r => r.Grants, StringComparer.Ordinal);

    private PolicyPack Pack(string payload, int formatVersion = PolicyPackManifest.SupportedFormatVersion) =>
        PolicyPackReader.Build(
            new PolicyPackManifest(
                formatVersion, "team-standard", "1.0.0", _publisher.KeyId, "Standard des Teams", Now),
            payload,
            _publisher.Sign);

    /// <summary>Ein echtes Schlüsselpaar — ein Attrappen-Signierer würde die Prüfung umgehen, nicht belegen.</summary>
    private sealed class TestSigner
    {
        private readonly Ed25519PrivateKeyParameters _private = new(RandomNumberGenerator.GetBytes(32), 0);

        public TestSigner()
        {
            var publicKey = _private.GeneratePublicKey().GetEncoded();
            KeyId = Convert.ToHexStringLower(SHA256.HashData(publicKey));
            Key = new PublisherKey(
                KeyId, Convert.ToBase64String(publicKey), "team", Now, null, ConnectorTrustLevel.Official);
        }

        public string KeyId { get; }

        public PublisherKey Key { get; }

        public byte[] Sign(byte[] message)
        {
            var signer = new Ed25519Signer();
            signer.Init(forSigning: true, _private);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }
    }
}
