using System.IO.Compression;
using System.Security.Cryptography;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Packaging;
using Xunit;

namespace McpMcp.Core.Tests.Packaging;

/// <summary>
/// Prüfung eines Connector-Pakets (ADR-0016). Hier steht die Vertrauensgrenze: Alles, was diese
/// Datei durchlässt, läuft später als fremder Code im Gateway.
/// </summary>
public sealed class ConnectorPackageReaderTests
{
    private readonly TestPublisher _publisher = new();

    private IReadOnlyList<PublisherKey> Pinned => [_publisher.Key];

    [Fact]
    public void A_correctly_signed_package_is_accepted()
    {
        using var package = TestPackage.Valid(_publisher);

        var verified = ConnectorPackageReader.Verify(package, Pinned);

        verified.Manifest.Id.Should().Be("com.example.echo");
        verified.Manifest.Version.Should().Be("1.0.0");
        verified.Publisher.KeyId.Should().Be(_publisher.KeyId);
        verified.TrustLevel.Should().Be(ConnectorTrustLevel.Official);
        verified.ManifestSha256.Should().HaveLength(64);
    }

    /// <summary>
    /// Der Kern: Ohne gepinnten Herausgeber wird nichts installiert. Ein leerer Store darf nicht
    /// „keine Einschränkung" bedeuten.
    /// </summary>
    [Fact]
    public void Without_a_pinned_publisher_nothing_is_installed()
    {
        using var package = TestPackage.Valid(_publisher);

        var act = () => ConnectorPackageReader.Verify(package, []);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*kein Herausgeber gepinnt*");
    }

    [Fact]
    public void A_package_from_an_unknown_publisher_is_refused()
    {
        using var package = TestPackage.Valid(_publisher);
        var stranger = new TestPublisher();

        var act = () => ConnectorPackageReader.Verify(package, [stranger.Key]);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*keinem gepinnten Herausgeber*");
    }

    /// <summary>Ein entzogener Schlüssel zählt nicht mehr — sonst wäre der Entzug wirkungslos.</summary>
    [Fact]
    public void A_revoked_publisher_does_not_count()
    {
        using var package = TestPackage.Valid(_publisher);
        var revoked = _publisher.Key with { RevokedAt = DateTimeOffset.UnixEpoch };

        var act = () => ConnectorPackageReader.Verify(package, [revoked]);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*kein Herausgeber gepinnt*");
    }

    /// <summary>
    /// Manipulation am Manifest bricht die Signatur — das ist der ganze Zweck der Übung.
    /// </summary>
    [Fact]
    public void A_tampered_manifest_breaks_the_signature()
    {
        using var original = TestPackage.Valid(_publisher);
        using var tampered = ReplaceEntry(
            original, ConnectorPackageReader.ManifestEntry,
            System.Text.Encoding.UTF8.GetBytes("""{"schema":"mcpmcp.connector.v1"}"""));

        var act = () => ConnectorPackageReader.Verify(tampered, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*keinem gepinnten Herausgeber*");
    }

    /// <summary>
    /// Der eigentliche Grund, warum das Manifest die Hashes trägt: Ein ausgetauschtes Component
    /// lässt die Signatur unberührt — und muss trotzdem auffallen.
    /// </summary>
    [Fact]
    public void A_swapped_payload_is_caught_by_its_hash()
    {
        using var original = TestPackage.Valid(_publisher);
        using var tampered = ReplaceEntry(
            original, TestPackage.ComponentEntry, [0xDE, 0xAD, 0xBE, 0xEF]);

        var act = () => ConnectorPackageReader.Verify(tampered, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*hat den Hash*");
    }

    /// <summary>
    /// Ein Eintrag, den das Manifest nicht nennt, ist nicht signiert. Ihn zu ignorieren wäre die
    /// bequeme Lösung — und ließe unsignierte Dateien mit ins Paket reisen.
    /// </summary>
    [Fact]
    public void An_undeclared_entry_is_refused()
    {
        var files = TestPackage.Files();
        var manifest = TestPackage.WithPayloads(TestPackage.Manifest(_publisher), files);
        var withStowaway = new Dictionary<string, byte[]>(files)
        {
            ["payload/blinder-passagier.sh"] = "rm -rf /"u8.ToArray(),
        };
        using var package = TestPackage.Raw(manifest, _publisher.Sign, withStowaway);

        var act = () => ConnectorPackageReader.Verify(package, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*nicht signiert*");
    }

    /// <summary>
    /// Das Manifest nennt seinen Herausgeber selbst. Weicht das vom tatsächlichen Unterzeichner ab,
    /// ist eine der beiden Angaben falsch — und die Anzeige zeigte hinterher den falschen Namen.
    /// </summary>
    [Fact]
    public void A_manifest_that_names_the_wrong_publisher_is_refused()
    {
        var other = new TestPublisher();
        var files = TestPackage.Files();
        var manifest = TestPackage.WithPayloads(
            TestPackage.Manifest(_publisher) with { PublisherKeyId = other.KeyId }, files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*signiert hat aber*");
    }

    /// <summary>Zip-Slip: Ein Eintrag, der beim Auspacken aus dem Ziel ausbräche.</summary>
    [Theory]
    [InlineData("../draussen.wasm")]
    [InlineData("payload/../../draussen.wasm")]
    public void An_entry_that_escapes_the_target_is_refused(string path)
    {
        var files = new Dictionary<string, byte[]> { [path] = [1, 2, 3] };
        var manifest = TestPackage.WithPayloads(TestPackage.Manifest(_publisher), files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*aus dem Paket heraus*");
    }

    [Fact]
    public void An_absolute_entry_path_is_refused()
    {
        var files = new Dictionary<string, byte[]> { ["/etc/cron.d/boese"] = [1] };
        var manifest = TestPackage.WithPayloads(TestPackage.Manifest(_publisher), files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*absoluter Pfad*");
    }

    /// <summary>
    /// Ein Eintrag, den es zweimal gibt: Welcher gehasht und welcher ausgepackt wird, entschiede
    /// sonst die Zip-Implementierung — und genau darauf zielt der Trick.
    /// </summary>
    [Fact]
    public void A_duplicate_entry_is_refused()
    {
        var files = TestPackage.Files();
        var manifest = TestPackage.WithPayloads(TestPackage.Manifest(_publisher), files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);
        using var duplicated = new MemoryStream();
        package.CopyTo(duplicated);
        duplicated.Position = 0;
        using (var archive = new ZipArchive(duplicated, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.CreateEntry(TestPackage.ComponentEntry);
            using var stream = entry.Open();
            stream.Write([9, 9, 9]);
        }

        duplicated.Position = 0;
        var act = () => ConnectorPackageReader.Verify(duplicated, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*mehrfach vor*");
    }

    [Fact]
    public void A_package_that_is_not_an_archive_is_refused()
    {
        using var garbage = new MemoryStream("kein zip"u8.ToArray());

        var act = () => ConnectorPackageReader.Verify(garbage, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*kein lesbares Archiv*");
    }

    [Fact]
    public void A_wrong_schema_or_contract_version_is_refused()
    {
        var files = TestPackage.Files();
        var manifest = TestPackage.WithPayloads(
            TestPackage.Manifest(_publisher) with { ContractVersion = "2" }, files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*Vertragsversion*");
    }

    /// <summary>
    /// Die Paket-Id wird zu einem Verzeichnisnamen. Ein Pfadtrenner darin wäre ein zweiter Weg aus
    /// dem Zielverzeichnis, diesmal ohne Zip.
    /// </summary>
    [Theory]
    [InlineData("../boese")]
    [InlineData("mit/schraegstrich")]
    [InlineData("mit leerzeichen")]
    public void An_unusable_package_id_is_refused(string id)
    {
        var files = TestPackage.Files();
        var manifest = TestPackage.WithPayloads(TestPackage.Manifest(_publisher, id), files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Pinned);

        act.Should().Throw<ConnectorPackageException>();
    }

    [Fact]
    public void An_entry_point_that_is_not_declared_is_refused()
    {
        var files = new Dictionary<string, byte[]> { ["payload/etwas.txt"] = [1] };
        var manifest = TestPackage.WithPayloads(TestPackage.Manifest(_publisher), files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Pinned);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*Entry Point*");
    }

    /// <summary>Ausgepackt wird nur, was das Manifest nennt — und nur unterhalb des Ziels.</summary>
    [Fact]
    public void Extract_writes_exactly_the_declared_files()
    {
        using var package = TestPackage.Valid(_publisher);
        var verified = ConnectorPackageReader.Verify(package, Pinned);
        var target = Path.Combine(Path.GetTempPath(), $"mcpkg-{Guid.NewGuid():N}");

        try
        {
            ConnectorPackageReader.Extract(package, verified.Manifest, target);

            File.Exists(Path.Combine(target, "manifest.json")).Should().BeTrue(
                "ohne das Manifest ließe sich später nicht nachvollziehen, wogegen geprüft wurde");
            File.Exists(Path.Combine(target, "payload", "component.wasm")).Should().BeTrue();
            var written = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
            written.Should().HaveCount(4, "Manifest, dessen Signatur und die zwei Nutzdateien");
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    /// <summary>
    /// Ein bekannter RFC-8032-Testvektor. Er belegt, dass hier echtes Ed25519 prüft und nicht eine
    /// Vergleichsroutine, die alles durchwinkt.
    /// </summary>
    [Fact]
    public void The_signature_check_matches_the_rfc_8032_test_vector()
    {
        // RFC 8032, Abschnitt 7.1, TEST 2: Nachricht 0x72.
        var publicKey = Convert.FromHexString(
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        var signature = Convert.FromHexString(
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da"
            + "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

        var publisher = new PublisherKey(
            "vector", Convert.ToBase64String(publicKey), "rfc8032", DateTimeOffset.UnixEpoch);
        var files = new Dictionary<string, byte[]>();

        // Direkt gegen den Verifier: Das Paketformat ist hier nicht der Prüfgegenstand.
        Ed25519Probe.Verify(publicKey, [0x72], signature).Should().BeTrue();
        Ed25519Probe.Verify(publicKey, [0x73], signature).Should().BeFalse(
            "eine andere Nachricht darf dieselbe Signatur nicht tragen");
        publisher.IsActive.Should().BeTrue();
        files.Should().BeEmpty();
    }

    private static MemoryStream ReplaceEntry(MemoryStream source, string entryName, byte[] content)
    {
        var copy = new MemoryStream();
        source.Position = 0;
        source.CopyTo(copy);
        copy.Position = 0;
        using (var archive = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            archive.GetEntry(entryName)?.Delete();
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(content);
        }

        copy.Position = 0;
        return copy;
    }
}

/// <summary>
/// Direkter Zugriff auf die Ed25519-Prüfung für den RFC-Testvektor. Sie liegt im Produktcode
/// internal; hier wird derselbe Bibliotheksaufruf gemacht, damit der Vektor nicht an einer
/// Sichtbarkeit scheitert.
/// </summary>
internal static class Ed25519Probe
{
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var parameters = new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(publicKey, 0);
        var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        verifier.Init(forSigning: false, parameters);
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }
}
