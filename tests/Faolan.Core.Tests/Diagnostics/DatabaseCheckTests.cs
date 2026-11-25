using AwesomeAssertions;

using Faolan.Abstractions.Operations;
using Faolan.Core.Diagnostics;
using Faolan.Core.Diagnostics.Checks;

using Xunit;

namespace Faolan.Core.Tests.Diagnostics;

public class DatabaseProviderCheckTests
{
    [Fact]
    public async Task Sqlite_is_the_default_and_passes()
    {
        var result = await new DatabaseProviderCheck()
            .RunAsync(DiagnosticWorld.Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["provider"].Should().Be("sqlite");
    }

    [Fact]
    public async Task An_unknown_provider_fails_instead_of_falling_back()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["FAOLAN_DB_PROVIDER"] = "postgre",
        });

        var result = await new DatabaseProviderCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        result.Remediation.Should().Contain("nicht still");
    }

    [Fact]
    public async Task Postgres_without_a_connection_string_fails()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["FAOLAN_DB_PROVIDER"] = "postgres",
        });

        var result = await new DatabaseProviderCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
    }

    [Fact]
    public async Task The_connection_string_itself_never_reaches_the_output()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["FAOLAN_DB_PROVIDER"] = "postgres",
            ["FAOLAN_DB_CONNECTION"] = "Host=db;Username=faolan;Password=Tr0ub4dor3",
        });

        var result = await new DatabaseProviderCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        LegacyEnvironmentVariablesCheckTests.Flatten(result).Should().NotContain("Tr0ub4dor3");
        result.SafeDetails!["verbindung_gesetzt"].Should().Be("ja");
    }
}

public class DatabaseProbeCheckTests
{
    private static DiagnosticContext With(DatabaseDiagnosticFacts facts)
        => DiagnosticWorld.Context() with { Database = new FakeDatabaseProbe(facts) };

    [Fact]
    public async Task Without_a_probe_the_database_checks_are_skipped_with_a_reason()
    {
        var context = DiagnosticWorld.Context();

        foreach (var check in new IDiagnosticCheck[]
        {
            new DatabaseReachabilityCheck(), new AppliedMigrationsCheck(), new PendingMigrationsCheck(),
        })
        {
            var result = await check.RunAsync(context, TestContext.Current.CancellationToken);
            result.Status.Should().Be(CheckStatus.Skipped);
            result.Summary.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task A_reachable_database_passes()
    {
        var result = await new DatabaseReachabilityCheck()
            .RunAsync(With(new DatabaseDiagnosticFacts(true)), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task An_unreachable_database_fails()
    {
        var facts = new DatabaseDiagnosticFacts(false, "Verbindung abgelehnt");

        var result = await new DatabaseReachabilityCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        result.Summary.Should().Contain("Verbindung abgelehnt");
    }

    [Fact]
    public async Task Migration_checks_are_skipped_while_the_database_is_unreachable()
    {
        var context = With(new DatabaseDiagnosticFacts(false, "keine Verbindung"));

        var applied = await new AppliedMigrationsCheck().RunAsync(context, TestContext.Current.CancellationToken);
        var pending = await new PendingMigrationsCheck().RunAsync(context, TestContext.Current.CancellationToken);

        applied.Status.Should().Be(CheckStatus.Skipped);
        pending.Status.Should().Be(CheckStatus.Skipped);
        // Querbezug über den Code, nicht über eine Aufrufreihenfolge.
        applied.Summary.Should().Contain(DiagnosticCodes.DatabaseReachable);
    }

    [Fact]
    public async Task Applied_migrations_pass_and_name_the_last_one()
    {
        var facts = new DatabaseDiagnosticFacts(true, null, ["20260101_Initial", "20260731_Tasks"], []);

        var result = await new AppliedMigrationsCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["letzte"].Should().Be("20260731_Tasks");
    }

    [Fact]
    public async Task An_empty_migration_history_warns_about_the_baseline_case()
    {
        var facts = new DatabaseDiagnosticFacts(true, null, [], []);

        var result = await new AppliedMigrationsCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("BaselinedLegacySchema");
    }

    [Fact]
    public async Task No_pending_migrations_passes()
    {
        var facts = new DatabaseDiagnosticFacts(true, null, ["20260101_Initial"], []);

        var result = await new PendingMigrationsCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task Pending_migrations_warn_and_point_at_the_backup()
    {
        var facts = new DatabaseDiagnosticFacts(true, null, ["20260101_Initial"], ["20260801_Neu"]);

        var result = await new PendingMigrationsCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.SafeDetails!["namen"].Should().Be("20260801_Neu");
        result.Remediation.Should().Contain("sichern");
    }
}

public class SqliteDatabaseFileCheckTests
{
    private static (DiagnosticContext Context, FakeFileProbe Files) World(
        IDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FAOLAN_DATA_DIR"] = "/data",
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        var files = new FakeFileProbe();
        files.Directories.Add("/data");
        return (DiagnosticWorld.Context(environment, files), files);
    }

    [Fact]
    public async Task The_current_file_passes()
    {
        var (context, files) = World();
        files.Files.Add(Path.Combine("/data", "faolan.db"));

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task An_empty_data_directory_warns_about_the_wrong_volume()
    {
        // Genau der Ausfall, der wie ein gelungener Start aussieht.
        var (context, _) = World();

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("Volume");
    }

    [Theory]
    [InlineData("mcpmcp.db")]
    [InlineData("bifrost.db")]
    public async Task An_old_name_alone_warns_but_does_not_demand_action(string oldName)
    {
        var (context, files) = World();
        files.Files.Add(Path.Combine("/data", oldName));

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("Kein Handlungsdruck");
    }

    [Theory]
    [InlineData("faolan.db", "bifrost.db")]
    [InlineData("faolan.db", "mcpmcp.db")]
    [InlineData("bifrost.db", "mcpmcp.db")]
    public async Task Several_files_side_by_side_warn_and_name_the_one_in_use(string used, string leftover)
    {
        var (context, files) = World();
        files.Files.Add(Path.Combine("/data", used));
        files.Files.Add(Path.Combine("/data", leftover));

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain($"Benutzt wird '{used}'");
    }

    [Fact]
    public async Task With_an_explicit_connection_string_the_check_is_skipped()
    {
        var (context, _) = World(new Dictionary<string, string>
        {
            ["FAOLAN_DB_CONNECTION"] = "Data Source=/woanders/bifrost.db",
        });

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task With_postgres_the_check_is_skipped()
    {
        var (context, _) = World(new Dictionary<string, string>
        {
            ["FAOLAN_DB_PROVIDER"] = "postgres",
        });

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }
}

/// <summary>
/// BFR-DB-0008 — die leere Instanz, das Bild des falschen Volumes nach dem Upgrade (Vertrag
/// umbenennung-faolan §3).
/// </summary>
public class EmptyInstanceCheckTests
{
    private static DiagnosticContext With(DatabaseDiagnosticFacts facts)
        => DiagnosticWorld.Context() with { Database = new FakeDatabaseProbe(facts) };

    [Fact]
    public async Task An_instance_without_anything_warns_and_points_at_the_volume()
    {
        var facts = new DatabaseDiagnosticFacts(true, null, ["m"], [], Content: new InstanceContentFacts(0, 0, 0));

        var result = await new EmptyInstanceCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("FAOLAN_DATA_VOLUME").And.Contain("FAOLAN_PGDATA_VOLUME")
            .And.Contain("docs/upgrade-faolan.md");
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public async Task Any_content_passes(int upstreams, int roles, int keys)
    {
        var facts = new DatabaseDiagnosticFacts(true, null, ["m"], [], Content: new InstanceContentFacts(upstreams, roles, keys));

        var result = await new EmptyInstanceCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["upstreams"].Should().Be(upstreams.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Uncounted_content_is_skipped_not_reported_as_empty()
    {
        var facts = new DatabaseDiagnosticFacts(true, null, ["m"], ["offen"]);

        var result = await new EmptyInstanceCheck().RunAsync(With(facts), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task An_unreachable_database_skips_the_check()
    {
        var result = await new EmptyInstanceCheck()
            .RunAsync(With(new DatabaseDiagnosticFacts(false, "weg")), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }
}

/// <summary>
/// BFR-DB-0007 — der Befund, der die Zusage „lückenloses Audit-Log" überhaupt erst überprüfbar
/// macht. Ohne ihn stand ein Verlust in einer Logzeile und in keinem Bericht.
/// </summary>
public class AuditCompletenessCheckTests
{
    [Fact]
    public async Task Without_a_probe_the_check_is_skipped_and_not_silently_green()
    {
        var result = await new AuditCompletenessCheck()
            .RunAsync(DiagnosticWorld.Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped,
            "keine Auskunft ist ausdrücklich nicht 'nichts verloren'");
        result.Summary.Should().Contain("unbeantwortet");
    }

    [Fact]
    public async Task No_loss_passes()
    {
        var context = DiagnosticWorld.Context() with
        {
            Audit = new FakeAuditProbe(new AuditDiagnosticFacts(0, 0, null, "best-effort")),
        };

        var result = await new AuditCompletenessCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["verloren_gesamt"].Should().Be("0");
    }

    [Fact]
    public async Task A_discarded_batch_appears_with_its_number_and_the_time_it_started()
    {
        var firstLoss = new DateTimeOffset(2026, 8, 1, 10, 30, 0, TimeSpan.Zero);
        var context = DiagnosticWorld.Context() with
        {
            Audit = new FakeAuditProbe(new AuditDiagnosticFacts(12, 500, firstLoss, "best-effort")),
        };

        var result = await new AuditCompletenessCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail,
            "eine gebrochene Zusage ist keine Randnotiz");
        result.Summary.Should().Contain("512", "der Bericht nennt die Zahl, nicht nur das Ereignis");
        result.SafeDetails!["verworfen_bei_annahme"].Should().Be("12");
        result.SafeDetails["aufgegeben_beim_schreiben"].Should().Be("500");
        result.SafeDetails["verloren_gesamt"].Should().Be("512");
        result.SafeDetails["erster_verlust"].Should().Contain("2026-08-01");
        result.Summary.Should().Contain("2026-08-01", "und seit wann");
        result.Remediation.Should().Contain("compliance");
    }
}
