using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Packaging;
using McpMcp.Core.Tests.Catalog;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace McpMcp.Core.Tests.Packaging;

/// <summary>
/// Ein Paket bringt Konnektor <b>und</b> Skill mit (Material 0021-EM, Option B).
/// <para>
/// Der Grund für das Ganze ist <c>required-tools</c>: Bisher konnte der Gateway die Zusage nur
/// prüfen und melden, wenn ein Tool fehlt. Ein Paket stellt sie her — die Tools kommen mit.
/// </para>
/// <para>
/// Der Preis dafür ist die Asymmetrie, die diese Tests festhalten: Ein Konnektor ist eingesperrt,
/// ein Skill nicht. Deshalb ist die Zustimmung an den Textinhalt gebunden und kennt keinen Rabatt
/// für vertrauenswürdige Herausgeber.
/// </para>
/// </summary>
public sealed class PackagedSkillTests : IDisposable
{
    private const string SkillText = "## Benutzung\nZuerst search_tools, dann invoke_tool.";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mcpmcp-skillpkg-{Guid.NewGuid():N}");

    private readonly TestPublisher _publisher = new();
    private readonly InMemoryPackageStore _store = new();
    private readonly FakeAssetStore _assets = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ConnectorPackageInstaller Installer(
        IAssetStore? assets = null, ConnectorTrustLevel? level = null)
        => new(
            _root,
            _store,
            new StaticTrustStore([level is null ? _publisher.Key : _publisher.Key with { TrustLevel = level.Value }]),
            (_, _) => Task.CompletedTask,
            _time,
            assets: assets ?? _assets);

    private string Consent(string text = SkillText, string version = "1.0.0")
        => TestPackage.ConsentFor(_publisher, text, version: version);

    [Fact]
    public async Task Skill_from_a_package_gets_the_package_prefix_and_its_origin()
    {
        using var package = TestPackage.WithSkill(_publisher, SkillText, whenToUse: "Beim ersten Aufruf");

        var result = await Installer().InstallAsync(
            package, new ConnectorInstallOptions([Consent()]), null,
            TestContext.Current.CancellationToken);

        result.Skills.Should().ContainSingle();
        var published = result.Skills[0];
        published.Name.Should().Be("com.example.echo/benutzung",
            "das Präfix verhindert, dass ein Paket einen handgeschriebenen Skill überschattet");
        published.ReplacedLocalEdit.Should().BeFalse();

        var stored = (await _assets.ListAsync(TestContext.Current.CancellationToken)).Single();
        stored.Source.Should().Be(new SkillSource("com.example.echo", "1.0.0"));
        stored.MetadataOrEmpty.WhenToUse.Should().Be("Beim ersten Aufruf");
        (await _assets.GetAsync(stored.Id, null, TestContext.Current.CancellationToken))
            .Content.Should().Be(SkillText, "ausgeliefert wird der Text, der signiert wurde");
    }

    /// <summary>
    /// Die Entscheidung, die dieses Increment trägt: Für einen Zugriff nach außen gibt es eine
    /// Laufzeitgrenze, für einen Satz nicht. Also keine Ausnahme, auch nicht für 'Official'.
    /// </summary>
    [Theory]
    [InlineData(ConnectorTrustLevel.Official)]
    [InlineData(ConnectorTrustLevel.ThirdParty)]
    public async Task Without_consent_no_skill_is_installed_at_any_trust_level(ConnectorTrustLevel level)
    {
        using var package = TestPackage.WithSkill(_publisher, SkillText);

        var act = async () => await Installer(level: level).InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ConnectorPackageException>())
            .WithMessage("*Skills mit, denen niemand zugestimmt hat*");
        (await _assets.ListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    /// <summary>
    /// Die Zustimmung bindet an den Hash des Textes. Ein Update mit geändertem Text ist damit
    /// automatisch neu zu bestätigen — sonst wäre die Zustimmung eine zu einem Namen, und unter
    /// demselben Namen stünde beim nächsten Mal etwas anderes.
    /// </summary>
    [Fact]
    public async Task Consent_expires_when_the_text_changes()
    {
        var alt = Consent();
        using var package = TestPackage.WithSkill(_publisher, "Ein ganz anderer Text.");

        var act = async () => await Installer().InstallAsync(
            package, new ConnectorInstallOptions([alt]), null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConnectorPackageException>();
    }

    [Fact]
    public async Task A_package_update_appends_a_version()
    {
        using var v1 = TestPackage.WithSkill(_publisher, SkillText);
        await Installer().InstallAsync(
            v1, new ConnectorInstallOptions([Consent()]), null, TestContext.Current.CancellationToken);

        const string neu = "## Benutzung\nJetzt mit describe_tool dazwischen.";
        using var v2 = TestPackage.WithSkill(_publisher, neu, version: "1.1.0");
        var result = await Installer().InstallAsync(
            v2, new ConnectorInstallOptions([Consent(neu, "1.1.0")]), null,
            TestContext.Current.CancellationToken);

        result.Skills[0].Version.Value.Should().Be(2);
        result.ReplacedLocalEdits.Should().BeEmpty("die vorige Fassung kam aus demselben Paket");
        var stored = (await _assets.ListAsync(TestContext.Current.CancellationToken)).Single();
        stored.Source!.PackageVersion.Should().Be("1.1.0");
    }

    /// <summary>
    /// Der Fall, der ohne Herkunftsfeld nicht erkennbar wäre: Jemand hat den Text angepasst, dann
    /// kommt ein Update. Angehängt wird es trotzdem — aber es wird gemeldet, und die eigene Fassung
    /// bleibt in der Historie.
    /// </summary>
    [Fact]
    public async Task Replacing_a_locally_edited_skill_is_reported_and_the_old_version_survives()
    {
        using var v1 = TestPackage.WithSkill(_publisher, SkillText);
        var first = await Installer().InstallAsync(
            v1, new ConnectorInstallOptions([Consent()]), null, TestContext.Current.CancellationToken);

        await _assets.PublishAsync(
            first.Skills[0].Id, "Meine eigene Fassung.", null, TestContext.Current.CancellationToken);

        const string neu = "## Benutzung\nHerstellerfassung 1.1.0.";
        using var v2 = TestPackage.WithSkill(_publisher, neu, version: "1.1.0");
        var result = await Installer().InstallAsync(
            v2, new ConnectorInstallOptions([Consent(neu, "1.1.0")]), null,
            TestContext.Current.CancellationToken);

        result.ReplacedLocalEdits.Should().ContainSingle()
            .Which.Name.Should().Be("com.example.echo/benutzung");

        var versions = await _assets.GetVersionsAsync(
            first.Skills[0].Id, TestContext.Current.CancellationToken);
        versions.Select(v => v.Content).Should().Contain("Meine eigene Fassung.",
            "die angepasste Fassung bleibt erreichbar — Zurückschalten existiert");
    }

    [Fact]
    public async Task A_failed_probe_leaves_no_skill_behind()
    {
        using var package = TestPackage.WithSkill(_publisher, SkillText);
        var installer = new ConnectorPackageInstaller(
            _root, _store, new StaticTrustStore([_publisher.Key]),
            (_, _) => Task.FromException(new InvalidOperationException("Connector antwortet nicht")),
            _time, assets: _assets);

        var act = async () => await installer.InstallAsync(
            package, new ConnectorInstallOptions([Consent()]), null,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _assets.ListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty(
            "eine Anweisung aus einem Konnektor, der nie gelaufen ist, wäre trotzdem in Umlauf");
    }

    /// <summary>
    /// Ohne Skill-Ablage würde der Konnektor installiert und die Anleitung dazu stillschweigend
    /// verschwinden. Lieber abweisen als halb liefern.
    /// </summary>
    [Fact]
    public async Task A_package_with_skills_is_rejected_when_no_skill_store_is_wired_up()
    {
        using var package = TestPackage.WithSkill(_publisher, SkillText);
        var installer = new ConnectorPackageInstaller(
            _root, _store, new StaticTrustStore([_publisher.Key]), (_, _) => Task.CompletedTask, _time);

        var act = async () => await installer.InstallAsync(
            package, new ConnectorInstallOptions([Consent()]), null,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ConnectorPackageException>())
            .WithMessage("*keine Skill-Ablage*");
    }

    [Fact]
    public async Task A_package_without_skills_keeps_working_unchanged()
    {
        using var package = TestPackage.Valid(_publisher);

        var result = await Installer().InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        result.Package.State.Should().Be(PackageState.Active);
        result.Skills.Should().BeEmpty();
    }
}
