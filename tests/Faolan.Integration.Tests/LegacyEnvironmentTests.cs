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
            .Which.Should().Contain(oldName).And.Contain("FAOLAN_DATA_DIR")
            .And.NotContain("/srv/gateway", "Werte stehen nie in der Meldung");
    }

    /// <summary>
    /// Review-Befund: Die alte Einstellung als Datei (<c>BIFROST_DATA_DIR_FILE</c>) ist dieselbe
    /// Einstellung. Ihr Inhalt wird verglichen wie ein direkter Wert.
    /// </summary>
    [Theory]
    [InlineData("BIFROST_DATA_DIR_FILE")]
    [InlineData("MCPMCP_DATA_DIR_FILE")]
    public void An_old_setting_given_as_a_file_is_compared_by_its_content(string oldName)
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(
            Environment(
                (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
                ("FAOLAN_DATA_DIR", "/data"),
                (oldName, "/run/secrets/datadir")),
            path => path == "/run/secrets/datadir" ? "/srv/gateway\n" : throw new FileNotFoundException(path));

        conflicts.Should().ContainSingle().Which.Should().Contain(oldName).And.NotContain("/srv/gateway");
    }

    [Fact]
    public void An_old_file_with_the_image_value_is_no_conflict()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(
            Environment(
                (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
                ("FAOLAN_DATA_DIR", "/data"),
                ("BIFROST_DATA_DIR_FILE", "/run/secrets/datadir")),
            _ => "/data\n");

        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void An_unreadable_old_file_stops_the_start()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(
            Environment(
                (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
                ("FAOLAN_DATA_DIR", "/data"),
                ("BIFROST_DATA_DIR_FILE", "/fehlt")),
            path => throw new FileNotFoundException(path));

        conflicts.Should().ContainSingle().Which.Should().Contain("nicht lesen");
    }

    /// <summary>
    /// Drittes Review: Der Rat bei unlesbarer Datei darf nicht auf <c>FAOLAN_DATA_DIR_FILE</c>
    /// zeigen — das Datenverzeichnis wird nur direkt gelesen.
    /// </summary>
    [Fact]
    public void The_advice_for_an_unreadable_file_names_the_direct_variable()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(
            Environment(
                (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
                ("FAOLAN_DATA_DIR", "/data"),
                ("BIFROST_DATA_DIR_FILE", "/fehlt")),
            path => throw new FileNotFoundException(path));

        conflicts.Should().ContainSingle().Which.Should().Contain("direkt unter FAOLAN_DATA_DIR setzen");
    }

    /// <summary>Drittes Review: Direkter Wert und Datei derselben alten Generation — nicht raten.</summary>
    [Fact]
    public void A_direct_value_and_a_file_of_the_same_old_name_stop_the_start()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(
            Environment(
                (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
                ("FAOLAN_DATA_DIR", "/data"),
                ("BIFROST_DATA_DIR", "/data"),
                ("BIFROST_DATA_DIR_FILE", "/run/secrets/datadir")),
            _ => "/srv/gateway");

        conflicts.Should().ContainSingle().Which.Should()
            .Contain("BIFROST_DATA_DIR und BIFROST_DATA_DIR_FILE").And.NotContain("/srv/gateway");
    }

    /// <summary>
    /// Viertes Review: Für ein Geheimnis, das ein abgeleitetes Image vorgibt, ist die Datei der
    /// richtige Weg — „nur direkt" gilt allein für Datenverzeichnis und WASI-Host.
    /// </summary>
    [Fact]
    public void The_advice_for_an_unreadable_secret_file_allows_the_new_file_variable()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(
            Environment(
                (EnvironmentNameChain.ImageDefaultsVariable, "FAOLAN_DB_CONNECTION=Host=db"),
                ("FAOLAN_DB_CONNECTION", "Host=db"),
                ("BIFROST_DB_CONNECTION_FILE", "/fehlt")),
            path => throw new FileNotFoundException(path));

        conflicts.Should().ContainSingle().Which.Should().Contain("als FAOLAN_DB_CONNECTION_FILE")
            .And.NotContain("nur direkt gelesen");
    }

    /// <summary>
    /// Viertes Review: Direkter Wert und Datei beim alten mitgelieferten WASI-Host — der Rat nennt
    /// das Entfernen, nicht nur das Umbenennen auf einen Pfad, den es im Image nicht mehr gibt.
    /// </summary>
    [Fact]
    public void Two_old_sources_for_the_wasi_host_get_the_advice_to_drop_them()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(
            Environment(
                (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
                ("FAOLAN_WASI_HOST", "/usr/local/bin/faolan-wasi-host"),
                ("BIFROST_WASI_HOST", "/usr/local/bin/bifrost-wasi-host"),
                ("BIFROST_WASI_HOST_FILE", "/run/secrets/host")),
            _ => "/usr/local/bin/bifrost-wasi-host");

        conflicts.Should().ContainSingle().Which.Should()
            .Contain("BIFROST_WASI_HOST, BIFROST_WASI_HOST_FILE) entfernen, das Image bringt den umbenannten Host")
            .And.Contain(WasiHostNames.LegacyBundledPath);
    }

    /// <summary>
    /// Drittes Review: Ein eigenes Programm, das zufällig <c>bifrost-wasi-host</c> heißt, ist nicht
    /// der mitgelieferte Host — der Rat zum Entfernen gälte dort nicht.
    /// </summary>
    [Fact]
    public void A_custom_host_with_the_old_name_gets_the_advice_to_set_the_new_variable()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
            ("FAOLAN_WASI_HOST", "/usr/local/bin/faolan-wasi-host"),
            ("BIFROST_WASI_HOST", "/opt/vendor/bifrost-wasi-host")));

        conflicts.Should().ContainSingle().Which.Should().Contain("unter FAOLAN_WASI_HOST setzen")
            .And.NotContain("BIFROST_WASI_HOST entfernen, das Image");
    }

    /// <summary>Review-Befund: Eine kaputte Deklaration darf die Prüfung nicht still abschalten.</summary>
    [Theory]
    [InlineData("FAOLAN_DATA_DIR:/data")]
    [InlineData("DATA_DIR=/data")]
    [InlineData("FAOLAN_=/data")]
    [InlineData("FAOLAN_DATA_DIR =/data")]
    [InlineData(";;")]
    [InlineData("FAOLAN_data_dir=/data")]
    [InlineData("")] // Viertes Review: ausdrücklich leer gesetzt schaltet die Prüfung nicht ab.
    [InlineData("   ")]
    public void A_malformed_declaration_stops_the_start(string declaration)
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, declaration),
            ("FAOLAN_DATA_DIR", "/data")));

        conflicts.Should().ContainSingle().Which.Should().Contain(EnvironmentNameChain.ImageDefaultsVariable);
    }

    /// <summary>
    /// Review-Befund: Wer den bis 0.12.0 mitgelieferten Host ausdrücklich gesetzt hatte, darf nicht
    /// den Rat bekommen, diesen Pfad unter dem neuen Namen zu setzen — dort liegt kein Programm mehr.
    /// </summary>
    [Fact]
    public void The_old_bundled_host_gets_the_advice_to_drop_the_variable()
    {
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, ImageDefaults),
            ("FAOLAN_WASI_HOST", "/usr/local/bin/faolan-wasi-host"),
            ("BIFROST_WASI_HOST", "/usr/local/bin/bifrost-wasi-host")));

        conflicts.Should().ContainSingle().Which.Should().Contain("BIFROST_WASI_HOST entfernen")
            .And.NotContain("unter FAOLAN_WASI_HOST setzen");
    }

    /// <summary>Review-Befund: Ein Geheimnis aus einer Vorgabe-Kollision erscheint nicht in der Meldung.</summary>
    [Fact]
    public void A_conflict_never_shows_the_values()
    {
        var secret = "Password=" + Guid.NewGuid().ToString("N");
        var conflicts = EnvironmentNameChain.FindImageDefaultConflicts(Environment(
            (EnvironmentNameChain.ImageDefaultsVariable, "FAOLAN_DB_CONNECTION=Host=db"),
            ("FAOLAN_DB_CONNECTION", "Host=db"),
            ("BIFROST_DB_CONNECTION", "Host=alt;" + secret)));

        conflicts.Should().ContainSingle().Which.Should().NotContain(secret).And.NotContain("Host=alt");
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
