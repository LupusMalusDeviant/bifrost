using Faolan.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Faolan.Persistence.Migrations.Sqlite;

/// <summary>Ermöglicht <c>dotnet ef migrations add</c> ohne laufenden Host. Der Connection-String ist nur ein Platzhalter fürs Scaffolding.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<FaolanDbContext>
{
    public FaolanDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<FaolanDbContext>()
            .UseFaolanDatabase(FaolanDbOptions.Sqlite, "Data Source=designtime.db")
            .Options);
}
