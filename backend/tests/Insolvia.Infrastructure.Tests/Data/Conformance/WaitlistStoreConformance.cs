using Insolvia.Data.Waitlist;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="IWaitlistStore"/> must do, against the memory twin
/// and the Postgres store (test_waitlist.py's record-shape tests, moved from
/// the DynamoDB item to what a store keeps). The port has no read, so each
/// suite reads back through its own probe.
/// </summary>
public abstract class WaitlistStoreConformance : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 7, 22, 0, 0, 0, 123, TimeSpan.Zero);

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private protected abstract IWaitlistStore Connect();

    /// <summary>Every record stored, oldest first.</summary>
    private protected abstract Task<IReadOnlyList<WaitlistRecord>> StoredAsync();

    [Fact]
    public async Task A_submission_is_kept_field_for_field()
    {
        var record = new WaitlistRecord(
            "00000000-0000-4000-8000-000000000001",
            At,
            new WaitlistSubmission("Ada Example", "Example & Byron LLP", "ada@example.test", "Best Case", "Two attorneys, chapter 7 heavy.", "www.insolvia.ai"));

        await Connect().AddAsync(record, Ct);

        Assert.Equal([record], await StoredAsync());
    }

    [Fact]
    public async Task Empty_optionals_round_trip_as_empty()
    {
        // The DynamoDB item omitted them rather than storing ""; the Postgres
        // row holds NULL; either way the record reads back as it was given.
        var record = new WaitlistRecord("00000000-0000-4000-8000-000000000002", At, new WaitlistSubmission("Ada", "LLP", "ada@example.test", "", "", ""));

        await Connect().AddAsync(record, Ct);

        Assert.Equal([record], await StoredAsync());
    }

    [Fact]
    public async Task Two_submissions_are_both_kept_in_time_order()
    {
        var later = new WaitlistRecord("00000000-0000-4000-8000-00000000000a", At.AddSeconds(1), new WaitlistSubmission("Bo", "LLP", "bo@example.test", "", "", ""));
        var earlier = new WaitlistRecord("00000000-0000-4000-8000-00000000000b", At, new WaitlistSubmission("Ada", "LLP", "ada@example.test", "", "", ""));

        await Connect().AddAsync(later, Ct);
        await Connect().AddAsync(earlier, Ct);

        Assert.Equal([earlier, later], await StoredAsync());
    }

    [Fact]
    public async Task The_same_id_is_refused_the_second_time()
    {
        var record = new WaitlistRecord("00000000-0000-4000-8000-000000000003", At, new WaitlistSubmission("Ada", "LLP", "ada@example.test", "", "", ""));
        await Connect().AddAsync(record, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().AddAsync(record with { Submission = record.Submission with { Name = "Other" } }, Ct));

        Assert.Equal([record], await StoredAsync());
    }
}

/// <summary>The oracle: <see cref="MemoryWaitlistStore"/>.</summary>
public sealed class MemoryWaitlistStoreConformance : WaitlistStoreConformance
{
    private readonly MemoryWaitlistStore _store = new();

    private protected override IWaitlistStore Connect() => _store;

    private protected override Task<IReadOnlyList<WaitlistRecord>> StoredAsync() =>
        Task.FromResult<IReadOnlyList<WaitlistRecord>>([.. _store.Records.OrderBy(record => record.SubmittedAt).ThenBy(record => record.Id, StringComparer.Ordinal)]);
}

/// <summary><see cref="PostgresWaitlistStore"/> on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresWaitlistStoreConformance(PostgresFixture postgres) : WaitlistStoreConformance
{
    private readonly PostgresHarness _harness = new(postgres);

    public override async ValueTask InitializeAsync() => await _harness.InitializeAsync();

    public override async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override IWaitlistStore Connect() => new PostgresWaitlistStore(_harness.NewContext());

    private protected override async Task<IReadOnlyList<WaitlistRecord>> StoredAsync()
    {
        var rows = await _harness.NewContext().Set<WaitlistEntryRow>().AsNoTracking().OrderBy(row => row.SubmittedAt).ThenBy(row => row.Id).ToListAsync(Ct);
        Assert.All(rows, row => Assert.Equal(WaitlistEntryRow.NewStatus, row.Status));
        return [.. rows.Select(row => row.ToDomain())];
    }
}
