using Bifrost.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bifrost.Persistence.Migrations.Sqlite;

/// <summary>Ermöglicht <c>dotnet ef migrations add</c> ohne laufenden Host. Der Connection-String ist nur ein Platzhalter fürs Scaffolding.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<BifrostDbContext>
{
    public BifrostDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, "Data Source=designtime.db")
            .Options);
}
