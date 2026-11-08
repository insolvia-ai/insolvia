using Insolvia.Data.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Tests;

[Collection(PostgresTests.Name)]
public sealed class ConcurrencyTests(PostgresFixture postgres)
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_conditional_update_applies_when_its_precondition_holds()
    {
        var (connectionString, widget) = await SeedAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);

        var rows = await context.Widgets
            .Where(w => w.FirmId == widget.FirmId && w.Id == widget.Id && w.Status == "draft")
            .ExecuteConditionalUpdateAsync(set => set.SetProperty(w => w.Status, "filed"), () => new StatusMovedException(), None);

        Assert.Equal(1, rows);
        Assert.Equal("filed", (await context.Widgets.AsNoTracking().SingleAsync(None)).Status);
    }

    [Fact]
    public async Task A_conditional_update_whose_precondition_fails_throws_the_callers_domain_error_and_changes_nothing()
    {
        var (connectionString, widget) = await SeedAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);

        await Assert.ThrowsAsync<StatusMovedException>(() => context.Widgets
            .Where(w => w.FirmId == widget.FirmId && w.Id == widget.Id && w.Status == "submitted")
            .ExecuteConditionalUpdateAsync(set => set.SetProperty(w => w.Status, "filed"), () => new StatusMovedException(), None));

        Assert.Equal("draft", (await context.Widgets.AsNoTracking().SingleAsync(None)).Status);
    }

    [Fact]
    public async Task Of_two_racing_writers_with_the_same_precondition_exactly_one_wins()
    {
        var (connectionString, widget) = await SeedAsync();
        await using var first = await TestDbContext.CreateAsync(connectionString);
        await using var second = await TestDbContext.CreateAsync(connectionString);

        Task<int> Move(TestDbContext context, string to) => context.Widgets
            .Where(w => w.FirmId == widget.FirmId && w.Id == widget.Id && w.Status == "draft")
            .ExecuteConditionalUpdateAsync(set => set.SetProperty(w => w.Status, to), () => new StatusMovedException(), None);

        var outcomes = await Task.WhenAll(Capture(Move(first, "filed")), Capture(Move(second, "dismissed")));

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is StatusMovedException);
    }

    [Fact]
    public async Task A_conditional_delete_and_a_conditional_sql_write_map_zero_rows_the_same_way()
    {
        var (connectionString, widget) = await SeedAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);

        await Assert.ThrowsAsync<StatusMovedException>(() => context.Widgets
            .Where(w => w.Id == widget.Id && w.Status == "filed")
            .ExecuteConditionalDeleteAsync(() => new StatusMovedException(), None));
        await Assert.ThrowsAsync<StatusMovedException>(() => context.Database.ExecuteConditionalSqlAsync(
            $"UPDATE insolvia.widgets SET status = 'filed' WHERE id = {widget.Id} AND status = {"submitted"}",
            () => new StatusMovedException(),
            None));

        Assert.Equal(1, await context.Database.ExecuteConditionalSqlAsync(
            $"UPDATE insolvia.widgets SET status = 'filed' WHERE id = {widget.Id} AND status = {"draft"}",
            () => new StatusMovedException(),
            None));
        Assert.Equal(1, await context.Widgets
            .Where(w => w.Id == widget.Id && w.Status == "filed")
            .ExecuteConditionalDeleteAsync(() => new StatusMovedException(), None));
    }

    [Fact]
    public async Task Creating_a_row_twice_maps_the_unique_violation_to_the_callers_domain_error()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var first = await TestDbContext.CreateAsync(connectionString))
        {
            first.Approvals.Add(new Approval { Id = Guid.NewGuid(), Digest = "sha256:abc" });
            await first.SaveChangesOrConflictAsync(() => new StatusMovedException("already approved"), null, None);
        }

        await using var second = await TestDbContext.CreateAsync(connectionString);
        second.Approvals.Add(new Approval { Id = Guid.NewGuid(), Digest = "sha256:abc" });

        var error = await Assert.ThrowsAsync<StatusMovedException>(() =>
            second.SaveChangesOrConflictAsync(() => new StatusMovedException("already approved"), "ix_approvals_digest", None));
        Assert.Equal("already approved", error.Message);
    }

    [Fact]
    public async Task A_transaction_commits_all_of_its_writes_together()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);
        var firm = Guid.NewGuid();

        await context.InTransactionAsync(
            async token =>
            {
                context.Widgets.Add(new Widget { FirmId = firm, Id = Guid.NewGuid() });
                await context.SaveChangesAsync(token);
                context.Approvals.Add(new Approval { Id = Guid.NewGuid(), Digest = "d1" });
                await context.SaveChangesAsync(token);
            },
            None);

        await using var check = await TestDbContext.CreateAsync(connectionString);
        Assert.Equal(1, await check.Widgets.CountAsync(None));
        Assert.Equal(1, await check.Approvals.CountAsync(None));
    }

    [Fact]
    public async Task A_transaction_that_throws_rolls_back_every_write_and_rethrows()
    {
        var (connectionString, widget) = await SeedAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);

        await Assert.ThrowsAsync<StatusMovedException>(() => context.InTransactionAsync(
            async token =>
            {
                context.Approvals.Add(new Approval { Id = Guid.NewGuid(), Digest = "d1" });
                await context.SaveChangesAsync(token);
                await context.Widgets
                    .Where(w => w.Id == widget.Id && w.Status == "submitted")
                    .ExecuteConditionalUpdateAsync(set => set.SetProperty(w => w.Status, "filed"), () => new StatusMovedException(), token);
            },
            None));

        await using var check = await TestDbContext.CreateAsync(connectionString);
        Assert.Equal(0, await check.Approvals.CountAsync(None));
    }

    [Fact]
    public async Task A_nested_transaction_joins_the_outer_one()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.InTransactionAsync(
            async token =>
            {
                await context.InTransactionAsync(
                    async inner =>
                    {
                        context.Approvals.Add(new Approval { Id = Guid.NewGuid(), Digest = "inner" });
                        await context.SaveChangesAsync(inner);
                    },
                    token);
                throw new InvalidOperationException("the outer unit fails after the inner one returned");
            },
            None));

        await using var check = await TestDbContext.CreateAsync(connectionString);
        Assert.Equal(0, await check.Approvals.CountAsync(None));
    }

    private async Task<(string ConnectionString, Widget Widget)> SeedAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var context = await TestDbContext.CreateAsync(connectionString);
        var widget = new Widget { FirmId = Guid.NewGuid(), Id = Guid.NewGuid(), Status = "draft" };
        context.Widgets.Add(widget);
        await context.SaveChangesAsync(None);
        return (connectionString, widget);
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (StatusMovedException error)
        {
            return error;
        }
    }
}
