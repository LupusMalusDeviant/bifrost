using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

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
            ["BIFROST_DB_PROVIDER"] = "postgre",
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
            ["BIFROST_DB_PROVIDER"] = "postgres",
        });

        var result = await new DatabaseProviderCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
    }

    [Fact]
    public async Task The_connection_string_itself_never_reaches_the_output()
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["BIFROST_DB_PROVIDER"] = "postgres",
            ["BIFROST_DB_CONNECTION"] = "Host=db;Username=bifrost;Password=Tr0ub4dor3",
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
            ["BIFROST_DATA_DIR"] = "/data",
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
        files.Files.Add(Path.Combine("/data", "bifrost.db"));

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

    [Fact]
    public async Task The_legacy_name_alone_warns_but_does_not_demand_action()
    {
        var (context, files) = World();
        files.Files.Add(Path.Combine("/data", "mcpmcp.db"));

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("Kein Handlungsdruck");
    }

    [Fact]
    public async Task Both_files_side_by_side_warn()
    {
        var (context, files) = World();
        files.Files.Add(Path.Combine("/data", "bifrost.db"));
        files.Files.Add(Path.Combine("/data", "mcpmcp.db"));

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
    }

    [Fact]
    public async Task With_an_explicit_connection_string_the_check_is_skipped()
    {
        var (context, _) = World(new Dictionary<string, string>
        {
            ["BIFROST_DB_CONNECTION"] = "Data Source=/woanders/bifrost.db",
        });

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task With_postgres_the_check_is_skipped()
    {
        var (context, _) = World(new Dictionary<string, string>
        {
            ["BIFROST_DB_PROVIDER"] = "postgres",
        });

        var result = await new SqliteDatabaseFileCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }
}
