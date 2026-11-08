using Insolvia.Data.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Insolvia.Data;

/// <summary>
/// What <c>dotnet ef migrations add</c> builds the model with. Adding a
/// migration does not connect; <c>dotnet ef database update</c> would, to
/// <c>INSOLVIA_DB_CONNECTION_STRING</c> or the compose database — but the
/// migrator, not that command, is how a database is migrated.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<InsolviaDbContext>
{
    private const string ComposeDatabase =
        "Host=127.0.0.1;Port=55432;Database=insolvia;Username=insolvia;Password=insolvia-local-only";

    public InsolviaDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(DatabaseSettings.ConnectionStringVariable) ?? ComposeDatabase;
        var dataSource = InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = connectionString });
        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(dataSource);
        return new InsolviaDbContext(options.Options);
    }
}
