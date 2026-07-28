using System.Text;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Packaging;
using Xunit;

namespace McpMcp.Core.Tests.Packaging;

/// <summary>
/// Was der Leser an einem mitgelieferten Skill prüft, bevor irgendjemand ihn zu Gesicht bekommt.
/// Der Text selbst ist über die Nutzdaten-Hashes schon abgedeckt — hier geht es um das, was das
/// Format sonst nicht sieht.
/// </summary>
public class PackagedSkillReaderTests
{
    private readonly TestPublisher _publisher = new();

    private IReadOnlyList<PublisherKey> Keys => [_publisher.Key];

    [Fact]
    public void A_declared_skill_is_read_back_with_its_text()
    {
        using var package = TestPackage.WithSkill(_publisher, "Erst suchen, dann aufrufen.");

        var verified = ConnectorPackageReader.Verify(package, Keys);
        var texts = ConnectorPackageReader.ReadSkillTexts(package, verified.Manifest);

        verified.Manifest.SkillsOrEmpty.Should().ContainSingle();
        texts["benutzung"].Should().Be("Erst suchen, dann aufrufen.");
    }

    /// <summary>
    /// Der wichtigste der vier Fälle: Zeigt ein Skill auf eine Datei, die nicht im Manifest steht,
    /// ist sein Text <b>nicht signiert</b> — dann steht hinter der Anweisung niemand.
    /// </summary>
    [Fact]
    public void A_skill_pointing_at_an_undeclared_file_is_rejected()
    {
        var files = TestPackage.Files(skillText: "egal");
        var manifest = TestPackage.WithPayloads(
            TestPackage.Manifest(
                _publisher,
                skills: [new ConnectorSkill("benutzung", "skills/anders.md")]),
            files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Keys);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*nicht signiert*");
    }

    [Fact]
    public void A_skill_name_with_a_separator_is_rejected()
    {
        var files = TestPackage.Files(skillText: "egal");
        var manifest = TestPackage.WithPayloads(
            TestPackage.Manifest(
                _publisher,
                skills: [new ConnectorSkill("fremd/benutzung", TestPackage.SkillEntry)]),
            files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Keys);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*Trenner*",
            "das Paketpräfix setzt der Gateway — sonst könnte ein Paket einen fremden Skill treffen");
    }

    [Fact]
    public void The_same_skill_name_twice_is_rejected()
    {
        var files = TestPackage.Files(skillText: "egal");
        var manifest = TestPackage.WithPayloads(
            TestPackage.Manifest(
                _publisher,
                skills:
                [
                    new ConnectorSkill("benutzung", TestPackage.SkillEntry),
                    new ConnectorSkill("benutzung", TestPackage.SkillEntry),
                ]),
            files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Keys);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*mehrfach deklariert*");
    }

    /// <summary>
    /// Ein Skill wird einem Sprachmodell vorgelegt. Was kein Text ist, hat dort nichts verloren —
    /// und ein Prüfer, der es durchreicht, verschiebt den Fehler nur nach hinten.
    /// </summary>
    [Fact]
    public void A_skill_that_is_not_text_is_rejected()
    {
        var files = new Dictionary<string, byte[]>(TestPackage.Files())
        {
            [TestPackage.SkillEntry] = [0xFF, 0xFE, 0x00, 0x80],
        };
        var manifest = TestPackage.WithPayloads(
            TestPackage.Manifest(
                _publisher, skills: [new ConnectorSkill("benutzung", TestPackage.SkillEntry)]),
            files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Keys);

        act.Should().Throw<ConnectorPackageException>().WithMessage("*UTF-8*");
    }

    [Fact]
    public void An_oversized_skill_is_rejected()
    {
        var files = new Dictionary<string, byte[]>(TestPackage.Files())
        {
            [TestPackage.SkillEntry] =
                Encoding.UTF8.GetBytes(new string('x', (int)ConnectorPackageReader.MaxSkillBytes + 1)),
        };
        var manifest = TestPackage.WithPayloads(
            TestPackage.Manifest(
                _publisher, skills: [new ConnectorSkill("benutzung", TestPackage.SkillEntry)]),
            files);
        using var package = TestPackage.Raw(manifest, _publisher.Sign, files);

        var act = () => ConnectorPackageReader.Verify(package, Keys);

        act.Should().Throw<ConnectorPackageException>();
    }

    /// <summary>
    /// Der Zustimmungseintrag trägt den Hash des Textes. Zwei Pakete mit demselben Skill-Namen,
    /// aber anderem Inhalt, dürfen nicht dieselbe Zustimmung einlösen können.
    /// </summary>
    [Fact]
    public void The_consent_token_follows_the_content()
    {
        var eins = TestPackage.ConsentFor(_publisher, "Fassung A");
        var zwei = TestPackage.ConsentFor(_publisher, "Fassung B");

        eins.Should().StartWith("skill:benutzung@").And.NotBe(zwei);
    }
}
