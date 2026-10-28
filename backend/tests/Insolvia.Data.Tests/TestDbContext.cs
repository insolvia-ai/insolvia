using System.Text.Json;
using Insolvia.Data.Connections;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Tests;

/// <summary>
/// A row shaped like the entities the stores will add: tenant column, a
/// status a conditional write guards, money, an instant, a court date, and a
/// JSONB document.
/// </summary>
public sealed class Widget
{
    public Guid FirmId { get; set; }

    public Guid Id { get; set; }

    public string Status { get; set; } = "draft";

    public decimal AmountOwed { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateOnly FiledOn { get; set; }

    public JsonDocument Body { get; set; } = JsonDocument.Parse("{}");

    public int SortKey { get; set; }
}

/// <summary>A row that may be created once (DynamoDB's attribute_not_exists put).</summary>
public sealed class Approval
{
    public Guid Id { get; set; }

    public string Digest { get; set; } = "";
}

/// <summary>
/// <see cref="InsolviaDbContext"/> plus test-only entities: every convention
/// the real context has, applied to tables the real schema does not.
/// </summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : InsolviaDbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<Approval> Approvals => Set<Approval>();

    public static async Task<TestDbContext> CreateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>();
        options.UseInsolviaNpgsql(InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = connectionString }));
        var context = new TestDbContext(options.Options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Widget>(widget =>
        {
            widget.HasKey(w => new { w.FirmId, w.Id });
            widget.Property(w => w.Status).HasMaxLength(32);
        });
        modelBuilder.Entity<Approval>(approval =>
        {
            approval.HasKey(a => a.Id);
            approval.HasIndex(a => a.Digest).IsUnique();
        });
    }
}

/// <summary>The caller-supplied domain error a conditional write maps zero rows to.</summary>
public sealed class StatusMovedException : Exception
{
    public StatusMovedException()
        : base("This case's status changed since it was read — reload it and try again.")
    {
    }

    public StatusMovedException(string message)
        : base(message)
    {
    }

    public StatusMovedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
