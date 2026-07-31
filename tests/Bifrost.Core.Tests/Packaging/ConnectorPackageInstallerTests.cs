using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Packaging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Packaging;

/// <summary>
/// Installation, Update, Rollback und Quarantäne (ADR-0016). Der Punkt dieser Datei: Eine Version,
/// die die Probe nicht besteht, darf nie in Betrieb gewesen sein.
/// </summary>
public sealed class ConnectorPackageInstallerTests : IDisposable
{
    private readonly TestPublisher _publisher = new();
    private readonly InMemoryPackageStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcpkg-root-{Guid.NewGuid():N}");
    private int _probes;
    private Exception? _probeFailure;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ConnectorPackageInstaller Installer(params PublisherKey[] pinned)
        => new(
            _root,
            _store,
            new StaticTrustStore(pinned.Length == 0 ? [_publisher.Key] : pinned),
            (context, ct) =>
            {
                _probes++;
                return _probeFailure is null ? Task.CompletedTask : Task.FromException(_probeFailure);
            },
            _time);

    [Fact]
    public async Task An_installed_package_becomes_the_active_version()
    {
        using var package = TestPackage.Valid(_publisher);

        var installed = await Installer().InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        installed.Package.State.Should().Be(PackageState.Active);
        installed.Package.PackageId.Should().Be("com.example.echo");
        installed.Package.TrustLevel.Should().Be(ConnectorTrustLevel.Official);
        installed.Package.ActivatedAt.Should().Be(_time.GetUtcNow());
        _probes.Should().Be(1, "installiert wird nur, was geprobt wurde");
        File.Exists(Path.Combine(_root, "com.example.echo", "1.0.0", "payload", "component.wasm"))
            .Should().BeTrue();
    }

    /// <summary>
    /// Der Kern der Quarantäne: Scheitert die Probe, bleiben keine Dateien liegen und nichts wird
    /// aktiv — aber der Fehlschlag steht als Beleg da.
    /// </summary>
    [Fact]
    public async Task A_package_that_fails_the_probe_never_becomes_active()
    {
        _probeFailure = new InvalidOperationException("Component startet nicht");
        using var package = TestPackage.Valid(_publisher);

        var act = async () => await Installer().InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _store.GetActiveAsync("com.example.echo", TestContext.Current.CancellationToken))
            .Should().BeNull();
        var versions = await _store.GetVersionsAsync("com.example.echo", TestContext.Current.CancellationToken);
        versions.Should().ContainSingle().Which.State.Should().Be(PackageState.Failed);
        versions[0].FailureReason.Should().Contain("startet nicht");
        // Der Sammelordner darf stehen bleiben; entscheidend ist, dass die Dateien des
        // Fehlversuchs weg sind — sonst wüchse die Quarantäne mit jedem Anlauf.
        var quarantine = Path.Combine(_root, ".quarantine");
        (Directory.Exists(quarantine)
            ? Directory.GetFileSystemEntries(quarantine)
            : []).Should().BeEmpty("die Dateien des Fehlversuchs werden aufgeräumt");
        Directory.Exists(Path.Combine(_root, "com.example.echo", "1.0.0")).Should().BeFalse(
            "eine gescheiterte Version hat nie am Zielort gestanden");
    }

    [Fact]
    public async Task An_update_supersedes_the_previous_version_and_rollback_brings_it_back()
    {
        var installer = Installer();
        using var first = TestPackage.Valid(_publisher, version: "1.0.0");
        await installer.InstallAsync(
            first, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(5));
        using var second = TestPackage.Valid(_publisher, version: "1.1.0");
        var updated = await installer.InstallAsync(
            second, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);
        updated.Package.Version.Should().Be("1.1.0");

        var versions = await _store.GetVersionsAsync("com.example.echo", TestContext.Current.CancellationToken);
        versions.Single(v => v.Version == "1.0.0").State.Should().Be(PackageState.Superseded,
            "die vorherige Version bleibt liegen — sonst wäre ein Rollback ein neuer Download");

        _time.Advance(TimeSpan.FromMinutes(1));
        var back = await installer.RollbackAsync(
            "com.example.echo", null, TestContext.Current.CancellationToken);

        back.Version.Should().Be("1.0.0");
        back.State.Should().Be(PackageState.Active);
        (await _store.GetVersionsAsync("com.example.echo", TestContext.Current.CancellationToken))
            .Single(v => v.Version == "1.1.0").State.Should().Be(PackageState.Superseded);
    }

    [Fact]
    public async Task Rollback_without_a_previous_version_says_so()
    {
        var installer = Installer();
        using var package = TestPackage.Valid(_publisher);
        await installer.InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        var act = async () => await installer.RollbackAsync(
            "com.example.echo", null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConnectorPackageException>()
            .WithMessage("*keine abgelöste Version*");
    }

    /// <summary>
    /// Dieselbe Version zweimal wäre nicht unterscheidbar — und ein stilles Überschreiben täuschte
    /// vor, es sei noch derselbe Inhalt.
    /// </summary>
    [Fact]
    public async Task The_same_version_cannot_be_installed_twice()
    {
        var installer = Installer();
        using var first = TestPackage.Valid(_publisher);
        await installer.InstallAsync(
            first, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);
        using var again = TestPackage.Valid(_publisher);

        var act = async () => await installer.InstallAsync(
            again, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConnectorPackageException>().WithMessage("*bereits installiert*");
    }

    /// <summary>Nach einem Fehlschlag darf dieselbe Version erneut versucht werden.</summary>
    [Fact]
    public async Task A_failed_version_can_be_retried()
    {
        _probeFailure = new InvalidOperationException("einmalig kaputt");
        var installer = Installer();
        using var broken = TestPackage.Valid(_publisher);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await installer.InstallAsync(
            broken, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken));

        _probeFailure = null;
        using var fixedPackage = TestPackage.Valid(_publisher);
        var installed = await installer.InstallAsync(
            fixedPackage, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        installed.Package.State.Should().Be(PackageState.Active);
        installed.Package.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task The_active_version_cannot_be_removed_by_accident()
    {
        var installer = Installer();
        using var package = TestPackage.Valid(_publisher);
        await installer.InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        var act = async () => await installer.RemoveVersionAsync(
            "com.example.echo", "1.0.0", null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConnectorPackageException>().WithMessage("*aktive Version*");
    }

    [Fact]
    public async Task Removing_the_package_takes_the_files_with_it()
    {
        var installer = Installer();
        using var package = TestPackage.Valid(_publisher);
        await installer.InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        await installer.RemovePackageAsync("com.example.echo", null, TestContext.Current.CancellationToken);

        (await _store.ListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "com.example.echo")).Should().BeFalse();
    }

    /// <summary>
    /// Der Resolver zeigt auf die aktive Version — und wandert beim Rollback mit. Sonst zeigte eine
    /// Upstream-Konfiguration nach dem Zurückschalten weiter auf die neue Datei.
    /// </summary>
    [Fact]
    public async Task The_resolver_follows_the_active_version()
    {
        var installer = Installer();
        var resolver = new ConnectorPackageResolver(_store);
        using var first = TestPackage.Valid(_publisher, version: "1.0.0");
        await installer.InstallAsync(
            first, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);
        using var second = TestPackage.Valid(_publisher, version: "2.0.0");
        await installer.InstallAsync(
            second, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        await resolver.RefreshAsync(TestContext.Current.CancellationToken);
        resolver.ResolveActive("com.example.echo")!.Value.EntryPoint.Should().Contain("2.0.0");

        await installer.RollbackAsync("com.example.echo", null, TestContext.Current.CancellationToken);
        await resolver.RefreshAsync(TestContext.Current.CancellationToken);

        resolver.ResolveActive("com.example.echo")!.Value.EntryPoint.Should().Contain("1.0.0");
        resolver.ResolveActive("gibt-es-nicht").Should().BeNull();
    }

    [Fact]
    public async Task A_package_for_another_platform_is_refused()
    {
        using var package = TestPackage.Valid(_publisher, platforms: ["solaris-sparc"]);

        var act = async () => await Installer().InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConnectorPackageException>().WithMessage("*Plattformen*");
    }
}
