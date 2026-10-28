using System.Text.Json;
using Insolvia.Data.Conventions;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data;

/// <summary>
/// The one EF Core context for Insolvia's database. Every table lives in the
/// <see cref="Schema"/> schema, and an entity joins the model by adding an
/// <see cref="IEntityTypeConfiguration{TEntity}"/> to this assembly: the
/// context picks configurations up itself, so a store never edits this file.
/// </summary>
/// <remarks>
/// The conventions here are the data-tier rules in <c>backend/CLAUDE.md</c>,
/// made mechanical: snake_case names (<see cref="InsolviaNpgsql"/>),
/// <c>timestamptz</c> for <see cref="DateTimeOffset"/>, <c>numeric</c> for
/// <see cref="decimal"/>, <c>date</c> for <see cref="DateOnly"/>, and
/// <c>jsonb</c> for a document column (<see cref="JsonDocument"/>: an entity's
/// <c>body</c> or <c>provenance</c>). A <see cref="DateTime"/> property is
/// refused when the model is built — it cannot say which clock it is on.
/// </remarks>
public class InsolviaDbContext : DbContext
{
    /// <summary>The Postgres schema every Insolvia table is created in.</summary>
    public const string Schema = "insolvia";

    /// <summary>The EF migrations history table, in <see cref="Schema"/>.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>The context the hosts and the migrator resolve.</summary>
    public InsolviaDbContext(DbContextOptions<InsolviaDbContext> options)
        : base(options)
    {
    }

    /// <summary>For a derived context (tests add their own entities this way).</summary>
    protected InsolviaDbContext(DbContextOptions options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Npgsql's defaults already map these; they are written here so the
        // rule is visible where the model is built, and so a provider change
        // could not move them silently.
        // timestamptz stores an instant, not an offset, and Npgsql refuses to
        // write a non-UTC offset rather than guess. Normalising on the way in
        // lets the domain hand over any DateTimeOffset; reads come back +00:00.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz").HaveConversion<UtcDateTimeOffsetConverter>();
        configurationBuilder.Properties<decimal>().HaveColumnType("numeric");
        configurationBuilder.Properties<DateOnly>().HaveColumnType("date");
        configurationBuilder.Properties<JsonDocument>().HaveColumnType("jsonb");

        configurationBuilder.Conventions.Add(_ => new NoDateTimeConvention());
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InsolviaDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
