using Bifrost.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bifrost.Persistence.Migrations.Postgres;

/// <summary>Ermöglicht <c>dotnet ef migrations add</c> ohne laufenden Host. Der Connection-String ist nur ein Platzhalter fürs Scaffolding.</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<BifrostDbContext>
{
    public BifrostDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Postgres, "Host=localhost;Database=bifrost;Username=designtime;Password=designtime")
            .Options);
}
