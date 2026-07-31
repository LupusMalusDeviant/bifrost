using System.Collections;
using AwesomeAssertions;
using Bifrost.Server;
using Xunit;

namespace Bifrost.Integration.Tests;

/// <summary>
/// Der Übergang von <c>MCPMCP_*</c> auf <c>BIFROST_*</c> (Umbenennung am 2026-07-31).
/// <para>
/// <b>Warum das geprüft wird und nicht bloß behauptet:</b> Die Umgebungsvariablen sind die gesamte
/// Konfiguration dieses Gateways. Greift die Übernahme nicht, startet eine bestehende Installation
/// nach dem Update auf lauter Vorgabewerten — mit leerer Datenbank neben der vollen, ohne
/// Fehlermeldung, und meldet dabei „bereit". Ein Ausfall, der wie ein Erfolg aussieht, wird nicht
/// bemerkt, sondern geglaubt.
/// </para>
/// </summary>
public sealed class LegacyEnvironmentTests
{
    private static Hashtable Environment(params (string Key, string Value)[] entries)
    {
        var table = new Hashtable();
        foreach (var (key, value) in entries)
        {
            table[key] = value;
        }

        return table;
    }

    [Fact]
    public void An_old_name_is_adopted_under_the_new_one()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("MCPMCP_DATA_DIR", "/data"),
            ("PATH", "/usr/bin")));

        plan.Should().ContainSingle()
            .Which.Should().Be(("MCPMCP_DATA_DIR", "BIFROST_DATA_DIR", "/data"));
    }

    /// <summary>
    /// Wer beide gesetzt hat, ist gerade beim Umstellen — dann ist der alte Wert der
    /// zurückgelassene. Andersherum würde eine vergessene Zeile in einer alten Compose-Datei die
    /// frisch gesetzte Konfiguration überschreiben, und zwar unsichtbar.
    /// </summary>
    [Fact]
    public void The_new_name_wins_when_both_are_set()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("MCPMCP_DB_PROVIDER", "sqlite"),
            ("BIFROST_DB_PROVIDER", "postgres")));

        plan.Should().BeEmpty();
    }

    /// <summary>
    /// Eine leer gesetzte Variable ist keine Festlegung — sonst bliebe eine Installation an einer
    /// leeren Zeile hängen, die jemand zum Abschalten hineingeschrieben hat.
    /// </summary>
    [Fact]
    public void An_empty_new_name_does_not_block_the_adoption()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("MCPMCP_AUDIT_MODE", "compliance"),
            ("BIFROST_AUDIT_MODE", "")));

        plan.Should().ContainSingle().Which.NewName.Should().Be("BIFROST_AUDIT_MODE");
    }

    [Fact]
    public void Variables_of_other_products_are_left_alone()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("ASPNETCORE_URLS", "http://+:8080"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")));

        plan.Should().BeEmpty();
    }

    /// <summary>
    /// <b>Der teuerste Fall.</b> Liegt im Datenverzeichnis nur die Datenbank unter dem alten Namen,
    /// muss sie gewinnen. Sonst legt der Gateway daneben eine leere neue an — ohne Server, ohne
    /// Rollen, ohne Schlüssel — und startet fehlerfrei in ein leeres System.
    /// </summary>
    [Fact]
    public void An_existing_database_under_the_old_name_wins()
    {
        var directory = Directory.CreateTempSubdirectory("bifrost-legacy-db");
        try
        {
            var legacy = Path.Combine(directory.FullName, "mcpmcp.db");
            File.WriteAllText(legacy, "alt");

            LegacyEnvironment.ResolveSqliteFile(directory.FullName).Should().Be(legacy);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_new_name_is_used_when_both_files_exist()
    {
        var directory = Directory.CreateTempSubdirectory("bifrost-both-db");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "mcpmcp.db"), "alt");
            var current = Path.Combine(directory.FullName, "bifrost.db");
            File.WriteAllText(current, "neu");

            LegacyEnvironment.ResolveSqliteFile(directory.FullName).Should().Be(current);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_fresh_installation_gets_the_new_name()
    {
        var directory = Directory.CreateTempSubdirectory("bifrost-fresh-db");
        try
        {
            LegacyEnvironment.ResolveSqliteFile(directory.FullName)
                .Should().Be(Path.Combine(directory.FullName, "bifrost.db"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
