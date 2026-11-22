using Faolan.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Faolan.Persistence.Migrations.Postgres;

/// <summary>Ermöglicht <c>dotnet ef migrations add</c> ohne laufenden Host. Der Connection-String ist nur ein Platzhalter fürs Scaffolding.</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<FaolanDbContext>
{
    public FaolanDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<FaolanDbContext>()
            .UseFaolanDatabase(FaolanDbOptions.Postgres, "Host=localhost;Database=bifrost;Username=designtime;Password=designtime")
            .Options);
}
