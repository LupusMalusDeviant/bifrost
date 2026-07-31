using System.Text;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Bifrost.Integration.Tests.Persistence;

public sealed class SqlitePersistenceTests : PersistenceTestsBase
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"bifrost-test-{Guid.NewGuid():N}.db");

    private readonly List<string> _extraDbPaths = [];

    protected override Task<DbContextOptions<BifrostDbContext>?> CreateOptionsAsync()
        => Task.FromResult<DbContextOptions<BifrostDbContext>?>(
            new DbContextOptionsBuilder<BifrostDbContext>()
                .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={_dbPath}")
                .Options);

    protected override Task<DbContextOptions<BifrostDbContext>> CreateFreshOptionsAsync(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bifrost-{name}-{Guid.NewGuid():N}.db");
        _extraDbPaths.Add(path);
        return Task.FromResult(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}")
            .Options);
    }

    protected override void MarkSkippedIfUnavailable()
    {
        // SQLite ist immer verfügbar.
    }

    protected override string InitialCreateMigration => "20260718091957_InitialCreate";

    public override async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _extraDbPaths.Append(_dbPath).Where(File.Exists))
        {
            File.Delete(path);
        }

        await base.DisposeAsync();
    }

    /// <summary>WP3-DoD: die SQLite-Datei selbst enthält keine Klartext-Secrets (Roh-Byte-Scan).</summary>
    [Fact]
    public async Task Database_file_contains_no_plaintext_secret()
    {
        var store = new EfUpstreamConfigStore(Factory, DataProtection);
        await store.AppendVersionAsync(ServerId.New(), ConfigWithSecret(), TestContext.Current.CancellationToken);

        SqliteConnection.ClearAllPools();
        var fileBytes = await File.ReadAllBytesAsync(_dbPath);

        fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("SUPERSECRET_VALUE_12345"))
            .Should().Be(-1, "die DB-Datei darf das Secret nie im Klartext enthalten (NFR-04, WP3-DoD)");
        fileBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("ConfigVersions"))
            .Should().BeGreaterThan(0, "Gegenprobe: die Datei ist lesbar und enthält das Schema");
    }
}
