using System.Text.RegularExpressions;

using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Core.Tests;

/// <summary>
/// Die Version steht an genau einer Stelle: <c>&lt;VersionPrefix&gt;</c> in
/// <c>Directory.Build.props</c>. Geprüft wird, dass <see cref="BifrostProductInfo.Version"/> ihr
/// tatsächlich folgt — ein zweiter Ort für dieselbe Zahl driftet, und zwar unbemerkt, weil beide
/// für sich plausibel aussehen.
/// <para>
/// <b>Früher stand hier ein Literal</b> (<c>Should().Be("0.11.0")</c>). Das prüfte nicht die
/// Kopplung, sondern verlangte bei jedem Versionssprung eine Nachpflege — und wurde beim Sprung auf
/// 0.12.0 prompt rot, im ersten Releaselauf, nach dem Push. Ein Test, der bei einer korrekten
/// Änderung fehlschlägt, erzieht dazu, ihn anzupassen statt ihn zu lesen.
/// </para>
/// </summary>
public class VersioningTests
{
    [Fact]
    public void Product_version_comes_from_the_shared_build_property()
    {
        var expected = VersionPrefixFromBuildProps();

        BifrostProductInfo.Version.Should().Be(
            expected,
            "die Produktversion wird aus <VersionPrefix> abgeleitet und nicht daneben gepflegt");

        typeof(BifrostProductInfo).Assembly.GetName().Version!.ToString(3)
            .Should().Be(BifrostProductInfo.Version);
    }

    /// <summary>
    /// Liest <c>&lt;VersionPrefix&gt;</c> aus der Datei, statt sie aus dem Kompilat zurückzurechnen.
    /// Beides aus derselben Quelle zu nehmen hieße, die Kopplung mit sich selbst zu vergleichen.
    /// </summary>
    private static string VersionPrefixFromBuildProps()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Directory.Build.props");
            if (File.Exists(candidate))
            {
                var match = Regex.Match(
                    File.ReadAllText(candidate),
                    @"<VersionPrefix>\s*(?<version>[^<\s]+)\s*</VersionPrefix>");

                match.Success.Should().BeTrue(
                    $"'{candidate}' muss ein <VersionPrefix> tragen — es ist die einzige Quelle");
                return match.Groups["version"].Value;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Directory.Build.props wurde oberhalb von "
            + $"'{AppContext.BaseDirectory}' nicht gefunden.");
    }
}
