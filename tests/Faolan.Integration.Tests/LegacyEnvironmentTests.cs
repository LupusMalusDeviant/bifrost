using System.Collections;
using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Server;
using Xunit;

namespace Faolan.Integration.Tests;

/// <summary>
/// Der Übergang über drei Generationen: <c>MCPMCP_*</c> → <c>BIFROST_*</c> → <c>FAOLAN_*</c>
/// (Vertrag <c>docs/contracts/umbenennung-faolan.md</c> §2.2, Nachweis aus §7).
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

    [Theory]
    [InlineData("MCPMCP_DATA_DIR")]
    [InlineData("BIFROST_DATA_DIR")]
    public void An_old_name_of_either_generation_is_adopted_under_the_current_one(string oldName)
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            (oldName, "/data"),
            ("PATH", "/usr/bin")));

        plan.Should().ContainSingle()
            .Which.Should().Be((oldName, "FAOLAN_DATA_DIR", "/data"));
    }

    /// <summary>
    /// Wer beide gesetzt hat, ist gerade beim Umstellen — dann ist der alte Wert der
    /// zurückgelassene. Andersherum würde eine vergessene Zeile in einer alten Compose-Datei die
    /// frisch gesetzte Konfiguration überschreiben, und zwar unsichtbar.
    /// </summary>
    [Fact]
    public void The_current_name_wins_over_every_older_generation()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("MCPMCP_DB_PROVIDER", "sqlite"),
            ("BIFROST_DB_PROVIDER", "sqlite"),
            ("FAOLAN_DB_PROVIDER", "postgres")));

        plan.Should().BeEmpty();
    }

    /// <summary>
    /// Zwischen den beiden abgelösten Generationen gilt dieselbe Regel: Die jüngere gewinnt. Eine
    /// Installation, die 2026-07 von MCPMCP_ auf BIFROST_ umgestellt und die alte Zeile stehen
    /// gelassen hat, darf jetzt nicht auf den Wert von vor dem Sommer zurückfallen.
    /// </summary>
    [Fact]
    public void Among_old_generations_the_younger_wins()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("MCPMCP_AUDIT_MODE", "standard"),
            ("BIFROST_AUDIT_MODE", "compliance")));

        plan.Should().ContainSingle()
            .Which.Should().Be(("BIFROST_AUDIT_MODE", "FAOLAN_AUDIT_MODE", "compliance"));
    }

    /// <summary>
    /// <c>_FILE</c>-Varianten sind gewöhnliche Namen der Kette — ein Geheimnis aus einer Datei
    /// darf beim Umbenennen nicht verloren gehen.
    /// </summary>
    [Fact]
    public void File_variants_go_through_the_same_chain()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("BIFROST_BACKUP_PASSPHRASE_FILE", "/run/secrets/passphrase"),
            ("MCPMCP_KEYRING_CERT_PASSWORD_FILE", "/run/secrets/cert")));

        plan.Should().BeEquivalentTo(
        [
            ("BIFROST_BACKUP_PASSPHRASE_FILE", "FAOLAN_BACKUP_PASSPHRASE_FILE", "/run/secrets/passphrase"),
            ("MCPMCP_KEYRING_CERT_PASSWORD_FILE", "FAOLAN_KEYRING_CERT_PASSWORD_FILE", "/run/secrets/cert"),
        ]);
    }

    /// <summary>
    /// Eine leer gesetzte Variable ist keine Festlegung — sonst bliebe eine Installation an einer
    /// leeren Zeile hängen, die jemand zum Abschalten hineingeschrieben hat.
    /// </summary>
    [Fact]
    public void An_empty_newer_name_does_not_block_the_adoption()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("MCPMCP_AUDIT_MODE", "compliance"),
            ("BIFROST_AUDIT_MODE", ""),
            ("FAOLAN_AUDIT_MODE", "")));

        plan.Should().ContainSingle()
            .Which.Should().Be(("MCPMCP_AUDIT_MODE", "FAOLAN_AUDIT_MODE", "compliance"));
    }

    [Fact]
    public void Variables_of_other_products_are_left_alone()
    {
        var plan = LegacyEnvironment.PlanAdoption(Environment(
            ("ASPNETCORE_URLS", "http://+:8080"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("BIFROST_", "nur-ein-praefix")));

        plan.Should().BeEmpty();
    }

    [Fact]
    public void The_chain_is_ordered_from_oldest_to_current()
    {
        EnvironmentNameChain.Generations.Should().Equal("MCPMCP_", "BIFROST_", "FAOLAN_");
        EnvironmentNameChain.CurrentPrefix.Should().Be("FAOLAN_");
    }

    /// <summary>
    /// <b>Der teuerste Fall.</b> Liegt im Datenverzeichnis nur die Datenbank unter einem alten
    /// Namen, muss sie gewinnen. Sonst legt der Gateway daneben eine leere neue an — ohne Server,
    /// ohne Rollen, ohne Schlüssel — und startet fehlerfrei in ein leeres System.
    /// </summary>
    [Theory]
    [InlineData("mcpmcp.db")]
    [InlineData("bifrost.db")]
    public void An_existing_database_under_an_old_name_wins(string oldName)
    {
        var directory = Directory.CreateTempSubdirectory("faolan-legacy-db");
        try
        {
            var legacy = Path.Combine(directory.FullName, oldName);
            File.WriteAllText(legacy, "alt");

            LegacyEnvironment.ResolveSqliteFile(directory.FullName).Should().Be(legacy);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(new[] { "bifrost.db", "mcpmcp.db" }, "bifrost.db")]
    [InlineData(new[] { "faolan.db", "mcpmcp.db" }, "faolan.db")]
    [InlineData(new[] { "faolan.db", "bifrost.db", "mcpmcp.db" }, "faolan.db")]
    public void Among_existing_files_the_youngest_generation_wins(string[] existing, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("faolan-several-db");
        try
        {
            foreach (var name in existing)
            {
                File.WriteAllText(Path.Combine(directory.FullName, name), name);
            }

            LegacyEnvironment.ResolveSqliteFile(directory.FullName)
                .Should().Be(Path.Combine(directory.FullName, expected));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private const string ImageDefaults = "FAOLAN_DATA_DIR=/data;FAOLAN_WASI_HOST=/usr/local/bin/faolan-wasi-host";

    /// <summary>
    /// Der Fall aus dem Review (H2): Das Image setzt <c>FAOLAN_DATA_DIR=/data</c>, der Betreiber hat
    /// seit 0.12.0 <c>BIFROST_DATA_DIR=/srv/gateway</c>. Nach „der neueste Name gewinnt" liefe der
    /// Gateway still auf <c>/data</c>. Das ist ein Konflikt, keine Übernahme.
    /// </summary>
    [Theory]
    [InlineData("BIFROST_DATA_DIR")]
    [InlineData("MCPMCP_DATA_DIR")]
    public void An_old_setting_hidden_by_an_image_default_is_a_conflict(string oldName)
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
            ("FAOLAN_DATA_DIR", "/data"),
            ("FAOLAN_WASI_HOST", "/usr/local/bin/faolan-wasi-host"),
            (oldName, "/srv/gateway")));

        conflicts.Should().ContainSingle()
            .Which.Should().Be((oldName, "/srv/gateway", "FAOLAN_DATA_DIR", "/data"));
    }

    [Fact]
    public void The_younger_old_generation_decides_the_conflict()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
            ("FAOLAN_DATA_DIR", "/data"),
            ("MCPMCP_DATA_DIR", "/ganz/alt"),
            ("BIFROST_DATA_DIR", "/data")));

        conflicts.Should().BeEmpty("der jüngere alte Wert gleicht der Vorgabe");
    }

    [Fact]
    public void An_old_setting_equal_to_the_image_default_is_no_conflict()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
            ("FAOLAN_DATA_DIR", "/data"),
            ("BIFROST_DATA_DIR", "/data"),
            ("BIFROST_WASI_HOST", "/usr/local/bin/faolan-wasi-host")));

        conflicts.Should().BeEmpty();
    }

    /// <summary>
    /// Trägt der neue Name einen anderen Wert als die Vorgabe, hat ihn der Betreiber gesetzt — dann
    /// gilt die gewöhnliche Regel, und der neue Name gewinnt.
    /// </summary>
    [Fact]
    public void A_current_name_set_by_the_operator_is_no_conflict()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
            ("FAOLAN_DATA_DIR", "/srv/neu"),
            ("BIFROST_DATA_DIR", "/srv/alt")));

        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Without_declared_image_defaults_there_is_no_conflict()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            ("FAOLAN_DATA_DIR", "/data"),
            ("BIFROST_DATA_DIR", "/srv/gateway")));

        conflicts.Should().BeEmpty("ausserhalb des Images setzt niemand den neuen Namen ungefragt");
    }

    /// <summary>
    /// Das Image und seine Deklaration dürfen nicht auseinanderlaufen: Eine Vorgabe ohne Eintrag in
    /// <c>FAOLAN_IMAGE_DEFAULTS</c> wäre wieder die stille Verdeckung.
    /// </summary>
    [Fact]
    public void The_dockerfile_declares_every_faolan_default_it_sets()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Dockerfile")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Dockerfile nicht gefunden.");
        }

        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var declared = System.Text.RegularExpressions.Regex
            .Match(dockerfile, "FAOLAN_IMAGE_DEFAULTS=\"([^\"]*)\"").Groups[1].Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var set = System.Text.RegularExpressions.Regex
            .Matches(dockerfile, @"^\s*(?:ENV\s+)?(FAOLAN_(?!IMAGE_DEFAULTS)[A-Z0-9_]+=[^\s\\]+)", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        set.Should().NotBeEmpty();
        declared.Should().BeEquivalentTo(set);
    }

    [Fact]
    public void A_fresh_installation_gets_the_current_name()
    {
        var directory = Directory.CreateTempSubdirectory("faolan-fresh-db");
        try
        {
            LegacyEnvironment.ResolveSqliteFile(directory.FullName)
                .Should().Be(Path.Combine(directory.FullName, "faolan.db"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
