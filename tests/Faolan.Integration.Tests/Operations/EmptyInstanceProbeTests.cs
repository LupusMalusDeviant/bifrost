using AwesomeAssertions;
using Faolan.Core.Diagnostics;
using Faolan.Core.Diagnostics.Checks;
using Faolan.Abstractions.Operations;
using Faolan.Persistence;
using Faolan.Persistence.Startup;
using Faolan.Server.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Integration.Tests.Operations;

/// <summary>
/// BFR-DB-0008 über eine echte, frisch migrierte Datenbank (drittes Review): Die Sonde zählt im
/// Schema, wie es ein Upgrade auf ein leeres Volume vorfindet, und der Befund warnt. Der Gegenfall —
/// eine Instanz mit Bestand — steht in <c>OperationsApiTests</c>.
/// </summary>
public sealed class EmptyInstanceProbeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("faolan-empty-instance").FullName;

    [Fact]
    public async Task A_freshly_migrated_database_is_reported_as_an_empty_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<FaolanDbContext>()
            .UseFaolanDatabase(FaolanDbOptions.Sqlite, $"Data Source={Path.Combine(_directory, "faolan.db")}")
            .Options;
        var factory = new Faolan.Integration.Tests.Persistence.TestDbContextFactory(options);
        await new DatabaseInitializer(factory).InitializeAsync(ct);

        var facts = await new EfDatabaseDiagnosticProbe(factory).DescribeAsync(ct);
        facts.Content.Should().Be(new InstanceContentFacts(0, 0, 0), "gezählt im echten Schema");

        var result = await new EmptyInstanceCheck().RunAsync(
            new DiagnosticContext { Environment = new Dictionary<string, string>(), Database = new FixedProbe(facts) }, ct);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("FAOLAN_DATA_VOLUME");
    }

    /// <summary>
    /// Viertes Review: Nach dem echten Start hat der Gateway seine eigene Rolle
    /// <c>ui-internal-admin</c> angelegt. Zählte sie mit, wäre keine gestartete Instanz je „leer" —
    /// auch die auf dem falschen Volume nicht. Geprüft wird deshalb nach dem Start der Hosted Services,
    /// ohne einen Schlüssel auszugeben, der selbst Bestand wäre.
    /// </summary>
    [Fact]
    public async Task A_started_gateway_without_operator_content_is_reported_as_an_empty_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new Faolan.Integration.Tests.Gateway.GatewayFixture();
        var probe = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IDatabaseDiagnosticProbe>(gateway.Services);

        var facts = await probe.DescribeAsync(ct);

        facts.Content.Should().Be(new InstanceContentFacts(0, 0, 0),
            "die Rolle, die der Gateway selbst anlegt, ist kein Bestand des Betreibers");
        var result = await new EmptyInstanceCheck().RunAsync(
            new DiagnosticContext { Environment = new Dictionary<string, string>(), Database = probe }, ct);
        result.Status.Should().Be(CheckStatus.Warning);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Ein Rest im Temp-Verzeichnis ist kein Testergebnis.
        }
    }

    private sealed class FixedProbe(DatabaseDiagnosticFacts facts) : IDatabaseDiagnosticProbe
    {
        public Task<DatabaseDiagnosticFacts> DescribeAsync(CancellationToken ct) => Task.FromResult(facts);
    }
}
