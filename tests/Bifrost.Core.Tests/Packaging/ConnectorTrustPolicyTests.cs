using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Packaging;
using Xunit;

namespace Bifrost.Core.Tests.Packaging;

/// <summary>
/// Vertrauensstufen (ADR-0016). Die Frage hier ist nicht „darf der Connector laufen" — das
/// entscheidet die Signatur — sondern „wie viel bekommt er, ohne dass jemand zustimmt".
/// </summary>
public sealed class ConnectorTrustPolicyTests
{
    private static readonly ConnectorGrantRequest Wants = new(
        FilesystemRead: ["/daten/ein"], Network: ["api.example.org:443"]);

    private static ConnectorManifest Manifest(ConnectorGrantRequest? grants = null) => new(
        ConnectorManifest.SchemaV1, "com.example.echo", "1.0.0",
        ConnectorManifest.SupportedContractVersion, "keyid", "Echo",
        UpstreamTransportKind.Wasi, "payload/c.wasm", "payload/c.wasm.sig",
        Payloads: [new ConnectorPayload("payload/c.wasm", new string('a', 64))],
        Grants: grants);

    /// <summary>Ein offizielles Paket bekommt, was im signierten Manifest steht.</summary>
    [Fact]
    public void Official_packages_get_what_the_manifest_declares()
    {
        var granted = ConnectorTrustPolicy.Evaluate(
            Manifest(Wants), ConnectorTrustLevel.Official, null, allowUntrusted: false);

        granted.Should().BeEquivalentTo(["fs-read:/daten/ein", "network:api.example.org:443"]);
    }

    /// <summary>
    /// Bei ThirdParty ist jeder Zugriff nach außen einzeln zu bestätigen. Ohne Zustimmung wird
    /// nicht etwa weniger gewährt — es wird abgelehnt, damit niemand mit halben Rechten läuft und
    /// später rätselt.
    /// </summary>
    [Fact]
    public void Third_party_packages_need_consent_for_every_access()
    {
        var act = () => ConnectorTrustPolicy.Evaluate(
            Manifest(Wants), ConnectorTrustLevel.ThirdParty, ["fs-read:/daten/ein"],
            allowUntrusted: false);

        act.Should().Throw<ConnectorPackageException>()
            .WithMessage("*network:api.example.org:443*");
    }

    [Fact]
    public void Third_party_packages_run_once_everything_is_accepted()
    {
        var granted = ConnectorTrustPolicy.Evaluate(
            Manifest(Wants), ConnectorTrustLevel.ThirdParty,
            ["fs-read:/daten/ein", "network:api.example.org:443"], allowUntrusted: false);

        granted.Should().HaveCount(2);
    }

    /// <summary>
    /// Zustimmung zu etwas, das gar nicht verlangt wird, erweitert nichts — sonst wüchse die
    /// Berechtigung mit einem Tippfehler in der Anfrage.
    /// </summary>
    [Fact]
    public void Consent_to_something_unrequested_grants_nothing()
    {
        var granted = ConnectorTrustPolicy.Evaluate(
            Manifest(new ConnectorGrantRequest(FilesystemRead: ["/daten/ein"])),
            ConnectorTrustLevel.ThirdParty,
            ["fs-read:/daten/ein", "network:ueberall:443", "secret:ALLES"],
            allowUntrusted: false);

        granted.Should().Equal("fs-read:/daten/ein");
    }

    /// <summary>Community ist deny-by-default: erst das Paket freigeben, dann die Zugriffe.</summary>
    [Fact]
    public void Community_packages_need_an_explicit_release()
    {
        var act = () => ConnectorTrustPolicy.Evaluate(
            Manifest(), ConnectorTrustLevel.Community, null, allowUntrusted: false);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*deny-by-default*");
    }

    [Fact]
    public void A_released_community_package_without_requests_is_fine()
    {
        var granted = ConnectorTrustPolicy.Evaluate(
            Manifest(), ConnectorTrustLevel.Community, null, allowUntrusted: true);

        granted.Should().BeEmpty();
    }

    /// <summary>
    /// „Core" ist mit dem Produkt ausgelieferter Code. Ein Paket dieser Stufe zu geben hieße, jede
    /// weitere Prüfung zu überspringen.
    /// </summary>
    [Fact]
    public void Core_is_not_installable()
    {
        var act = () => ConnectorTrustPolicy.Evaluate(
            Manifest(), ConnectorTrustLevel.Core, null, allowUntrusted: true);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*nicht installierbar*");
    }
}
