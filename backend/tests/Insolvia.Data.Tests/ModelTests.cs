using System.Text.Json;
using Insolvia.Data.Connections;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Data.Tests;

[Collection(PostgresTests.Name)]
public sealed class ModelTests(PostgresFixture postgres)
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Tables_and_columns_are_snake_case_in_the_insolvia_schema_with_the_mandated_types()
    {
        await using var context = await TestDbContext.CreateAsync(await postgres.CreateDatabaseAsync());

        var columns = await ColumnsAsync(context, "widgets");

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["firm_id"] = "uuid",
                ["id"] = "uuid",
                ["status"] = "character varying",
                ["amount_owed"] = "numeric",
                ["updated_at"] = "timestamp with time zone",
                ["filed_on"] = "date",
                ["body"] = "jsonb",
                ["sort_key"] = "integer",
            },
            columns);
    }

    [Fact]
    public async Task A_jsonb_body_round_trips_and_can_be_queried_in_sql()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var firm = Guid.NewGuid();
        await using (var write = await TestDbContext.CreateAsync(connectionString))
        {
            write.Widgets.Add(new Widget
            {
                FirmId = firm,
                Id = Guid.NewGuid(),
                AmountOwed = 1234.56m,
                UpdatedAt = DateTimeOffset.Parse("2026-10-27T12:00:00-04:00", System.Globalization.CultureInfo.InvariantCulture),
                FiledOn = new DateOnly(2026, 10, 27),
                Body = JsonDocument.Parse("""{"debtor":{"name":"Ada"},"provenance":{"name":"intake"}}"""),
            });
            write.Widgets.Add(new Widget { FirmId = firm, Id = Guid.NewGuid(), Body = JsonDocument.Parse("""{"debtor":{"name":"Grace"}}""") });
            await write.SaveChangesAsync(None);
        }

        await using var read = await TestDbContext.CreateAsync(connectionString);
        var ada = await read.Widgets
            .Where(w => EF.Functions.JsonContains(w.Body, """{"debtor":{"name":"Ada"}}"""))
            .SingleAsync(None);

        Assert.Equal("intake", ada.Body.RootElement.GetProperty("provenance").GetProperty("name").GetString());
        Assert.Equal(1234.56m, ada.AmountOwed);
        Assert.Equal(new DateOnly(2026, 10, 27), ada.FiledOn);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture), ada.UpdatedAt);
    }

    [Fact]
    public void A_DateTime_property_is_refused_when_the_model_is_built()
    {
        var options = new DbContextOptionsBuilder<DateTimeContext>();
        options.UseInsolviaNpgsql(InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = "Host=unused" }));
        using var context = new DateTimeContext(options.Options);

        var error = Assert.Throws<InvalidOperationException>(() => context.Model);

        Assert.Contains("Stamped.At is a DateTime", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_model_has_no_change_that_a_migration_does_not_capture()
    {
        // A store that adds an entity and forgets `dotnet ef migrations add`
        // fails here, in CI, rather than in the migrator on deploy.
        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = "Host=unused" }));
        using var context = new InsolviaDbContext(options.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Migrating_twice_applies_each_migration_once_and_records_it_in_the_insolvia_schema()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = connectionString }));

        await using (var first = new InsolviaDbContext(options.Options))
        {
            await first.Database.MigrateAsync(None);
        }

        await using var second = new InsolviaDbContext(options.Options);
        await second.Database.MigrateAsync(None);

        Assert.Empty(await second.Database.GetPendingMigrationsAsync(None));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(None);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM insolvia.__ef_migrations_history", connection);
        Assert.Equal(second.Database.GetMigrations().LongCount(), (long)(await count.ExecuteScalarAsync(None))!);
    }

    private static async Task<Dictionary<string, string>> ColumnsAsync(DbContext context, string table)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync(None);
        await using var command = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'insolvia' AND table_name = @table",
            connection);
        command.Parameters.AddWithValue("table", table);
        var columns = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync(None);
        while (await reader.ReadAsync(None))
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        await connection.CloseAsync();
        return columns;
    }

    public sealed class Stamped
    {
        public Guid Id { get; set; }

        public DateTime At { get; set; }
    }

    private sealed class DateTimeContext(DbContextOptions<DateTimeContext> options) : InsolviaDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Stamped>();
        }
    }
}
