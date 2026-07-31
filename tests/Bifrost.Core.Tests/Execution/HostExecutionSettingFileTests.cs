using AwesomeAssertions;

using Bifrost.Core.Execution;

using Xunit;

namespace Bifrost.Core.Tests.Execution;

/// <summary>
/// Der geschriebene Wert im Datenverzeichnis. Er ist der Unterschied zwischen „die Instanz nimmt
/// jeden Start neu an, dass es so gemeint war" und „jemand hat es festgehalten, und man kann es
/// ändern" (ADR-0025 E3, Punkt 2).
/// </summary>
public sealed class HostExecutionSettingFileTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"bfr-pol-datei-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Eine_frische_installation_hat_noch_keinen_wert()
        => new HostExecutionSettingFile(_dataDirectory).Read().Should().BeNull();

    [Fact]
    public void Der_geschriebene_wert_wird_vollstaendig_wiedergelesen()
    {
        var store = new HostExecutionSettingFile(_dataDirectory);
        var record = new HostExecutionSettingRecord(
            true,
            HostExecutionOrigin.AdoptedFromExistingInstance,
            new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero),
            ["alt (Stdio: /usr/bin/tool)"],
            "Bestand uebernommen.");

        store.Write(record);

        var reread = store.Read()!;
        reread.Allowed.Should().Be(record.Allowed);
        reread.Origin.Should().Be(record.Origin);
        reread.WrittenAt.Should().Be(record.WrittenAt);
        reread.Note.Should().Be(record.Note);
        reread.Upstreams.Should().Equal(record.Upstreams);
        File.Exists(store.Location).Should().BeTrue();
    }

    /// <summary>
    /// Der Wert steht in Klartext da und lässt sich von Hand ändern — das ist der Sinn: aus einer
    /// unsichtbaren Vorgabe wird etwas, das jemand anfassen kann.
    /// </summary>
    [Fact]
    public void Der_wert_ist_fuer_menschen_lesbar()
    {
        var store = new HostExecutionSettingFile(_dataDirectory);
        store.Write(new HostExecutionSettingRecord(
            true, HostExecutionOrigin.AdoptedFromExistingInstance, DateTimeOffset.UnixEpoch,
            ["alt"], "Bestand uebernommen."));

        var content = File.ReadAllText(store.Location);

        content.Should().Contain("AdoptedFromExistingInstance");
        content.Should().Contain("alt");
    }

    /// <summary>
    /// Eine beschädigte Datei sähe sonst aus wie eine frische Instanz — und die Instanz stellte sich
    /// ohne Ansage um. Ein Fehler ist hier die ehrlichere Antwort als ein <c>null</c>.
    /// </summary>
    [Fact]
    public void Eine_beschaedigte_datei_wird_nicht_als_frische_instanz_gelesen()
    {
        var store = new HostExecutionSettingFile(_dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Location)!);
        File.WriteAllText(store.Location, "{ das ist kein JSON");

        var act = () => store.Read();

        act.Should().Throw<HostExecutionSettingException>();
    }

    [Fact]
    public void Ein_zweiter_wert_ersetzt_den_ersten()
    {
        var store = new HostExecutionSettingFile(_dataDirectory);
        store.Write(new HostExecutionSettingRecord(
            true, HostExecutionOrigin.AdoptedFromExistingInstance, DateTimeOffset.UnixEpoch, [], "erst"));
        store.Write(new HostExecutionSettingRecord(
            false, HostExecutionOrigin.Environment, DateTimeOffset.UnixEpoch, [], "dann"));

        store.Read()!.Allowed.Should().BeFalse();
        store.Read()!.Note.Should().Be("dann");
    }
}
