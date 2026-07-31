using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.Isolation;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// Image per Digest oder Policy-Pin; <c>latest</c> sichtbar warnen (WP3.2 Punkt 4).
/// <para>
/// <b>Warum das eine Sicherheitsfrage ist:</b> Der Container ist die Isolationsgrenze, aber
/// <em>was</em> darin läuft, entscheidet das Image. Ein Tag ist ein Zeiger und kann umgehängt
/// werden — dann läuft morgen ein anderes Programm, ohne dass sich an der Konfiguration etwas
/// geändert hätte. Nur ein Digest legt den Inhalt fest.
/// </para>
/// </summary>
public sealed class ImageReferenceTests
{
    /// <summary>
    /// Ein Registry-Port sieht aus wie ein Tag. Wer naiv am letzten Doppelpunkt trennt, hält
    /// <c>5000/werkzeug</c> für einen Tag — und warnt dann über ein Image, das gar nicht gemeint
    /// war, oder schlimmer: gar nicht.
    /// </summary>
    [Theory]
    [InlineData("alpine", "alpine", null, ImagePinKind.Floating)]
    [InlineData("alpine:latest", "alpine", "latest", ImagePinKind.Floating)]
    [InlineData("alpine:3.20", "alpine", "3.20", ImagePinKind.Tag)]
    [InlineData("registry.local:5000/werkzeug", "registry.local:5000/werkzeug", null, ImagePinKind.Floating)]
    [InlineData("registry.local:5000/werkzeug:1.2", "registry.local:5000/werkzeug", "1.2", ImagePinKind.Tag)]
    public void A_reference_is_split_at_the_right_colon(
        string image, string repository, string? tag, ImagePinKind pin)
    {
        var info = ImageReference.Parse(image);

        info.Repository.Should().Be(repository);
        info.Tag.Should().Be(tag);
        info.Pin.Should().Be(pin);
    }

    [Fact]
    public void A_digest_wins_over_a_tag_standing_next_to_it()
    {
        var info = ImageReference.Parse("werkzeug:1.2@sha256:" + new string('a', 64));

        info.Pin.Should().Be(ImagePinKind.Digest);
        info.Digest.Should().StartWith("sha256:");
        info.Repository.Should().Be("werkzeug");
    }

    /// <summary>
    /// <c>latest</c> und „gar kein Tag" sind derselbe Zustand — die Runtime ergänzt <c>:latest</c>.
    /// Beide müssen sichtbar warnen, ein Digest gar nicht.
    /// </summary>
    [Theory]
    [InlineData("alpine")]
    [InlineData("alpine:latest")]
    public void A_floating_reference_warns_visibly(string image)
        => ImageReference.DescribeRisk(image).Should().NotBeNull()
            .And.Subject.As<string>().Should().Contain("Digest");

    [Fact]
    public void A_digest_reference_has_nothing_to_warn_about()
        => ImageReference.DescribeRisk("werkzeug@sha256:" + new string('b', 64)).Should().BeNull();

    /// <summary>
    /// Auch ein fester Tag bekommt einen Hinweis — leiser, aber vorhanden. Ein Tag ist ein Zeiger,
    /// keine Zusage.
    /// </summary>
    [Fact]
    public void Even_a_fixed_tag_gets_a_note()
        => ImageReference.DescribeRisk("alpine:3.20").Should().NotBeNull();

    /// <summary>
    /// Die Warnung ist bewusst kein Fehler: Einen Tag abzulehnen hiesse, den üblichen Weg zu
    /// verbieten und bestehende Konfigurationen stillzulegen. Wer den Pin erzwingen will, schaltet
    /// ihn ausdrücklich ein — <b>dann</b> ist es ein Fehler.
    /// </summary>
    [Fact]
    public void The_pin_is_only_enforced_when_it_was_asked_for()
    {
        ImageReference.SatisfiesPin("alpine:3.20", requireDigest: false, out var none)
            .Should().BeTrue();
        none.Should().BeNull();

        ImageReference.SatisfiesPin("alpine:3.20", requireDigest: true, out var problem)
            .Should().BeFalse();
        problem.Should().Contain("RequireImageDigest");
    }

    /// <summary>
    /// Und der verlangte Pin greift bis in den Startaufruf: Eine Konfiguration mit
    /// <c>RequireImageDigest</c> und einem Tag startet nicht, statt still das Falsche zu ziehen.
    /// </summary>
    [Fact]
    public void A_required_digest_stops_the_launch()
    {
        var act = () => ContainerLaunchPolicy.BuildRunArguments(new ContainerLaunchRequest(
            new IsolationOptions(
                IsolationMode.Container, Image: "alpine:latest", RequireImageDigest: true),
            ContainerIdentity.ForUpstream("werkzeug", "instanz-1"),
            ContainerLifetime.Session));

        act.Should().Throw<ArgumentException>().WithMessage("*Digest*");
    }
}
