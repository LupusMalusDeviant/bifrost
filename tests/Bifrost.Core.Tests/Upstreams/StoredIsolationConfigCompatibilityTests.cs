using System.Text.Json;

using AwesomeAssertions;

using Bifrost.Abstractions;

using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

/// <summary>
/// Der Datenverlust-Regressionstest zur Umbenennung <c>CliIsolationOptions</c> →
/// <c>IsolationOptions</c> und <c>CliIsolationMode</c> → <c>IsolationMode</c> (M3-Vertrag §4,
/// ADR-0025 E5).
/// <para>
/// <b>Warum das ein Sicherheits- und kein Kosmetikthema ist:</b> Upstream-Konfigurationen liegen
/// als JSON in der Datenbank (<c>EfUpstreamConfigStore</c>, DataProtection-verschlüsselt). Eine
/// Konfiguration, die nach dem Upgrade nicht mehr gelesen wird, ist Datenverlust — dieselbe
/// Fehlerklasse wie eine umbenannte DataProtection-Purpose (siehe <c>CryptographicNamesTests</c>).
/// Der Unterschied zu dort: Ein Typname steht in keinem Dokument, ein
/// <b>Eigenschaftsname</b> schon, und ein Enum steht als <b>Zahl</b> drin.
/// </para>
/// <para>
/// Deshalb prüft dieser Test nicht die Umbenennung, sondern ihr Gegenteil: Ein wörtlich
/// festgehaltenes Dokument aus der Zeit <em>vor</em> der Umbenennung muss sich unverändert lesen
/// lassen — mit denselben Werten, und ohne dass die neu angehängten Felder etwas verschieben.
/// </para>
/// <para>
/// <b>Wer hier rot wird, hat zwei Möglichkeiten</b>: die Änderung zurücknehmen oder einen
/// Migrationslauf schreiben, der jede gespeicherte Version umschreibt. Den Test anzupassen ist
/// keine dritte.
/// </para>
/// </summary>
public class StoredIsolationConfigCompatibilityTests
{
    /// <summary>
    /// Der Store serialisiert mit den <b>Vorgabeoptionen</b> (<c>EfUpstreamConfigStore</c>:
    /// <c>JsonSerializer.SerializeToUtf8Bytes(config)</c>). Deshalb wird hier ohne Optionen
    /// gelesen — ein Test mit abweichenden Optionen bewiese etwas über einen anderen Weg.
    /// </summary>
    private static UpstreamServerConfig Read(string json)
        => JsonSerializer.Deserialize<UpstreamServerConfig>(json)
            ?? throw new InvalidOperationException("Das Altdokument ergab null.");

    /// <summary>
    /// Wörtlich so geschrieben, wie die Fassung <b>vor</b> der Umbenennung es abgelegt hat:
    /// PascalCase-Eigenschaftsnamen, Enums als Zahl, und ohne die Felder, die es damals nicht gab
    /// (<c>StopTimeoutSeconds</c>, <c>RequireImageDigest</c>).
    /// </summary>
    private const string StoredBeforeTheRename = """
        {
          "Slug": "alt-cli",
          "DisplayName": "Vor der Umbenennung angelegt",
          "Kind": 3,
          "Enabled": true,
          "Stdio": null,
          "Http": null,
          "OpenApi": null,
          "Restart": null,
          "CallTimeout": null,
          "Cli": {
            "Executable": "/usr/bin/werkzeug",
            "Tools": [
              {
                "Name": "lauf",
                "Description": null,
                "FixedArguments": ["--einmal"],
                "AllowCallerArguments": false,
                "Parameters": null,
                "Risk": 0,
                "MaxConcurrency": null
              }
            ],
            "WorkingDirectory": null,
            "EnvironmentVariables": null,
            "TimeoutSeconds": null,
            "MaxOutputBytes": 65536,
            "AllowPathLookup": false,
            "AllowedExecutableRoots": null,
            "AllowedWorkingDirectoryRoots": null,
            "AllowedReadRoots": ["/daten/ein"],
            "AllowedWriteRoots": null,
            "MaxConcurrency": 4,
            "OutputEncoding": "utf-8",
            "ExecutableSha256": null,
            "Isolation": {
              "Mode": 1,
              "Image": "alpine:3.20",
              "Runtime": "docker",
              "User": "65532:65532",
              "MemoryLimitMb": 512,
              "CpuLimit": 1,
              "PidLimit": 128,
              "NetworkAllow": null,
              "TmpfsSizeMb": 64
            }
          },
          "Wasi": null,
          "OpenRpc": null
        }
        """;

    /// <summary>
    /// Ein CLI-Upstream im Container-Modus, angelegt vor der Umbenennung. Er muss sich nach ihr
    /// vollständig und wertgleich lesen lassen.
    /// </summary>
    [Fact]
    public void A_container_configuration_written_before_the_rename_still_reads()
    {
        var config = Read(StoredBeforeTheRename);

        config.Slug.Should().Be("alt-cli");
        config.Kind.Should().Be(UpstreamTransportKind.Cli);

        var isolation = config.Cli?.Isolation;
        isolation.Should().NotBeNull(
            "der Abschnitt hiess im Dokument schon immer 'Isolation' — der Typname steht dort nicht");
        isolation!.Mode.Should().Be(
            IsolationMode.Container,
            "die 1 im Dokument war und bleibt der Container-Modus");
        isolation.Image.Should().Be("alpine:3.20");
        isolation.Runtime.Should().Be("docker");
        isolation.User.Should().Be("65532:65532");
        isolation.MemoryLimitMb.Should().Be(512);
        isolation.CpuLimit.Should().Be(1.0);
        isolation.PidLimit.Should().Be(128);
        isolation.TmpfsSizeMb.Should().Be(64);
        isolation.NetworkAllow.Should().BeNull("leer heisst: kein Netzwerk");

        config.Cli!.AllowedReadRoots.Should().ContainSingle().Which.Should().Be("/daten/ein");
    }

    /// <summary>
    /// Die neu angehängten Felder verschieben nichts: Ein Altdokument, das sie nicht kennt, bekommt
    /// ihre Vorgaben — und die Vorgaben sind die bisherige Wirklichkeit.
    /// </summary>
    [Fact]
    public void Fields_added_by_the_rename_default_to_the_previous_behaviour()
    {
        var isolation = Read(StoredBeforeTheRename).Cli!.Isolation!;

        isolation.StopTimeoutSeconds.Should().Be(
            10, "ein Altdokument kennt das Feld nicht und darf davon nicht anders laufen");
        isolation.RequireImageDigest.Should().BeFalse(
            "einen Digest nachtraeglich zu verlangen wuerde bestehende Konfigurationen stilllegen");
    }

    /// <summary>
    /// Die Zahlen hinter dem Enum sind Teil des gespeicherten Dokuments. Sie umzusortieren würde
    /// aus jedem gespeicherten <c>Container</c> ein <c>Host</c> machen — eine stille Herabstufung
    /// der Isolation, genau die Klasse Fehler, die ADR-0018 verbietet.
    /// </summary>
    [Theory]
    [InlineData(IsolationMode.Host, 0)]
    [InlineData(IsolationMode.Container, 1)]
    public void The_numeric_value_of_the_mode_never_changes(IsolationMode mode, int stored)
    {
        ((int)mode).Should().Be(stored);
        JsonSerializer.Deserialize<IsolationMode>(
            stored.ToString(System.Globalization.CultureInfo.InvariantCulture)).Should().Be(mode);
    }

    /// <summary>
    /// Die Gegenrichtung: Was heute geschrieben wird, trägt dieselben Namen wie das Altdokument.
    /// Ohne diese Probe wäre der Test oben auch dann grün, wenn das Schreiben ab jetzt etwas
    /// anderes ablegt als das, was er liest.
    /// </summary>
    [Fact]
    public void What_is_written_today_carries_the_same_names_as_before()
    {
        var json = JsonSerializer.Serialize(new IsolationOptions(
            IsolationMode.Container, Image: "alpine:3.20"));

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        names.Should().Contain(
            ["Mode", "Image", "Runtime", "User", "MemoryLimitMb", "CpuLimit", "PidLimit",
             "NetworkAllow", "TmpfsSizeMb"],
            "diese Namen stehen in jeder bestehenden Datenbank");
        document.RootElement.GetProperty("Mode").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// stdio hat mit dieser Welle ein Isolationsfeld bekommen. Ein stdio-Upstream aus der Zeit
    /// davor kennt es nicht — und muss deshalb weiterhin im Host-Modus laufen. Ihn beim Upgrade
    /// stillzulegen wäre die Verhaltensänderung, die ADR-0025 E3 ausdrücklich ablehnt.
    /// </summary>
    [Fact]
    public void An_old_stdio_configuration_keeps_host_mode_and_an_undecided_ssrf_switch()
    {
        const string stored = """
            {
              "Slug": "alt-stdio",
              "DisplayName": "Bestand",
              "Kind": 0,
              "Enabled": true,
              "Stdio": {
                "Command": "/usr/bin/server",
                "Arguments": ["--stdio"],
                "EnvironmentVariables": null,
                "WorkingDirectory": null
              },
              "Http": null
            }
            """;

        var config = Read(stored);

        config.Stdio.Should().NotBeNull();
        config.Stdio!.Command.Should().Be("/usr/bin/server");
        config.Stdio.Isolation.Should().BeNull(
            "ohne Angabe gilt der bisherige Host-Modus; das Feld allein aendert nichts");
    }

    /// <summary>
    /// Der Bestandsteil der SSRF-Frage: Ein vor der Umstellung geschriebener HTTP-Upstream trägt
    /// <c>AllowPrivateTargets = null</c> — „nicht entschieden", nicht „verboten". Er läuft weiter.
    /// Was sich ändert, gilt nur für <b>Neuanlagen</b> (siehe <c>SecureUpstreamDefaults</c>).
    /// </summary>
    [Fact]
    public void An_old_http_configuration_keeps_its_undecided_private_target_switch()
    {
        const string stored = """
            {
              "Slug": "alt-http",
              "DisplayName": "Bestand",
              "Kind": 1,
              "Enabled": true,
              "Http": {
                "Endpoint": "https://beispiel.test/mcp",
                "Headers": null,
                "AllowLegacySse": true,
                "OAuth": null
              }
            }
            """;

        Read(stored).Http!.AllowPrivateTargets.Should().BeNull(
            "Bestandsinstanzen haben den Schalter nie gesetzt; sie beim Upgrade abzuklemmen waere "
            + "dieselbe stille Verhaltensaenderung, die ADR-0025 E3 ablehnt");
    }
}
